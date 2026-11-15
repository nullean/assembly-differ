#nullable enable
using System;
using System.Collections.Generic;
using System.IO;
using System.Linq;
using NuGet.Configuration;
using NuGet.Frameworks;
using NuGet.Packaging;
using NuGet.Packaging.Core;
using NuGet.Versioning;

namespace Differ.ApiFiles;

/// <summary>
/// The dependencies of a <c>.nupkg</c> read on its own, found in the packages folder restore fills
/// (<c>~/.nuget/packages</c>, or <c>NUGET_PACKAGES</c>). Nothing is downloaded: a dependency that is not there is left
/// out, and an enum from it is written as a cast.
/// </summary>
internal static class PackageDependencies
{
	/// <summary>The directories holding the assemblies the package's dependencies, and theirs, give a framework to compile against.</summary>
	public static IReadOnlyList<string> Directories(string package, NuGetFramework framework)
	{
		var folder = PackagesFolder(Path.GetDirectoryName(Path.GetFullPath(package)));
		if (folder is null || !Directory.Exists(folder))
			return [];
		return Directories(package, framework, folder);
	}

	internal static IReadOnlyList<string> Directories(string package, NuGetFramework framework, string packagesFolder)
	{
		var directories = new List<string>();
		var seen = new HashSet<string>(StringComparer.OrdinalIgnoreCase);
		var pending = new Queue<PackageDependency>();
		using (var reader = new PackageArchiveReader(package))
		{
			seen.Add(reader.GetIdentity().Id);
			foreach (var dependency in Dependencies(reader, framework))
				pending.Enqueue(dependency);
		}

		while (pending.Count > 0)
		{
			var dependency = pending.Dequeue();
			if (!seen.Add(dependency.Id) || Installed(packagesFolder, dependency) is not { } installed)
				continue;
			var reader = new PackageFolderReader(installed);
			foreach (var directory in CompileDirectories(reader, installed, framework))
				if (!directories.Contains(directory, StringComparer.Ordinal))
					directories.Add(directory);
			foreach (var next in Dependencies(reader, framework))
				pending.Enqueue(next);
		}
		return directories;
	}

	private static string? PackagesFolder(string? directory)
	{
		try
		{
			return SettingsUtility.GetGlobalPackagesFolder(Settings.LoadDefaultSettings(directory));
		}
		catch (NuGetConfigurationException)
		{
			return null;
		}
	}

	/// <summary>The dependencies of the group nearest the framework, as restore picks it.</summary>
	private static IEnumerable<PackageDependency> Dependencies(PackageReaderBase reader, NuGetFramework framework)
	{
		var groups = reader.GetPackageDependencies().ToList();
		var nearest = new FrameworkReducer().GetNearest(framework, groups.Select(g => g.TargetFramework));
		return nearest is null ? [] : groups.First(g => g.TargetFramework.Equals(nearest)).Packages;
	}

	/// <summary>
	/// The version restore would take, the lowest the range allows, when it is in the packages folder; otherwise the
	/// lowest there that the range allows.
	/// </summary>
	private static string? Installed(string packagesFolder, PackageDependency dependency)
	{
		var directory = Path.Combine(packagesFolder, dependency.Id.ToLowerInvariant());
		if (!Directory.Exists(directory))
			return null;
		var versions = Directory
			.EnumerateDirectories(directory)
			.Select(d => NuGetVersion.TryParse(Path.GetFileName(d), out var version) ? (Directory: d, Version: version) : default)
			.Where(v => v.Version is not null && dependency.VersionRange.Satisfies(v.Version))
			// a folder restore is still writing, or gave up on, has no .nupkg.metadata yet
			.Where(v => File.Exists(Path.Combine(v.Directory, ".nupkg.metadata")))
			.OrderBy(v => v.Version)
			.ToList();
		return versions.Count == 0 ? null : versions[0].Directory;
	}

	/// <summary>The folders of the assemblies the package gives the framework to compile against: ref/ when it has one, otherwise lib/.</summary>
	private static IEnumerable<string> CompileDirectories(PackageFolderReader reader, string root, NuGetFramework framework)
	{
		foreach (var groups in new[] { reader.GetItems("ref").ToList(), reader.GetLibItems().ToList() })
		{
			var nearest = new FrameworkReducer().GetNearest(framework, groups.Select(g => g.TargetFramework));
			if (nearest is null)
				continue;
			var dlls = groups
				.First(g => g.TargetFramework.Equals(nearest))
				.Items
				.Where(i => i.EndsWith(".dll", StringComparison.OrdinalIgnoreCase))
				.Select(i => Path.GetFullPath(Path.GetDirectoryName(Path.Combine(root, i))!))
				.Distinct(StringComparer.Ordinal)
				.ToList();
			if (dlls.Count > 0)
				return dlls;
		}
		return [];
	}
}
