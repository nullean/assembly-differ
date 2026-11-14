using AwesomeAssertions;
using Nullean.Snapshots;

namespace Differ.Tests;

/// <summary>
/// The tool run without a command, in a directory holding a solution: a library whose previous release sits on a local
/// feed, a test project and a project that is not packable. The tool does not build; the tests run dotnet build
/// themselves, for real, so they take seconds each.
/// </summary>
[NotInParallel(nameof(ProjectsTests))]
public class ProjectsTests
{
	private const string PackageId = "AssemblyDiffer.Tests.Projects.Lib";

	private sealed class Repository : IDisposable
	{
		public Repository(string scenario, bool released = true)
		{
			Fixture = Scenarios.Create(scenario);
			Directory = System.IO.Directory.CreateDirectory(Path.Combine(Fixture.Root, "repo")).FullName;
			var feed = System.IO.Directory.CreateDirectory(Path.Combine(Fixture.Root, "feed")).FullName;
			var (oldSource, newSource) = Scenarios.All[scenario];

			Project("Lib", newSource, $"<PackageId>{PackageId}</PackageId>");
			Project("Lib.Tests", "namespace Lib.Tests; public class Checks { }", "<IsTestProject>true</IsTestProject>");
			Project("Internal", "namespace Internal; public class Helper { }", "<IsPackable>false</IsPackable>");
			File.WriteAllText(
				Path.Combine(Directory, "Repo.slnx"),
				"""
				<Solution>
				  <Project Path="Lib/Lib.csproj" />
				  <Project Path="Lib.Tests/Lib.Tests.csproj" />
				  <Project Path="Internal/Internal.csproj" />
				</Solution>
				"""
			);
			File.WriteAllText(
				Path.Combine(Directory, "NuGet.config"),
				$"""
				<?xml version="1.0" encoding="utf-8"?>
				<configuration>
				  <packageSources>
				    <clear />
				    <add key="local" value="{feed}" />
				  </packageSources>
				</configuration>
				"""
			);
			if (released)
				CommandsTests.Package(Path.Combine(feed, $"{PackageId}.1.0.0.nupkg"), PackageId, "1.0.0", Fixture.OldAssembly, "net11.0");
			_ = oldSource;
		}

		public Fixture Fixture { get; }

		public string Directory { get; }

		private void Project(string name, string source, string properties)
		{
			var directory = System.IO.Directory.CreateDirectory(Path.Combine(Directory, name)).FullName;
			File.WriteAllText(
				Path.Combine(directory, $"{name}.csproj"),
				$"""
				<Project Sdk="Microsoft.NET.Sdk">
				  <PropertyGroup>
				    <TargetFramework>net11.0</TargetFramework>
				    <AssemblyName>{name}</AssemblyName>
				    <Nullable>enable</Nullable>
				    {properties}
				  </PropertyGroup>
				</Project>
				"""
			);
			File.WriteAllText(Path.Combine(directory, "Source.cs"), source);
		}

		private Dictionary<string, string> Environment =>
			new() { ["NUGET_PACKAGES"] = Path.Combine(Fixture.Root, "packages"), ["MSBUILDDISABLENODEREUSE"] = "1", };

		public void Build()
		{
			var start = new System.Diagnostics.ProcessStartInfo("dotnet", ["build", "Repo.slnx", "-c", "Release", "-nologo", "-v", "q"])
			{
				WorkingDirectory = Directory,
				RedirectStandardOutput = true,
				RedirectStandardError = true,
			};
			foreach (var (name, value) in Environment)
				start.Environment[name] = value;
			using var process = System.Diagnostics.Process.Start(start)!;
			var output = process.StandardOutput.ReadToEnd() + process.StandardError.ReadToEnd();
			process.WaitForExit();
			process.ExitCode.Should().Be(0, output);
		}

		public async Task<string> Run(params string[] args)
		{
			var environment = Environment;
			var result = await Cli.RunInAsync(Directory, environment, args);
			// the release unpacks under the temporary directory, which differs per machine
			var text = Report.Of(result, Fixture).Replace(Path.GetTempPath().Replace('\\', '/').TrimEnd('/'), "{temp}");
			return text.Replace("/private{temp}", "{temp}");
		}

		public void Dispose() => Fixture.Dispose();
	}

	[Test]
	public async Task Compares_the_library_and_skips_tests_and_unpackable_projects()
	{
		using var repository = new Repository("method-removed");
		repository.Build();

		(await repository.Run()).Should().MatchSnapshot();
		(await repository.Run("--include-tests", "--include-unpackable")).Should().MatchSnapshot(name: "everything");
		(await repository.Run("--project", "Internal/Internal.csproj")).Should().MatchSnapshot(name: "named");
	}

	[Test]
	public async Task A_library_without_a_release_has_nothing_to_compare()
	{
		using var repository = new Repository("method-added", released: false);
		repository.Build();

		(await repository.Run("-f", "json")).Should().MatchSnapshot();
	}

	[Test]
	public async Task A_project_without_a_build_is_skipped_with_a_warning()
	{
		using var repository = new Repository("method-removed");

		(await repository.Run()).Should().MatchSnapshot();
	}

	[Test]
	public async Task A_directory_without_projects_says_what_to_do()
	{
		using var fixture = Scenarios.Create("unchanged");

		var result = await Cli.RunInAsync(fixture.Root, new Dictionary<string, string>(), []);

		result.ExitCode.Should().Be(1);
		Report.Scrub(result.Error, fixture).Trim().Should().Be("[diff] no solution or project in {fixture}; run a command, or pass --project (see --help)");
	}
}
