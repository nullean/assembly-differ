#nullable enable
using System;
using System.Collections.Generic;
using System.IO;
using System.Linq;
using Nullean.PublicApi;

namespace Differ.ApiFiles;

/// <summary>Checks committed .api files against a build, and reports what changed by path, with where in the files.</summary>
internal static class Verify
{
	public static int Run(string directory, SortedDictionary<string, string> expected, IReadOnlyList<ApiFileCommand.Assembly> assemblies, string update)
	{
		var committed = ApiFileCommand.Existing(directory);
		if (committed.Count == expected.Count && committed.All(c => expected.TryGetValue(c.Key, out var text) && text == c.Value))
		{
			Console.WriteLine($"[api] {directory} matches the build: {expected.Count} file{(expected.Count == 1 ? "" : "s")}");
			return 0;
		}

		var worst = SuggestedVersionChange.None;
		var names = expected.Keys.Concat(committed.Keys).Select(Name).Distinct().OrderBy(n => n, StringComparer.Ordinal);
		foreach (var name in names)
		{
			var committedFiles = committed.Where(c => Name(c.Key) == name).ToList();
			var expectedFiles = expected.Where(e => Name(e.Key) == name).ToList();
			if (committedFiles.Count == expectedFiles.Count && committedFiles.All(c => expected.TryGetValue(c.Key, out var text) && text == c.Value))
				continue;
			var fresh = assemblies.FirstOrDefault(a => a.FileName == name);
			if (committedFiles.Count == 0)
			{
				Console.WriteLine($"[api] {name}: the build has it, and {directory} has no .api file for it");
				worst = Max(worst, SuggestedVersionChange.Minor);
				continue;
			}
			if (fresh is null)
			{
				Console.WriteLine($"[api] {name}: {directory} has {string.Join(", ", committedFiles.Select(f => f.Key))}, and the build has no {name}");
				worst = Max(worst, SuggestedVersionChange.Major);
				continue;
			}

			var document = ApiParser.Parse(committedFiles.Select(f => ((string?)Path.Combine(directory, f.Key), f.Value)));
			// what the build would write, read back, so both sides hold only what a file says
			var built = ApiParser.Parse(ApiWriter.Write(fresh.Api)).Assembly;
			var result = Nullean.PublicApi.Diff.ApiComparison.Compare(document.Assembly, built);
			if (result.Changes.Count == 0)
			{
				Console.WriteLine($"[api] {name}: the files differ from what the build writes, but the API is the same; regenerate them");
				worst = Max(worst, SuggestedVersionChange.Patch);
				continue;
			}
			var suggestion = result.Suggest();
			worst = Max(worst, suggestion);
			Console.WriteLine(
				$"[api] {name}: {result.Changes.Count} change{(result.Changes.Count == 1 ? "" : "s")} since the committed files, suggested version change: {suggestion}"
			);
			foreach (var change in result.Changes)
			{
				var location = (change.OldNode is null ? null : document.LocationOf(change.OldNode)) ??
					(change.OldScope is null ? null : document.LocationOf(change.OldScope));
				Console.WriteLine($"  {(location is null ? "" : $"{location.File}:{location.Line}  ")}{ApiReport.Line(change)}");
			}
		}
		Console.WriteLine($"[api] {directory} does not match the build. Suggested version change: {worst}");
		Console.WriteLine($"[api] Update the files with: {update}");
		return ApiFileCommand.Stale;
	}

	/// <summary>The API a file belongs to: the folder of a split API, or the file's name.</summary>
	private static string Name(string relative) =>
		relative.IndexOf('/') is var slash and >= 0 ? relative.Substring(0, slash) : relative.Substring(0, relative.Length - ".api".Length);

	private static SuggestedVersionChange Max(SuggestedVersionChange a, SuggestedVersionChange b) => a > b ? a : b;
}

/// <summary>A change on one line: where, what, and who it breaks.</summary>
internal static class ApiReport
{
	public static string Line(Nullean.PublicApi.Diff.ApiChange change)
	{
		var where = change.Where + (change.Part.Length > 0 ? " › " + change.Part : "");
		var what = change.Kind switch
		{
			Nullean.PublicApi.Diff.ApiChangeKind.Added => "added " + change.New,
			Nullean.PublicApi.Diff.ApiChangeKind.Removed => "removed " + change.Old,
			_ => $"{change.Old ?? "-"} → {change.New ?? "-"}",
		};
		var verdict = change.Level.ToString().ToLowerInvariant();
		if (change.Breaks != Nullean.PublicApi.Diff.ApiAudience.None)
			verdict += ": breaks " + Audience(change.Breaks) + ", " + Compatibility(change.Compatibility);
		return $"{where}  {what}  [{verdict}]" + (change.Note is null ? "" : $" ({change.Note})");
	}

	private static string Audience(Nullean.PublicApi.Diff.ApiAudience audience)
	{
		var who = new List<string>();
		if ((audience & Nullean.PublicApi.Diff.ApiAudience.Callers) != 0)
			who.Add("callers");
		if ((audience & Nullean.PublicApi.Diff.ApiAudience.Implementers) != 0)
			who.Add("implementers");
		if ((audience & Nullean.PublicApi.Diff.ApiAudience.Inheritors) != 0)
			who.Add("inheritors");
		return string.Join(" and ", who);
	}

	private static string Compatibility(Nullean.PublicApi.Diff.ApiCompatibility compatibility) => compatibility switch
	{
		Nullean.PublicApi.Diff.ApiCompatibility.Binary => "compiled code only",
		Nullean.PublicApi.Diff.ApiCompatibility.Source => "source only",
		_ => "compiled code and source",
	};
}
