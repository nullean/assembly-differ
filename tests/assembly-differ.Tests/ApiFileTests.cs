using AwesomeAssertions;
using Nullean.Snapshots;

namespace Differ.Tests;

/// <summary>The api command: printing and writing .api files, and checking committed ones with --verify.</summary>
public class ApiFileTests
{
	[Test]
	public async Task Prints_the_api_of_an_assembly()
	{
		using var fixture = Scenarios.Create("unchanged");

		var result = await Cli.RunAsync("api", fixture.OldAssembly);

		Report.Of(result, fixture).Should().MatchSnapshot();
	}

	[Test]
	public async Task Prints_the_api_as_json()
	{
		using var fixture = Scenarios.Create("unchanged");

		var result = await Cli.RunAsync("api", fixture.OldAssembly, "-f", "json");

		Report.Of(result, fixture).Should().MatchSnapshot();
	}

	[Test]
	public async Task Writes_files_that_verify()
	{
		using var fixture = Scenarios.Create("unchanged");
		var output = Path.Combine(fixture.Root, "api");

		var written = await Cli.RunAsync("api", fixture.OldDirectory, "-o", output);
		var verified = await Cli.RunAsync("api", fixture.OldDirectory, "--verify", output);

		written.ExitCode.Should().Be(0, written.Error);
		Report.Of(verified, fixture).Should().MatchSnapshot();
		File.ReadAllText(Path.Combine(output, "Lib.api")).Should().StartWith("assembly Lib\n");
	}

	public static IEnumerable<string> Changes() => ["method-removed", "interface-member-added", "parameter-added", "type-added", "required-property-added"];

	[Test]
	[MethodDataSource(nameof(Changes))]
	public async Task Verify_reports_what_changed_since_the_committed_files(string scenario)
	{
		using var fixture = Scenarios.Create(scenario);
		var output = Path.Combine(fixture.Root, "api");
		await Cli.RunAsync("api", fixture.OldDirectory, "-o", output);

		var result = await Cli.RunAsync("api", fixture.NewDirectory, "--verify", output);

		result.ExitCode.Should().Be(5);
		Report.Of(result, fixture).Should().MatchSnapshot(name: scenario);
	}

	[Test]
	public async Task Split_files_verify_and_rewrite()
	{
		using var fixture = Scenarios.Create("method-removed");
		var output = Path.Combine(fixture.Root, "api");

		await Cli.RunAsync("api", fixture.OldDirectory, "-o", output, "--split", "namespace");
		var verified = await Cli.RunAsync("api", fixture.OldDirectory, "--verify", output, "--split", "namespace");
		var stale = await Cli.RunAsync("api", fixture.NewDirectory, "--verify", output, "--split", "namespace");
		var rewritten = await Cli.RunAsync("api", fixture.NewDirectory, "-o", output, "--split", "namespace");

		verified.ExitCode.Should().Be(0, verified.Out);
		stale.ExitCode.Should().Be(5);
		Report.Of(rewritten, fixture).Should().MatchSnapshot();
		(await Cli.RunAsync("api", fixture.NewDirectory, "--verify", output, "--split", "namespace")).ExitCode.Should().Be(0);
	}

	[Test]
	public async Task Verify_fails_when_the_directory_has_no_files()
	{
		using var fixture = Scenarios.Create("unchanged");

		var result = await Cli.RunAsync("api", fixture.OldDirectory, "--verify", Path.Combine(fixture.Root, "missing"));

		Report.Of(result, fixture).Should().MatchSnapshot();
	}

	[Test]
	public async Task Json_is_only_printed()
	{
		using var fixture = Scenarios.Create("unchanged");

		var result = await Cli.RunAsync("api", fixture.OldAssembly, "-f", "json", "-o", fixture.Root);

		Report.Of(result, fixture).Should().MatchSnapshot();
	}
}
