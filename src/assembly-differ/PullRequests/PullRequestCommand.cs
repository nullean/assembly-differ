using System;
using System.Collections.Generic;
using System.IO;
using System.Linq;
using System.Net;
using System.Text.Json;
using System.Threading.Tasks;
using Nullean.Argh;

namespace Differ.PullRequests;

internal sealed class PullRequestCommand
{
	/// <summary>
	/// Puts a report on the pull request a GitHub Actions run is for: the job summary, one comment kept up to date, and
	/// the release label for the suggested version change.
	/// </summary>
	/// <remarks>
	/// Run a command with --format json first; this reads the api-changes.json it writes. The comment is found again by a
	/// hidden marker and updated, and the labels this tool set are recorded in it: a later run swaps only those, never a
	/// label a person added. It needs GITHUB_TOKEN with pull-requests: write. Without write access, as on a pull request
	/// from a fork, it writes the job summary, warns and succeeds; outside a pull request it writes the job summary only.
	/// </remarks>
	/// <param name="report">The api-changes.json a command wrote with --format json.</param>
	/// <param name="repository">The repository as owner/name; defaults to GITHUB_REPOSITORY.</param>
	/// <param name="pullRequest">The pull request number; defaults to the one in GITHUB_EVENT_PATH.</param>
	/// <param name="majorLabel">The label for a Major change, matching release-drafter's; empty sets none.</param>
	/// <param name="minorLabel">The label for a Minor change; empty sets none.</param>
	/// <param name="patchLabel">The label for a Patch change; empty sets none.</param>
	/// <param name="noComment">Leave the comment alone.</param>
	/// <param name="noLabels">Leave the labels alone.</param>
	/// <example>assembly-differ release build/output/MyLib.1.3.0.nupkg -f json -o build/output</example>
	/// <example>assembly-differ pull-request build/output/api-changes.json</example>
	[CommandName("pull-request")]
	public async Task<int> PullRequest(
		[Argument] string report,
		string? repository = null,
		int? pullRequest = null,
		string majorLabel = "breaking",
		string minorLabel = "feature",
		string patchLabel = "",
		bool noComment = false,
		bool noLabels = false
	)
	{
		var comment = !noComment;
		var labels = !noLabels;
		if (!File.Exists(report))
		{
			Console.Error.WriteLine($"[diff] no report at {report}; write one with --format json");
			return 1;
		}
		using var document = JsonDocument.Parse(await File.ReadAllTextAsync(report));
		var root = document.RootElement;

		var suggested = root.GetProperty("suggestedVersionChange").GetString();
		var label = suggested switch
		{
			"Major" => majorLabel,
			"Minor" => minorLabel,
			"Patch" => patchLabel,
			_ => "",
		};
		string[] wanted = labels && label.Length > 0 ? [label] : [];
		var body = Comment.Render(root, wanted);

		var summary = Env("GITHUB_STEP_SUMMARY");
		if (!string.IsNullOrEmpty(summary))
			await File.AppendAllTextAsync(summary, body + Environment.NewLine);

		repository ??= Env("GITHUB_REPOSITORY");
		pullRequest ??= PullRequestFromEvent();
		if (repository is null || pullRequest is null)
		{
			Console.WriteLine("[diff] Not a pull request: wrote the job summary only");
			return 0;
		}

		var token = Env("GITHUB_TOKEN") ?? Env("GH_TOKEN");
		if (string.IsNullOrEmpty(token))
		{
			Console.Error.WriteLine("[diff] warning: GITHUB_TOKEN is not set, so the pull request is left as it is");
			return 0;
		}

		var api = new Uri(Env("GITHUB_API_URL") ?? "https://api.github.com");
		using var github = new GitHubApi(api, token, repository, pullRequest.Value);
		try
		{
			var existing = await github.FindComment(Comment.Marker);
			if (labels)
				await SetLabels(github, existing is { } e ? Comment.LabelsIn(e.Body) : [], wanted);
			if (comment)
			{
				if (existing is { } found)
					await github.UpdateComment(found.Id, body);
				else
					await github.CreateComment(body);
			}
			Console.WriteLine($"[diff] Updated pull request #{pullRequest} in {repository}: {suggested}{(wanted.Length > 0 ? $", labelled {wanted[0]}" : "")}");
			return 0;
		}
		catch (GitHubApiException e) when (e.Status is HttpStatusCode.Forbidden or HttpStatusCode.NotFound or HttpStatusCode.Unauthorized)
		{
			// a pull request from a fork gets a read-only token
			Console.Error.WriteLine(
				$"[diff] warning: the token cannot write to pull request #{pullRequest} ({(int)e.Status}), as on a pull request from a fork; wrote the job summary only"
			);
			return 0;
		}
		catch (Exception e) when (e is GitHubApiException or System.Net.Http.HttpRequestException)
		{
			Console.Error.WriteLine($"[diff] could not update pull request #{pullRequest} in {repository}: {e.Message}");
			return 1;
		}
	}

	/// <summary>Removes the labels this tool set before and no longer wants, and adds the ones it wants.</summary>
	private static async Task SetLabels(GitHubApi github, IReadOnlyCollection<string> setBefore, IReadOnlyCollection<string> wanted)
	{
		var present = await github.Labels();
		foreach (var stale in setBefore.Except(wanted, StringComparer.OrdinalIgnoreCase))
			if (present.Contains(stale, StringComparer.OrdinalIgnoreCase))
				await github.RemoveLabel(stale);
		foreach (var label in wanted)
			if (!present.Contains(label, StringComparer.OrdinalIgnoreCase))
				await github.AddLabel(label);
	}

	/// <summary>An environment variable, with an empty one read as unset.</summary>
	private static string? Env(string name) => Environment.GetEnvironmentVariable(name) is { Length: > 0 } value ? value : null;

	private static int? PullRequestFromEvent()
	{
		var path = Env("GITHUB_EVENT_PATH");
		if (string.IsNullOrEmpty(path) || !File.Exists(path))
			return null;
		using var document = JsonDocument.Parse(File.ReadAllText(path));
		return document.RootElement.TryGetProperty("pull_request", out var pr) && pr.TryGetProperty("number", out var number) ? number.GetInt32() : null;
	}
}
