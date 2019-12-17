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
				metadata.GetMetadataAsync(currentVersion, false, false, cacheContext, NullLogger.Instance, CancellationToken.None)
					.GetAwaiter().GetResult();

			var previousPackage =
				searchMetadata
					.Cast<PackageSearchMetadata>()
					.OrderByDescending(p => p.Version)
					.FirstOrDefault(p => p.Version < nugetVersion);

			if (previousPackage == null) return NuGetPackage.Skip;

			return base.InstallPackage(packageName, previousPackage.Version.ToString(), targetDirectory);
		}
	}
}
