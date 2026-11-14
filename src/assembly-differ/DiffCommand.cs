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
using Differ.Engine;
using Nullean.Argh;

namespace Differ;

internal sealed class DiffCommands
{
	/// <summary>
	/// Compares the public API of two versions of a library and suggests the version change it calls for. Pick a command
	/// for where the versions come from; diff with provider specs, such as nuget|Proc|0.13.0, still works for existing builds.
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
	/// <param name="format">-f, --format, The format of the diff output: xml, markdown, asciidoc, github-comment or json.</param>
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
	[Hidden]
	public int Diff(
		[Argument] string first,
		[Argument] string second,
		List<string>? target = null,
		string format = "xml",
		string? output = null,
		string preventChange = "none",
		bool allowEmptyPreviousNuget = false
	)
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
			return DiffRunner.Run(
				new DiffSide(firstProvider, firstProviderName),
				new DiffSide(secondProvider, secondProviderName),
				new DiffSettings(targets, format, output, preventChangeValue, allowEmptyPreviousNuget)
			);
		}
		catch (Exception e)
		{
			Console.Error.WriteLine(e);
			return 1;
		}
	}
}
