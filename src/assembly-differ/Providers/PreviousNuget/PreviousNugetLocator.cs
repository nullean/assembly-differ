using System;
using System.Collections.Generic;
using System.Linq;
using System.Threading;
using NuGet.Common;
using NuGet.Configuration;
using NuGet.Protocol;
using NuGet.Protocol.Core.Types;
using NuGet.Versioning;
using Differ.Providers.NuGet;

namespace Differ.Providers.PreviousNuGet
{
	public class PreviousNugetLocator : Differ.Providers.NuGet.NuGet
	{
		public PreviousNugetLocator(NuGetInstallerOptions options) : base(options)
		{}

		public override NuGetPackage InstallPackage(string packageName, string currentVersion, string targetDirectory)
		{
			var nugetVersion = new NuGetVersion(currentVersion);
			var providers = new List<Lazy<INuGetResourceProvider>>();
			providers.AddRange(Repository.Provider.GetCoreV3());
			var packageSource = new PackageSource("https://api.nuget.org/v3/index.json");
			var sourceRepository = new SourceRepository(packageSource, providers);
			var metadata = sourceRepository.GetResource<PackageMetadataResource>();
			using var cacheContext = new SourceCacheContext();
			var searchMetadata =
				metadata.GetMetadataAsync(packageName, false, false, cacheContext, NullLogger.Instance, CancellationToken.None)
					.GetAwaiter().GetResult().ToList();

			Console.WriteLine($"Found {searchMetadata.Count} packages for {packageName}");
			var previousPackage =
				searchMetadata
					.Cast<PackageSearchMetadata>()
					.OrderByDescending(p => p.Version)
					.FirstOrDefault(p => p.Version < nugetVersion);

			if (previousPackage == null)
			{
				Console.WriteLine($"No previous nuget package found for {packageName}: {currentVersion}");
				// return not found, we don't immediately want to error on this in CI
				// might introduce a flag to fail later on
				return NuGetPackage.NotFound;
			}
			Console.WriteLine($"Found previous nuget package found for {packageName} {currentVersion}: {previousPackage.Version}");

			return base.InstallPackage(packageName, previousPackage.Version.ToString(), targetDirectory);
		}
	}
}
