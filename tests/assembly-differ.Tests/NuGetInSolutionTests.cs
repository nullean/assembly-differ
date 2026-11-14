using AwesomeAssertions;
using Nullean.Snapshots;

namespace Differ.Tests;

/// <summary>
/// <c>nuget</c> run in a solution: two projects reference a package on a local feed, one at 1.0.0 and one at 1.1.0,
/// and the feed also holds 2.0.0. The tests restore the projects themselves; the tool only reads what restore wrote.
/// </summary>
[NotInParallel(nameof(ProjectsTests))]
public class NuGetInSolutionTests
{
	private const string PackageId = "AssemblyDiffer.Tests.Dependency";

	private sealed class Repository : IDisposable
	{
		public Repository()
		{
			Fixture = Scenarios.Create("method-removed");
			Directory = System.IO.Directory.CreateDirectory(Path.Combine(Fixture.Root, "repo")).FullName;
			var feed = System.IO.Directory.CreateDirectory(Path.Combine(Fixture.Root, "feed")).FullName;
			CommandsTests.Package(Path.Combine(feed, $"{PackageId}.1.0.0.nupkg"), PackageId, "1.0.0", Fixture.OldAssembly, "net11.0");
			CommandsTests.Package(Path.Combine(feed, $"{PackageId}.1.1.0.nupkg"), PackageId, "1.1.0", Fixture.OldAssembly, "net11.0");
			CommandsTests.Package(Path.Combine(feed, $"{PackageId}.2.0.0.nupkg"), PackageId, "2.0.0", Fixture.NewAssembly, "net11.0");

			Project("App", "1.0.0");
			Project("Worker", "1.1.0");
			File.WriteAllText(
				Path.Combine(Directory, "Repo.slnx"),
				"""
				<Solution>
				  <Project Path="App/App.csproj" />
				  <Project Path="Worker/Worker.csproj" />
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
		}

		public Fixture Fixture { get; }

		public string Directory { get; }

		private Dictionary<string, string> Environment =>
			new() { ["NUGET_PACKAGES"] = Path.Combine(Fixture.Root, "packages"), ["MSBUILDDISABLENODEREUSE"] = "1" };

		private void Project(string name, string version)
		{
			var directory = System.IO.Directory.CreateDirectory(Path.Combine(Directory, name)).FullName;
			File.WriteAllText(
				Path.Combine(directory, $"{name}.csproj"),
				$"""
				<Project Sdk="Microsoft.NET.Sdk">
				  <PropertyGroup>
				    <TargetFramework>net11.0</TargetFramework>
				  </PropertyGroup>
				  <ItemGroup>
				    <PackageReference Include="{PackageId}" Version="{version}" />
				  </ItemGroup>
				</Project>
				"""
			);
		}

		public void Restore()
		{
			var start = new System.Diagnostics.ProcessStartInfo("dotnet", ["restore", "Repo.slnx", "-v", "q"])
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

		public async Task<string> Run(params string[] args) => Report.Of(await Cli.RunInAsync(Directory, Environment, ["nuget", .. args]), Fixture);

		public void Dispose() => Fixture.Dispose();
	}

	[Test]
	public async Task Compares_what_each_project_resolves_with_the_latest_release()
	{
		using var repository = new Repository();
		repository.Restore();

		(await repository.Run(PackageId)).Should().MatchSnapshot();
	}

	[Test]
	public async Task Compares_what_each_project_resolves_with_a_version()
	{
		using var repository = new Repository();
		repository.Restore();

		(await repository.Run(PackageId, "1.1.0")).Should().MatchSnapshot();
	}

	[Test]
	public async Task Says_to_restore_first()
	{
		using var repository = new Repository();

		(await repository.Run(PackageId)).Should().MatchSnapshot();
	}
}
