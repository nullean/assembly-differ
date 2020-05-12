using System;
using System.IO;

namespace Differ.Exporters
{
	public class XmlExporter : IAssemblyComparisonExporter
	{
		public string Format { get; } = "xml";

		public void Export(AssemblyComparison assemblyComparison, string outputPath)
		{
			var xml = assemblyComparison.Diff.ToXml();
			using (var writer = new StreamWriter(Path.Combine(outputPath, Path.ChangeExtension(assemblyComparison.First.Name, "xml"))))
				writer.Write(xml);
		}
	}
}
