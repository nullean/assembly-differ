using System;
using System.Collections.Generic;
using System.IO;
using System.Linq;
using System.Reflection.Metadata;
using Differ.Exporters;
using Differ.Providers;
using Differ.Providers.GitHub;
using Differ.Providers.NuGet;
using Differ.Providers.PreviousNuGet;
using JustAssembly.Core;
using Mono.Options;

namespace Differ
{
	internal static class Program
	{
		private static string _format = "xml";
		private static bool _help;
		private static string _output = Directory.GetCurrentDirectory();
		private static SuggestedVersionChange _preventChange = SuggestedVersionChange.None;

		private static HashSet<string> Targets { get; } = new HashSet<string>(StringComparer.OrdinalIgnoreCase);

		private static int Main(string[] args)
		{
			var providers = new AssemblyProviderFactoryCollection(
				new AssemblyProviderFactory(),
				new DirectoryAssemblyProviderFactory(),
				new NuGetAssemblyProviderFactory(new Providers.NuGet.NuGet()),
				new PreviousNuGetAssemblyProviderFactory(new PreviousNugetLocator()),
				new GitHubAssemblyProviderFactory(new Git(Environment.GetEnvironmentVariable("GIT")))
			);

			var exporters = new ExporterCollection(
				new XmlExporter(),
				new MarkdownExporter(),
				new AsciiDocExporter(),
				new GitHubActionCommentExporter()
			);

			var options = new OptionSet
			{
				{"t|target=", "the assembly targets. Defaults to *all* assemblies located by the provider", AddTarget },
				{"f|format=", $"the format of the diff output. Supported formats are {exporters.SupportedFormats}. Defaults to {_format}", f => _format = f},
				{"o|output=", "the output directory. Defaults to current directory", o => _output = o},
				{"p|prevent-change=", "Fail if the change detected is higher then specified: none|patch|minor|major. Defaults to `none` which will never fail.",
					c => _preventChange = Enum.Parse<SuggestedVersionChange>(c, true)},
				{"h|?|help", "show this message and exit", h => _help = h != null},
			};

			if (_help)
			{
				ShowHelp(options, providers);
				return 2;
			}


			if (args.Length < 2)
			{
				ShowHelp(options, providers);
				return 2;
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
				return 2;
			}

			try
			{
				var firstProvider = providers.GetProvider(unflaggedArgs[0]);
				var secondProvider = providers.GetProvider(unflaggedArgs[1]);

				if (!exporters.Contains(_format))
					throw new Exception($"No exporter for format '{_format}'");

				var exporter = exporters[_format];

				var first = firstProvider.GetAssemblies(Targets).ToList();
				var second = secondProvider.GetAssemblies(Targets).ToList();
				var pairs = CreateAssemblyPairs(first, second).ToList();

				if (!pairs.Any())
				{
					Console.Error.WriteLine($"Unable to create diff!");
					Console.Error.WriteLine($" {firstProvider.GetType().Name}: {first.Count()} assemblies");
					Console.Error.WriteLine($" {secondProvider.GetType().Name}: {second.Count()} assemblies");
					return 1;
				}

				var result = new AllComparisonResults(pairs);
				foreach (var assemblyPair in pairs)
				{
					assemblyPair.Diff =
						APIDiffHelper.GetAPIDifferences(assemblyPair.First.FullName, assemblyPair.Second.FullName);

					if (assemblyPair.Diff == null)
					{
						Console.WriteLine($"No diff between {assemblyPair.First.FullName} and {assemblyPair.Second.FullName}");
						continue;
					}
					Console.WriteLine($"Difference found: {firstProvider.GetType().Name}:{assemblyPair.First.Name} and {secondProvider.GetType().Name}:{assemblyPair.Second.Name}");

					if (exporter is IAssemblyComparisonExporter c)
						c.Export(assemblyPair, _output);
				}
				if (exporter is IAllComparisonResultsExporter allExporter)
					allExporter.Export(result, _output);

				if (_preventChange > SuggestedVersionChange.None && result.SuggestedVersionChange >= _preventChange)
				{
					Console.Error.WriteLine($"Needed version change '{result.SuggestedVersionChange}' exceeds or equals configured lock: '{_preventChange}");
					return 4;
				}
				Console.WriteLine($"Suggested version change: {result.SuggestedVersionChange}");

			}
			catch (Exception e)
			{
				Console.Error.WriteLine(e);
				return 1;
			}
			return 0;
		}

		private static IEnumerable<AssemblyComparison> CreateAssemblyPairs(IEnumerable<FileInfo> first, IEnumerable<FileInfo> second) =>
			first.Join(second,
				f => f.Name.ToUpperInvariant(),
				f => f.Name.ToUpperInvariant(),
				(f1, f2) => new AssemblyComparison(f1, f2));

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
