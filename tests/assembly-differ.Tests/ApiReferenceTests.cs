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

		public string Package => Path.Combine(Fixture.Root, "out", "Uses.1.0.0.nupkg");

		public void Build() => Dotnet("build", "-c", "Release", "-v", "q");

		/// <summary>Packs the project into <see cref="Package"/>; its nuspec says it depends on the package with the enum.</summary>
		public void Pack() => Dotnet("pack", "-c", "Release", "-v", "q", "-o", Path.GetDirectoryName(Package)!);

		private void Dotnet(params string[] args)
		{
			var start = new ProcessStartInfo("dotnet", args) { WorkingDirectory = Directory, RedirectStandardOutput = true, RedirectStandardError = true, };
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

		/// <summary>Runs with a packages folder of its own instead of the one restore filled.</summary>
		public async Task<CliResult> RunWithPackages(string packages, params string[] args) =>
			await Cli.RunInAsync(Directory, new Dictionary<string, string>(Environment) { ["NUGET_PACKAGES"] = packages }, args);

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

	[Test]
	public async Task A_package_finds_its_dependencies_in_the_packages_folder()
	{
		using var repository = new Repository();
		repository.Pack();
		var empty = System.IO.Directory.CreateDirectory(Path.Combine(repository.Fixture.Root, "empty")).FullName;

		var restored = await repository.Run("api", repository.Package);
		var missing = await repository.RunWithPackages(empty, "api", repository.Package);

		restored.ExitCode.Should().Be(0, restored.Error);
		restored.Out.Should().Contain("void Run(Mode mode = Mode.Auto)");
		// nothing is downloaded: a dependency that is not there leaves the value a cast
		missing.ExitCode.Should().Be(0, missing.Error);
		missing.Out.Should().Contain("void Run(Mode mode = (Mode)5)");
	}

	/// <summary>
	/// A package that depends on Middle, which depends on Modes [1.1.0, ). The packages folder holds Modes 1.0.0, below
	/// the range, 1.1.0 half restored, 1.2.0 and 2.0.0; each declares value 5 under its own name, so the name written
	/// says which one was read: the lowest the range allows that restore finished, as restore picks.
	/// </summary>
	[Test]
	public async Task A_package_takes_the_version_restore_would_through_its_dependencies_dependencies()
	{
		using var repository = new Repository();
		repository.Build();
		var root = repository.Fixture.Root;
		var packages = System.IO.Directory.CreateDirectory(Path.Combine(root, "synthetic")).FullName;
		Installed(packages, "Modes", "1.0.0", root, "Below", finished: true);
		Installed(packages, "Modes", "1.1.0", root, "Unfinished", finished: false);
		Installed(packages, "Modes", "1.2.0", root, "Chosen", finished: true);
		Installed(packages, "Modes", "2.0.0", root, "Higher", finished: true);
		Installed(packages, "Middle", "1.0.0", root, null, finished: true, dependency: ("Modes", "1.1.0"));
		var package = Path.Combine(root, "Uses.nupkg");
		Nupkg(package, "Uses", "1.0.0", ("Middle", "1.0.0"), repository.Assembly);

		var result = await repository.RunWithPackages(packages, "api", package);

		result.ExitCode.Should().Be(0, result.Error);
		result.Out.Should().Contain("void Run(Mode mode = Mode.Chosen)");
	}

	private static void Installed(
		string packages,
		string id,
		string version,
		string root,
		string? name,
		bool finished,
		(string Id, string Version)? dependency = null
	)
	{
		var directory = System.IO.Directory.CreateDirectory(Path.Combine(packages, id.ToLowerInvariant(), version)).FullName;
		File.WriteAllText(Path.Combine(directory, id.ToLowerInvariant() + ".nuspec"), Nuspec(id, version, dependency));
		if (finished)
			File.WriteAllText(Path.Combine(directory, ".nupkg.metadata"), "{}");
		if (name is null)
			return;
		var lib = System.IO.Directory.CreateDirectory(Path.Combine(directory, "lib", "net11.0")).FullName;
		Fixture.Compile($"namespace Dep {{ public enum Mode {{ Off, On, {name} = 5 }} }}", Path.Combine(lib, "Lib.dll"), referenceAssemblies: true);
	}

	private static void Nupkg(string path, string id, string version, (string Id, string Version) dependency, string assembly)
	{
		using var zip = System.IO.Compression.ZipFile.Open(path, System.IO.Compression.ZipArchiveMode.Create);
		using (var writer = new StreamWriter(zip.CreateEntry(id + ".nuspec").Open()))
			writer.Write(Nuspec(id, version, dependency));
		System.IO.Compression.ZipFileExtensions.CreateEntryFromFile(zip, assembly, "lib/net11.0/" + Path.GetFileName(assembly));
	}

	private static string Nuspec(string id, string version, (string Id, string Version)? dependency) =>
		$"""
		<?xml version="1.0" encoding="utf-8"?>
		<package xmlns="http://schemas.microsoft.com/packaging/2013/05/nuspec.xsd">
		  <metadata>
		    <id>{id}</id><version>{version}</version><authors>tests</authors><description>tests</description>
		    <dependencies>
		      <group targetFramework="net11.0">{(dependency is { } d ? $"<dependency id=\"{d.Id}\" version=\"{d.Version}\" />" : "")}</group>
		    </dependencies>
		  </metadata>
		</package>
		""";
}
