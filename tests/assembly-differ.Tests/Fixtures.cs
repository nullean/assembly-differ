using System.Collections.Immutable;
using Microsoft.CodeAnalysis;
using Microsoft.CodeAnalysis.CSharp;

namespace Differ.Tests;

/// <summary>
/// An old and a new build of the same library, compiled from C# source into <c>old/Lib.dll</c> and <c>new/Lib.dll</c>
/// under a fresh temporary directory. Both sides share an assembly name, so the <c>directory</c> provider pairs them.
/// </summary>
public sealed class Fixture : IDisposable
{
	public const string AssemblyName = "Lib";

	private static readonly ImmutableArray<MetadataReference> References =
	[
		.. ((string)AppContext.GetData("TRUSTED_PLATFORM_ASSEMBLIES")!)
			.Split(Path.PathSeparator)
			.Where(p => Path.GetFileName(p).StartsWith("System.", StringComparison.Ordinal) || Path.GetFileName(p) is "netstandard.dll" or "mscorlib.dll")
			.Select(p => (MetadataReference)MetadataReference.CreateFromFile(p)),
	];

	private Fixture(string root)
	{
		Root = root;
		OldDirectory = Directory.CreateDirectory(Path.Combine(root, "old")).FullName;
		NewDirectory = Directory.CreateDirectory(Path.Combine(root, "new")).FullName;
	}

	public string Root { get; }
	public string OldDirectory { get; }
	public string NewDirectory { get; }
	public string OldAssembly => Path.Combine(OldDirectory, AssemblyName + ".dll");
	public string NewAssembly => Path.Combine(NewDirectory, AssemblyName + ".dll");

	public static Fixture Create(string oldSource, string newSource)
	{
		var root = Path.Combine(Path.GetTempPath(), "assembly-differ-tests", Guid.NewGuid().ToString("N"));
		var fixture = new Fixture(root);
		Compile(oldSource, fixture.OldAssembly);
		Compile(newSource, fixture.NewAssembly);
		return fixture;
	}

	public static void Compile(string source, string path)
	{
		var compilation = CSharpCompilation.Create(
			AssemblyName,
			[CSharpSyntaxTree.ParseText(source, new CSharpParseOptions(LanguageVersion.Latest))],
			References,
			new CSharpCompilationOptions(OutputKind.DynamicallyLinkedLibrary, deterministic: true, nullableContextOptions: NullableContextOptions.Enable)
		);
		using var stream = File.Create(path);
		var result = compilation.Emit(stream);
		if (!result.Success)
		{
			var errors = result.Diagnostics.Where(d => d.Severity == DiagnosticSeverity.Error);
			throw new InvalidOperationException($"fixture does not compile:{Environment.NewLine}{string.Join(Environment.NewLine, errors)}");
		}
	}

	public void Dispose()
	{
		try
		{
			Directory.Delete(Root, true);
		}
		catch (IOException)
		{
			// a process that still holds a file; the temporary directory is cleaned up by the OS eventually
		}
	}
}
