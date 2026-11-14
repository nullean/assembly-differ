using BenchmarkDotNet.Attributes;
using BenchmarkDotNet.Running;
using Nullean.PublicApi;
using Nullean.PublicApi.Diff;

BenchmarkSwitcher.FromAssembly(typeof(PublicApiBenchmarks).Assembly).Run(args);

/// <summary>
/// Reading an assembly's API from metadata, writing it as a .api file, parsing the file, and comparing two APIs, for
/// a framework assembly of each size. Run with: dotnet run -c Release --project benchmarks/Nullean.PublicApi.Benchmarks
/// </summary>
[MemoryDiagnoser]
public class PublicApiBenchmarks
{
	private byte[] _image = [];
	private ApiAssembly _api = null!;
	private string _text = "";
	private ApiAssembly _parsed = null!;

	[Params("System.Collections.Immutable", "System.Private.CoreLib")]
	public string Assembly { get; set; } = "";

	[GlobalSetup]
	public void Setup()
	{
		var path = ((string)AppContext.GetData("TRUSTED_PLATFORM_ASSEMBLIES")!).Split(Path.PathSeparator).First(
			p => Path.GetFileNameWithoutExtension(p) == Assembly
		);
		_image = File.ReadAllBytes(path);
		_api = ApiReader.Read(new MemoryStream(_image));
		_text = ApiWriter.Write(_api);
		_parsed = ApiParser.Parse(_text).Assembly;
	}

	[Benchmark]
	public ApiAssembly Read() => ApiReader.Read(new MemoryStream(_image));

	[Benchmark]
	public string Write() => ApiWriter.Write(_api);

	[Benchmark]
	public ApiDocument Parse() => ApiParser.Parse(_text);

	[Benchmark]
	public ApiComparisonResult Compare() => ApiComparison.Compare(_api, _parsed);
}
