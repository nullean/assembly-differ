using AwesomeAssertions;
using Nullean.Snapshots;

namespace Differ.Tests;

/// <summary>
/// Characterization tests: they pin down what the <c>diff</c> command prints and returns today, wrong answers included
/// (see the issues named in the scenarios' snapshots). The command is a contract other builds rely on, so a change in
/// any of these snapshots is a change that has to be made on purpose.
/// </summary>
public class DiffCommandTests
{
	public static IEnumerable<(string, string)> ScenariosAndFormats() => from scenario in Scenarios.Names()
																		 from format in new[] { "xml", "markdown", "asciidoc", "github-comment" }
																		 select (scenario, format);

	[Test]
	[MethodDataSource(nameof(ScenariosAndFormats))]
	public async Task Diffs_two_directories(string scenario, string format)
	{
		using var fixture = Scenarios.Create(scenario);
		var output = Directory.CreateDirectory(Path.Combine(fixture.Root, "output")).FullName;

		var result = await Cli.RunAsync("diff", $"directory|{fixture.OldDirectory}", $"directory|{fixture.NewDirectory}", "-f", format, "-o", output);

		Report.Of(result, fixture, output).Should().MatchSnapshot(name: $"{scenario}.{format}");
	}

	[Test]
	public async Task Prevent_change_sets_the_exit_code()
	{
		var table = new System.Text.StringBuilder();
		table.AppendLine("scenario: none patch minor major");
		foreach (var scenario in Scenarios.Names())
		{
			using var fixture = Scenarios.Create(scenario);
			var codes = new List<int>();
			foreach (var level in new[] { "none", "patch", "minor", "major" })
			{
				var result = await Cli.RunAsync("diff", $"assembly|{fixture.OldAssembly}", $"assembly|{fixture.NewAssembly}", "-p", level);
				codes.Add(result.ExitCode);
			}
			table.AppendLine($"{scenario}: {string.Join(' ', codes)}");
		}

		table.ToString().Should().MatchSnapshot();
	}

	public static IEnumerable<(string, string)> Failures() =>
		[
			("unknown-provider", "diff nope|x assembly|{new}"),
			("unknown-format", "diff assembly|{old} assembly|{new} -f html"),
			("missing-assembly", "diff assembly|{fixture}/missing.dll assembly|{new}"),
			("missing-directory", "diff directory|{fixture}/missing directory|{new-dir}"),
			("nothing-to-pair", "diff directory|{fixture}/empty directory|{new-dir}"),
			("allow-empty-without-previous-nuget", "diff directory|{fixture}/empty directory|{new-dir} -a"),
			("unknown-prevent-change", "diff assembly|{old} assembly|{new} -p huge"),
			("without-the-diff-command", "assembly|{old} assembly|{new}"),
		];

	[Test]
	[MethodDataSource(nameof(Failures))]
	public async Task Fails(string name, string commandLine)
	{
		using var fixture = Scenarios.Create("method-removed");
		Directory.CreateDirectory(Path.Combine(fixture.Root, "empty"));
		// arguments are separated by single spaces; none of the fixture paths contain one
		var expanded = commandLine
			.Split(' ')
			.Select(
				a => a
					.Replace("{old}", fixture.OldAssembly)
					.Replace("{new}", fixture.NewAssembly)
					.Replace("{new-dir}", fixture.NewDirectory)
					.Replace("{fixture}", fixture.Root)
			)
			.ToArray();

		var result = await Cli.RunAsync(expanded);

		// an exception prints its stack trace, which moves with every edit; its first line is the message
		var firstErrorLine = result.Error.Split('\n', 2)[0].TrimEnd();
		var report = $"exit code: {result.ExitCode}\n--- stdout\n{result.Out.TrimEnd()}\n--- stderr (first line)\n{firstErrorLine}\n";
		Report.Scrub(report, fixture).Should().MatchSnapshot(name: name);
	}
}
