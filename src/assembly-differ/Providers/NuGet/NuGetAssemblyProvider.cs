using System;
using System.Collections.Generic;
using System.IO;
using System.Linq;

namespace Differ.Providers.NuGet
{
	public class NuGetAssemblyProviderFactory : IAssemblyProviderFactory
	{
		protected INuGet Installer { get; }

		public NuGetAssemblyProviderFactory(INuGet installer) =>
			Installer = installer ?? throw new ArgumentNullException(nameof(installer));

		public virtual string Name { get; } = "nuget";

		public string Format => $"{Name}|<package id>|<version>|[framework version]";

		public virtual IAssemblyProvider Create(string[] command) =>
			new NuGetAssemblyProvider(Installer, command);
	}

	public class NuGetAssemblyProvider : IAssemblyProvider
	{
		private readonly INuGet _installer;
		private readonly NuGetDiffCommand _command;

		public NuGetAssemblyProvider(INuGet installer, string[] command)
		{
			_installer = installer ?? throw new ArgumentNullException(nameof(installer));
			_command = new NuGetDiffCommand(command);
		}

		public IEnumerable<FileInfo> GetAssemblies(HashSet<string> targets)
		{
			var tempDir = Path.Combine(_command.TempDir, "differ", "nuget");
			if (!Directory.Exists(tempDir))
				Directory.CreateDirectory(tempDir);


			var packageDirectory = Path.Combine(tempDir, _command.Package);

			if (!Directory.Exists(packageDirectory))
				Directory.CreateDirectory(packageDirectory);

			var versionDirectory = Path.Combine(packageDirectory, _command.Version);
			var package = _installer.InstallPackage(_command.Package, _command.Version, versionDirectory);
			if (package == NuGetPackage.NotFound) return Enumerable.Empty<FileInfo>();
			var packageFrameworks =
				new HashSet<string>(package.FrameworkVersions.Select(f => f.GetShortFolderName()), StringComparer.InvariantCultureIgnoreCase);

			var frameworkVersion = packageFrameworks.First();

			if (_command.FrameworkVersion != null)
			{
				if (packageFrameworks.Contains(_command.FrameworkVersion))
					frameworkVersion = _command.FrameworkVersion;
				else
					Console.WriteLine($"{package.Id} does not contain framework " +
					                  $"{_command.FrameworkVersion}. Using {frameworkVersion}");
			}

			// dependent assemblies need to copied to the same directory as the target assembly
			CopyAssemblies(package, frameworkVersion);

			IEnumerable<string> GetFiles(string dir)
			{
				if (!Directory.Exists(dir)) return Enumerable.Empty<string>();
				return Directory.EnumerateFiles(dir, "*.dll");
			}
			var libPath = Path.Combine(package.Path, "lib", frameworkVersion);
			var toolsPath = Path.Combine(package.Path, "tools", frameworkVersion);
			var toolsAnyPath = Path.Combine(package.Path, "tools", frameworkVersion, "any");

			return GetFiles(libPath).Concat(GetFiles(toolsPath)).Concat(GetFiles(toolsAnyPath))
				.Where(f => targets.Count == 0 || targets.Contains(Path.GetFileNameWithoutExtension(f)))
				.Select(f => new FileInfo(f));
		}

		private void CopyAssemblies(NuGetPackage package, string frameworkVersion)
		{
			if (package == NuGetPackage.NotFound) return;

			foreach (var dependency in package.Dependencies)
			{
				var nearest = dependency.GetNearest(frameworkVersion);
				var dependencyPath = Path.Combine(dependency.Path, "lib", nearest.GetShortFolderName());

				foreach (var file in Directory.EnumerateFiles(dependencyPath))
				{
					var fileInfo = new FileInfo(file);
					File.Copy(fileInfo.FullName, Path.Combine(package.Path, "lib", frameworkVersion, fileInfo.Name), true);
				}
			}
		}
	}
}
