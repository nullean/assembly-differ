using System.Diagnostics;
using AwesomeAssertions;

namespace Differ.Tests;

/// <summary>
/// An enum default from a package: <c>void Run(Mode mode = Mode.Auto)</c>, where <c>Mode</c> is in a package on a
/// local feed. A library's build output does not hold its packages, so the value is only written by name when the
/// command finds the package: through the project's restore, or a directory passed with --reference.
/// </summary>
[NotInParallel(nameof(ProjectsTests))]
public class ApiReferenceTests
{
	private const string PackageId = "AssemblyDiffer.Tests.Modes";

	private sealed class Repository : IDisposable
	{
		public Repository()
		{
			Fixture = Fixture.Create("namespace Dep { public enum Mode { Off, On, Auto = 5 } }", "namespace Dep { }", referenceAssemblies: true);
			Directory = System.IO.Directory.CreateDirectory(Path.Combine(Fixture.Root, "repo")).FullName;
			var feed = System.IO.Directory.CreateDirectory(Path.Combine(Fixture.Root, "feed")).FullName;
			CommandsTests.Package(Path.Combine(feed, $"{PackageId}.1.0.0.nupkg"), PackageId, "1.0.0", Fixture.OldAssembly, "net11.0");
			File.WriteAllText(
				Path.Combine(Directory, "Uses.csproj"),
				$"""
				<Project Sdk="Microsoft.NET.Sdk">
				  <PropertyGroup>
				    <TargetFramework>net11.0</TargetFramework>
				  </PropertyGroup>
				  <ItemGroup>
				    <PackageReference Include="{PackageId}" Version="1.0.0" />
				  </ItemGroup>
				</Project>
				"""
			);
			File.WriteAllText(
				Path.Combine(Directory, "Runner.cs"),
				"namespace Uses { public class Runner { public void Run(Dep.Mode mode = Dep.Mode.Auto) { } } }"
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

		public string Packages => Path.Combine(Fixture.Root, "packages");

		public string Assembly => Path.Combine(Directory, "bin", "Release", "net11.0", "Uses.dll");

		private Dictionary<string, string> Environment => new() { ["NUGET_PACKAGES"] = Packages, ["MSBUILDDISABLENODEREUSE"] = "1" };

		public void Build()
		{
			var start = new ProcessStartInfo("dotnet", ["build", "-c", "Release", "-v", "q"])
			{
				WorkingDirectory = Directory,
				RedirectStandardOutput = true,
				RedirectStandardError = true,
			};
			foreach (var (name, value) in Environment)
				start.Environment[name] = value;
			using var process = Process.Start(start)!;
			var output = process.StandardOutput.ReadToEnd() + process.StandardError.ReadToEnd();
			process.WaitForExit();
			process.ExitCode.Should().Be(0, output);
			// the build output holds the library alone, as a library's does
			File.Exists(Path.Combine(Path.GetDirectoryName(Assembly)!, "Lib.dll")).Should().BeFalse();
		}

		public async Task<CliResult> Run(params string[] args) => await Cli.RunInAsync(Directory, Environment, args);

		public void Dispose() => Fixture.Dispose();
	}

	[Test]
	public async Task A_project_finds_its_packages_enums()
	{
		using var repository = new Repository();
		repository.Build();

		var result = await repository.Run("api");

		result.ExitCode.Should().Be(0, result.Error);
		result.Out.Should().Contain("void Run(Mode mode = Mode.Auto)");
	}

	[Test]
	public async Task An_assembly_finds_them_in_a_reference_directory()
	{
		using var repository = new Repository();
		repository.Build();
		var package = Path.Combine(repository.Packages, PackageId.ToLowerInvariant(), "1.0.0", "lib", "net11.0");

		var alone = await repository.Run("api", repository.Assembly);
		var referenced = await repository.Run("api", repository.Assembly, "-r", package);

		alone.Out.Should().Contain("void Run(Mode mode = (Mode)5)");
		referenced.ExitCode.Should().Be(0, referenced.Error);
		referenced.Out.Should().Contain("void Run(Mode mode = Mode.Auto)");
	}

	[Test]
	public async Task A_missing_reference_directory_is_an_error()
	{
		using var repository = new Repository();

		var result = await repository.Run("api", repository.Fixture.OldAssembly, "-r", Path.Combine(repository.Fixture.Root, "nowhere"));

		result.ExitCode.Should().Be(1);
		result.Error.Should().Contain("which --reference names");
	}
}
