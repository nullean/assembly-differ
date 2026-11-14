using System;
using System.Collections.Generic;
using System.IO;
using System.Linq;
using Differ.Engine;

namespace Differ.Exporters;

public class GitHubActionCommentExporter : IAllComparisonResultsExporter
{
	public string Format { get; } = "github-comment";

	public void Export(AllComparisonResults results, OutputWriterFactory factory)
	{
		var prevent = results.PreventVersionChange;
		using var writer = factory.Create("github-breaking-changes-comments.md");

		var breakingComparisons = results.Comparisons.Where(c => c.Diff != null && c.SuggestedVersionChange >= prevent).ToList();

		var breakingChanges = new List<IDiffItem>();
		var assembliesWithBreakingChanges = new HashSet<AssemblyComparison>();
		var differences = new Dictionary<AssemblyComparison, int>();
		int deleted = 0, modified = 0, introduced = 0;
		foreach (var b in breakingComparisons)
		{
			b.Diff.Visit((item, depth) =>
			{
				if (item.DiffType == DiffType.Deleted)
					deleted++;
				if (depth > 2 && item.DiffType == DiffType.Modified)
					modified++;
				if (item.DiffType == DiffType.New)
					introduced++;
				if (item.DiffType == DiffType.Deleted || item.DiffType == DiffType.New || depth > 2 && item.DiffType == DiffType.Modified)
					differences[b] = differences.GetValueOrDefault(b) + 1;

				//TODO make this configurable, don't count assembly reference changes as breaking
				if (item is AssemblyReferenceDiffItem)
					return;

				if (depth < 2)
					return;
				var changedType = depth == 2 && item.DiffType == DiffType.Modified;
				// type is modified, count its changes not the type itself
				if (changedType)
					return;

				// a modified member is breaking because of a change inside it, which is counted on its own
				var breaking = AssemblyComparison.Classify(item) == SuggestedVersionChange.Major;
				if (breaking && item is not IMetadataDiffItem { DiffType: DiffType.Modified })
				{
					breakingChanges.Add(item);
					assembliesWithBreakingChanges.Add(b);
				}
			});
		}

		var totalChanges = deleted + modified + introduced;
		if (results.Comparisons.Count == 1)
			writer.WriteLine($@"### API Changes: `{Path.GetFileNameWithoutExtension(results.Comparisons.First().First.Name)}`");
		else
			writer.WriteLine($@"### API Changes");

		var versionChange = Enum.GetName(typeof(SuggestedVersionChange), results.SuggestedVersionChange);
		if (breakingChanges.Count > 0)
		{
			writer.WriteLine(
				$@"
```diff
Scanned: 📑 {Assemblies(results.Comparisons.Count)}, Suggested change in version: {versionChange}
- ⚠️  {breakingChanges.Count} breaking change(s) detected in 📑 {Assemblies(assembliesWithBreakingChanges.Count)}"
			);
		}
		else
		{
			writer.WriteLine(
				$@"
```diff
Scanned: 📑 {Assemblies(results.Comparisons.Count)}, Suggested change in version: {versionChange}
+ {breakingChanges.Count} breaking change(s) detected."
			);
		}

		if (totalChanges > 0)
		{
			writer.WriteLine(
				$@"
+ 🌟 {introduced} new additions
- 🔴 {deleted} removals
- 🔷 {modified} modifications
```"
			);
		}
		else
		{
			writer.WriteLine(
				$@"
🌟 {introduced} new additions, 🔴 {deleted} removals, 🔷 {modified} modifications
```"
			);
		}

		if (results.SuggestedVersionChange < prevent)
			return;

		if (results.Comparisons.Count == 1 && results.Comparisons.First().Diff == null)
			return;

		foreach (var c in results.Comparisons)
		{
			if (c.Diff == null)
			{
				writer.WriteLine(
					$@"-----

<b>📑 {c.First.Name}
</b> <pre><b> No public API Changes detected</pre></b>
"
				);
				continue;
			}

			writer.WriteLine(
				$@"
-----

<details>
<summary><b>📑 {c.First.Name}
</b> <pre><b> Click here to see the {differences.GetValueOrDefault(c)} differences </b>
</summary>

```diff
"
			);
			c.Diff.Visit(
				((item, i) =>
				{
					var changedType = i == 2 && item.DiffType == DiffType.Modified;
					if (item.DiffType == DiffType.Deleted)
						writer.Write("- 🔴 ");
					else if (item.DiffType == DiffType.New)
						writer.Write("+ 🌟 ");
					else if (i > 2 && item.DiffType == DiffType.Modified)
						writer.Write("+ 🔷 ");
					else if (changedType)
						writer.WriteLine($"```{Environment.NewLine}```diff");

					var isAssemblyRefChange = item is AssemblyReferenceDiffItem;
					var breakingMarker = !isAssemblyRefChange && i >= 2 && !changedType && item.IsBreakingChange;

					writer.WriteLine($"{item.HumanReadable} {(breakingMarker ? "💥 " : "")}");
				})
			);
			writer.WriteLine(@"```");
			writer.WriteLine("</details>");
		}
	}

	private static string Assemblies(int count) => count == 1 ? "1 assembly" : $"{count} assemblies";
}
