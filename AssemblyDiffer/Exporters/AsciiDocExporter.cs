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

				foreach (var typeElement in doc.Descendants("Type"))
				{
					writer.WriteLine();
					WriteTypeElement(writer, typeElement);
				}
			}
		}

		private void WriteTypeElement(StreamWriter writer, XElement typeElement)
		{
			var typeName = typeElement.Attribute("Name")?.Value;
			var diffType = (DiffType) Enum.Parse(typeof(DiffType), typeElement.Attribute("DiffType").Value);

			switch(diffType)
			{
				case DiffType.Deleted:
					writer.WriteLine("[discrete]");
					writer.WriteLine($"=== `{typeName}`");
					writer.WriteLine();
					writer.WriteLine("[horizontal]");
					writer.WriteLine("type:: deleted");
					break;
				case DiffType.Modified:
					WriteMemberElements(writer, typeName, typeElement);
					break;
				case DiffType.New:
					writer.WriteLine("[discrete]");
					writer.WriteLine($"=== `{typeName}`");
					writer.WriteLine();
					writer.WriteLine("[horizontal]");
					writer.WriteLine("type:: added");
					break;
				default:
					throw new ArgumentOutOfRangeException();
			}
		}

		private void WriteMemberElements(StreamWriter writer, string typeName, XElement typeElement)
		{
			var memberElements = typeElement.Elements("Method")
				.Concat(typeElement.Elements("Property"))
				.Where(m =>
				{
					var diffType = m.Attribute("DiffType").Value;
					return diffType == "New" || diffType == "Deleted" || m.Descendants("DiffItem").Any();
				}).ToList();

			if (memberElements.Any())
			{
				writer.WriteLine("[discrete]");
				writer.WriteLine($"=== `{typeName}`");
				writer.WriteLine();
				writer.WriteLine("[horizontal]");
				foreach (var memberElement in memberElements)
				{
					var memberName = memberElement.Attribute("Name")?.Value;
					if (!string.IsNullOrEmpty(memberName) && Enum.TryParse(memberElement.Attribute("DiffType")?.Value,
						    out DiffType diffType))
					{
						var memberType = memberElement.Name.LocalName.ToLowerInvariant();

						switch (diffType)
						{
							case DiffType.Deleted:
								writer.WriteLine($"`{memberName}` {memberType}:: deleted");
								break;
							case DiffType.Modified:
								if (memberType == "method")
								{
									var diffItem = memberElement.Descendants("DiffItem").FirstOrDefault();
									if (diffItem != null)
									{
										writer.WriteLine($"`{memberName}` {memberType}::");
										writer.WriteLine(
											Regex.Replace(diffItem.Value, "changed from (.*?) to (.*).",
												"changed from `$1` to `$2`."));
									}
								}
								else if (memberType == "property")
								{
									var methods = memberElement.Elements("Method");
									if (methods.Any())
									{
										foreach (var propertyMethod in methods)
										{
											writer.WriteLine($"`{memberName}` {memberType} {propertyMethod.Attribute("Name").Value}ter::");
											var diffItem = propertyMethod.Descendants("DiffItem").FirstOrDefault();
											if (diffItem != null)
											{
												if (Regex.IsMatch(diffItem.Value, "changed from (.*?) to (.*)."))
												{
													writer.WriteLine(
														Regex.Replace(diffItem.Value, "changed from (.*?) to (.*).",
															"changed from `$1` to `$2`."));
												}
												else if (Regex.IsMatch(diffItem.Value, "Method changed"))
												{
													writer.WriteLine(
														Regex.Replace(diffItem.Value, "Method changed (.*)",
															"changed $1"));
												}
												else
													writer.WriteLine(diffItem.Value);
											}
										}
									}

									var propertyDiffItem = memberElement.Elements("DiffItem").FirstOrDefault();
									if (propertyDiffItem != null)
									{
										writer.WriteLine($"`{memberName}` {memberType}::");
										if (Regex.IsMatch(propertyDiffItem.Value, "changed from (.*?) to (.*)."))
										{
											writer.WriteLine(
												Regex.Replace(propertyDiffItem.Value, "changed from (.*?) to (.*).",
													"changed from `$1` to `$2`."));
										}
										else if (Regex.IsMatch(propertyDiffItem.Value, "Method changed"))
										{
											writer.WriteLine(
												Regex.Replace(propertyDiffItem.Value, "Method changed (.*)",
													"changed $1"));
										}
										else
											writer.WriteLine(propertyDiffItem.Value);
									}

								}
								break;
							case DiffType.New:
								writer.WriteLine($"`{memberName}` {memberType}:: added");
								break;
							default:
								throw new ArgumentOutOfRangeException();
						}
					}
				}
			}
		}
	}
}
