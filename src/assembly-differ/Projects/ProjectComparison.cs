using System;
using System.Collections.Generic;
using System.IO;
using System.Linq;
using Differ.Engine;
using Differ.Providers;
using Differ.Providers.NuGet;
using Differ.Providers.PreviousNuGet;
using NuGet.Frameworks;
using NuGet.Versioning;

namespace Differ.Projects;

/// <summary>A project to compare: its file, package id, assembly name and the framework compared.</summary>
internal sealed record Project(string Path, string PackageId, string AssemblyName, NuGetFramework Framework);

/// <summary>Finds, builds and compares the projects in a directory with their latest releases.</summary>
internal static class ProjectComparison
{
	public sealed record Options(IReadOnlyList<string> Projects, bool IncludeTests, bool IncludeUnpackable);

	public static int Run(string directory, Options options, ReportOptions report)
	{
		var candidates = Candidates(directory, options.Projects);
		if (candidates.Count == 0)
		{
			Console.Error.WriteLine($"[diff] no solution or project in {directory}; run a command, or pass --project (see --help)");
			return 1;
		}

		var projects = new List<Project>();
		foreach (var path in candidates)
		{
			var properties = Dotnet.Properties(path, ["IsPackable", "IsTestProject", "PackageId", "AssemblyName", "TargetFramework", "TargetFrameworks"]);
			var name = System.IO.Path.GetFileNameWithoutExtension(path);
			var explicitlyNamed = options.Projects.Count > 0;
			if (!explicitlyNamed && IsTrue(properties["IsTestProject"]) && !options.IncludeTests)
			{
				Console.WriteLine($"[diff] Skipping {name}: a test project (--include-tests compares it)");
				continue;
			}
			if (!explicitlyNamed && !IsTrue(properties["IsPackable"]) && !options.IncludeUnpackable)
			{
				Console.WriteLine($"[diff] Skipping {name}: IsPackable is false (--include-unpackable compares it)");
				continue;
			}
			var frameworks = (properties["TargetFrameworks"].Length > 0 ? properties["TargetFrameworks"] : properties["TargetFramework"])
				.Split(';', StringSplitOptions.RemoveEmptyEntries | StringSplitOptions.TrimEntries)
				.Select(NuGetFramework.Parse)
				.ToList();
			if (frameworks.Count == 0)
			{
				Console.WriteLine($"[diff] Skipping {name}: it has no target framework");
				continue;
			}
			projects.Add(new Project(path, properties["PackageId"], properties["AssemblyName"], Nupkg.Newest(frameworks)));
		}
		if (projects.Count == 0)
		{
			Console.WriteLine("[diff] Nothing to compare");
			Console.WriteLine($"[diff] Suggested version change: {SuggestedVersionChange.None}");
			return 0;
		}

		var oldAssemblies = new List<DiffSide>();
		var newAssemblies = new List<DiffSide>();
		var installer = new Providers.NuGet.NuGet();
		foreach (var project in projects)
		{
			var latest = PreviousRelease.Find(project.PackageId, new NuGetVersion(int.MaxValue, 0, 0));
			if (latest is null)
			{
				Console.WriteLine($"[diff] {project.PackageId} has no release yet: nothing to compare");
				continue;
			}
			var targetPath = Dotnet.Properties(
				project.Path,
				["TargetPath"],
				$"TargetFramework={project.Framework.GetShortFolderName()}",
				"Configuration=Release"
			)["TargetPath"];
			if (!File.Exists(targetPath))
			{
				Console.Error.WriteLine(
					$"[diff] warning: skipping {project.AssemblyName}, which has no build output at {targetPath}; build it first with: dotnet build -c Release"
				);
				continue;
			}
			var release = $"{project.PackageId} {latest.ToNormalizedString()}";
			var released = new NuGetAssemblyProvider(installer, [project.PackageId, latest.ToNormalizedString(), project.Framework.GetShortFolderName()])
				.GetAssemblies(new HashSet<string>(StringComparer.OrdinalIgnoreCase) { project.AssemblyName })
				.ToList();
			if (released.Count == 0)
			{
				// a tool package, for one, ships its assembly outside lib/
				Console.WriteLine($"[diff] {release} ships no {project.AssemblyName}.dll to compare with");
				continue;
			}
			Console.WriteLine($"[diff] Comparing {project.AssemblyName} ({project.Framework.GetShortFolderName()}) with {release}");
			oldAssemblies.Add(new DiffSide(new FixedAssemblyProvider(released), "nuget"));
			newAssemblies.Add(new DiffSide(new FixedAssemblyProvider([new FileInfo(targetPath)]), "assembly"));
		}
		if (oldAssemblies.Count == 0)
		{
			Console.WriteLine($"[diff] Suggested version change: {SuggestedVersionChange.None}");
			return 0;
		}

		var targets = new HashSet<string>(report.Target ?? projects.Select(p => p.AssemblyName), StringComparer.OrdinalIgnoreCase);
		var failOn = report.FailOn;
		return DiffRunner.Run(
			new DiffSide(new CompositeAssemblyProvider(oldAssemblies.Select(s => s.Provider)), "projects"),
			new DiffSide(new CompositeAssemblyProvider(newAssemblies.Select(s => s.Provider)), "projects"),
			new DiffSettings(targets, report.Format, report.Output, failOn, AllowEmptyPreviousNuget: false)
		);
	}

	private static bool IsTrue(string value) => string.Equals(value, "true", StringComparison.OrdinalIgnoreCase);

	/// <summary>
	/// The project files to consider: the ones named, or the projects of the one solution in the directory, or the
	/// project files in it.
	/// </summary>
	internal static List<string> Candidates(string directory, IReadOnlyList<string> named)
	{
		if (named.Count > 0)
			return named.Select(System.IO.Path.GetFullPath).ToList();

		var solutions = Directory.EnumerateFiles(directory, "*.slnx").Concat(Directory.EnumerateFiles(directory, "*.sln")).ToList();
		if (solutions.Count > 1)
			throw new ArgumentException($"{directory} holds more than one solution; pass --project for the projects to compare");
		if (solutions.Count == 1)
		{
			var solutionDirectory = System.IO.Path.GetDirectoryName(solutions[0])!;
			return Dotnet.SolutionProjects(solutions[0]).Select(p => System.IO.Path.GetFullPath(System.IO.Path.Combine(solutionDirectory, p))).ToList();
		}
		return Directory
			.EnumerateFiles(directory, "*.csproj")
			.Concat(Directory.EnumerateFiles(directory, "*.fsproj"))
			.Concat(Directory.EnumerateFiles(directory, "*.vbproj"))
			.ToList();
	}
}

/// <summary>Assemblies already found, such as the ones a release was unpacked to.</summary>
internal sealed class FixedAssemblyProvider(IReadOnlyList<FileInfo> assemblies) : IAssemblyProvider
{
	public IEnumerable<FileInfo> GetAssemblies(HashSet<string> targets) =>
		assemblies.Where(a => targets.Count == 0 || targets.Contains(System.IO.Path.GetFileNameWithoutExtension(a.Name)));
}

/// <summary>The assemblies of several providers together, such as one per project.</summary>
internal sealed class CompositeAssemblyProvider(IEnumerable<IAssemblyProvider> providers) : IAssemblyProvider
{
	private readonly List<IAssemblyProvider> _providers = providers.ToList();

	public IEnumerable<FileInfo> GetAssemblies(HashSet<string> targets) => _providers.SelectMany(p => p.GetAssemblies(targets)).ToList();
}
