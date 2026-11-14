using System.IO.Compression;
using AwesomeAssertions;
using Nullean.Snapshots;

namespace Differ.Tests;

/// <summary>The command for each source: <c>files</c>, <c>nuget</c>, <c>release</c> and <c>github</c>.</summary>
public class CommandsTests
{
	public static IEnumerable<string> Helps() => ["", "files", "nuget", "release", "github"];

	[Test]
	[MethodDataSource(nameof(Helps))]
	public async Task Help(string command)
	{
		var result = await Cli.RunAsync(command.Length == 0 ? ["--help"] : [command, "--help"]);

		result.Out.Should().MatchSnapshot(name: command.Length == 0 ? "root" : command);
	}

	[Test]
	public async Task Files_compares_two_assemblies()
	{
		using var fixture = Scenarios.Create("method-removed");

		var result = await Cli.RunAsync("files", fixture.OldAssembly, fixture.NewAssembly);

		Report.Of(result, fixture).Should().MatchSnapshot();
	}

	[Test]
	public async Task Files_compares_two_directories_for_a_target()
	{
		using var fixture = Scenarios.Create("method-added");

		var result = await Cli.RunAsync("files", fixture.OldDirectory, fixture.NewDirectory, "-t", "Lib", "-f", "json");

		Report.Of(result, fixture).Should().MatchSnapshot();
	}

	[Test]
	public async Task Files_compares_two_packages_on_the_newest_framework_both_ship()
	{
		using var fixture = Scenarios.Create("type-removed");
		var oldPackage = Package(fixture, "old.nupkg", "1.0.0", fixture.OldAssembly, "netstandard2.0", "net8.0");
		var newPackage = Package(fixture, "new.nupkg", "1.1.0", fixture.NewAssembly, "net8.0", "net10.0");

		var result = await Cli.RunAsync("files", oldPackage, newPackage);

		Report.Of(result, fixture).Should().MatchSnapshot();
	}

	[Test]
	public async Task Files_fails_on_packages_without_a_framework_in_common()
	{
		using var fixture = Scenarios.Create("unchanged");
		var oldPackage = Package(fixture, "old.nupkg", "1.0.0", fixture.OldAssembly, "netstandard2.0");
		var newPackage = Package(fixture, "new.nupkg", "1.1.0", fixture.NewAssembly, "net8.0");

		var result = await Cli.RunAsync("files", oldPackage, newPackage);

		result.ExitCode.Should().Be(1);
		result.Error.Should().Contain("no target framework in common; pass --framework");
	}

	[Test]
	public async Task Release_needs_a_package_and_version_for_a_dll()
	{
		using var fixture = Scenarios.Create("unchanged");

		var result = await Cli.RunAsync("release", fixture.NewAssembly);

		result.ExitCode.Should().Be(1);
		result.Error.Trim().Should().Be("[diff] pass --package and --version, or a .nupkg to read them from");
	}

	[Test]
	[NotInParallel(nameof(NuGetProviderTests))]
	public async Task Release_compares_a_package_with_the_release_before_it()
	{
		// Proc 0.14.0 from nuget.org, compared with 0.13.0, the release before it; 0.14.0 changed signatures
		using var fixture = Scenarios.Create("unchanged");
		var package = Path.Combine(fixture.Root, "proc.0.14.0.nupkg");
		using (var http = new HttpClient())
			await File.WriteAllBytesAsync(package, await http.GetByteArrayAsync("https://api.nuget.org/v3-flatcontainer/proc/0.14.0/proc.0.14.0.nupkg"));

		var result = await Cli.RunAsync("release", package, "-f", "json", "--fail-on", "major");

		result.ExitCode.Should().Be(4, result.Out + result.Error);
		result.Out.Should().Contain("[diff] Comparing with Proc 0.13.0, the release before 0.14.0").And.Contain("\"suggestedVersionChange\": \"Major\"");
	}

	[Test]
	[NotInParallel(nameof(NuGetProviderTests))]
	public async Task NuGet_compares_two_published_versions()
	{
		// Proc 0.13.0 marks NoWrapInThread obsolete, and changes nothing else
		var result = await Cli.RunAsync("nuget", "Proc", "0.12.0", "0.13.0", "-t", "proc");

		result.ExitCode.Should().Be(0, result.Error);
		result.Out.Should().Contain("### `NoWrapInThread`\nAttribute `System.ObsoleteAttribute::.ctor(System.String)` is added.");
		result.Out.Should().Contain("[diff] Suggested version change: Minor");
	}

	/// <summary>Writes a .nupkg holding the assembly once for every framework given.</summary>
	internal static string Package(Fixture fixture, string fileName, string version, string assembly, params string[] frameworks) =>
		Package(Path.Combine(fixture.Root, fileName), "Lib", version, assembly, frameworks);

	/// <summary>Writes a .nupkg of package <paramref name="id"/> to <paramref name="path"/>, holding the assembly once for every framework given.</summary>
	internal static string Package(string path, string id, string version, string assembly, params string[] frameworks)
	{
		using var zip = ZipFile.Open(path, ZipArchiveMode.Create);
		var nuspec = zip.CreateEntry("Lib.nuspec");
		using (var writer = new StreamWriter(nuspec.Open()))
			writer.Write(
				$"""
				<?xml version="1.0" encoding="utf-8"?>
				<package xmlns="http://schemas.microsoft.com/packaging/2013/05/nuspec.xsd">
				  <metadata><id>{id}</id><version>{version}</version><authors>tests</authors><description>tests</description></metadata>
				</package>
				"""
			);
		foreach (var framework in frameworks)
			zip.CreateEntryFromFile(assembly, $"lib/{framework}/Lib.dll");
		return path;
	}
}
