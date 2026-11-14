using System;
using System.Collections.Generic;
using System.ComponentModel.DataAnnotations;
using System.IO;
using System.Linq;
using Differ.Engine;
using Differ.Providers;
using Differ.Providers.NuGet;
using Differ.Providers.PreviousNuGet;
using NuGet.Frameworks;
using NuGet.Versioning;
using Nullean.Argh;

namespace Differ;

/// <summary>How a comparison is reported, and when it fails.</summary>
public sealed class ReportOptions
{
	/// <summary>-t, --target, Only compare these assemblies, by file name without extension. Repeat it for more than one.</summary>
	public List<string>? Target { get; set; }

	/// <summary>-f, --format, The report: markdown, xml, asciidoc, github-comment or json.</summary>
	[AllowedValues("markdown", "xml", "asciidoc", "github-comment", "json")]
	public string Format { get; set; } = "markdown";

	/// <summary>-o, --output, A directory or file to write the report to, besides the console.</summary>
	public string? Output { get; set; }

	/// <summary>Exit with code 4 when the suggested version change is at least this: none (never), patch, minor or major.</summary>
	[AllowedValues("none", "patch", "minor", "major")]
	public string FailOn { get; set; } = "none";
}

/// <summary>Compares the public API of two versions of a library and suggests the version change it calls for.</summary>
internal sealed class ApiCommands
{
	/// <summary>Compares two local builds: dlls, directories of dlls, or .nupkg files.</summary>
	/// <param name="old">The old build: a .dll, a directory or a .nupkg.</param>
	/// <param name="new">The new build: a .dll, a directory or a .nupkg.</param>
	/// <param name="framework">The target framework to take from a .nupkg, such as net8.0. Defaults to the newest both packages ship.</param>
	/// <param name="report">How to report the comparison.</param>
	/// <example>assembly-differ files bin/1.2.0/MyLib.dll bin/1.3.0/MyLib.dll</example>
	/// <example>assembly-differ files MyLib.1.2.0.nupkg MyLib.1.3.0.nupkg --framework net8.0</example>
	[CommandName("files")]
	public int Files([Argument] string old, [Argument] string @new, string? framework = null, [AsParameters] ReportOptions? report = null) =>
		Run(
			() =>
			{
				var chosen = Framework(framework, old, @new);
				return (Local(old, chosen), Local(@new, chosen));
			},
			report
		);

	/// <summary>Compares two versions of a package; in a solution, the version your projects use with another.</summary>
	/// <remarks>
	/// In a directory with a solution or project files, nuget X compares the version of X your projects resolve with
	/// its latest stable release, and nuget X 2.0.0 with 2.0.0: what updating would change. It reads the resolved version,
	/// and the target framework each project compiles X for, from obj/project.assets.json, so restore first. When projects
	/// resolve different versions, each is compared. In a solution it shows only the changes to the types and members your
	/// built projects reference, the ones that affect you, unless --all is set; build first. Members match by name, and a
	/// constant you use does not show, because the compiler copies its value into your code. Give two versions to compare
	/// them anywhere.
	/// </remarks>
	/// <param name="package">The package id.</param>
	/// <param name="version">With one version, the version to update to; with two, the old one.</param>
	/// <param name="newVersion">The new version, when the old one is given too.</param>
	/// <param name="framework">The target framework to compare, such as net8.0. In a solution, defaults to the one each project compiles against.</param>
	/// <param name="all">In a solution, show every change, not only the ones to API your built projects use.</param>
	/// <param name="report">How to report the comparison.</param>
	/// <example>assembly-differ nuget Polly</example>
	/// <example>assembly-differ nuget Polly 8.5.0</example>
	/// <example>assembly-differ nuget Proc 0.13.0 0.14.0</example>
	[CommandName("nuget")]
	public int NuGet(
		[Argument] string package,
		[Argument] string? version = null,
		[Argument] string? newVersion = null,
		string? framework = null,
		bool all = false,
		[AsParameters] ReportOptions? report = null
	)
	{
		var installer = new Providers.NuGet.NuGet();
		if (version is not null && newVersion is not null)
			return Run(
				() =>
					(new DiffSide(new NuGetAssemblyProvider(installer, Spec(package, version, framework)), "nuget"), new DiffSide(
						new NuGetAssemblyProvider(installer, Spec(package, newVersion, framework)),
						"nuget"
					)),
				report
			);

		IReadOnlyList<Projects.Resolution> resolutions;
		try
		{
			resolutions = Projects.ResolvedPackages.Find(Directory.GetCurrentDirectory(), package);
		}
		catch (Exception e) when (e is Projects.Dotnet.SdkException or ArgumentException)
		{
			Console.Error.WriteLine($"[diff] {e.Message}");
			return 1;
		}
		if (resolutions.Count == 0)
		{
			Console.Error.WriteLine(
				$"[diff] no project in {Directory.GetCurrentDirectory()} resolves {package}; give two versions to compare, such as: assembly-differ nuget {package} 1.0.0 2.0.0"
			);
			return 1;
		}

		var target = version;
		if (target is null)
		{
			var latest = PreviousRelease.Find(package, new NuGetVersion(int.MaxValue, 0, 0));
			if (latest is null)
			{
				Console.Error.WriteLine($"[diff] no stable release of {package} on {string.Join(", ", PreviousRelease.Sources().Select(s => s.Name))}");
				return 1;
			}
			target = latest.ToNormalizedString();
		}

		var exitCode = 0;
		foreach (var group in resolutions.GroupBy(r => (r.Version, Folder: framework ?? r.LibFolder)))
		{
			var names = group.Select(r => r.Project).Distinct().ToList();
			var projects = string.Join(", ", names);
			var resolve = names.Count == 1 ? "resolves" : "resolve";
			if (NuGetVersion.Parse(group.Key.Version) == NuGetVersion.Parse(target))
			{
				Console.WriteLine($"[diff] {projects} already {resolve} {package} {target}");
				continue;
			}
			Console.WriteLine($"[diff] Comparing {package} {group.Key.Version}, which {projects} {resolve} for {group.Key.Folder}, with {target}");
			var filter = all ? null : UsesFilter(group.ToList());
			var code = Run(
				() =>
					(new DiffSide(new NuGetAssemblyProvider(installer, Spec(package, group.Key.Version, group.Key.Folder)), "nuget"), new DiffSide(
						new NuGetAssemblyProvider(installer, Spec(package, target, group.Key.Folder)),
						"nuget"
					)),
				report,
				filter
			);
			exitCode = Math.Max(exitCode, code);
		}
		return exitCode;
	}

