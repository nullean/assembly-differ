#nullable enable
using System;
using System.Collections.Generic;
using System.ComponentModel.DataAnnotations;
using System.IO;
using System.Linq;
using System.Text;
using System.Text.Json;
using Nullean.Argh;
using Nullean.PublicApi;

namespace Differ.ApiFiles;

/// <summary>Compares the committed .api files of two versions of a repository, without building or loading anything.</summary>
internal sealed class ApiDiffCommand
{
	/// <summary>What one API did: its name, the version change it calls for and the changes, by path.</summary>
	internal sealed record Entry(string Name, SuggestedVersionChange Suggested, int Breaking, IReadOnlyList<Item> Changes);

	/// <summary>One change: what it is, and how far it reaches.</summary>
	internal sealed record Item(string Text, string Change, SuggestedVersionChange Level);

	/// <summary>Compares the .api files in two directories: what changed from the old ones to the new ones, by path.</summary>
	/// <remarks>
	/// Each API is compared with the one of the same name, so the files of one target framework are compared with the files
	/// of the same framework. An API only the new directory has is added, a minor change, and one only the old directory
	/// has is removed, a major one. Nothing is built, so it compares what two commits committed, such as the base of a
	/// pull request and its head, and says what the pull request itself changes however much main has changed since the
	/// last release. Always exits with 0 when it could compare; read the suggested version change from the report.
	/// </remarks>
	/// <param name="oldDirectory">The directory of the earlier .api files, such as the base branch's api/.</param>
	/// <param name="newDirectory">The directory of the later .api files.</param>
	/// <param name="format">text prints each change; json writes api-changes.json, with the APIs without changes too.</param>
	/// <param name="output">A file to write the json report to, besides the summary on the console.</param>
	/// <example>assembly-differ api-diff base/api api</example>
	/// <example>assembly-differ api-diff base/api api --format json --output api-delta.json</example>
	[CommandName("api-diff")]
	public int ApiDiff(
		[Argument] string oldDirectory,
		[Argument] string newDirectory,
		[AllowedValues("text", "json")] string format = "text",
		string? output = null
	)
	{
		if (format == "json" && output is null)
		{
			Console.Error.WriteLine("[api] json is written to a file: pass --output");
			return 1;
		}
		try
		{
			var entries = Compare(oldDirectory, newDirectory, out var removed, out var added);
			var worst = entries
				.Select(e => e.Suggested)
				.Append(removed.Count > 0 ? SuggestedVersionChange.Major : SuggestedVersionChange.None)
				.Append(added.Count > 0 ? SuggestedVersionChange.Minor : SuggestedVersionChange.None)
				.Max();
			var breaking = entries.Sum(e => e.Breaking) + removed.Count;
			// the same change in every target framework is one change
			var distinct = entries
				.SelectMany(e => e.Changes.Where(c => c.Level == SuggestedVersionChange.Major).Select(c => (Library(e.Name), c.Text)))
				.Distinct()
				.Count() + removed.Count;
			foreach (var entry in entries.Where(e => e.Changes.Count > 0))
			{
				Console.WriteLine(
					$"[api] {entry.Name}: {entry.Changes.Count} change{(entry.Changes.Count == 1 ? "" : "s")}, suggested version change: {entry.Suggested}"
				);
				foreach (var item in entry.Changes)
					Console.WriteLine($"  {item.Text}");
			}
			foreach (var name in removed)
				Console.WriteLine($"[api] {name}: only in {oldDirectory}, so removed");
			foreach (var name in added)
				Console.WriteLine($"[api] {name}: only in {newDirectory}, so added");
			var unchanged = entries.Count(e => e.Changes.Count == 0);
			Console.WriteLine($"[api] {entries.Count} API{(entries.Count == 1 ? "" : "s")} compared, {unchanged} without changes");
			Console.WriteLine($"[api] Suggested version change: {worst}");
			if (output is not null)
			{
				File.WriteAllBytes(output, Json(entries, removed, added, worst, breaking, distinct));
				Console.WriteLine($"[api] Wrote {output}");
			}
			return 0;
		}
		catch (Exception e) when (e is IOException or ArgumentException or FormatException or InvalidDataException)
		{
			Console.Error.WriteLine($"[api] {e.Message}");
			return 1;
		}
	}

