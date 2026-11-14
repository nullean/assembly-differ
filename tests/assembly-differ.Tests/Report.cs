using System.Text;

namespace Differ.Tests;

/// <summary>Turns a run into the text a snapshot keeps: the exit code, both streams and the files written.</summary>
public static class Report
{
	public static string Of(CliResult result, Fixture fixture, string? outputDirectory = null)
	{
		var report = new StringBuilder();
		report.AppendLine($"exit code: {result.ExitCode}");
		report.AppendLine("--- stdout");
		report.AppendLine(result.Out.TrimEnd());
		report.AppendLine("--- stderr");
		report.AppendLine(result.Error.TrimEnd());
		if (outputDirectory != null)
		{
			report.AppendLine("--- files");
			foreach (var file in Directory.EnumerateFiles(outputDirectory).Order(StringComparer.Ordinal))
				report.AppendLine(Path.GetFileName(file));
		}
		return Scrub(report.ToString(), fixture);
	}

	/// <summary>Replaces the fixture's temporary directory, which differs per run, with <c>{fixture}</c>.</summary>
	public static string Scrub(string text, Fixture fixture)
	{
		var root = fixture.Root;
		// macOS hands out /var/... temporary paths that resolve to /private/var/...; the tool prints either
		text = text.Replace("/private" + root, "{fixture}").Replace(root, "{fixture}");
		// packages unpack under the temporary directory, which differs per machine
		var temp = Path.GetTempPath().TrimEnd('/', '\\');
		text = text.Replace("/private" + temp, "{temp}").Replace(temp, "{temp}");
		// every run has a scratch folder of its own
		text = System.Text.RegularExpressions.Regex.Replace(text.Replace('\\', '/'), "assembly-differ/[0-9a-f]{32}", "assembly-differ/{run}");
		return text;
	}
}