	/// <summary>Compares a new build with the release before it.</summary>
	/// <remarks>
	/// Give a .nupkg and the package id and version come from it; for a .dll or a directory, pass --package and --version.
	/// The release compared with is the newest stable one before that version, on the feeds your NuGet.config lists.
	/// Without one, this is a first release: there is nothing to compare, and the command succeeds unless
	/// --require-baseline is set.
	/// </remarks>
	/// <param name="path">The new build: a .nupkg, a .dll or a directory.</param>
	/// <param name="package">The package id; read from a .nupkg when not given.</param>
	/// <param name="version">The version being released; read from a .nupkg when not given.</param>
	/// <param name="framework">The target framework to compare, such as net8.0. Defaults to the newest the .nupkg ships.</param>
	/// <param name="requireBaseline">Fail when there is no earlier release to compare with.</param>
	/// <param name="report">How to report the comparison.</param>
	/// <example>assembly-differ release build/output/MyLib.1.3.0.nupkg --fail-on major</example>
	/// <example>assembly-differ release src/MyLib/bin/Release/net8.0 --package MyLib --version 1.3.0 -t MyLib</example>
	[CommandName("release")]
	public int Release(
		[Argument] string path,
		string? package = null,
		string? version = null,
		string? framework = null,
		bool requireBaseline = false,
		[AsParameters] ReportOptions? report = null
	)
	{
		report ??= new ReportOptions();
		string? baseline = null;
		var exitCode = Run(
			() =>
			{
				if (Nupkg.IsPackage(path))
				{
					var identity = Nupkg.Identity(path);
					package ??= identity.Id;
					version ??= identity.Version;
				}
				if (package is null || version is null)
					throw new ArgumentException("pass --package and --version, or a .nupkg to read them from");

				var previous = PreviousRelease.Find(package, NuGetVersion.Parse(version));
				if (previous is null)
					return null;
				baseline = previous.ToNormalizedString();
				Console.WriteLine($"[diff] Comparing with {package} {baseline}, the release before {version}");

				var chosen = Framework(framework, path);
				return (new DiffSide(
					new NuGetAssemblyProvider(new Providers.NuGet.NuGet(), Spec(package, baseline, chosen?.GetShortFolderName())),
					"nuget"
				), Local(path, chosen));
			},
			report,
			() => baseline
		);
		if (exitCode is { } code)
			return code;

		// no earlier release: a first release has nothing to compare with
		var sources = string.Join(", ", PreviousRelease.Sources().Select(s => s.Name));
		if (requireBaseline)
		{
			Console.Error.WriteLine($"[diff] No release of {package} before {version} on {sources}, and --require-baseline is set");
			return 1;
		}
		Console.WriteLine($"[diff] No release of {package} before {version} on {sources}: this is its first release, so there is nothing to compare");
		DiffRunner.FirstRelease(package!, report.Format, report.Output);
		Console.WriteLine($"[diff] Suggested version change: {SuggestedVersionChange.None}");
		return 0;
	}

