using JustAssembly.Core;

namespace Differ.Exporters
{
	public interface IExporter
	{
		string Format { get; }

		void Export(AssemblyDiffPair assemblyDiffPair, string outputPath);
	}
}
