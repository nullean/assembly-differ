using System.IO.Compression;
using AwesomeAssertions;

namespace Differ.Tests;

/// <summary>
/// <c>release</c> in a directory with a solution compares the assemblies the solution builds, not the dependencies a
/// package bundles: Lib is the solution's library, Dep a dependency the package ships next to it, and only Dep changed.
/// </summary>
public class OwnAssembliesTests
{
	private const string Id = "AssemblyDiffer.Tests.Own";

	private sealed class Repository : IDisposable
	{
		public Repository()
		{
			Fixture = Scenarios.Create("method-removed");
			var feed = Directory.CreateDirectory(Path.Combine(Fixture.Root, "feed")).FullName;
			Work = Directory.CreateDirectory(Path.Combine(Fixture.Root, "work")).FullName;
			Directory.CreateDirectory(Path.Combine(Work, "Lib"));
			File.WriteAllText(
				Path.Combine(Work, "Lib", "Lib.csproj"),
				"""
				<Project Sdk="Microsoft.NET.Sdk">
				  <PropertyGroup>
				    <TargetFramework>net11.0</TargetFramework>
				    <AssemblyName>Lib</AssemblyName>
				  </PropertyGroup>
				</Project>
				"""
			);
			File.WriteAllText(Path.Combine(Work, "Repo.slnx"), """<Solution><Project Path="Lib/Lib.csproj" /></Solution>""");
			File.WriteAllText(
				Path.Combine(Work, "NuGet.config"),
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
			Pack(Path.Combine(feed, $"{Id}.1.0.0.nupkg"), "1.0.0", Fixture.OldAssembly);
			Package = Pack(Path.Combine(Fixture.Root, $"{Id}.1.1.0.nupkg"), "1.1.0", Fixture.NewAssembly);
		}

		public Fixture Fixture { get; }

		public string Work { get; }

		public string Package { get; }

		// Lib is the same in both releases; Dep is the assembly that changes
		private string Pack(string path, string version, string dependency)
		{
			CommandsTests.Package(path, Id, version, Fixture.NewAssembly, "net10.0", "Lib.dll");
			using var zip = ZipFile.Open(path, ZipArchiveMode.Update);
			zip.CreateEntryFromFile(dependency, "lib/net10.0/Dep.dll");
			return path;
		}

		public Task<CliResult> Run(params string[] args) =>
			Cli.RunInAsync(Work, new Dictionary<string, string> { ["NUGET_PACKAGES"] = Path.Combine(Fixture.Root, "packages") }, args);

		public void Dispose() => Fixture.Dispose();
	}

	[Test]
	public async Task A_bundled_dependency_is_not_compared_by_default()
	{
		using var repository = new Repository();

		var result = await repository.Run("release", repository.Package);

		result.ExitCode.Should().Be(0, result.Error);
		result.Out.Should().Contain("Comparing only the assemblies this solution builds: Lib").And.Contain("Suggested version change: None");
	}

	[Test]
	public async Task All_assemblies_compares_the_bundled_dependency_too()
	{
		using var repository = new Repository();

		var result = await repository.Run("release", repository.Package, "--all-assemblies");

		result.ExitCode.Should().Be(0, result.Error);
		result.Out.Should().NotContain("Comparing only").And.Contain("Suggested version change: Major");
	}

	[Test]
	public async Task A_target_names_the_assemblies_itself()
	{
		using var repository = new Repository();

		var result = await repository.Run("release", repository.Package, "-t", "Dep");

		result.ExitCode.Should().Be(0, result.Error);
		result.Out.Should().NotContain("Comparing only").And.Contain("Suggested version change: Major");
	}
}
