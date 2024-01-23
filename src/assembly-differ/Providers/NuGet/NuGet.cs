using System.Collections.Generic;
using System.IO;
using System.Linq;
using System.Threading;
using NuGet.Common;
using NuGet.Configuration;
using NuGet.Frameworks;
using NuGet.Packaging;
using NuGet.Packaging.Core;
using NuGet.Packaging.Signing;
using NuGet.Protocol.Core.Types;
using NuGet.Resolver;
using NuGet.Versioning;

namespace Differ.Providers.NuGet
{
	public interface INuGet
	{
		NuGetPackage InstallPackage(string packageName, string version, string targetDirectory);
	}

	public class NuGetInstallerOptions
	{
		public string NuGetConfigSearchDirectory { get; set; }
	}

	public class NuGet : INuGet
	{
		protected NuGetInstallerOptions Options { get; }

		public NuGet(NuGetInstallerOptions options)
			=> Options = options;

		public virtual NuGetPackage InstallPackage(string packageName, string version, string targetDirectory)
		{
			if (!Directory.Exists(targetDirectory)) Directory.CreateDirectory(targetDirectory);

			var packageVersion = NuGetVersion.Parse(version);
			var nuGetFramework = NuGetFramework.AnyFramework;
			var settings = Settings.LoadDefaultSettings(root: Options?.NuGetConfigSearchDirectory);
			var sourceRepositoryProvider = new SourceRepositoryProvider(
				new PackageSourceProvider(settings), Repository.Provider.GetCoreV3());

			using (var cacheContext = new SourceCacheContext())
			{
				var repositories = sourceRepositoryProvider.GetRepositories();
				var availablePackages = new HashSet<SourcePackageDependencyInfo>(PackageIdentityComparer.Default);
				GetPackageDependencies(
					new PackageIdentity(packageName, packageVersion),
					nuGetFramework, cacheContext, NullLogger.Instance, repositories, availablePackages);

				var resolverContext = new PackageResolverContext(
					DependencyBehavior.Lowest,
					new[] {packageName},
					Enumerable.Empty<string>(),
					Enumerable.Empty<PackageReference>(),
					Enumerable.Empty<PackageIdentity>(),
					availablePackages,
					sourceRepositoryProvider.GetRepositories().Select(s => s.PackageSource),
					NullLogger.Instance);

				var resolver = new PackageResolver();
				var packagesToInstall = resolver.Resolve(resolverContext, CancellationToken.None)
					.Select(p => availablePackages.Single(x => PackageIdentityComparer.Default.Equals(x, p)));
				var packagePathResolver = new PackagePathResolver(Path.GetFullPath(targetDirectory));
				var packageExtractionContext = new PackageExtractionContext(
					PackageSaveMode.Nuspec | PackageSaveMode.Files | PackageSaveMode.Nupkg,
					XmlDocFileSaveMode.None,
					ClientPolicyContext.GetClientPolicy(settings, NullLogger.Instance),
					NullLogger.Instance);
				var downloadContext = new PackageDownloadContext(cacheContext);

				foreach (var packageToInstall in packagesToInstall)
				{
					var installedPath = packagePathResolver.GetInstalledPath(packageToInstall);
					if (installedPath != null) continue;
					var downloadResource = packageToInstall.Source
						.GetResourceAsync<DownloadResource>(CancellationToken.None).GetAwaiter().GetResult();
					var downloadResult = downloadResource.GetDownloadResourceResultAsync(
						packageToInstall,
						downloadContext,
						SettingsUtility.GetGlobalPackagesFolder(settings),
						NullLogger.Instance, CancellationToken.None).GetAwaiter().GetResult();

					PackageExtractor.ExtractPackageAsync(
						downloadResult.PackageSource,
						downloadResult.PackageStream,
						packagePathResolver,
						packageExtractionContext,
						CancellationToken.None).GetAwaiter().GetResult();
				}
			}

			return new NuGetPackage(Path.Combine(targetDirectory, $"{packageName}.{version}"));
		}

		public static void GetPackageDependencies(PackageIdentity package,
			NuGetFramework framework,
			SourceCacheContext cacheContext,
			ILogger logger,
			IEnumerable<SourceRepository> repositories,
			ISet<SourcePackageDependencyInfo> availablePackages)
		{
			if (availablePackages.Contains(package)) return;

			foreach (var sourceRepository in repositories)
			{
				var dependencyInfoResource = sourceRepository.GetResource<DependencyInfoResource>();
				var dependencyInfo = dependencyInfoResource.ResolvePackage(
					package, framework, cacheContext, logger, CancellationToken.None).GetAwaiter().GetResult();

				if (dependencyInfo == null) continue;

				availablePackages.Add(dependencyInfo);
				foreach (var dependency in dependencyInfo.Dependencies)
				{
					GetPackageDependencies(
						new PackageIdentity(dependency.Id, dependency.VersionRange.MinVersion),
						framework, cacheContext, logger, repositories, availablePackages);
				}
			}
		}
	}
}
