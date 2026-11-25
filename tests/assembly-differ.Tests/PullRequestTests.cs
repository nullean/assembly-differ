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
	public async Task Fails_a_breaking_change_until_the_allow_label_is_added()
	{
		using var fixture = Scenarios.Create("method-removed");
		using var github = new FakeGitHub();
		var report = await ReportFor(fixture, "report");

		var (blocked, _) = await PullRequest(github, fixture, report, true, "--fail-on", "major");

		blocked.ExitCode.Should().Be(4);
		blocked.Error.Should().Contain("add the breaking-allowed label to the pull request to allow it");
		github.Labels.Should().Equal("breaking");
		github.Comments.Should().ContainSingle().Which.Body.Should().Contain("add the `breaking-allowed` label to allow it");

		// the author agrees: adding the label runs the workflow again
		github.Labels.Add("breaking-allowed");
		var (allowed, _) = await PullRequest(github, fixture, report, true, "--fail-on", "major");

		allowed.ExitCode.Should().Be(0, allowed.Error);
		allowed.Out.Should().Contain("The Major change is allowed by the breaking-allowed label");
		github.Labels.Should().Equal("breaking", "breaking-allowed");
		github.Comments.Should().ContainSingle().Which.Body.Should().Contain("The `breaking-allowed` label allows this change.");
	}

	[Test]
	public async Task A_change_below_fail_on_passes()
	{
		using var fixture = Scenarios.Create("method-added");
		using var github = new FakeGitHub();

		var (result, _) = await PullRequest(github, fixture, await ReportFor(fixture, "report"), true, "--fail-on", "major");

		result.ExitCode.Should().Be(0, result.Error);
		github.Labels.Should().Equal("feature");
	}

	[Test]
	public async Task The_allow_label_can_be_renamed()
	{
		using var fixture = Scenarios.Create("method-removed");
		using var github = new FakeGitHub();
		github.Labels.Add("api-break-ok");

		var (result, _) = await PullRequest(github, fixture, await ReportFor(fixture, "report"), true, "--fail-on", "major", "--allow-label", "api-break-ok");

		result.ExitCode.Should().Be(0, result.Error);
	}

	[Test]
	public async Task Outside_a_pull_request_fail_on_still_applies()
	{
		using var fixture = Scenarios.Create("method-removed");
		using var github = new FakeGitHub();

		var (result, _) = await PullRequest(github, fixture, await ReportFor(fixture, "report"), false, "--fail-on", "major");

		result.ExitCode.Should().Be(4);
		result.Error.Should().Contain("The Major change is at least --fail-on major").And.NotContain("add the");
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
		result.Error.Should().StartWith("[diff] could not read pull request #7 in owner/repo:").And.NotContain("   at ");
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

	[Test]
	public async Task Says_all_is_good_and_lists_what_it_compared_when_nothing_changed()
	{
		using var fixture = Scenarios.Create("unchanged");
		using var github = new FakeGitHub();

		var (result, _) = await PullRequest(github, fixture, await ReportFor(fixture, "report"));

		result.ExitCode.Should().Be(0, result.Error);
		github.Comments.Should().ContainSingle().Which.Body.Should().MatchSnapshot();
	}

	[Test]
	public async Task Lists_the_assemblies_without_changes_next_to_the_ones_with()
	{
		using var fixture = Scenarios.Create("unchanged");
		using var github = new FakeGitHub();
		var report = Path.Combine(fixture.Root, "two.json");
		await File.WriteAllTextAsync(
			report,
			"""
			{
			  "firstRelease": false,
			  "baseline": "1.1.0",
			  "suggestedVersionChange": "Minor",
			  "breakingChanges": 0,
			  "assemblies": [
			    { "name": "Lib.Extras.dll", "suggestedVersionChange": "Minor", "breakingChanges": 0,
			      "changes": [ { "kind": "Declaration", "name": "type Extras", "change": "added", "suggestedVersionChange": "Minor" } ] },
			    { "name": "Lib.dll", "suggestedVersionChange": "None", "breakingChanges": 0, "changes": [] }
			  ],
			  "removedAssemblies": [],
			  "addedAssemblies": []
			}
			"""
		);

		await PullRequest(github, fixture, report);

		github.Comments.Should().ContainSingle().Which.Body.Should().MatchSnapshot();
	}

	[Test]
	public async Task Reports_committed_API_files_that_match_in_the_same_comment()
	{
		using var fixture = Scenarios.Create("unchanged");
		using var github = new FakeGitHub();
		var apiReport = Path.Combine(fixture.Root, "api.txt");
		await File.WriteAllTextAsync(apiReport, "[api] api matches the build: 6 files\n");

		var (result, summary) = await PullRequest(github, fixture, await ReportFor(fixture, "report"), true, "--api-code", "0", "--api-report", apiReport);

		result.ExitCode.Should().Be(0, result.Error);
		github.Comments.Should().ContainSingle().Which.Body.Should().MatchSnapshot();
		summary.Should().Contain("The committed API files match the build (6 files)");
	}

	[Test]
	public async Task Fails_with_5_and_says_how_to_update_committed_API_files_that_differ()
	{
		using var fixture = Scenarios.Create("unchanged");
		using var github = new FakeGitHub();
		var apiReport = Path.Combine(fixture.Root, "api.txt");
		await File.WriteAllTextAsync(
			apiReport,
			"""
			[api] Lib: 1 change since the committed files, suggested version change: Minor
			  api/Lib.api:12  Lib › Widget › Resize(double)  added void Resize(double factor)  [minor]
			[api] api does not match the build. Suggested version change: Minor
			[api] Update the files with: assembly-differ api -o api
			"""
		);

		var (result, _) = await PullRequest(github, fixture, await ReportFor(fixture, "report"), true, "--api-code", "5", "--api-report", apiReport);

		result.ExitCode.Should().Be(5, result.Error);
		result.Error.Should().Contain("do not match the build");
		github.Comments.Should().ContainSingle().Which.Body.Should().MatchSnapshot();
	}

	[Test]
	public async Task An_API_files_check_that_could_not_run_fails_with_its_own_exit_code()
	{
		using var fixture = Scenarios.Create("unchanged");
		using var github = new FakeGitHub();

		var (result, _) = await PullRequest(github, fixture, await ReportFor(fixture, "report"), true, "--api-code", "1");

		result.ExitCode.Should().Be(1, result.Error);
		github.Comments.Should().ContainSingle().Which.Body.Should().Contain("could not be checked (exit code 1)");
	}
}
