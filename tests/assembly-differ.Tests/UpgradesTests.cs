using AwesomeAssertions;
using Nullean.Snapshots;

namespace Differ.Tests;

/// <summary>
/// <c>upgrades</c> in a solution whose app references two packages on a local feed: the next release of one removes a
/// method the app calls, the next release of the other only adds API, and a third is up to date.
/// </summary>
[NotInParallel(nameof(ProjectsTests))]
public class UpgradesTests
{
	private const string Breaking = "AssemblyDiffer.Tests.Breaking";
	private const string Additive = "AssemblyDiffer.Tests.Additive";
	private const string Current = "AssemblyDiffer.Tests.Current";

	private static string Widget(string space, string members) => $"namespace {space}; public class Widget {{ {members} }}";

	[Test]
	public async Task Lists_the_updates_and_what_they_break_in_the_code()
	{
		using var fixture = Fixture.Create("", "");
		var root = fixture.Root;
		var feed = Directory.CreateDirectory(Path.Combine(root, "feed")).FullName;
		Publish(Breaking, "1.0.0", Widget("Breaking", "public void Draw() { } public void Erase() { }"));
		Publish(Breaking, "2.0.0", Widget("Breaking", "public void Erase() { }"));
		Publish(Additive, "1.0.0", Widget("Additive", "public void Draw() { }"));
		Publish(Additive, "1.1.0", Widget("Additive", "public void Draw() { } public void Paint() { }"));
		Publish(Current, "1.0.0", Widget("Current", "public void Draw() { }"));

		void Publish(string id, string version, string source)
		{
			var assembly = Path.Combine(root, $"{id}.{version}.dll");
			Fixture.Compile(source, assembly, referenceAssemblies: true, assemblyName: id);
			CommandsTests.Package(Path.Combine(feed, $"{id}.{version}.nupkg"), id, version, assembly, "net11.0", fileName: $"{id}.dll");
		}

		var repo = Directory.CreateDirectory(Path.Combine(root, "repo")).FullName;
		var app = Directory.CreateDirectory(Path.Combine(repo, "App")).FullName;
		File.WriteAllText(
			Path.Combine(app, "App.csproj"),
			$"""
			<Project Sdk="Microsoft.NET.Sdk">
			  <PropertyGroup><TargetFramework>net11.0</TargetFramework></PropertyGroup>
			  <ItemGroup>
			    <PackageReference Include="{Breaking}" Version="1.0.0" />
			    <PackageReference Include="{Additive}" Version="1.0.0" />
			    <PackageReference Include="{Current}" Version="1.0.0" />
			  </ItemGroup>
			</Project>
			"""
		);
		File.WriteAllText(
			Path.Combine(app, "Use.cs"),
			"namespace App; public static class Use { public static void Run() { new Breaking.Widget().Draw(); new Additive.Widget().Draw(); } }"
		);
		File.WriteAllText(Path.Combine(repo, "Repo.slnx"), """<Solution><Project Path="App/App.csproj" /></Solution>""");
		File.WriteAllText(
			Path.Combine(repo, "NuGet.config"),
			$"""
			<?xml version="1.0" encoding="utf-8"?>
			<configuration><packageSources><clear /><add key="local" value="{feed}" /></packageSources></configuration>
			"""
		);
		var environment = new Dictionary<string, string> { ["NUGET_PACKAGES"] = Path.Combine(root, "packages"), ["MSBUILDDISABLENODEREUSE"] = "1" };
		Dotnet(repo, environment, "build", "Repo.slnx", "-c", "Release", "-nologo", "-v", "q");

		var result = await Cli.RunInAsync(repo, environment, "upgrades", "--fail-on", "major");

		Report.Of(result, fixture).Should().MatchSnapshot();
	}

	private static void Dotnet(string directory, Dictionary<string, string> environment, params string[] args)
	{
		var start = new System.Diagnostics.ProcessStartInfo("dotnet", args)
		{
			WorkingDirectory = directory,
			RedirectStandardOutput = true,
			RedirectStandardError = true,
		};
		foreach (var (name, value) in environment)
			start.Environment[name] = value;
		using var process = System.Diagnostics.Process.Start(start)!;
		var output = process.StandardOutput.ReadToEnd() + process.StandardError.ReadToEnd();
		process.WaitForExit();
		process.ExitCode.Should().Be(0, output);
	}
}
