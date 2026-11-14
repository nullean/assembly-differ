using System;
using System.Collections.Generic;
using System.IO;
using System.Linq;

namespace Differ.Providers.NuGet;

public class NuGetAssemblyProviderFactory : IAssemblyProviderFactory
{
	protected INuGet Installer { get; }

	public NuGetAssemblyProviderFactory(INuGet installer) => Installer = installer ?? throw new ArgumentNullException(nameof(installer));

	public virtual string Name { get; } = "nuget";

	public string Format => $"{Name}|<package id>|<version>|[framework version]";

	public virtual IAssemblyProvider Create(string[] command) => new NuGetAssemblyProvider(Installer, command, Name);
}

public class NuGetAssemblyProvider : IAssemblyProvider
{
	private readonly INuGet _installer;
	private readonly NuGetDiffCommand _command;

	private readonly string _providerName;

	/// <param name="installer">Downloads and unpacks the package.</param>
	/// <param name="command">The provider spec's parts after the provider name.</param>
	/// <param name="providerName">
	/// The provider's name, which keeps each provider's packages in their own folder: <c>previous-nuget</c> and <c>nuget</c>
	/// for the same version unpack different packages.
	/// </param>
	public NuGetAssemblyProvider(INuGet installer, string[] command, string providerName = "nuget")
	{
		_providerName = providerName;
		_installer = installer ?? throw new ArgumentNullException(nameof(installer));
		_command = new NuGetDiffCommand(command);
	}

	public IEnumerable<FileInfo> GetAssemblies(HashSet<string> targets)
	{
		var tempDir = Scratch.Folder(_providerName);
		if (!Directory.Exists(tempDir))
			Directory.CreateDirectory(tempDir);

		var packageDirectory = Path.Combine(tempDir, _command.Package);

		if (!Directory.Exists(packageDirectory))
			Directory.CreateDirectory(packageDirectory);

		var versionDirectory = Path.Combine(packageDirectory, _command.Version);
		// dependencies are copied next to the package's own assemblies, so a folder from an earlier run is not reused
		if (Directory.Exists(versionDirectory))
			Directory.Delete(versionDirectory, true);
		var package = _installer.InstallPackage(_command.Package, _command.Version, versionDirectory);
		if (package == NuGetPackage.NotFound)
			return Enumerable.Empty<FileInfo>();
		var packageFrameworks = new HashSet<string>(package.FrameworkVersions.Select(f => f.GetShortFolderName()), StringComparer.InvariantCultureIgnoreCase);

		var frameworkVersion = packageFrameworks.FirstOrDefault();
		if (frameworkVersion is null)
		{
			// a package of analyzers or build files only, for one
			Console.WriteLine($"{package.Id} {_command.Version} ships no assemblies under lib/");
			return [];
		}

		if (_command.FrameworkVersion != null)
		{
			if (packageFrameworks.Contains(_command.FrameworkVersion))
				frameworkVersion = _command.FrameworkVersion;
			else
				Console.WriteLine($"{package.Id} does not contain framework " + $"{_command.FrameworkVersion}. Using {frameworkVersion}");
		}

		// dependent assemblies need to copied to the same directory as the target assembly
		CopyAssemblies(package, frameworkVersion);

		IEnumerable<string> GetFiles(string dir)
		{
			if (!Directory.Exists(dir))
				return Enumerable.Empty<string>();
			return Directory.EnumerateFiles(dir, "*.dll");
		}

		var libPath = Path.Combine(package.Path, "lib", frameworkVersion);
		var toolsPath = Path.Combine(package.Path, "tools", frameworkVersion);
		var toolsAnyPath = Path.Combine(package.Path, "tools", frameworkVersion, "any");

		return GetFiles(libPath)
			.Concat(GetFiles(toolsPath))
			.Concat(GetFiles(toolsAnyPath))
			.Where(f => targets.Count == 0 || targets.Contains(Path.GetFileNameWithoutExtension(f)))
			.Select(f => new FileInfo(f));
	}

	private void CopyAssemblies(NuGetPackage package, string frameworkVersion)
	{
		if (package == NuGetPackage.NotFound)
			return;

		foreach (var dependency in package.Dependencies)
		{
			var nearest = dependency.GetNearest(frameworkVersion);
			var dependencyPath = Path.Combine(dependency.Path, "lib", nearest.GetShortFolderName());
			if (!Directory.Exists(dependencyPath))
				continue;

			foreach (var file in Directory.EnumerateFiles(dependencyPath))
			{
				var fileInfo = new FileInfo(file);
				File.Copy(fileInfo.FullName, Path.Combine(package.Path, "lib", frameworkVersion, fileInfo.Name), true);
			}
		}
	}
}
