using System;
using System.IO;
using System.Linq;
using System.Text.RegularExpressions;
using System.Xml.Linq;
using JustAssembly.Core;

namespace Differ.Exporters
{
	public class AsciiDocExporter : IExporter
	{
		public string Format { get; } = "asciidoc";

		public void Export(AssemblyDiffPair assemblyDiffPair, string outputPath)
		{
			// IDiffItem implementations are internal so parse from XML for now
			var xml = assemblyDiffPair.Diff.ToXml();
			var doc = XDocument.Parse(xml);
			var name = assemblyDiffPair.First.Name;
			using (var writer = new StreamWriter(Path.Combine(outputPath, Path.ChangeExtension(name, "asciidoc"))))
			{
				writer.WriteLine($"== Breaking changes for {Path.GetFileNameWithoutExtension(name)}");
				writer.WriteLine();

				foreach (var typeElement in doc.Descendants("Type"))
					WriteTypeElement(writer, typeElement);
			}
		}

		private void WriteTypeElement(StreamWriter writer, XElement typeElement)
		{
			var typeName = typeElement.Attribute("Name")?.Value;
			var diffType = (DiffType) Enum.Parse(typeof(DiffType), typeElement.Attribute("DiffType").Value);

			switch(diffType)
			{
				case DiffType.Deleted:
					writer.WriteLine($"[float]{Environment.NewLine}=== `{typeName}` is deleted");
					break;
				case DiffType.Modified:
					WriteMemberElements(writer, typeName, typeElement);
					break;
				case DiffType.New:
					writer.WriteLine($"[float]{Environment.NewLine}=== `{typeName}` is added");
					break;
				default:
					throw new ArgumentOutOfRangeException();
			}
		}

		private void WriteMemberElements(StreamWriter writer, string typeName, XElement typeElement)
		{
			var memberElements = typeElement.Elements("Method").Concat(typeElement.Elements("Property"));

			if (memberElements.Any())
				writer.WriteLine($"[float]{Environment.NewLine}=== `{typeName}`");

			foreach (var memberElement in memberElements)
			{
				var memberName = memberElement.Attribute("Name")?.Value;
				if (!string.IsNullOrEmpty(memberName) && Enum.TryParse(typeElement.Attribute("DiffType")?.Value, out DiffType diffType))
				{
					switch (diffType)
					{
						case DiffType.Deleted:
							writer.WriteLine($"[float]{Environment.NewLine}==== `{memberName}` is deleted");
							break;
						case DiffType.Modified:
							var diffItem = memberElement.Descendants("DiffItem").FirstOrDefault();
							if (diffItem != null)
							{
								writer.WriteLine($"[float]{Environment.NewLine}==== `{memberName}`");
								writer.WriteLine(
									Regex.Replace(diffItem.Value, "changed from (.*?) to (.*).", "changed from `$1` to `$2`."));
							}
							break;
						case DiffType.New:
							writer.WriteLine($"[float]{Environment.NewLine}==== `{memberName}` is added");
							break;
						default:
							throw new ArgumentOutOfRangeException();
					}
				}
			}
		}
	}
}
