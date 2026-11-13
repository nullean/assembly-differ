using AwesomeAssertions;

namespace Differ.Tests;

/// <summary>
/// Compares this build with another one, such as the last release, on generated APIs the corpus does not hold: every
/// format must come out the same. It runs only when <c>ASSEMBLY_DIFFER_REFERENCE</c> names the other build's
/// <c>assembly-differ.dll</c>; <c>ASSEMBLY_DIFFER_SEEDS</c> sets how many seeds, after the corpus's, it tries.
/// </summary>
public class ReferenceTests
{
	private static readonly string? Reference = Environment.GetEnvironmentVariable("ASSEMBLY_DIFFER_REFERENCE");

	public static IEnumerable<int> Seeds() =>
		Enumerable.Range(1001, int.TryParse(Environment.GetEnvironmentVariable("ASSEMBLY_DIFFER_SEEDS"), out var count) ? count : 200);

	[Test]
	[MethodDataSource(nameof(Seeds))]
	public async Task Matches_the_reference_build(int seed)
	{
		if (string.IsNullOrEmpty(Reference))
			Skip.Test("set ASSEMBLY_DIFFER_REFERENCE to another build's assembly-differ.dll to compare with it");

		var (oldSource, newSource) = RichApi.Pair(seed);
		using var fixture = Fixture.Create(oldSource, newSource);
		foreach (var format in new[] { "xml", "markdown", "asciidoc", "github-comment" })
		{
			string[] args = ["diff", $"assembly|{fixture.OldAssembly}", $"assembly|{fixture.NewAssembly}", "-f", format];
			var expected = await Cli.RunToolAsync(Reference!, args);
			var actual = await Cli.RunAsync(args);
			actual.Should().Be(expected, $"seed {seed} in {format}:\n--- old\n{oldSource}\n--- new\n{newSource}");
		}
	}
}
