using System;
using System.Collections.Generic;
using System.Linq;
using System.Threading;
using Differ.Providers.NuGet;
using NuGet.Common;
using NuGet.Configuration;
using NuGet.Protocol;
using NuGet.Protocol.Core.Types;
using NuGet.Versioning;

namespace Differ.Providers.PreviousNuGet;

public class PreviousNugetLocator : Differ.Providers.NuGet.NuGet
{
	public override NuGetPackage InstallPackage(string packageName, string currentVersion, string targetDirectory)
	{
		var nugetVersion = new NuGetVersion(currentVersion);
		var previousVersion = PreviousRelease.Find(packageName, nugetVersion);
		if (previousVersion == null)
		{
			Console.WriteLine($"No previous nuget package found for {packageName}: {currentVersion}");
			// return not found, we don't immediately want to error on this in CI
			// might introduce a flag to fail later on
			return NuGetPackage.NotFound;
		}
		Console.WriteLine($"Found previous nuget package found for {packageName} {currentVersion}: {previousVersion}");

		return base.InstallPackage(packageName, previousVersion.ToString(), targetDirectory);
	}
}
