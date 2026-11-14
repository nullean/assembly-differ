using System;
using System.Collections.Generic;
using System.ComponentModel.DataAnnotations;
using System.IO;
using System.Linq;
using Differ.Engine;
using Differ.Providers;
using Differ.Providers.GitHub;
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

	/// <summary>Compares two published versions of a package.</summary>
	/// <param name="package">The package id.</param>
	/// <param name="oldVersion">The old version.</param>
	/// <param name="newVersion">The new version.</param>
	/// <param name="framework">The target framework to compare, such as net8.0. Defaults to the first the package ships.</param>
	/// <param name="report">How to report the comparison.</param>
	/// <example>assembly-differ nuget Proc 0.13.0 0.14.0</example>
	[CommandName("nuget")]
	public int NuGet(
		[Argument] string package,
		[Argument] string oldVersion,
		[Argument] string newVersion,
		string? framework = null,
		[AsParameters] ReportOptions? report = null
	) =>
		Run(
			() =>
			{
				var installer = new Providers.NuGet.NuGet();
				return (new DiffSide(new NuGetAssemblyProvider(installer, Spec(package, oldVersion, framework)), "nuget"), new DiffSide(
					new NuGetAssemblyProvider(installer, Spec(package, newVersion, framework)),
					"nuget"
				));
			},
			report
		);

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

	/// <summary>Compares a new build with an older commit of a GitHub repository, built with the command you give.</summary>
	/// <param name="repository">The repository as owner/name.</param>
	/// <param name="commit">The commit, branch or tag to build as the old version.</param>
	/// <param name="path">The new build: a .dll or a directory.</param>
	/// <param name="build">The command that builds the old commit, run in its checkout.</param>
	/// <param name="artifacts">Where the build leaves the assemblies, relative to the checkout.</param>
	/// <param name="report">How to report the comparison.</param>
	/// <example>assembly-differ github nullean/proc 0.13.0 src/Proc/bin/Release/netstandard2.0 --build "dotnet build -c Release" --artifacts src/Proc/bin/Release/netstandard2.0</example>
	[CommandName("github")]
	public int GitHub(
		[Argument] string repository,
		[Argument] string commit,
		[Argument] string path,
		string build,
		string artifacts,
		[AsParameters] ReportOptions? report = null
	) =>
		Run(
			() =>
				(new DiffSide(
					new GitHubAssemblyProviderFactory(new Git(Environment.GetEnvironmentVariable("GIT"))).Create([repository, commit, build, artifacts]),
					"github"
				), Local(path, null)),
			report
		);

	private static int Run(Func<(DiffSide Old, DiffSide New)> sides, ReportOptions? report) =>
		Run(() => (((DiffSide Old, DiffSide New)?)sides()), report, () => null)!.Value;

	/// <summary>Compares the two sides <paramref name="sides"/> returns, or returns <c>null</c> when it returns none.</summary>
	private static int? Run(Func<(DiffSide Old, DiffSide New)?> sides, ReportOptions? report, Func<string?> baseline)
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
				new DiffSettings(targets, report.Format, report.Output, failOn, AllowEmptyPreviousNuget: false) { Baseline = baseline() }
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
