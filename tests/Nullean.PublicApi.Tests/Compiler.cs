using System.Collections.Immutable;
using Microsoft.CodeAnalysis;
using Microsoft.CodeAnalysis.CSharp;

namespace Nullean.PublicApi.Tests;

/// <summary>Compiles C# source into an assembly in memory, against the running framework's assemblies.</summary>
public static class Compiler
{
	public static readonly ImmutableArray<MetadataReference> References =
	[
		.. ((string)AppContext.GetData("TRUSTED_PLATFORM_ASSEMBLIES")!)
			.Split(Path.PathSeparator)
			.Where(p => Path.GetFileName(p).StartsWith("System.", StringComparison.Ordinal) || Path.GetFileName(p) is "netstandard.dll" or "mscorlib.dll")
			.Select(p => (MetadataReference)MetadataReference.CreateFromFile(p)),
	];

	public static MemoryStream Compile(string source, string assemblyName = "Lib", bool nullable = true)
	{
		var compilation = CSharpCompilation.Create(
			assemblyName,
			[CSharpSyntaxTree.ParseText(source, new CSharpParseOptions(LanguageVersion.Preview))],
			References,
			new CSharpCompilationOptions(
				OutputKind.DynamicallyLinkedLibrary,
				deterministic: true,
				allowUnsafe: true,
				nullableContextOptions: nullable ? NullableContextOptions.Enable : NullableContextOptions.Disable
			)
		);
		var stream = new MemoryStream();
		var result = compilation.Emit(stream);
		if (!result.Success)
			throw new InvalidOperationException(
				"does not compile:" + Environment.NewLine + string.Join(
					Environment.NewLine,
					result.Diagnostics.Where(d => d.Severity == DiagnosticSeverity.Error)
				)
			);
		stream.Position = 0;
		return stream;
	}

	/// <summary>The <c>.api</c> text of the assembly <paramref name="source"/> compiles to.</summary>
	public static string Api(string source, bool nullable = true)
	{
		using var stream = Compile(source, nullable: nullable);
		return ApiWriter.Write(ApiReader.Read(stream));
	}
}
