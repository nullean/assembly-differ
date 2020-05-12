using System;
using System.Collections.Generic;
using System.IO;
using System.Linq;
using System.Text.RegularExpressions;
using System.Xml.Linq;
using JustAssembly.Core;
using JustAssembly.Core.DiffItems.References;
using Telerik.JustDecompiler.Ast.Statements;

namespace Differ.Exporters
{
	/**
	 *
```diff
Scanned: 📑 7 projects
- ⚠️ 12 breaking changes detected in 📑 3 projects ⚠️
```

### 📑 MyNamespace.ProjectA

```diff
- 🔴 IChangeList
- 🔹 IExtensions.MyMethod(string x)
+ 🔷 IExtensions.MyMethod(string x, string y)
````







	 */
	public class GitHubActionCommentExporter : IAllComparisonResultsExporter
	{
		public string Format { get; } = "github-comment";

		public void Export(AllComparisonResults results, string outputPath)
		{
			var prevent = results.PreventVersionChange;
			if (results.SuggestedVersionChange < prevent)
				return;

			using var writer = new StreamWriter(Path.Combine(outputPath, "github-breaking-changes-comments.md"));

			var breakingComparisons = results.Comparisons
				.Where(c => c.SuggestedVersionChange >= prevent)
				.ToList();


			var breakingChanges = new List<IDiffItem>();
			int deleted = 0, modified = 0, introduced = 0;
			foreach (var b in breakingComparisons)
			{
				b.Diff.Visit((item, depth) =>
				{
					if (item.DiffType == DiffType.Deleted) deleted++;
					if (depth > 2 && item.DiffType == DiffType.Modified) modified++;
					if (item.DiffType == DiffType.New) introduced++;

					//TODO make this configurable, don't count assembly reference changes as breaking
					if (item is AssemblyReferenceDiffItem) return;

					if (depth < 2) return;
					var changedType = depth == 2 && item.DiffType == DiffType.Modified;
					// type is modified, count its changes not the type itself
					if (changedType) return;

					if (IncludeDiffType(prevent, item))
						breakingChanges.Add(item);
				});
			}

			writer.WriteLine($@"## Public API Changes

```diff
Scanned: 📑 {results.Comparisons.Count} project(s)
- ⚠️  {breakingChanges.Count} breaking change(s) detected in 📑 {breakingComparisons.Count} project(s) ⚠️
```
```diff
+ {introduced} new additions
- {deleted} removals
- {modified} modifications
```");
			foreach (var c in results.Comparisons)
			{
				writer.WriteLine($@"
-----

<details>
<summary><b>📑 {c.First.Name}
</b> <pre><b> Click here to see the {introduced+deleted+modified} differences </b>
</summary>

```diff
");
				c.Diff.Visit(((item, i) =>
				{
					var changedType = i == 2 && item.DiffType == DiffType.Modified;
					if (item.DiffType == DiffType.Deleted)
						writer.Write("- 🔴 ");
					else if (item.DiffType == DiffType.New)
						writer.Write("+ 🌟 ");
					else if (i> 2 && item.DiffType == DiffType.Modified)
						writer.Write("+ 🔷 ");
					else if (changedType)
						writer.WriteLine($"```{Environment.NewLine}```diff");

					var isAssemblyRefChange = item is AssemblyReferenceDiffItem;
					var breakingMarker = !isAssemblyRefChange && i >= 2 && !changedType && item.IsBreakingChange;

					writer.WriteLine($"{item.HumanReadable} {(breakingMarker ? "💥 " : "")}");
				}));
				writer.WriteLine(@"```");
				writer.WriteLine("</details>");
			}
		}

		private static bool IncludeDiffType(SuggestedVersionChange prevent, IDiffItem change)
		{
			switch (prevent)
			{
				case SuggestedVersionChange.Major:
					return change.IsBreakingChange;
				case SuggestedVersionChange.Minor:
					return change.IsBreakingChange || change.DiffType == DiffType.New;
				case SuggestedVersionChange.Patch:
					return true;
				default:
					return false;
			}
		}

		private void WriteTypeElement(StreamWriter writer, XElement typeElement)
		{
			var typeName = typeElement.Attribute("Name")?.Value;
			var diffType = (DiffType) Enum.Parse(typeof(DiffType), typeElement.Attribute("DiffType").Value);

			switch(diffType)
			{
				case DiffType.Deleted:
					writer.WriteLine($"## `{typeName}` is deleted");
					break;
				case DiffType.Modified:
					WriteMemberElements(writer, typeName, typeElement);
					break;
				case DiffType.New:
					writer.WriteLine($"## `{typeName}` is new");
					break;
				default:
					throw new ArgumentOutOfRangeException();
			}
		}

		private void WriteMemberElements(StreamWriter writer, string typeName, XElement typeElement)
		{
			var memberElements = typeElement.Elements("Method").Concat(typeElement.Elements("Property"));

			if (memberElements.Any())
				writer.WriteLine($"## `{typeName}`");

			foreach (var memberElement in memberElements)
			{
				var memberName = memberElement.Attribute("Name")?.Value;
				if (!string.IsNullOrEmpty(memberName) && Enum.TryParse(typeElement.Attribute("DiffType")?.Value, out DiffType diffType))
				{
					switch (diffType)
					{
						case DiffType.Deleted:
							writer.WriteLine($"### `{memberName}` is deleted");
							break;
						case DiffType.Modified:
							var diffItem = memberElement.Descendants("DiffItem").FirstOrDefault();
							if (diffItem != null)
							{
								writer.WriteLine($"### `{memberName}`");
								writer.WriteLine(
									Regex.Replace(diffItem.Value, "changed from (.*?) to (.*).", "changed from `$1` to `$2`."));
							}
							else
								writer.WriteLine($"### `{memberName}` is added");
							break;
						case DiffType.New:
							writer.WriteLine($"### `{memberName}` is added");
							break;
						default:
							throw new ArgumentOutOfRangeException();
					}
				}
			}
		}
	}
}
