using System;
using System.Collections.Generic;
using System.IO;
using System.Linq;
using Differ.Exporters;
using Differ.Providers;
using Differ.Providers.GitHub;
using Differ.Providers.NuGet;
using JustAssembly.Core;
using Mono.Options;

namespace Differ
{
	internal static class Program
	{
		private static string _format = "xml";
		private static bool _help;
		private static string _output = Directory.GetCurrentDirectory();
		private static HashSet<string> _targets;

		private static HashSet<string> Targets =>
			_targets ?? (_targets = new HashSet<string>(StringComparer.OrdinalIgnoreCase));

		private static void Main(string[] args)
		{
			var providers = new AssemblyProviderFactoryCollection(
				new AssemblyProviderFactory(),
				new DirectoryAssemblyProviderFactory(),
				new NuGetAssemblyProviderFactory(new Providers.NuGet.NuGet(
					Environment.GetEnvironmentVariable("NUGET"),
					Environment.GetEnvironmentVariable("NUGET_SOURCES"))),
				new GitHubAssemblyProviderFactory(new Git(Environment.GetEnvironmentVariable("GIT")))
			);

			var exporters = new ExporterCollection(
				new XmlExporter(),
				new MarkdownExporter(),
				new AsciiDocExporter()
			);

			var options = new OptionSet
			{
				{"t|target=", "the assembly targets. Defaults to *all* assemblies located by the provider", AddTarget },
				{"f|format=", $"the format of the diff output. Supported formats are {exporters.SupportedFormats}. Defaults to {_format}", f => _format = f},
				{"o|output=", "the output directory. Defaults to current directory", o => _output = o},
				{"h|?|help", "show this message and exit", h => _help = h != null},
			};

			if (_help)
			{
				ShowHelp(options, providers);
				return;
			}


			if (args.Length < 2)
			{
				ShowHelp(options, providers);
				Environment.ExitCode = 1;
				return;
			}

			List<string> unflaggedArgs;
			try
			{
				unflaggedArgs = options.Parse(args);
			}
			catch (OptionException e)
			{
				Console.WriteLine(e.Message);
				Console.WriteLine("Try 'Differ.exe --help' for more information.");
				Environment.ExitCode = 1;
				return;
			}

			try
			{
				var firstProvider = providers.GetProvider(unflaggedArgs[0]);
				var secondProvider = providers.GetProvider(unflaggedArgs[1]);

				if (!exporters.Contains(_format))
					throw new Exception($"No exporter for format '{_format}'");

				var exporter = exporters[_format];

				foreach (var assemblyPair in CreateAssemblyPairs(firstProvider, secondProvider))
				{
					assemblyPair.Diff =
						APIDiffHelper.GetAPIDifferences(assemblyPair.First.FullName, assemblyPair.Second.FullName);

					if (assemblyPair.Diff == null)
					{
						Console.WriteLine($"No diff between {assemblyPair.First.FullName} and {assemblyPair.Second.FullName}");
						continue;
					}

					exporter.Export(assemblyPair, _output);
				}
			}
			catch (Exception e)
			{
				Console.WriteLine(e);
				Environment.ExitCode = 1;
			}
		}

		private static IEnumerable<AssemblyDiffPair> CreateAssemblyPairs(IAssemblyProvider firstProvider, IAssemblyProvider secondProvider) =>
			firstProvider.GetAssemblies(_targets).Join(secondProvider.GetAssemblies(_targets),
				f => f.Name.ToUpperInvariant(),
				f => f.Name.ToUpperInvariant(),
				(f1, f2) => new AssemblyDiffPair(f1, f2));

		private static void AddTarget(string input)
		{
			if (string.IsNullOrEmpty(input))
				return;

			var parts = input.Split(',', '|');
			foreach (var part in parts)
				Targets.Add(part);
		}

		private static void ShowHelp(OptionSet options, AssemblyProviderFactoryCollection providerFactoryCollection)
		{
			Console.ForegroundColor = ConsoleColor.Green;
			Console.WriteLine("Differ");
			Console.WriteLine("------");
			Console.WriteLine("Diffs assemblies from different sources");
			Console.WriteLine();
			Console.WriteLine("Differ.exe <Old Assembly Provider> <New Assembly Provider> [Options]");
			Console.WriteLine();
			Console.WriteLine("Supported Assembly Providers:");
			Console.WriteLine();
			foreach (var providerFactory in providerFactoryCollection)
				Console.WriteLine($"  {providerFactory.Format}");
			Console.WriteLine();
			Console.WriteLine("Options:");
			options.WriteOptionDescriptions(Console.Out);
			Console.WriteLine();
			Console.ResetColor();
		}
	}
}
