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

	/// <summary>The newest stable version of <paramref name="packageId"/> lower than <paramref name="version"/>, or <c>null</c>.</summary>
	public static NuGetVersion? Find(string packageId, NuGetVersion version)
	{
		NuGetVersion? previous = null;
		using var cache = new SourceCacheContext();
		foreach (var source in Sources())
		{
			var repository = Repository.Factory.GetCoreV3(source);
			var resource = repository.GetResource<FindPackageByIdResource>();
			var versions = resource.GetAllVersionsAsync(packageId, cache, NullLogger.Instance, CancellationToken.None).GetAwaiter().GetResult();
			foreach (var candidate in versions ?? [])
				if (!candidate.IsPrerelease && candidate < version && (previous is null || candidate > previous))
					previous = candidate;
		}
		return previous;
	}
}
