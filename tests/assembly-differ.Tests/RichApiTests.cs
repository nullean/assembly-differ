using AwesomeAssertions;
using Nullean.Snapshots;

namespace Differ.Tests;

public class RichApiTests
{
	public static IEnumerable<int> Seeds() => Enumerable.Range(1, 400);

	[Test]
	[MethodDataSource(nameof(Seeds))]
	public void Every_seed_compiles(int seed)
	{
		var (oldSource, newSource) = RichApi.Pair(seed);
		var directory = Path.Combine(Path.GetTempPath(), "assembly-differ-tests", Guid.NewGuid().ToString("N"));
		Directory.CreateDirectory(directory);
		try
		{
			var compile = () => Fixture.Compile(oldSource, Path.Combine(directory, "old.dll"));
			compile.Should().NotThrow(oldSource);
			compile = () => Fixture.Compile(newSource, Path.Combine(directory, "new.dll"));
			compile.Should().NotThrow(newSource);
		}
		finally
		{
			Directory.Delete(directory, true);
		}
	}
}

/// <summary>
/// The corpus: what <c>diff</c> reports for a fixed set of generated old and new versions, in every format. The
/// snapshots were written by the JustAssembly engine; any other engine has to reproduce them.
/// </summary>
public class CorpusTests
{
	public static IEnumerable<int> Seeds() => Enumerable.Range(1, 100);

	[Test]
	[MethodDataSource(nameof(Seeds))]
	public async Task Reports(int seed)
	{
		var (oldSource, newSource) = RichApi.Pair(seed);
		using var fixture = Fixture.Create(oldSource, newSource);
		var report = new System.Text.StringBuilder();
		foreach (var format in new[] { "xml", "markdown", "asciidoc", "github-comment" })
		{
			var result = await Cli.RunAsync("diff", $"assembly|{fixture.OldAssembly}", $"assembly|{fixture.NewAssembly}", "-f", format);
			report.AppendLine($"=== {format}");
			report.AppendLine(Report.Of(result, fixture));
		}
		report.ToString().Should().MatchSnapshot(name: $"seed-{seed:000}");
	}
}
