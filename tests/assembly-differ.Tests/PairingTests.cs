using AwesomeAssertions;
using Nullean.Snapshots;

namespace Differ.Tests;

/// <summary>How the assemblies on the two sides are paired, and what happens to one only a single side has.</summary>
public class PairingTests
{
	[Test]
	public async Task A_single_assembly_on_each_side_is_compared_whatever_its_name()
	{
		using var fixture = Scenarios.Create("method-removed");
		var copy = Path.Combine(fixture.Root, "Lib - Copy.dll");
		File.Copy(fixture.OldAssembly, copy);

		var result = await Cli.RunAsync("diff", $"assembly|{copy}", $"assembly|{fixture.NewAssembly}");

		Report.Of(result, fixture).Should().MatchSnapshot();
	}

	public static IEnumerable<(string, string)> Unpaired() =>
		[("added-untargeted", "new"), ("added-targeted", "new -t Lib -t Extra"), ("removed-untargeted", "old"), ("removed-targeted", "old -t Lib -t Extra"),];

	[Test]
	[MethodDataSource(nameof(Unpaired))]
	public async Task An_assembly_only_one_side_has_is_reported(string name, string setup)
	{
		using var fixture = Scenarios.Create("unchanged");
		var parts = setup.Split(' ');
		var side = parts[0] == "new" ? fixture.NewDirectory : fixture.OldDirectory;
		Fixture.Compile("namespace Extra; public class Thing { }", Path.Combine(side, "Extra.dll"));

		var result = await Cli.RunAsync(["diff", $"directory|{fixture.OldDirectory}", $"directory|{fixture.NewDirectory}", .. parts.Skip(1)]);

		Report.Of(result, fixture).Should().MatchSnapshot(name: name);
	}
}
