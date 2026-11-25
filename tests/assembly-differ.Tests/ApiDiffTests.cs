using AwesomeAssertions;
using Nullean.Snapshots;

namespace Differ.Tests;

/// <summary>The api-diff command: two directories of committed .api files, compared without building anything.</summary>
public class ApiDiffTests
{
	private static async Task<(string Old, string New)> Write(Fixture fixture)
	{
		var before = Path.Combine(fixture.Root, "base-api");
		var after = Path.Combine(fixture.Root, "head-api");
		(await Cli.RunAsync("api", fixture.OldDirectory, "-o", before)).ExitCode.Should().Be(0);
		(await Cli.RunAsync("api", fixture.NewDirectory, "-o", after)).ExitCode.Should().Be(0);
		return (before, after);
	}

	[Test]
	public async Task Says_what_a_removed_method_breaks()
	{
		using var fixture = Scenarios.Create("method-removed");
		var (before, after) = await Write(fixture);

		var result = await Cli.RunAsync("api-diff", before, after);

		result.ExitCode.Should().Be(0, result.Error);
		Report.Of(result, fixture).Should().MatchSnapshot();
	}

	[Test]
	public async Task Lists_the_apis_without_changes_too()
	{
		using var fixture = Scenarios.Create("unchanged");
		var (before, after) = await Write(fixture);

		var result = await Cli.RunAsync("api-diff", before, after);

		result.ExitCode.Should().Be(0, result.Error);
		result.Out.Should().Contain("1 API compared, 1 without changes").And.Contain("Suggested version change: None");
	}

	[Test]
	public async Task Writes_the_report_the_pull_request_comment_reads()
	{
		using var fixture = Scenarios.Create("method-removed");
		var (before, after) = await Write(fixture);
		var report = Path.Combine(fixture.Root, "delta.json");

		var result = await Cli.RunAsync("api-diff", before, after, "--format", "json", "--output", report);

		result.ExitCode.Should().Be(0, result.Error);
		File.ReadAllText(report).Replace("\r\n", "\n", StringComparison.Ordinal).Should().MatchSnapshot();
	}

	[Test]
	public async Task An_api_only_one_side_has_is_added_or_removed()
	{
		using var fixture = Scenarios.Create("unchanged");
		var (before, after) = await Write(fixture);
		File.Move(Path.Combine(after, "Lib.api"), Path.Combine(after, "Other.api"));

		var result = await Cli.RunAsync("api-diff", before, after);

		result.ExitCode.Should().Be(0, result.Error);
		result.Out.Should().Contain("Lib: only in").And.Contain("so removed").And.Contain("Other: only in").And.Contain("so added");
		result.Out.Should().Contain("Suggested version change: Major");
	}

	[Test]
	public async Task A_missing_directory_has_no_apis_so_everything_is_added()
	{
		using var fixture = Scenarios.Create("unchanged");
		var (_, after) = await Write(fixture);

		var result = await Cli.RunAsync("api-diff", Path.Combine(fixture.Root, "no-such-directory"), after);

		result.ExitCode.Should().Be(0, result.Error);
		result.Out.Should().Contain("Lib: only in").And.Contain("so added").And.Contain("Suggested version change: Minor");
	}

	[Test]
	public async Task Json_needs_an_output_file()
	{
		using var fixture = Scenarios.Create("unchanged");
		var (before, after) = await Write(fixture);

		var result = await Cli.RunAsync("api-diff", before, after, "--format", "json");

		result.ExitCode.Should().Be(1);
		result.Error.Should().Contain("pass --output");
	}
}
