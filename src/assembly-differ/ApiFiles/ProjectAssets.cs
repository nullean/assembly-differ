#nullable enable
using System;
using System.Collections.Generic;
using System.IO;
using System.Linq;
using System.Text.Json;
using NuGet.Frameworks;

namespace Differ.ApiFiles;

/// <summary>
/// The packages a project restored, from its <c>obj/project.assets.json</c>. A library's build output holds only the
/// library, so the assemblies that declare the enums its API uses from a package are found here, where restore put them.
/// </summary>
internal static class ProjectAssets
{
	/// <summary>
	/// The directories that hold the assemblies a framework compiles against from packages, in the packages folders the
	/// file names; empty when the file is missing or says nothing for the framework.
	/// </summary>
	public static IReadOnlyList<string> Directories(string? assetsFile, NuGetFramework framework)
	{
		if (assetsFile is null || !File.Exists(assetsFile))
			return [];
		using var document = JsonDocument.Parse(File.ReadAllBytes(assetsFile));
		var root = document.RootElement;
		if (!root.TryGetProperty("targets", out var targets) || !root.TryGetProperty("libraries", out var libraries))
			return [];

		var folders = root.TryGetProperty("packageFolders", out var packageFolders) ? packageFolders.EnumerateObject().Select(f => f.Name).ToList() : [];
		if (folders.Count == 0)
			return [];

		var directories = new List<string>();
		var seen = new HashSet<string>(StringComparer.Ordinal);
		foreach (var target in targets.EnumerateObject())
		{
			// "net8.0", or ".NETCoreApp,Version=v8.0" in older files; "net8.0/linux-x64" is the same compile set for a runtime
			if (target.Name.Contains('/') || !NuGetFramework.Parse(target.Name).Equals(framework))
				continue;
			foreach (var library in target.Value.EnumerateObject())
			{
				if (
					!library.Value.TryGetProperty("type", out var type) || type.GetString() != "package" ||
					!library.Value.TryGetProperty("compile", out var compile) || !libraries.TryGetProperty(library.Name, out var entry) ||
					!entry.TryGetProperty("path", out var path)
				)
					continue;
				foreach (var asset in compile.EnumerateObject())
				{
					// _._ marks a framework the package supports with nothing to reference
					if (!asset.Name.EndsWith(".dll", StringComparison.OrdinalIgnoreCase))
						continue;
					var relative = Path.GetDirectoryName(Path.Combine(path.GetString()!, asset.Name))!;
					foreach (var folder in folders)
					{
						var directory = Path.GetFullPath(Path.Combine(folder, relative));
						if (Directory.Exists(directory) && seen.Add(directory))
						{
							directories.Add(directory);
							break;
						}
					}
				}
			}
		}
		return directories;
	}
}
