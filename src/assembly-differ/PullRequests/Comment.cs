using System;
using System.Collections.Generic;
using System.Linq;
using System.Text;
using System.Text.Json;

namespace Differ.PullRequests;

/// <summary>Writes the pull request comment and job summary for an <c>api-changes.json</c> report.</summary>
public static class Comment
{
	/// <summary>The hidden line that marks the comment as this tool's, so a later run updates it.</summary>
	public const string Marker = "<!-- assembly-differ -->";

	private const string LabelsMarkerPrefix = "<!-- assembly-differ-labels: ";

	/// <summary>GitHub refuses a comment over 65536 characters; the details stop well before that.</summary>
	private const int MaxLength = 60000;

	/// <summary>
	/// The check of the committed <c>.api</c> files: the exit code of <c>api --verify</c> (0 when they match the build, 5
	/// when they do not) and what it printed.
	/// </summary>
	public sealed record ApiFiles(int Code, string Report);

	public static string Render(
		JsonElement report,
		IReadOnlyCollection<string> labels,
		Gate gate = Gate.None,
		string allowLabel = "",
		ApiFiles? apiFiles = null
	)
	{
		var body = new StringBuilder();
		body.AppendLine(Marker);
		body.AppendLine(LabelsMarkerPrefix + string.Join(",", labels) + " -->");
		switch (gate)
		{
			case Gate.Blocked:
				body.AppendLine(
					allowLabel.Length > 0
						? $"> [!CAUTION]\n> This check fails on this change. If it is intended, add the `{allowLabel}` label to allow it.\n"
						: "> [!CAUTION]\n> This check fails on this change.\n"
				);
				break;
			case Gate.Allowed:
				body.AppendLine($"> [!NOTE]\n> The `{allowLabel}` label allows this change.\n");
				break;
		}

		if (report.TryGetProperty("firstRelease", out var first) && first.GetBoolean())
		{
			body.AppendLine("### API changes: first release");
			body.AppendLine();
			body.AppendLine("There is no earlier release to compare with.");
			WriteApiFiles(body, apiFiles);
			return body.ToString();
		}

		var suggested = report.GetProperty("suggestedVersionChange").GetString();
		var breaking = report.GetProperty("breakingChanges").GetInt32();
		body.AppendLine($"### API changes: suggested version change **{suggested}**");
		body.AppendLine();
		var baseline = report.TryGetProperty("baseline", out var b) ? $"Compared with `{b.GetString()}`. " : "";
		var assemblies = report.GetProperty("assemblies").EnumerateArray().ToList();
		var removed = Names(report, "removedAssemblies").ToList();
		var added = Names(report, "addedAssemblies").ToList();
		var unchanged = assemblies.Count > 0 && removed.Count == 0 && added.Count == 0 && assemblies.All(a => a.GetProperty("changes").GetArrayLength() == 0);
		body.AppendLine(
			baseline +
				(breaking > 0 ? $"⚠️ {Count(breaking, "breaking change")}." : unchanged ? "✅ All good: the public API is unchanged." : "No breaking changes.")
		);

		foreach (var name in removed)
			body.AppendLine($"- 🔴 assembly `{name}` is removed 💥");
		foreach (var name in added)
			body.AppendLine($"- 🌟 assembly `{name}` is added");

		// what was compared is listed too, so it is clear the tool picked up what was expected
		var untouched = assemblies.Where(a => a.GetProperty("changes").GetArrayLength() == 0).ToList();
		if (untouched.Count > 0)
		{
			body.AppendLine();
			foreach (var assembly in untouched)
				body.AppendLine($"- ✅ **{assembly.GetProperty("name").GetString()}**: no changes");
		}

		var truncated = 0;
		foreach (var assembly in assemblies)
		{
			var changes = assembly.GetProperty("changes");
			if (changes.GetArrayLength() == 0)
				continue;
			var assemblyBreaking = assembly.GetProperty("breakingChanges").GetInt32();
			var details = new StringBuilder();
			foreach (var change in changes.EnumerateArray())
				truncated += WriteChange(details, change, 0, body.Length);
			body.AppendLine();
			body.AppendLine(
				$"<details><summary><b>{assembly.GetProperty("name").GetString()}</b>: {assembly.GetProperty("suggestedVersionChange").GetString()}, " +
					$"{(assemblyBreaking == 0 ? "no breaking changes" : Count(assemblyBreaking, "breaking change"))}</summary>"
			);
			body.AppendLine();
			body.Append(details);
			body.AppendLine();
			body.AppendLine("</details>");
		}
		if (truncated > 0)
			body.AppendLine().AppendLine(
				$"{Count(truncated, "more change")} did not fit in this comment; the job summary and `api-changes.json` have them all."
			);
		WriteApiFiles(body, apiFiles);
		return body.ToString();
	}

