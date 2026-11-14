using System;
using System.Collections.Generic;
using System.IO;
using System.Linq;
using Differ.Engine;
using Differ.Exporters;
using Differ.Providers;

namespace Differ;

/// <summary>One side of a comparison: where its assemblies come from, and the provider's name.</summary>
internal sealed record DiffSide(IAssemblyProvider Provider, string ProviderName);

internal sealed record DiffSettings(HashSet<string> Targets, string Format, string? Output, SuggestedVersionChange FailOn, bool AllowEmptyPreviousNuget)
{
	/// <summary>The release the old side is, when a command resolved one.</summary>
	public string? Baseline { get; init; }
}

/// <summary>Compares the assemblies two sides provide, writes the report and returns the exit code.</summary>
internal static class DiffRunner
{
	/// <summary>Writes the report of a first release, which has nothing to compare with, in the formats that have one.</summary>
	public static void FirstRelease(string package, string format, string? output)
	{
		var writers = new OutputWriterFactory(output);
		if (format == "json")
		{
			using var writer = writers.Create("api-changes.json");
			writer.WriteLine(JsonExporter.FirstRelease());
		}
		else if (format == "github-comment")
		{
			using var writer = writers.Create("github-breaking-changes-comments.md");
			writer.WriteLine($"### API Changes: `{package}`");
			writer.WriteLine();
			writer.WriteLine($"This is the first release of `{package}`: there is no earlier release to compare with.");
		}
	}

	public static int Run(DiffSide first, DiffSide second, DiffSettings settings)
	{
		var exporters = new ExporterCollection(
			new XmlExporter(),
			new MarkdownExporter(),
			new AsciiDocExporter(),
			new GitHubActionCommentExporter(),
			new JsonExporter()
		);
		var outputWriterFactory = new OutputWriterFactory(settings.Output);
		var targets = settings.Targets;
		var format = settings.Format;
		var preventChangeValue = settings.FailOn;
		var allowEmptyPreviousNuget = settings.AllowEmptyPreviousNuget;
		var firstProvider = first.Provider;
		var secondProvider = second.Provider;
		var firstProviderName = first.ProviderName;
		var secondProviderName = second.ProviderName;

		try
		{
			if (!exporters.Contains(format))
				throw new Exception($"No exporter for format '{format}'");

			var exporter = exporters[format];

			var firstAssemblies = firstProvider.GetAssemblies(targets).ToList();
			var secondAssemblies = secondProvider.GetAssemblies(targets).ToList();
			var pairs = CreateAssemblyPairs(firstAssemblies, secondAssemblies).ToList();
			var onlyInFirst = Unpaired(firstAssemblies, pairs.Select(p => p.First));
			var onlyInSecond = Unpaired(secondAssemblies, pairs.Select(p => p.Second));

			if (!pairs.Any())
			{
				if (allowEmptyPreviousNuget && (firstProviderName == "previous-nuget" || secondProviderName == "previous-nuget"))
				{
					Console.WriteLine($"[diff] No previous nuget found for: {firstProviderName}");
					Console.WriteLine($"[diff]   {firstProvider.GetType().Name}: {firstAssemblies.Count} assemblies");
					Console.WriteLine($"[diff]   {secondProvider.GetType().Name}: {secondAssemblies.Count} assemblies");
					Console.WriteLine($"[diff]");
					Console.WriteLine($"[diff]   NOT treated as an error because --allow-empty-previous-nuget was set");
					return 0;
				}

				Console.Error.WriteLine($"[diff] Unable to create diff!");
				Console.Error.WriteLine($"[diff]   {firstProvider.GetType().Name}: {firstAssemblies.Count} assemblies");
				Console.Error.WriteLine($"[diff]   {secondProvider.GetType().Name}: {secondAssemblies.Count} assemblies");
				return 1;
			}

			foreach (var assembly in onlyInFirst)
				Console.WriteLine($"[diff] Only in the old version: {assembly.Name}");
			foreach (var assembly in onlyInSecond)
				Console.WriteLine($"[diff] Only in the new version: {assembly.Name}");

			// without targets, an assembly only one side has is as likely a dependency as API, so it only counts when named
			var result = new AllComparisonResults(pairs, preventChangeValue)
			{
				Baseline = settings.Baseline,
				Removed = targets.Count > 0 ? onlyInFirst : [],
				Added = targets.Count > 0 ? onlyInSecond : [],
			};
			foreach (var assemblyPair in pairs)
			{
				assemblyPair.Diff = ApiDiff.Compare(assemblyPair.First.FullName, assemblyPair.Second.FullName);

				if (assemblyPair.Diff == null)
				{
					Console.WriteLine($"[diff] No diff between {assemblyPair.First.FullName} and {assemblyPair.Second.FullName}");
					continue;
				}
				Console.WriteLine(
					$"[diff] Difference found: {firstProvider.GetType().Name}:{assemblyPair.First.Name} and {secondProvider.GetType().Name}:{assemblyPair.Second.Name}"
				);

				if (exporter is IAssemblyComparisonExporter c)
					c.Export(assemblyPair, outputWriterFactory);
			}
			if (exporter is IAllComparisonResultsExporter allExporter)
				allExporter.Export(result, outputWriterFactory);

			if (preventChangeValue > SuggestedVersionChange.None && result.SuggestedVersionChange >= preventChangeValue)
			{
				Console.Error.WriteLine(
					$"[diff] Needed version change '{result.SuggestedVersionChange}' exceeds or equals configured lock: '{preventChangeValue}"
				);
				return 4;
			}
			Console.WriteLine($"[diff] Suggested version change: {result.SuggestedVersionChange}");
		}
		catch (Exception e)
		{
			Console.Error.WriteLine(e);
			return 1;
		}
		return 0;
	}

	/// <summary>
	/// Pairs the assemblies by file name. When each side has exactly one assembly they are compared whatever their names,
	/// such as a dll and a renamed copy of it.
	/// </summary>
	private static IEnumerable<AssemblyComparison> CreateAssemblyPairs(IReadOnlyList<FileInfo> first, IReadOnlyList<FileInfo> second) =>
		first.Count == 1 && second.Count == 1
			? [new AssemblyComparison(first[0], second[0])]
			: first.Join(second, f => f.Name.ToUpperInvariant(), f => f.Name.ToUpperInvariant(), (f1, f2) => new AssemblyComparison(f1, f2));

	private static List<FileInfo> Unpaired(IEnumerable<FileInfo> assemblies, IEnumerable<FileInfo> paired)
	{
		var pairedPaths = new HashSet<string>(paired.Select(p => p.FullName));
		return assemblies.Where(a => !pairedPaths.Contains(a.FullName)).ToList();
	}
}
