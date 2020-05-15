using JustAssembly.Core;

namespace Differ.Exporters
{
	public interface IExporter
	{
		string Format { get; }
	}

	/// <summary> Represents an exporter that emits each <see cref="AssemblyComparison"/> to a single file </summary>
	public interface IAssemblyComparisonExporter : IExporter
	{
		void Export(AssemblyComparison assemblyComparison, OutputWriterFactory writerFactory);
	}

	/// <summary> Represents an exporter that exports all results into a single file </summary>
	public interface IAllComparisonResultsExporter : IExporter
	{
		void Export(AllComparisonResults results, OutputWriterFactory writerFactory);

	}
}
