#nullable enable
using System;
using System.Collections.Generic;
using System.IO;
using System.Linq;
using Differ.Exporters;
using Differ.Providers;
using Differ.Providers.GitHub;
using Differ.Providers.NuGet;
using Differ.Providers.PreviousNuGet;
using JustAssembly.Core;
using Nullean.Argh;

namespace Differ
{
	internal sealed class DiffCommands
	{
		/// <summary>
		/// Compares and diffs assemblies from different sources, outputting differences in XML,
		/// Markdown, or AsciiDoc.
		/// </summary>
		/// <param name="first">
		/// Old assembly provider: assembly|&lt;path&gt;, directory|&lt;path&gt;, nuget|&lt;id&gt;|&lt;version&gt;|[tfm],
		/// previous-nuget|&lt;id&gt;|&lt;version&gt;|[tfm], or github|&lt;owner/repo&gt;|&lt;commit&gt;|&lt;build command&gt;|&lt;relative output path&gt;.
		/// </param>
		/// <param name="second">New assembly provider, same format as <paramref name="first"/>.</param>
		/// <param name="target">
		/// -t, --target, The assembly targets. Defaults to *all* assemblies located by the provider. May be
		/// given more than once.
		/// </param>
		/// <param name="format">-f, --format, The format of the diff output: xml, markdown, or asciidoc.</param>
		/// <param name="output">-o, --output, The output directory or file name. If not specified only prints to console.</param>
		/// <param name="preventChange">
		/// -p, --prevent-change, Fail if the change detected is higher than specified: none, patch, minor,
		/// or major. Defaults to none, which never fails.
		/// </param>
		/// <param name="allowEmptyPreviousNuget">
		/// -a, --allow-empty-previous-nuget, Don't fail when no previous nuget package could be found to diff against.
		/// </param>
		[DefaultCommand]
		[CommandName("diff")]
		public int Diff(
			[Argument] string first,
			[Argument] string second,
			List<string>? target = null,
			string format = "xml",
			string? output = null,
			string preventChange = "none",
			bool allowEmptyPreviousNuget = false)
		{
			// Enum-typed parameters with a non-zero default member fail to compile against this
			// version of Argh (https://github.com/nullean/argh/issues/75), so `--prevent-change` stays
			// a string and is parsed manually here instead.
			var preventChangeValue = Enum.Parse<SuggestedVersionChange>(preventChange, true);
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

			var outputWriterFactory = new OutputWriterFactory(output);
			var targets = new HashSet<string>(StringComparer.OrdinalIgnoreCase);
			foreach (var t in target ?? [])
			foreach (var part in t.Split(',', '|'))
				if (!string.IsNullOrEmpty(part))
					targets.Add(part);

			try
			{
				var firstProvider = providers.GetProvider(first);
				var secondProvider = providers.GetProvider(second);
				var firstProviderName = providers.GetProviderName(first);
				var secondProviderName = providers.GetProviderName(second);

				if (!exporters.Contains(format))
					throw new Exception($"No exporter for format '{format}'");

				var exporter = exporters[format];

				var firstAssemblies = firstProvider.GetAssemblies(targets).ToList();
				var secondAssemblies = secondProvider.GetAssemblies(targets).ToList();
				var pairs = CreateAssemblyPairs(firstAssemblies, secondAssemblies).ToList();

				if (!pairs.Any())
				{
					if (allowEmptyPreviousNuget &&
						(firstProviderName == "previous-nuget" || secondProviderName == "previous-nuget"))
					{
						Console.WriteLine($"[diff] No previous nuget found for: {firstProviderName}");
						Console.WriteLine($"[diff]   {firstProvider.GetType().Name}: {firstAssemblies.Count} assemblies");
						Console.WriteLine($"[diff]   {secondProvider.GetType().Name}: {secondAssemblies.Count} assemblies");
						Console.WriteLine($"[diff]");
						Console.WriteLine($"[diff]   NOT treated as an error because --allow-empty-previous-nuget was set");
						return 0;
					}

					Console.Error.WriteLine($"[diff] Unable to create diff!");
					Console.Error.WriteLine($"[diff]   {firstProvider.GetType().Name}: {firstAssemblies.Count} assemblies");
					Console.Error.WriteLine($"[diff]   {secondProvider.GetType().Name}: {secondAssemblies.Count} assemblies");
					return 1;
				}

				var result = new AllComparisonResults(pairs, preventChangeValue);
				foreach (var assemblyPair in pairs)
				{
					assemblyPair.Diff =
						APIDiffHelper.GetAPIDifferences(assemblyPair.First.FullName, assemblyPair.Second.FullName);

					if (assemblyPair.Diff == null)
					{
						Console.WriteLine($"[diff] No diff between {assemblyPair.First.FullName} and {assemblyPair.Second.FullName}");
						continue;
					}
					Console.WriteLine($"[diff] Difference found: {firstProvider.GetType().Name}:{assemblyPair.First.Name} and {secondProvider.GetType().Name}:{assemblyPair.Second.Name}");

					if (exporter is IAssemblyComparisonExporter c)
						c.Export(assemblyPair, outputWriterFactory);
				}
				if (exporter is IAllComparisonResultsExporter allExporter)
					allExporter.Export(result, outputWriterFactory);

				if (preventChangeValue > SuggestedVersionChange.None && result.SuggestedVersionChange >= preventChangeValue)
				{
					Console.Error.WriteLine($"[diff] Needed version change '{result.SuggestedVersionChange}' exceeds or equals configured lock: '{preventChangeValue}");
					return 4;
				}
				Console.WriteLine($"[diff] Suggested version change: {result.SuggestedVersionChange}");
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
	}
}