	/// <summary>Compares the APIs in two directories, in name order; the names only one side has are returned apart.</summary>
	internal static List<Entry> Compare(string oldDirectory, string newDirectory, out List<string> removed, out List<string> added)
	{
		var oldFiles = ApiFileCommand.Existing(oldDirectory);
		var newFiles = ApiFileCommand.Existing(newDirectory);
		removed = [];
		added = [];
		var entries = new List<Entry>();
		foreach (var name in oldFiles.Keys.Concat(newFiles.Keys).Select(Name).Distinct().OrderBy(n => n, StringComparer.Ordinal))
		{
			var before = oldFiles.Where(f => Name(f.Key) == name).ToList();
			var after = newFiles.Where(f => Name(f.Key) == name).ToList();
			if (before.Count == 0)
			{
				added.Add(name);
				continue;
			}
			if (after.Count == 0)
			{
				removed.Add(name);
				continue;
			}
			if (before.Count == after.Count && before.All(f => newFiles.TryGetValue(f.Key, out var text) && text == f.Value))
			{
				entries.Add(new Entry(name, SuggestedVersionChange.None, 0, []));
				continue;
			}

			var oldDocument = ApiParser.Parse(before.Select(f => ((string?)Path.Combine(oldDirectory, f.Key), f.Value)));
			var newDocument = ApiParser.Parse(after.Select(f => ((string?)Path.Combine(newDirectory, f.Key), f.Value)));
			var result = Nullean.PublicApi.Diff.ApiComparison.Compare(oldDocument.Assembly, newDocument.Assembly);
			var items = result
				.Changes
				.Select(
					c => new Item(
						ApiReport.Line(c),
						c.Kind switch
						{
							Nullean.PublicApi.Diff.ApiChangeKind.Added => "added",
							Nullean.PublicApi.Diff.ApiChangeKind.Removed => "removed",
							_ => "modified",
						},
						c.Level
					)
				)
				.ToList();
			// files that differ while the API they describe is the same: only how they were written
			entries.Add(new Entry(name, items.Count == 0 ? SuggestedVersionChange.None : result.Suggest(), result.Breaking.Count(), items));
		}
		return entries;
	}

	/// <summary>The report in the shape of api-changes.json, so the pull request comment renders it as it does the others.</summary>
	private static byte[] Json(
		IReadOnlyList<Entry> entries,
		IReadOnlyList<string> removed,
		IReadOnlyList<string> added,
		SuggestedVersionChange worst,
		int breaking,
		int distinct
	)
	{
		using var stream = new MemoryStream();
		using (var json = new Utf8JsonWriter(stream, new JsonWriterOptions { Indented = true }))
		{
			json.WriteStartObject();
			json.WriteBoolean("firstRelease", false);
			json.WriteString("suggestedVersionChange", worst.ToString());
			json.WriteNumber("breakingChanges", breaking);
			json.WriteNumber("distinctBreakingChanges", distinct);
			json.WriteStartArray("assemblies");
			foreach (var entry in entries)
			{
				json.WriteStartObject();
				json.WriteString("name", entry.Name);
				json.WriteString("suggestedVersionChange", entry.Suggested.ToString());
				json.WriteNumber("breakingChanges", entry.Breaking);
				json.WriteStartArray("changes");
				foreach (var item in entry.Changes)
				{
					json.WriteStartObject();
					json.WriteString("kind", "Declaration");
					json.WriteString("name", item.Text);
					json.WriteString("change", item.Change);
					json.WriteString("suggestedVersionChange", item.Level.ToString());
					json.WriteEndObject();
				}
				json.WriteEndArray();
				json.WriteEndObject();
			}
			json.WriteEndArray();
			json.WriteStartArray("removedAssemblies");
			foreach (var name in removed)
				json.WriteStringValue(name);
			json.WriteEndArray();
			json.WriteStartArray("addedAssemblies");
			foreach (var name in added)
				json.WriteStringValue(name);
			json.WriteEndArray();
			json.WriteEndObject();
		}
		return stream.ToArray();
	}

	private static readonly System.Text.RegularExpressions.Regex FrameworkSuffix = new(
		@"\.(net\d+(\.\d+)?|netstandard\d+\.\d+|netcoreapp\d+\.\d+)$",
		System.Text.RegularExpressions.RegexOptions.IgnoreCase | System.Text.RegularExpressions.RegexOptions.CultureInvariant
	);

	/// <summary>The library an API belongs to, without the target framework its name may end in.</summary>
	private static string Library(string name) => FrameworkSuffix.Replace(name, "");

	/// <summary>The API a file belongs to: the folder of a split API, or the file's name.</summary>
	private static string Name(string relative) =>
		relative.IndexOf('/') is var slash and >= 0 ? relative.Substring(0, slash) : relative.Substring(0, relative.Length - ".api".Length);
}