	private static int Run(
		Func<(DiffSide Old, DiffSide New)> sides,
		ReportOptions? report,
		Func<AssemblyComparison, IMetadataDiffItem, IMetadataDiffItem?>? filter = null
	) => Run(() => (((DiffSide Old, DiffSide New)?)sides()), report, () => null, filter)!.Value;

	/// <summary>
	/// The filter to the API the projects use, read from their builds; null, with a warning, when none is built.
	/// </summary>
	private static Func<AssemblyComparison, IMetadataDiffItem, IMetadataDiffItem?>? UsesFilter(IReadOnlyList<Projects.Resolution> resolutions)
	{
		var builds = resolutions.Select(r => (r.Project, Output: Projects.ResolvedPackages.BuildOutput(r))).ToList();
		var unbuilt = builds.Where(b => b.Output is null).Select(b => b.Project).Distinct().ToList();
		var outputs = builds.Where(b => b.Output is not null).Select(b => b.Output!).Distinct().ToList();
		if (outputs.Count == 0)
		{
			Console.Error.WriteLine(
				$"[diff] warning: {string.Join(", ", unbuilt)} not built, so every change is shown; build first to see only the changes your code uses"
			);
			return null;
		}
		if (unbuilt.Count > 0)
			Console.Error.WriteLine($"[diff] warning: {string.Join(", ", unbuilt)} not built, so what they use is left out");
		Console.WriteLine("[diff] Showing the changes to API your projects use; --all shows every change");
		return (pair, diff) => UsageFilter.Apply(diff, UsedApi.Read(outputs, [Path.GetFileNameWithoutExtension(pair.First.Name)]));
	}

	/// <summary>Compares the two sides <paramref name="sides"/> returns, or returns <c>null</c> when it returns none.</summary>
	private static int? Run(
		Func<(DiffSide Old, DiffSide New)?> sides,
		ReportOptions? report,
		Func<string?> baseline,
		Func<AssemblyComparison, IMetadataDiffItem, IMetadataDiffItem?>? filter = null
	)
	{
		report ??= new ReportOptions();
		var targets = new HashSet<string>(report.Target ?? [], StringComparer.OrdinalIgnoreCase);
		try
		{
			if (sides() is not var (oldSide, newSide))
				return null;
			var failOn = Enum.Parse<SuggestedVersionChange>(report.FailOn, ignoreCase: true);
			return DiffRunner.Run(
				oldSide,
				newSide,
				new DiffSettings(targets, report.Format, report.Output, failOn, AllowEmptyPreviousNuget: false) { Baseline = baseline(), Filter = filter }
			);
		}
		catch (Exception e) when (e is ArgumentException or FileNotFoundException or DirectoryNotFoundException)
		{
			Console.Error.WriteLine($"[diff] {e.Message}");
			return 1;
		}
		catch (Exception e)
		{
			Console.Error.WriteLine(e);
			return 1;
		}
	}

	private static string[] Spec(string package, string version, string? framework) => framework is null ? [package, version] : [package, version, framework];

	/// <summary>A local build: a .nupkg, a directory of assemblies, or one assembly.</summary>
	private static DiffSide Local(string path, NuGetFramework? framework)
	{
		if (Nupkg.IsPackage(path))
			return new DiffSide(new NupkgAssemblyProvider(path, framework ?? Nupkg.Frameworks(path).FirstOrDefault() ?? NuGetFramework.AnyFramework), "nupkg");
		if (Directory.Exists(path))
			return new DiffSide(new DirectoryAssemblyProvider(path), "directory");
		return new DiffSide(new AssemblyProvider(path), "assembly");
	}

	/// <summary>The framework to compare: the one given, or the newest every .nupkg among the paths ships.</summary>
	private static NuGetFramework? Framework(string? framework, params string[] paths)
	{
		if (framework is not null)
			return NuGetFramework.ParseFolder(framework);
		var packages = paths.Where(Nupkg.IsPackage).Select(Nupkg.Frameworks).ToList();
		if (packages.Count == 0)
			return null;
		var common = packages.Skip(1).Aggregate((IEnumerable<NuGetFramework>)packages[0], (shared, next) => shared.Where(next.Contains)).FirstOrDefault();
		return common ?? throw new ArgumentException("the packages ship no target framework in common; pass --framework");
	}
}