	/// <summary>The section on the committed .api files, when they were checked.</summary>
	private static void WriteApiFiles(StringBuilder body, ApiFiles? apiFiles)
	{
		if (apiFiles is null)
			return;
		body.AppendLine();
		body.AppendLine("#### Public API files");
		body.AppendLine();
		var lines = apiFiles.Report.Split('\n').Select(l => l.TrimEnd()).Where(l => l.Length > 0).ToArray();
		switch (apiFiles.Code)
		{
			case 0:
				var files = lines.Select(l => System.Text.RegularExpressions.Regex.Match(l, @"matches the build: (\d+) files?")).FirstOrDefault(m => m.Success);
				body.AppendLine(
					files is null ? "✅ The committed API files match the build." : $"✅ The committed API files match the build ({files.Groups[1].Value} files)."
				);
				break;
			case 5:
				var update = lines.FirstOrDefault(l => l.StartsWith("[api] Update the files with: ", StringComparison.Ordinal));
				var suggested = lines.Select(l => System.Text.RegularExpressions.Regex.Match(l, @"Suggested version change: (\w+)")).LastOrDefault(
					m => m.Success
				)?.Groups[1].Value;
				body.AppendLine($"❌ The committed API files are out of date{(suggested is null ? "." : $": suggested version change **{suggested}**.")}");
				body.AppendLine();
				body.AppendLine("```text");
				foreach (var line in lines.Where(l => l != update))
					body.AppendLine(line);
				body.AppendLine("```");
				if (update is not null)
					body.AppendLine().AppendLine($"Update them with `{update["[api] Update the files with: ".Length..]}`, and commit the result.");
				break;
			default:
				body.AppendLine($"⚠️ The committed API files could not be checked (exit code {apiFiles.Code}).");
				if (lines.Length > 0)
				{
					body.AppendLine();
					body.AppendLine("```text");
					foreach (var line in lines)
						body.AppendLine(line);
					body.AppendLine("```");
				}
				break;
		}
	}

	/// <summary>The labels an earlier comment says this tool set.</summary>
	public static IReadOnlyCollection<string> LabelsIn(string body)
	{
		var start = body.IndexOf(LabelsMarkerPrefix, StringComparison.Ordinal);
		if (start < 0)
			return [];
		start += LabelsMarkerPrefix.Length;
		var end = body.IndexOf(" -->", start, StringComparison.Ordinal);
		return end < 0 ? [] : body[start..end].Split(',', StringSplitOptions.RemoveEmptyEntries);
	}

	private static int WriteChange(StringBuilder details, JsonElement change, int depth, int written)
	{
		if (written + details.Length > MaxLength)
			return 1 + Children(change).Sum(c => WriteChange(details, c, depth + 1, written));

		var kind = change.GetProperty("kind").GetString();
		var name = change.GetProperty("name").GetString();
		var icon = change.GetProperty("change").GetString() switch
		{
			"added" => "🌟",
			"removed" => "🔴",
			_ => "🔷",
		};
		var children = Children(change).ToList();
		var breaking = children.Count == 0 && change.GetProperty("suggestedVersionChange").GetString() == "Major" ? " 💥" : "";
		var text = kind switch
		{
			"Declaration" => name,
			"Attribute" => $"attribute `{name}`",
			_ => $"{kind!.ToLowerInvariant()} `{name}`",
		};
		details.AppendLine($"{new string(' ', depth * 2)}- {icon} {text}{breaking}");

		var skipped = 0;
		foreach (var child in children)
			skipped += WriteChange(details, child, depth + 1, written);
		return skipped;
	}

	private static IEnumerable<JsonElement> Children(JsonElement change) => change.TryGetProperty("changes", out var children) ? children.EnumerateArray() : [];

	private static IEnumerable<string> Names(JsonElement report, string property) =>
		report.TryGetProperty(property, out var names) ? names.EnumerateArray().Select(n => n.GetString()!) : [];

	private static string Count(int count, string noun) => count == 1 ? $"1 {noun}" : $"{count} {noun}s";
}
