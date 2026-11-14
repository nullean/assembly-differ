using AwesomeAssertions;
using CsCheck;
using Nullean.PublicApi.Diff;

namespace Nullean.PublicApi.Tests;

/// <summary>
/// Properties over generated libraries (<see cref="GeneratedLibrary"/>): each compiles, and its API is read, written,
/// parsed and compared the way the framework assemblies are. CsCheck shrinks a failing library to a small one and prints
/// the seed that reproduces it.
/// </summary>
public class Properties
{
	private static readonly Gen<uint[]> Choices = Gen.UInt.Array[0, 300];

	private static readonly Gen<string> Libraries = Choices.Select(GeneratedLibrary.Source);

	private const int Iterations = 150;

	private static byte[] Compile(string source)
	{
		using var stream = Compiler.Compile(source);
		return stream.ToArray();
	}

	[Test]
	public void The_reader_agrees_with_Roslyn() => Libraries.Sample(source => RoslynOracleTests.Compare(Compile(source)), iter: Iterations);

	[Test]
	public void Writing_and_parsing_gives_the_same_text_and_model() =>
		Libraries.Sample(
			source =>
			{
				var read = ApiReader.Read(new MemoryStream(Compile(source)));
				var text = ApiWriter.Write(read);
				var parsed = ApiParser.Parse(text).Assembly;
				ApiWriter.Write(parsed).Should().Be(text);
				ModelDump.Lines(parsed).Should().Equal(ModelDump.Lines(read));
			},
			iter: Iterations
		);

	[Test]
	public void Split_files_parse_back_to_the_same_model() =>
		Libraries.Sample(
			source =>
			{
				var read = ApiReader.Read(new MemoryStream(Compile(source)));
				foreach (var split in new[] { ApiSplit.Namespace, ApiSplit.TopLevelNamespace })
				{
					var parsed = ApiParser.Parse(ApiWriter.Write(read, split).Select(f => ((string?)f.Name, f.Text))).Assembly;
					ModelDump.Lines(parsed).Should().Equal(ModelDump.Lines(read));
				}
			},
			iter: Iterations
		);

	[Test]
	public void An_api_compared_with_itself_or_its_file_has_no_changes() =>
		Libraries.Sample(
			source =>
			{
				var read = ApiReader.Read(new MemoryStream(Compile(source)));
				ApiComparison.Compare(read, read).Changes.Should().BeEmpty();
				ApiComparison.Compare(read, ApiParser.Parse(ApiWriter.Write(read)).Assembly).Changes.Should().BeEmpty();
			},
			iter: Iterations
		);

	/// <summary>
	/// Comparing two libraries one way and the other gives mirrored changes: an addition one way is a removal the other,
	/// and a change has its old and new sides swapped.
	/// </summary>
	[Test]
	public void Comparing_the_other_way_round_mirrors_the_changes() =>
		Gen.Select(Libraries, Libraries).Sample(
			(first, second) =>
			{
				var a = ApiReader.Read(new MemoryStream(Compile(first)));
				var b = ApiReader.Read(new MemoryStream(Compile(second)));

				static IEnumerable<string> Keys(ApiComparisonResult result, bool swap) =>
					result
						.Changes
						.Select(
							c =>
								(c.Kind, swap) switch
								{
									(ApiChangeKind.Added, true) => $"removed {c.Where}",
									(ApiChangeKind.Removed, true) => $"added {c.Where}",
									(ApiChangeKind.Added, false) => $"added {c.Where}",
									(ApiChangeKind.Removed, false) => $"removed {c.Where}",
									(_, true) => $"changed {c.Where} {c.Part}: {c.New} -> {c.Old}",
									_ => $"changed {c.Where} {c.Part}: {c.Old} -> {c.New}",
								}
						)
						.Order(StringComparer.Ordinal);

				Keys(ApiComparison.Compare(a, b), swap: false).Should().Equal(Keys(ApiComparison.Compare(b, a), swap: true));
			},
			iter: 60
		);

	[Test]
	public void Every_generated_library_compiles() => Libraries.Sample(source => Compile(source), iter: 400);
}
