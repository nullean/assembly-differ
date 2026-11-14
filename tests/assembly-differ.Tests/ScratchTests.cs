using AwesomeAssertions;

namespace Differ.Tests;

/// <summary>The run's scratch folder, under a temporary folder of the test's own so other runs do not interfere.</summary>
public class ScratchTests
{
	private static async Task<(CliResult Result, string[] Left)> Run(Fixture fixture, bool keep)
	{
		var temp = Directory.CreateDirectory(Path.Combine(fixture.Root, "tmp")).FullName;
		var environment = new Dictionary<string, string> { ["TMPDIR"] = temp + "/", ["TMP"] = temp, ["TEMP"] = temp };
		if (keep)
			environment["ASSEMBLY_DIFFER_KEEP_SCRATCH"] = "1";
		var package = CommandsTests.Package(fixture, "new.nupkg", "1.0.0", fixture.NewAssembly, "net11.0");
		var result = await Cli.RunInAsync(fixture.Root, environment, "files", fixture.OldAssembly, package);
		var scratch = Path.Combine(temp, "assembly-differ");
		return (result, Directory.Exists(scratch) ? Directory.GetDirectories(scratch) : []);
	}

	[Test]
	public async Task Is_deleted_when_the_run_ends()
	{
		using var fixture = Scenarios.Create("method-removed");

		var (result, left) = await Run(fixture, keep: false);

		result.ExitCode.Should().Be(0, result.Error);
		result.Out.Should().Contain("Draw()");
		left.Should().BeEmpty();
	}

	[Test]
	public async Task Is_kept_on_request()
	{
		using var fixture = Scenarios.Create("method-removed");

		var (result, left) = await Run(fixture, keep: true);

		left.Should().ContainSingle();
		result.Error.Should().Contain("[diff] kept the scratch folder");
		Directory.EnumerateFiles(left[0], "Lib.dll", SearchOption.AllDirectories).Should().NotBeEmpty();
	}
}
