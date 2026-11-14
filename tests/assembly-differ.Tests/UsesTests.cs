using AwesomeAssertions;
using Nullean.Snapshots;

namespace Differ.Tests;

/// <summary>
/// <c>nuget</c> in a solution shows only the changes to API the built projects use. Version 2.0.0 of the package
/// removes <c>Widget.Draw()</c> and changes the type of <c>Widget.Size</c>; the app calls only <c>Draw()</c>.
/// </summary>
[NotInParallel(nameof(ProjectsTests))]
public class UsesTests
{
	private const string PackageId = "AssemblyDiffer.Tests.Used";

	private const string OldSource =
		"""
		namespace Lib;
		public class Widget
		{
			public int Size { get; set; }
			public void Draw() { }
			public string Name(int index) => "";
		}
		public interface IShape { double Area(); }
		""";

	private const string NewSource =
		"""
		namespace Lib;
		public class Widget
		{
			public long Size { get; set; }
			public string Name(int index) => "";
			public void Erase() { }
		}
		public interface IShape { double Area(); }
		public class Gadget { }
		""";

	private sealed class Repository : IDisposable
	{
		public Repository()
		{
			Fixture = Fixture.Create(OldSource, NewSource, referenceAssemblies: true);
			Directory = System.IO.Directory.CreateDirectory(Path.Combine(Fixture.Root, "repo")).FullName;
			var feed = System.IO.Directory.CreateDirectory(Path.Combine(Fixture.Root, "feed")).FullName;
			CommandsTests.Package(Path.Combine(feed, $"{PackageId}.1.0.0.nupkg"), PackageId, "1.0.0", Fixture.OldAssembly, "net11.0");
			CommandsTests.Package(Path.Combine(feed, $"{PackageId}.2.0.0.nupkg"), PackageId, "2.0.0", Fixture.NewAssembly, "net11.0");

			var app = System.IO.Directory.CreateDirectory(Path.Combine(Directory, "App")).FullName;
			File.WriteAllText(
				Path.Combine(app, "App.csproj"),
				$"""
				<Project Sdk="Microsoft.NET.Sdk">
				  <PropertyGroup><TargetFramework>net11.0</TargetFramework></PropertyGroup>
				  <ItemGroup><PackageReference Include="{PackageId}" Version="1.0.0" /></ItemGroup>
				</Project>
				"""
			);
			File.WriteAllText(Path.Combine(app, "Use.cs"), "namespace App; public static class Use { public static void Run() => new Lib.Widget().Draw(); }");
			File.WriteAllText(Path.Combine(Directory, "Repo.slnx"), """<Solution><Project Path="App/App.csproj" /></Solution>""");
			File.WriteAllText(
				Path.Combine(Directory, "NuGet.config"),
				$"""
				<?xml version="1.0" encoding="utf-8"?>
				<configuration><packageSources><clear /><add key="local" value="{feed}" /></packageSources></configuration>
				"""
			);
		}

		public Fixture Fixture { get; }

		public string Directory { get; }

		private Dictionary<string, string> Environment =>
			new() { ["NUGET_PACKAGES"] = Path.Combine(Fixture.Root, "packages"), ["MSBUILDDISABLENODEREUSE"] = "1" };

		public void Dotnet(params string[] args)
		{
			var start = new System.Diagnostics.ProcessStartInfo("dotnet", args)
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

		public async Task<string> Run(params string[] args) =>
			Report.Of(await Cli.RunInAsync(Directory, Environment, ["nuget", PackageId, "2.0.0", .. args]), Fixture);

		public void Dispose() => Fixture.Dispose();
	}

	[Test]
	public async Task Shows_only_the_changes_the_app_uses()
	{
		using var repository = new Repository();
		repository.Dotnet("build", "Repo.slnx", "-c", "Release", "-nologo", "-v", "q");

		(await repository.Run()).Should().MatchSnapshot();
		(await repository.Run("--all")).Should().MatchSnapshot(name: "all");
	}

	[Test]
	public async Task Shows_every_change_with_a_warning_before_a_build()
	{
		using var repository = new Repository();
		repository.Dotnet("restore", "Repo.slnx", "-v", "q");

		(await repository.Run()).Should().MatchSnapshot();
	}
}
