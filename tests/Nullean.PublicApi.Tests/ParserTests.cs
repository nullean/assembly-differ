using AwesomeAssertions;

namespace Nullean.PublicApi.Tests;

/// <summary>Parsing a <c>.api</c> file and writing it again gives the same file, for samples and framework assemblies.</summary>
public class ParserTests
{
	[Test]
	[MethodDataSource(typeof(WriterTests), nameof(WriterTests.Cases))]
	public void Round_trips_a_case(string name, string source)
	{
		using var stream = Compiler.Compile(source);
		RoundTrips(ApiWriter.Write(ApiReader.Read(stream)));
	}

	[Test]
	[MethodDataSource(typeof(RoslynOracleTests), nameof(RoslynOracleTests.FrameworkAssemblies))]
	public void Round_trips_a_framework_assembly(string name) => RoundTrips(ApiWriter.Write(ApiReader.Read(RoslynOracleTests.FrameworkPath(name))));

	[Test]
	[MethodDataSource(typeof(WriterTests), nameof(WriterTests.Cases))]
	public void Parses_back_the_model_of_a_case(string name, string source)
	{
		using var stream = Compiler.Compile(source);
		SameModel(ApiReader.Read(stream));
	}

	[Test]
	[MethodDataSource(typeof(RoslynOracleTests), nameof(RoslynOracleTests.FrameworkAssemblies))]
	public void Parses_back_the_model_of_a_framework_assembly(string name) => SameModel(ApiReader.Read(RoslynOracleTests.FrameworkPath(name)));

	[Test]
	[Arguments("System.Private.CoreLib", ApiSplit.Namespace)]
	[Arguments("System.Private.CoreLib", ApiSplit.TopLevelNamespace)]
	[Arguments("System.Text.Json", ApiSplit.Namespace)]
	public void Parses_back_a_split_api(string name, ApiSplit split)
	{
		var read = ApiReader.Read(RoslynOracleTests.FrameworkPath(name));
		var files = ApiWriter.Write(read, split);
		files.Count.Should().BeGreaterThan(1);
		var parsed = ApiParser.Parse(files.Select(f => ((string?)f.Name, f.Text))).Assembly;
		var differences = ModelDump
			.Lines(read)
			.Zip(ModelDump.Lines(parsed))
			.Where(p => p.First != p.Second)
			.Take(5)
			.Select(p => $"read:   {p.First}\nparsed: {p.Second}");
		differences.Should().BeEmpty();
		ModelDump.Lines(parsed).Count.Should().Be(ModelDump.Lines(read).Count);
	}

	[Test]
	public void Knows_the_file_of_each_member_of_a_split_api()
	{
		var document = ApiParser.Parse([
			("a.api", "assembly Lib\nusing Lib.B: Other\n\nnamespace Lib.A\n  public class Widget\n    public Other Make()\n"),
			("b.api", "assembly Lib\n\nnamespace Lib.B\n  public class Other\n"),
		]);
		var widget = document.Assembly.Types.Single(t => t.Name == "Widget");
		document.LocationOf(widget.Members[0]).Should().Be(new ApiLocation("a.api", 6));
		document.LocationOf(document.Assembly.Types.Single(t => t.Name == "Other")).Should().Be(new ApiLocation("b.api", 4));
	}

	/// <summary>A delegate's Invoke attributes are not written; everything else the reader knows comes back.</summary>
	private static void SameModel(ApiAssembly read)
	{
		var parsed = ApiParser.Parse(ApiWriter.Write(read)).Assembly;
		var expected = ModelDump.Lines(read);
		var actual = ModelDump.Lines(parsed);
		var differences = expected.Zip(actual).Where(p => p.First != p.Second).Take(5).Select(p => $"read:   {p.First}\nparsed: {p.Second}").ToList();
		differences.Should().BeEmpty();
		actual.Count.Should().Be(expected.Count);
	}

	private static void RoundTrips(string text)
	{
		var again = ApiWriter.Write(ApiParser.Parse(text).Assembly);
		if (again == text)
			return;
		var expected = text.Split('\n');
		var actual = again.Split('\n');
		var line = Enumerable.Range(0, Math.Min(expected.Length, actual.Length)).FirstOrDefault(
			i => expected[i] != actual[i],
			Math.Min(expected.Length, actual.Length)
		);
		var context = string.Join("\n", expected.Skip(Math.Max(0, line - 3)).Take(4));
		$"line {line + 1}: {(line < actual.Length ? actual[line] : "<end>")}".Should().Be(
			$"line {line + 1}: {(line < expected.Length ? expected[line] : "<end>")}",
			context
		);
	}

	[Test]
	public void Reports_where_a_file_is_wrong()
	{
		var act = () => ApiParser.Parse("assembly Lib\n\nnamespace Lib\n  public class Widget\n    public Missing Make()\n");
		act.Should().Throw<ApiParseException>().Which.Line.Should().Be(5);
	}

	[Test]
	public void Knows_the_line_of_each_member()
	{
		var document = ApiParser.Parse("assembly Lib\n\nnamespace Lib\n  public class Widget\n    public Widget()\n    public int Size { get; }\n");
		var widget = document.Assembly.Types.Single();
		document.LineOf(widget).Should().Be(4);
		document.LineOf(widget.Members[1]).Should().Be(6);
	}
}
