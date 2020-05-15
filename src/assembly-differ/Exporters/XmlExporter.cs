using System;
using System.IO;

namespace Differ.Exporters
{
	public class XmlExporter : IAssemblyComparisonExporter
	{
		public string Format { get; } = "xml";

		public void Export(AssemblyComparison assemblyComparison, OutputWriterFactory factory)
		{
			var xml = assemblyComparison.Diff.ToXml();
			using var writer = factory.Create(Path.ChangeExtension(assemblyComparison.First.Name, "xml"));
			writer.Write(xml);
		}
	}
}
