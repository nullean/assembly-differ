using System;
using System.IO;

namespace Differ.Exporters
{
	public class XmlExporter : IExporter
	{
		public string Format { get; } = "xml";

		public void Export(AssemblyDiffPair assemblyDiffPair, string outputPath)
		{
			var xml = assemblyDiffPair.Diff.ToXml();
			using (var writer = new StreamWriter(Path.Combine(outputPath, Path.ChangeExtension(assemblyDiffPair.First.Name, "xml"))))
				writer.Write(xml);
		}
	}
}
