using System;
using System.Collections.Generic;
using System.ComponentModel.DataAnnotations;
using System.IO;
using System.Linq;
using System.Text;
using System.Text.Encodings.Web;
using System.Text.Json;
using Differ.Engine;
using Differ.Exporters;
using Differ.Providers.NuGet;
using Differ.Providers.PreviousNuGet;
using NuGet.Versioning;
using Nullean.Argh;

namespace Differ.Projects;

/// <summary>One package with a newer release, and what moving to it would change.</summary>
internal sealed record Upgrade(
	string PackageId,
	string From,
	string To,
	string Projects,
	SuggestedVersionChange Change,
	int BreakingChanges,
	bool UsesOnly,
	string? Note
);

internal sealed class UpgradesCommand
{
	/// <summary>Lists the packages a solution references that have a newer release, and what updating each would break.</summary>
	/// <remarks>
	/// It reads the packages every project in the directory references directly, as obj/project.assets.json resolves them,
	/// so restore first. Each one with a newer stable release on your NuGet.config feeds is compared with it, the way
	/// nuget X is: only the changes to API your built projects use count, unless --all is set, so build first. Use
	/// assembly-differ nuget X to see one package's changes in full.
	/// </remarks>
	/// <param name="all">Count every change, not only the ones to API your built projects use.</param>
	/// <param name="format">-f, --format, markdown, a table, or json.</param>
	/// <param name="output">-o, --output, A directory or file to write the report to, besides the console.</param>
	/// <param name="failOn">Exit with code 4 when an update's change to the API your code uses is at least this; none never fails.</param>
	/// <example>dotnet build &amp;&amp; assembly-differ upgrades</example>
	[CommandName("upgrades")]
	public int Upgrades(
		bool all = false,
		[AllowedValues("markdown", "json")] string format = "markdown",
		string? output = null,
		SuggestedVersionChange failOn = SuggestedVersionChange.None
	)
	{
		IReadOnlyList<(string PackageId, Resolution Resolution)> direct;
		try
		{
			direct = ResolvedPackages.Direct(Directory.GetCurrentDirectory());
		}
		catch (Exception e) when (e is Dotnet.SdkException or ArgumentException)
		{
			Console.Error.WriteLine($"[diff] {e.Message}");
			return 1;
		}
		if (direct.Count == 0)
		{
			Console.Error.WriteLine($"[diff] no restored project in {Directory.GetCurrentDirectory()} references a package; run dotnet restore first");
			return 1;
		}

		var upgrades = new List<Upgrade>();
		var upToDate = 0;
		foreach (var group in direct.GroupBy(d => (Id: d.PackageId.ToLowerInvariant(), d.Resolution.Version, d.Resolution.LibFolder)))
		{
			var id = group.First().PackageId;
			var resolutions = group.Select(d => d.Resolution).ToList();
			var projects = string.Join(", ", resolutions.Select(r => r.Project).Distinct());
			var latest = PreviousRelease.Find(id, new NuGetVersion(int.MaxValue, 0, 0));
			if (latest is null || latest <= NuGetVersion.Parse(group.Key.Version))
			{
				upToDate++;
				continue;
			}
			var to = latest.ToNormalizedString();
			Console.Error.WriteLine($"[diff] Comparing {id} {group.Key.Version} with {to}");
			if (group.Key.LibFolder.Length == 0)
			{
				upgrades.Add(new Upgrade(id, group.Key.Version, to, projects, SuggestedVersionChange.None, 0, false, "ships no assemblies to compare"));
				continue;
			}
			upgrades.Add(Compare(id, group.Key.Version, to, group.Key.LibFolder, projects, all ? null : ApiCommands.UsesFilter(resolutions, quiet: true)));
		}

		var text = format == "json" ? Json(upgrades, upToDate) : Markdown(upgrades, upToDate);
		using (var writer = new OutputWriterFactory(output).Create(format == "json" ? "upgrades.json" : "upgrades.md"))
			writer.WriteLine(text);

		var worst = upgrades.Select(u => u.Change).DefaultIfEmpty(SuggestedVersionChange.None).Max();
		if (failOn > SuggestedVersionChange.None && worst >= failOn)
		{
			Console.Error.WriteLine(
				$"[diff] An update's change to the API your code uses is {worst}, at least --fail-on {failOn.ToString().ToLowerInvariant()}"
			);
			return 4;
		}
		return 0;
	}

