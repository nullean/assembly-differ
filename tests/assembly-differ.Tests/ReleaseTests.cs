using AwesomeAssertions;
using Nullean.Snapshots;

namespace Differ.Tests;

/// <summary>
/// <c>release</c> against a local folder feed: a NuGet.config in the working directory lists only that folder, and
/// NUGET_PACKAGES points at a folder of the test's own, so nothing reaches nuget.org or the user's package cache.
/// </summary>
public class ReleaseTests
{
	private const string Id = "AssemblyDiffer.Tests.Lib";

	private sealed class Feed : IDisposable
	{
		public Feed(Fixture fixture)
		{
			Fixture = fixture;
			Directory = System.IO.Directory.CreateDirectory(Path.Combine(fixture.Root, "feed")).FullName;
			WorkingDirectory = System.IO.Directory.CreateDirectory(Path.Combine(fixture.Root, "work")).FullName;
			File.WriteAllText(
				Path.Combine(WorkingDirectory, "NuGet.config"),
				$"""
				<?xml version="1.0" encoding="utf-8"?>
				<configuration>
				  <packageSources>
				    <clear />
				    <add key="local" value="{Directory}" />
				  </packageSources>
				</configuration>
				"""
			);
			Environment = new Dictionary<string, string> { ["NUGET_PACKAGES"] = Path.Combine(fixture.Root, "packages") };
		}

		public Fixture Fixture { get; }
		public string Directory { get; }
		public string WorkingDirectory { get; }
		public IReadOnlyDictionary<string, string> Environment { get; }

		public void Publish(string version, string assembly) =>
			CommandsTests.Package(Path.Combine(Directory, $"{Id}.{version}.nupkg"), Id, version, assembly, "net10.0");

		public string Pack(string version, string assembly) =>
			CommandsTests.Package(Path.Combine(Fixture.Root, $"{Id}.{version}.nupkg"), Id, version, assembly, "net10.0");

		public Task<CliResult> Run(params string[] args) => Cli.RunInAsync(WorkingDirectory, Environment, args);

		public void Dispose() => Fixture.Dispose();
	}

	[Test]
	public async Task Compares_with_the_newest_stable_release_before_the_version()
	{
		using var feed = new Feed(Scenarios.Create("method-removed"));
		feed.Publish("1.0.0", feed.Fixture.NewAssembly);
		feed.Publish("1.1.0", feed.Fixture.OldAssembly);
		feed.Publish("1.2.0-beta.1", feed.Fixture.NewAssembly);
		feed.Publish("2.0.0", feed.Fixture.NewAssembly);
		var package = feed.Pack("1.2.0", feed.Fixture.NewAssembly);

		var result = await feed.Run("release", package, "-f", "json");

		Report.Of(result, feed.Fixture).Should().MatchSnapshot();
	}

	[Test]
	public async Task A_first_release_succeeds_with_nothing_to_compare()
	{
		using var feed = new Feed(Scenarios.Create("unchanged"));
		var package = feed.Pack("1.0.0", feed.Fixture.NewAssembly);

		var result = await feed.Run("release", package, "-f", "json", "--fail-on", "patch");

		Report.Of(result, feed.Fixture).Should().MatchSnapshot();
	}

	[Test]
	public async Task A_first_release_fails_when_a_baseline_is_required()
	{
		using var feed = new Feed(Scenarios.Create("unchanged"));
		var package = feed.Pack("1.0.0", feed.Fixture.NewAssembly);

		var result = await feed.Run("release", package, "--require-baseline");

		Report.Of(result, feed.Fixture).Should().MatchSnapshot();
	}

	[Test]
	public async Task A_first_release_writes_a_github_comment_that_says_so()
	{
		using var feed = new Feed(Scenarios.Create("unchanged"));
		var package = feed.Pack("1.0.0", feed.Fixture.NewAssembly);

		var result = await feed.Run("release", package, "-f", "github-comment");

		Report.Of(result, feed.Fixture).Should().MatchSnapshot();
	}
}
