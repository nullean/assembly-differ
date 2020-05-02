using System;
using System.IO;
using System.Linq;
using System.Text.RegularExpressions;
using System.Xml.Linq;
using JustAssembly.Core;

namespace Differ.Exporters
{
	public class GitHubActionCommentExporter : IAllComparisonResultsExporter
	{
		public string Format { get; } = "github-comment";

		public void Export(AllComparisonResults results, string outputPath)
		{
			if (results.SuggestedVersionChange != SuggestedVersionChange.Major)
				return;

			foreach (var comparison in results.Comparisons)
			{
			}
			//var x = results.Comparisons


			// var xml = results.Diff.ToXml();
			// var doc = XDocument.Parse(xml);
			// var name = results.First.Name;
			// using var writer = new StreamWriter(Path.Combine(outputPath, Path.ChangeExtension(name, "md")));
			//
			// writer.WriteLine($"## API Changes: `{Path.GetFileNameWithoutExtension(name)}`");
			// writer.WriteLine();
			//
			// foreach (var typeElement in doc.Descendants("Type"))
			// 	this.WriteTypeElement(writer, typeElement);
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
