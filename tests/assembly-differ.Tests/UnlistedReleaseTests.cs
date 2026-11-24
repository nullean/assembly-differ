using AwesomeAssertions;

namespace Differ.Tests;

/// <summary>
/// <c>release</c> against a v3 feed that has unlisted versions: an unlisted version is never the baseline, however
/// high its number, because its owner took it off the feed on purpose.
/// </summary>
public class UnlistedReleaseTests
{
	private const string Id = "AssemblyDiffer.Tests.Lib";

	[Test]
	public async Task An_unlisted_version_is_not_the_release_to_compare_with()
	{
		using var fixture = Scenarios.Create("method-removed");
		using var feed = new FakeNuGetFeed(Id);
		feed.Add("1.0.0", listed: true, CommandsTests.Package(Path.Combine(fixture.Root, $"{Id}.1.0.0.nupkg"), Id, "1.0.0", fixture.OldAssembly, "net10.0"));
		// 8.0.1 is the highest version below the build's, and was unlisted by its owner
		feed.Add("8.0.1", listed: false, CommandsTests.Package(Path.Combine(fixture.Root, $"{Id}.8.0.1.nupkg"), Id, "8.0.1", fixture.NewAssembly, "net10.0"));
		var package = CommandsTests.Package(Path.Combine(fixture.Root, $"{Id}.9.0.0.nupkg"), Id, "9.0.0", fixture.NewAssembly, "net10.0");
		var work = Directory.CreateDirectory(Path.Combine(fixture.Root, "work")).FullName;
		File.WriteAllText(
			Path.Combine(work, "NuGet.config"),
			$"""
			<?xml version="1.0" encoding="utf-8"?>
			<configuration>
			  <packageSources>
			    <clear />
			    <add key="fake" value="{feed.ServiceIndexUrl}" allowInsecureConnections="true" />
			  </packageSources>
			</configuration>
			"""
		);
		var environment = new Dictionary<string, string> { ["NUGET_PACKAGES"] = Path.Combine(fixture.Root, "packages") };

		var result = await Cli.RunInAsync(work, environment, "release", package);

		result.ExitCode.Should().Be(0, result.Error);
		result.Out.Should().Contain($"Comparing with {Id} 1.0.0, the release before 9.0.0");
		// 1.0.0 is the old assembly: the method the new one removed is the change, and it is a major one
		result.Out.Should().Contain("Suggested version change: Major");
	}
}
