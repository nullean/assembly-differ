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

	public static string Render(JsonElement report, IReadOnlyCollection<string> labels, Gate gate = Gate.None, string allowLabel = "")
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
			return body.ToString();
		}

		var suggested = report.GetProperty("suggestedVersionChange").GetString();
		var breaking = report.GetProperty("breakingChanges").GetInt32();
		body.AppendLine($"### API changes: suggested version change **{suggested}**");
		body.AppendLine();
		var baseline = report.TryGetProperty("baseline", out var b) ? $"Compared with `{b.GetString()}`. " : "";
		body.AppendLine(baseline + (breaking == 0 ? "No breaking changes." : $"⚠️ {Count(breaking, "breaking change")}."));

		foreach (var name in Names(report, "removedAssemblies"))
			body.AppendLine($"- 🔴 assembly `{name}` is removed 💥");
		foreach (var name in Names(report, "addedAssemblies"))
			body.AppendLine($"- 🌟 assembly `{name}` is added");

		var truncated = 0;
		foreach (var assembly in report.GetProperty("assemblies").EnumerateArray())
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
		return body.ToString();
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
