using System;
using System.Collections.Generic;
using System.IO;
using System.Linq;
using System.Threading;
using NuGet.Common;
using NuGet.Configuration;
using NuGet.Protocol;
using NuGet.Protocol.Core.Types;
using NuGet.Versioning;

namespace Differ.Providers.PreviousNuGet;

/// <summary>The newest stable release of a package before a given version, on the feeds NuGet.config lists.</summary>
public static class PreviousRelease
{
	/// <summary>
	/// The NuGet settings for the current directory: its NuGet.config files up to the root, then the user's and the
	/// machine's, the way <c>dotnet restore</c> reads them.
	/// </summary>
	public static ISettings Settings() => global::NuGet.Configuration.Settings.LoadDefaultSettings(Directory.GetCurrentDirectory());

	/// <summary>The enabled package sources, by name, from <see cref="Settings"/>.</summary>
	public static IReadOnlyList<PackageSource> Sources() => new PackageSourceProvider(Settings()).LoadPackageSources().Where(s => s.IsEnabled).ToList();

	/// <summary>
	/// The newest stable, listed version of <paramref name="packageId"/> lower than <paramref name="version"/>, or
	/// <c>null</c>. An unlisted version is one its owner took off the feed's search results, such as a mistaken
	/// publish, and is never the release a new build is compared with.
	/// </summary>
	public static NuGetVersion? Find(string packageId, NuGetVersion version)
	{
		NuGetVersion? previous = null;
		using var cache = new SourceCacheContext();
		foreach (var source in Sources())
		{
			var repository = Repository.Factory.GetCoreV3(source);
			foreach (var candidate in ListedVersions(repository, packageId, cache))
				if (!candidate.IsPrerelease && candidate < version && (previous is null || candidate > previous))
					previous = candidate;
		}
		return previous;
	}

	private static IEnumerable<NuGetVersion> ListedVersions(SourceRepository repository, string packageId, SourceCacheContext cache)
	{
		// the flat container lists every version, unlisted ones too, so the listing comes from the registration
		var metadata = TryGetMetadata(repository);
		if (metadata is not null)
		{
			var packages = metadata
				.GetMetadataAsync(packageId, includePrerelease: false, includeUnlisted: false, cache, NullLogger.Instance, CancellationToken.None)
				.GetAwaiter()
				.GetResult();
			return packages.Select(p => p.Identity.Version);
		}

		var resource = repository.GetResource<FindPackageByIdResource>();
		return resource.GetAllVersionsAsync(packageId, cache, NullLogger.Instance, CancellationToken.None).GetAwaiter().GetResult() ?? [];
	}

	// a v3 feed without a registration resource throws, as it did not before: list its flat container as it was
	private static PackageMetadataResource? TryGetMetadata(SourceRepository repository)
	{
		try
		{
			return repository.GetResource<PackageMetadataResource>();
		}
		catch (Exception e) when (e is InvalidOperationException or AggregateException { InnerException: InvalidOperationException })
		{
			return null;
		}
	}
}
