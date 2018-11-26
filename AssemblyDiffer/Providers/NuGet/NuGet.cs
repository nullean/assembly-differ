using System;
using System.Collections.Generic;
using System.IO;
using System.Linq;
using ProcNet;

namespace Differ.Providers.NuGet
{
	public interface INuGet
	{
		NuGetPackage InstallPackage(string packageName, string version, string targetDirectory);
	}

	public class NuGet : ExecutableBase, INuGet
	{
		private const string NugetExe = "nuget.exe";
		private readonly string[] _sources = { "https://www.nuget.org/api/v2/", "https://api.nuget.org/v3/index.json" };
		private readonly string _executable;

		public NuGet(string executable = null, string sources = null)
		{
			if (!string.IsNullOrEmpty(sources))
				_sources = sources.Split(';');

			if (!string.IsNullOrEmpty(executable))
			{
				if (!File.Exists(executable))
					throw new FileNotFoundException($"{NugetExe} does not exist at {executable}");

				_executable = executable;
			}
			else
				_executable = FindExecutable(NugetExe);
		}

		public NuGetPackage InstallPackage(string packageName, string version, string targetDirectory)
		{
			if (!Directory.Exists(targetDirectory))
				Directory.CreateDirectory(targetDirectory);

			var args = new List<string>
			{
				"install", packageName,
				"-Version", version,
				"-ExcludeVersion", "-NonInteractive"
			};

			foreach (var source in _sources)
			{
				args.Add("-Source");
				args.Add(source);
			}

			var startArguments = new StartArguments(_executable, args)
			{
				WorkingDirectory = targetDirectory
			};

			var processResult = Proc.Start(startArguments);
			if (processResult.ExitCode != 0)
			{
				var lines = string.Join(Environment.NewLine, processResult.ConsoleOut.Select(c => c.Line));
				throw new Exception($"{NugetExe} returned non-zero exit code: {lines}");
			}

			return new NuGetPackage(Path.Combine(targetDirectory, packageName));
		}
	}
}
