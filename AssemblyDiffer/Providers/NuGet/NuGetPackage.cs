using System;
using System.Collections.Generic;
using System.IO;
using System.IO.Compression;
using System.Linq;
using System.Xml.Linq;
using NuGet.Frameworks;

namespace Differ.Providers.NuGet
{
	public class NuGetPackage : NuGetPackageBase
	{
		public NuGetPackage(string path) : base(path)
		{
			var directory = new DirectoryInfo(path);
			var parentDirectory = directory.Parent;
			if (parentDirectory != null)
			{
				Dependencies = Directory.GetDirectories(parentDirectory.FullName)
					.Where(d => System.IO.Path.GetFileName(d) != directory.Name)
					.Select(d => new NuGetPackageDependency(d))
					.ToList();
			}
		}

		public IReadOnlyList<INuGetPackage> Dependencies { get; }
	}

	public class NuGetPackageDependency : NuGetPackageBase
	{
		public NuGetPackageDependency(string directory) : base(directory)
		{
		}
	}

	public abstract class NuGetPackageBase : INuGetPackage
	{
		private static readonly FrameworkReducer Reducer = new FrameworkReducer();

		protected NuGetPackageBase(string directory)
		{
			if (directory == null)
				throw new ArgumentNullException(nameof(directory));

			if (!Directory.Exists(directory))
				throw new DirectoryNotFoundException($"{directory} does not exist");

			var packageZip =  Directory.EnumerateFiles(directory, "*.nupkg").First();

			XDocument document;

			using(var file = File.OpenRead(packageZip))
			using (var archive = new ZipArchive(file, ZipArchiveMode.Read, false))
			{
				var entry = archive.Entries.First(e => System.IO.Path.GetExtension(e.FullName) == ".nuspec");
				using(var nuspec = entry.Open())
					document = XDocument.Load(nuspec);
			}

			if (document == null)
				throw new Exception($"No nuspec found in {packageZip}");

			var ns = document.Root.Name.Namespace;
			var metadata = document.Root.Element(ns + "metadata");

			if (metadata == null)
				throw new Exception($"No metadata found in nuspec document in {packageZip}");

			Id = metadata.Element(ns + "id").Value.Trim();
			Version = metadata.Element(ns + "version").Value.Trim();
			Path = directory;

			FrameworkVersions =  Directory.EnumerateDirectories(System.IO.Path.Combine(Path, "lib"))
				.Select(d => NuGetFramework.ParseFolder(System.IO.Path.GetFileName(d)))
				.ToList();
		}

		public string Id { get; }
		public string Version { get; }
		public string Path { get; }
		public IReadOnlyList<NuGetFramework> FrameworkVersions { get; }

		public NuGetFramework GetNearest(string frameworkVersion)
		{
			var framework = NuGetFramework.Parse(frameworkVersion);
			return Reducer.GetNearest(framework, FrameworkVersions);
		}
	}

	public interface INuGetPackage
	{
		string Id { get; }

		string Version { get; }

		string Path { get; }

		IReadOnlyList<NuGetFramework> FrameworkVersions { get; }

		NuGetFramework GetNearest(string frameworkVersion);
	}
}
