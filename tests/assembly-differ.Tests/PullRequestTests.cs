using System.Net;
using AwesomeAssertions;
using Nullean.Snapshots;

namespace Differ.Tests;

/// <summary>
/// The pull-request command against <see cref="FakeGitHub"/>, with reports the tool writes for the scenarios. One at a
/// time: two listeners starting together can be handed the same free port.
/// </summary>
[NotInParallel(nameof(PullRequestTests))]
public class PullRequestTests
{
	private static async Task<string> ReportFor(Fixture fixture, string name)
	{
		var output = Directory.CreateDirectory(Path.Combine(fixture.Root, name)).FullName;
		var result = await Cli.RunAsync("files", fixture.OldAssembly, fixture.NewAssembly, "-f", "json", "-o", output);
		result.ExitCode.Should().Be(0, result.Error);
		return Path.Combine(output, "api-changes.json");
	}

	private static async Task<(CliResult Result, string Summary)> PullRequest(
		FakeGitHub github,
		Fixture fixture,
		string report,
		bool onPullRequest = true,
		params string[] args
	)
	{
		var eventPath = Path.Combine(fixture.Root, "event.json");
		await File.WriteAllTextAsync(eventPath, """{ "pull_request": { "number": 7 } }""");
		var summary = Path.Combine(fixture.Root, "summary.md");
		var environment = new Dictionary<string, string>
		{
			["GITHUB_API_URL"] = github.Url,
			["GITHUB_TOKEN"] = "token",
			["GITHUB_REPOSITORY"] = onPullRequest ? "owner/repo" : "",
			["GITHUB_EVENT_PATH"] = onPullRequest ? eventPath : "",
			["GITHUB_STEP_SUMMARY"] = summary,
		};
		var result = await Cli.RunInAsync(fixture.Root, environment, ["pull-request", report, .. args]);
		return (result, File.Exists(summary) ? await File.ReadAllTextAsync(summary) : "");
	}

	[Test]
	public async Task Comments_and_labels_a_breaking_change()
	{
		using var fixture = Scenarios.Create("method-removed");
		using var github = new FakeGitHub();

		var (result, summary) = await PullRequest(github, fixture, await ReportFor(fixture, "report"));

		result.ExitCode.Should().Be(0, result.Error);
		github.Labels.Should().Equal(["breaking"], result.Out + result.Error + string.Join("\n", github.Requests));
		github.Comments.Should().ContainSingle();
		github.Comments[0].Body.Should().MatchSnapshot();
		summary.Should().Contain(github.Comments[0].Body);
	}

	[Test]
	public async Task Updates_its_comment_and_swaps_only_its_own_label()
	{
		using var fixture = Scenarios.Create("method-removed");
		using var github = new FakeGitHub();
		github.Labels.Add("documentation");
		var breaking = await ReportFor(fixture, "breaking");

		await PullRequest(github, fixture, breaking);
		github.Labels.Should().Equal("documentation", "breaking");

		// the next push only adds API
		using var additions = Scenarios.Create("method-added");
		File.Copy(additions.OldAssembly, fixture.OldAssembly, overwrite: true);
		File.Copy(additions.NewAssembly, fixture.NewAssembly, overwrite: true);
		var (result, _) = await PullRequest(github, fixture, await ReportFor(fixture, "additions"));

		result.ExitCode.Should().Be(0, result.Error);
		github.Comments.Should().ContainSingle().Which.Body.Should().Contain("suggested version change **Minor**");
		github.Labels.Should().Equal("documentation", "feature");
	}

	[Test]
	public async Task Takes_off_its_label_when_nothing_changes_any_more()
	{
		using var fixture = Scenarios.Create("method-removed");
		using var github = new FakeGitHub();
		await PullRequest(github, fixture, await ReportFor(fixture, "breaking"));

		File.Copy(fixture.OldAssembly, fixture.NewAssembly, overwrite: true);
		await PullRequest(github, fixture, await ReportFor(fixture, "unchanged"));

		github.Labels.Should().BeEmpty();
		github.Comments.Should().ContainSingle().Which.Body.Should().Contain("suggested version change **None**");
	}

	[Test]
	public async Task Never_removes_a_label_a_person_added()
	{
		using var fixture = Scenarios.Create("unchanged");
		using var github = new FakeGitHub();
		github.Labels.Add("breaking");

		await PullRequest(github, fixture, await ReportFor(fixture, "unchanged"));

		github.Labels.Should().Equal("breaking");
	}

	[Test]
	public async Task Writes_the_summary_and_succeeds_without_write_access()
	{
		using var fixture = Scenarios.Create("method-removed");
		using var github = new FakeGitHub { RefuseWrites = HttpStatusCode.Forbidden };

		var (result, summary) = await PullRequest(github, fixture, await ReportFor(fixture, "report"));

		result.ExitCode.Should().Be(0);
		result.Error.Should().Contain("warning: the token cannot write to pull request #7 (403)");
		github.Comments.Should().BeEmpty();
		summary.Should().Contain("suggested version change **Major**");
	}

	[Test]
	public async Task Fails_with_a_message_when_GitHub_cannot_be_reached()
	{
		using var fixture = Scenarios.Create("method-removed");
		var report = await ReportFor(fixture, "report");
		var environment = new Dictionary<string, string>
		{
			["GITHUB_API_URL"] = "http://127.0.0.1:9/",
			["GITHUB_TOKEN"] = "token",
			// on a CI runner this is the real job summary
			["GITHUB_STEP_SUMMARY"] = "",
		};

		var result = await Cli.RunInAsync(fixture.Root, environment, "pull-request", report, "--repository", "owner/repo", "--pull-request", "7");

		result.ExitCode.Should().Be(1);
		result.Error.Should().StartWith("[diff] could not update pull request #7 in owner/repo:").And.NotContain("   at ");
	}

	[Test]
	public async Task Outside_a_pull_request_writes_the_summary_only()
	{
		using var fixture = Scenarios.Create("method-removed");
		using var github = new FakeGitHub();

		var (result, summary) = await PullRequest(github, fixture, await ReportFor(fixture, "report"), onPullRequest: false);

		result.ExitCode.Should().Be(0, result.Error);
		result.Out.Should().Contain("Not a pull request: wrote the job summary only");
		github.Requests.Should().BeEmpty();
		summary.Should().Contain("suggested version change **Major**");
	}

	[Test]
	public async Task A_first_release_gets_a_comment_and_no_label()
	{
		using var fixture = Scenarios.Create("unchanged");
		using var github = new FakeGitHub();
		var report = Path.Combine(fixture.Root, "first.json");
		await File.WriteAllTextAsync(report, """{ "firstRelease": true, "suggestedVersionChange": "None", "breakingChanges": 0, "assemblies": [] }""");

		await PullRequest(github, fixture, report);

		github.Labels.Should().BeEmpty();
		github.Comments.Should().ContainSingle().Which.Body.Should().Contain("### API changes: first release");
	}

	[Test]
	public async Task No_comment_and_no_labels_leave_the_pull_request_alone()
	{
		using var fixture = Scenarios.Create("method-removed");
		using var github = new FakeGitHub();

		var (result, _) = await PullRequest(github, fixture, await ReportFor(fixture, "report"), true, "--no-comment", "--no-labels");

		result.ExitCode.Should().Be(0, result.Error);
		github.Comments.Should().BeEmpty();
		github.Labels.Should().BeEmpty();
	}
}