	/// <summary>Compares two versions of a package without the reports, which a table of many does not need.</summary>
	private static Upgrade Compare(
		string id,
		string from,
		string to,
		string libFolder,
		string projects,
		Func<AssemblyComparison, IMetadataDiffItem, IMetadataDiffItem?>? filter
	)
	{
		var console = Console.Out;
		try
		{
			// the providers say what they download; one line per package is enough here
			Console.SetOut(TextWriter.Null);
			var installer = new Providers.NuGet.NuGet();
			var targets = new HashSet<string>(StringComparer.OrdinalIgnoreCase);
			var oldAssemblies = new NuGetAssemblyProvider(installer, [id, from, libFolder]).GetAssemblies(targets).ToList();
			var newAssemblies = new NuGetAssemblyProvider(installer, [id, to, libFolder]).GetAssemblies(targets).ToList();
			var pairs = oldAssemblies.Join(
				newAssemblies,
				a => a.Name.ToUpperInvariant(),
				a => a.Name.ToUpperInvariant(),
				(a, b) => new AssemblyComparison(a, b)
			).ToList();
			var removed = oldAssemblies.Where(a => !pairs.Any(p => p.First == a)).ToList();
			foreach (var pair in pairs)
			{
				pair.Diff = ApiDiff.Compare(pair.First.FullName, pair.Second.FullName);
				if (pair.Diff is not null && filter is not null)
					pair.Diff = filter(pair, pair.Diff);
			}
			var results = new AllComparisonResults(pairs, SuggestedVersionChange.None) { Removed = filter is null ? removed : [] };
			var breaking = pairs.Sum(p => p.Diff is null ? 0 : JsonExporter.CountBreaking(p.Diff)) + results.Removed.Count;
			return new Upgrade(id, from, to, projects, results.SuggestedVersionChange, breaking, filter is not null, null);
		}
		catch (Exception e)
		{
			return new Upgrade(id, from, to, projects, SuggestedVersionChange.None, 0, false, $"could not compare: {e.Message}");
		}
		finally
		{
			Console.SetOut(console);
		}
	}

	private static string Markdown(IReadOnlyList<Upgrade> upgrades, int upToDate)
	{
		var table = new StringBuilder();
		if (upgrades.Count == 0)
			return $"Every package is up to date ({upToDate}).";
		var change = upgrades.Any(u => u.UsesOnly) ? "Change to what you use" : "Change";
		table.AppendLine($"| Package | From | To | {change} | Breaking changes | Projects |");
		table.AppendLine("|---|---|---|---|---|---|");
		foreach (var upgrade in upgrades.OrderByDescending(u => u.Change).ThenBy(u => u.PackageId, StringComparer.OrdinalIgnoreCase))
		{
			var breaking = upgrade.Note ?? (upgrade.UsesOnly ? $"{upgrade.BreakingChanges} in what you use" : $"{upgrade.BreakingChanges}");
			table.AppendLine($"| {upgrade.PackageId} | {upgrade.From} | {upgrade.To} | {upgrade.Change} | {breaking} | {upgrade.Projects} |");
		}
		table.AppendLine();
		table.Append($"{upToDate} more up to date. `assembly-differ nuget <package> <version>` shows one update in full.");
		return table.ToString();
	}

	private static string Json(IReadOnlyList<Upgrade> upgrades, int upToDate)
	{
		using var stream = new MemoryStream();
		using (var json = new Utf8JsonWriter(stream, new JsonWriterOptions { Indented = true, Encoder = JavaScriptEncoder.UnsafeRelaxedJsonEscaping }))
		{
			json.WriteStartObject();
			json.WriteNumber("upToDate", upToDate);
			json.WriteStartArray("upgrades");
			foreach (var upgrade in upgrades)
			{
				json.WriteStartObject();
				json.WriteString("package", upgrade.PackageId);
				json.WriteString("from", upgrade.From);
				json.WriteString("to", upgrade.To);
				json.WriteString("suggestedVersionChange", upgrade.Change.ToString());
				json.WriteNumber("breakingChanges", upgrade.BreakingChanges);
				json.WriteBoolean("usesOnly", upgrade.UsesOnly);
				json.WriteString("projects", upgrade.Projects);
				if (upgrade.Note is not null)
					json.WriteString("note", upgrade.Note);
				json.WriteEndObject();
			}
			json.WriteEndArray();
			json.WriteEndObject();
		}
		return Encoding.UTF8.GetString(stream.ToArray());
	}
}
