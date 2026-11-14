using System;
using System.Collections.Generic;
using System.IO;
using System.Linq;
using NuGet.Frameworks;
using NuGet.Packaging;

namespace Differ.Providers;

/// <summary>The assemblies a <c>.nupkg</c> file ships for one target framework, unpacked into a folder of their own.</summary>
public class NupkgAssemblyProvider : IAssemblyProvider
{
	private readonly string _path;
	private readonly NuGetFramework _framework;

	public NupkgAssemblyProvider(string path, NuGetFramework framework)
	{
		if (!File.Exists(path))
			throw new FileNotFoundException($"no package found at {path}");
		_path = path;
		_framework = framework;
	}

	public IEnumerable<FileInfo> GetAssemblies(HashSet<string> targets)
	{
		var directory = Scratch.Folder("nupkg", Path.GetFileNameWithoutExtension(_path), _framework.GetShortFolderName());

		using var reader = new PackageArchiveReader(_path);
		var group = reader.GetLibItems().FirstOrDefault(g => g.TargetFramework.Equals(_framework));
		if (group is null)
			return [];

		var assemblies = new List<FileInfo>();
		foreach (var item in group.Items.Where(i => i.EndsWith(".dll", StringComparison.OrdinalIgnoreCase)))
		{
			if (targets.Count > 0 && !targets.Contains(Path.GetFileNameWithoutExtension(item)))
				continue;
			var destination = Path.Combine(directory, Path.GetFileName(item));
			using (var source = reader.GetStream(item))
			using (var file = File.Create(destination))
				source.CopyTo(file);
			assemblies.Add(new FileInfo(destination));
		}
		return assemblies;
	}
}

/// <summary>What a <c>.nupkg</c> file says about itself.</summary>
public static class Nupkg
{
	public static bool IsPackage(string path) => path.EndsWith(".nupkg", StringComparison.OrdinalIgnoreCase);

	public static (string Id, string Version) Identity(string path)
	{
		using var reader = new PackageArchiveReader(path);
		var identity = reader.GetIdentity();
		return (identity.Id, identity.Version.ToNormalizedString());
	}

	/// <summary>The target frameworks the package ships assemblies for: .NET first, then .NET Standard, then .NET Framework, newest first.</summary>
	public static IReadOnlyList<NuGetFramework> Frameworks(string path)
	{
		using var reader = new PackageArchiveReader(path);
		return Order(reader.GetLibItems().Where(g => g.Items.Any(i => i.EndsWith(".dll", StringComparison.OrdinalIgnoreCase))).Select(g => g.TargetFramework));
	}

	/// <summary>The framework <see cref="Frameworks"/> would list first.</summary>
	public static NuGetFramework Newest(IEnumerable<NuGetFramework> frameworks) => Order(frameworks)[0];

	private static List<NuGetFramework> Order(IEnumerable<NuGetFramework> frameworks) =>
		frameworks
			.OrderByDescending(
				f =>
					f.Framework switch
					{
						FrameworkConstants.FrameworkIdentifiers.NetCoreApp => 3,
						FrameworkConstants.FrameworkIdentifiers.NetStandard => 2,
						FrameworkConstants.FrameworkIdentifiers.Net => 1,
						_ => 0,
					}
			)
			.ThenByDescending(f => f.Version)
			.ToList();
}
