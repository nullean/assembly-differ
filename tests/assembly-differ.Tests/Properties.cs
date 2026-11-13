using System.Text;
using System.Text.RegularExpressions;
using AwesomeAssertions;
using CsCheck;

namespace Differ.Tests;

/// <summary>
/// A small public API: which of a fixed set of types exist, and which of a fixed set of members each one has. Two of
/// these are an old and a new version, so set differences say exactly what was added and what was removed.
/// </summary>
public sealed record Api(IReadOnlyList<(int Type, int Member)> Members, IReadOnlyList<int> Types)
{
	private static readonly string[] MemberTemplates =
	[
		"public void M{0}() {{ }}",
		"public int P{0} {{ get; set; }}",
		"public int F{0}(int x) => x;",
		"public static string S{0}(string s, int n) => s;",
	];

	public const int TypeCount = 3;
	public static readonly int MemberCount = MemberTemplates.Length * 2;

	public static readonly Gen<Api> Gen = from types in CsCheck.Gen.Int[0, TypeCount - 1].HashSet[1, TypeCount]
										  from members in CsCheck.Gen.Select(
		CsCheck.Gen.Int[0, TypeCount - 1],
		CsCheck.Gen.Int[0, MemberCount - 1]
	).HashSet[0, 12]
										  select new Api([.. members.Where(m => types.Contains(m.Item1)).Order()], [.. types.Order()]);

	public string Source()
	{
		var source = new StringBuilder("namespace Lib;\n");
		foreach (var type in Types)
		{
			source.AppendLine($"public class T{type}");
			source.AppendLine("{");
			foreach (var (_, member) in Members.Where(m => m.Type == type))
				source.AppendLine("\t" + string.Format(MemberTemplates[member % MemberTemplates.Length], member));
			source.AppendLine("}");
		}
		return source.ToString();
	}

	public bool RemovesFrom(Api older) => older.Types.Except(Types).Any() || older.Members.Where(m => Types.Contains(m.Type)).Except(Members).Any();

	public override string ToString() => Source();
}

/// <summary>
/// Properties of the <c>diff</c> command over generated APIs. Every case compiles two assemblies and starts the tool,
/// so the iteration counts are kept small; a failure prints the two sources it shrank to.
/// </summary>
public partial class Properties
{
	private const int Iterations = 25;

	private static async Task<CliResult> Diff(Api older, Api newer)
	{
		using var fixture = Fixture.Create(older.Source(), newer.Source());
		return await Cli.RunAsync("diff", $"assembly|{fixture.OldAssembly}", $"assembly|{fixture.NewAssembly}");
	}

	private static string SuggestedChange(CliResult result) =>
		SuggestedChangePattern().Match(result.Out) is { Success: true } match ? match.Groups[1].Value : $"<none> in: {result.Out}{result.Error}";

	private static int Count(CliResult result, string diffType) =>
		Regex.Matches(result.Out, $"<(Type|Method|Property|Field)\\b[^>]*DiffType=\"{diffType}\"").Count;

	[Test]
	public async Task An_api_diffed_with_itself_has_no_changes() =>
		await Api.Gen.SampleAsync(
			async api =>
			{
				var result = await Diff(api, api);
				result.ExitCode.Should().Be(0);
				result.Out.Should().Contain("[diff] No diff between");
				// today an unchanged API suggests Patch; the plan is None
				SuggestedChange(result).Should().Be("Patch");
			},
			iter: Iterations
		);

	[Test]
	public async Task Removing_anything_public_is_major() =>
		await Api
			.Gen
			.Select(Api.Gen)
			.Where(pair => pair.Item2.RemovesFrom(pair.Item1))
			.SampleAsync(
				async pair =>
				{
					var result = await Diff(pair.Item1, pair.Item2);
					SuggestedChange(result).Should().Be("Major");
				},
				iter: Iterations
			);

	[Test]
	public async Task Swapping_old_and_new_turns_additions_into_removals() =>
		await Api
			.Gen
			.Select(Api.Gen)
			.SampleAsync(
				async pair =>
				{
					var forward = await Diff(pair.Item1, pair.Item2);
					var backward = await Diff(pair.Item2, pair.Item1);
					Count(backward, "Deleted").Should().Be(Count(forward, "New"));
					Count(backward, "New").Should().Be(Count(forward, "Deleted"));
				},
				iter: Iterations
			);

	[Test]
	[Skip("Pure additions are suggested as Major today: https://github.com/nullean/assembly-differ/issues/58")]
	public async Task Only_adding_is_minor() =>
		await Api
			.Gen
			.Select(Api.Gen)
			.Where(pair => !pair.Item2.RemovesFrom(pair.Item1) && pair.Item1.Source() != pair.Item2.Source())
			.SampleAsync(
				async pair =>
				{
					var result = await Diff(pair.Item1, pair.Item2);
					SuggestedChange(result).Should().Be("Minor");
				},
				iter: Iterations
			);

	[GeneratedRegex(@"Suggested version change: (\w+)")]
	private static partial Regex SuggestedChangePattern();
}
