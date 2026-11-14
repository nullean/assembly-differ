using System;
using System.Collections.Generic;
using System.IO;
using System.Linq;
using System.Text.Json;

namespace Differ.Projects;

/// <summary>A package version a project resolves, and the lib/ folder it compiles against, such as net8.0.</summary>
internal sealed record Resolution(string Project, string ProjectPath, string TargetFramework, string Version, string LibFolder);

/// <summary>Reads which version of a package the projects in a directory resolve, from their project.assets.json.</summary>
internal static class ResolvedPackages
{
	public static IReadOnlyList<Resolution> Find(string directory, string packageId)
	{
		var resolutions = new List<Resolution>();
		var unrestored = new List<string>();
		foreach (var project in ProjectComparison.Candidates(directory, []))
		{
			var assets = Dotnet.Properties(project, ["ProjectAssetsFile"])["ProjectAssetsFile"];
			var name = Path.GetFileNameWithoutExtension(project);
			if (!File.Exists(assets))
			{
				unrestored.Add(name);
				continue;
			}
			using var document = JsonDocument.Parse(File.ReadAllText(assets));
			foreach (var target in document.RootElement.GetProperty("targets").EnumerateObject())
			{
				// targets per runtime identifier repeat the framework's
				if (target.Name.Contains('/'))
					continue;
				foreach (var library in target.Value.EnumerateObject())
				{
					var slash = library.Name.IndexOf('/');
					if (slash < 0 || !string.Equals(library.Name[..slash], packageId, StringComparison.OrdinalIgnoreCase))
						continue;
					if (LibFolder(library.Value) is { } folder)
						resolutions.Add(new Resolution(name, project, target.Name, library.Name[(slash + 1)..], folder));
				}
			}
		}
		if (unrestored.Count > 0)
			Console.Error.WriteLine($"[diff] warning: not restored, so not read: {string.Join(", ", unrestored)}; run dotnet restore first");
		return resolutions;
	}

	/// <summary>
	/// The assembly a project builds for a target framework, in Release or else Debug, or null when it is not built.
	/// </summary>
	public static string? BuildOutput(Resolution resolution)
	{
		foreach (var configuration in new[] { "Release", "Debug" })
		{
			var path = Dotnet.Properties(
				resolution.ProjectPath,
				["TargetPath"],
				$"TargetFramework={resolution.TargetFramework}",
				$"Configuration={configuration}"
			)["TargetPath"];
			if (File.Exists(path))
				return path;
		}
		return null;
	}

	/// <summary>The lib/ folder of the first assembly the target compiles against, or null for none.</summary>
	private static string? LibFolder(JsonElement library)
	{
		if (!library.TryGetProperty("compile", out var compile))
			return null;
		foreach (var asset in compile.EnumerateObject())
		{
			var parts = asset.Name.Split('/');
			// "_._" marks a framework the package supports without an assembly
			if (parts.Length == 3 && parts[0] is "lib" or "ref" && parts[2] != "_._")
				return parts[1];
		}
		return null;
	}
}
