#nullable enable
using System;
using System.Collections.Generic;
using System.ComponentModel.DataAnnotations;
using System.IO;
using System.Linq;
using System.Text;
using Differ.Projects;
using Differ.Providers;
using NuGet.Frameworks;
using Nullean.Argh;

namespace Differ.ApiFiles;

/// <summary>Writes the public API of a build as .api files, and checks committed ones against a build.</summary>
internal sealed class ApiFileCommand
{
	/// <summary>The exit code of --verify when the committed files do not match the build.</summary>
	public const int Stale = 5;

	/// <summary>Writes the public API of a build as .api files, or checks committed ones against it.</summary>
	/// <remarks>
	/// A .api file is the API as C#: an indented tree of namespaces, types and members, one declaration per line, with a
	/// header that names the types from other assemblies. Commit them next to the code and a change to the API shows in
	/// the pull request as a change to the file; --verify fails the build when they are out of date. Without a path it
	/// reads the solution or projects in the current directory, as the projects command does, and takes each library's
	/// Release build for every framework it targets; it does not build, so run dotnet build -c Release first. An assembly
	/// whose frameworks have the same API gets one file; otherwise one per framework, such as MyLib.net8.0.api.
	/// </remarks>
	/// <param name="path">A .dll, a directory of them, or a .nupkg. Defaults to the libraries in the current directory.</param>
	/// <param name="output">-o, Write the files to this directory, and remove the .api files there the API no longer has.</param>
	/// <param name="verify">Check that the .api files in this directory match the build, and exit with 5 when they do not.</param>
	/// <param name="split">Split each API into a file per namespace, or per namespace's first two parts, such as System.Collections, for large assemblies.</param>
	/// <param name="format">-f, text writes .api files; json prints the API with every part of each declaration as a field.</param>
	/// <param name="target">-t, Only these assemblies, by file name without extension. Repeat it for more than one.</param>
	/// <param name="framework">Only this target framework, such as net8.0.</param>
	/// <param name="project">Read this project file, whatever kind it is. Repeat it for more than one.</param>
	/// <param name="includeTests">Read test projects too.</param>
	/// <param name="includeUnpackable">Read projects with IsPackable set to false too.</param>
	/// <example>dotnet build -c Release &amp;&amp; assembly-differ api -o api</example>
	/// <example>assembly-differ api --verify api</example>
	/// <example>assembly-differ api bin/Release/net8.0/MyLib.dll</example>
	/// <example>assembly-differ api MyLib.1.2.0.nupkg --split namespace -o api</example>
	[CommandName("api")]
	public int Api(
		[Argument] string? path = null,
		string? output = null,
		string? verify = null,
		[AllowedValues("none", "namespace", "top-level-namespace")] string split = "none",
		[AllowedValues("text", "json")] string format = "text",
		List<string>? target = null,
		string? framework = null,
		List<string>? project = null,
		bool includeTests = false,
		bool includeUnpackable = false
	)
	{
		var splitting = split switch
		{
			"namespace" => Nullean.PublicApi.ApiSplit.Namespace,
			"top-level-namespace" => Nullean.PublicApi.ApiSplit.TopLevelNamespace,
			_ => Nullean.PublicApi.ApiSplit.None,
		};
		try
		{
			if (format == "json" && (output is not null || verify is not null))
				throw new ArgumentException("json is printed; -o and --verify work with .api files, the text format");
			if (output is not null && verify is not null)
				throw new ArgumentException("pass -o to write the files, or --verify to check them, not both");

			var targets = new HashSet<string>(target ?? [], StringComparer.OrdinalIgnoreCase);
			var builds = Builds(path, framework, targets, new ProjectComparison.Options(project ?? [], includeTests, includeUnpackable));
			if (builds.Count == 0)
			{
				Console.Error.WriteLine("[api] no assembly to read");
				return 1;
			}
			var assemblies = Read(builds);

			if (format == "json")
			{
				Console.WriteLine(ApiJson.Write(assemblies.Select(a => a.Api).ToList()));
				return 0;
			}

			var files = Files(assemblies, splitting);
			if (verify is not null)
				return Verify.Run(verify, files, assemblies, UpdateCommand(path, split, target, framework, project, includeTests, includeUnpackable, verify));
			if (output is not null)
			{
				Write(output, files);
				return 0;
			}
			foreach (var file in files)
			{
				if (files.Count > 1)
					Console.WriteLine($"// {file.Key}");
				Console.Write(file.Value);
			}
			return 0;
		}
		catch (Exception e) when (e is ArgumentException or FileNotFoundException or DirectoryNotFoundException or Dotnet.SdkException or Nullean.PublicApi.ApiParseException)
		{
			Console.Error.WriteLine($"[api] {e.Message}");
			return 1;
		}
	}

	/// <summary>An assembly to read, for a framework if it is known.</summary>
	internal sealed record Build(string AssemblyName, string? Framework, string Path);

	/// <summary>An assembly's API, for each framework that has it, under the file name it is written to.</summary>
	internal sealed record Assembly(string FileName, Nullean.PublicApi.ApiAssembly Api);

	private static List<Build> Builds(string? path, string? framework, HashSet<string> targets, ProjectComparison.Options options)
	{
		bool Wanted(string name) => targets.Count == 0 || targets.Contains(name);

		var chosen = framework is null ? null : NuGetFramework.ParseFolder(framework);

		if (path is null)
		{
			var directory = Directory.GetCurrentDirectory();
			var candidates = ProjectComparison.Candidates(directory, options.Projects);
			if (candidates.Count == 0)
				throw new ArgumentException($"no solution or project in {directory}; pass a path or --project (see --help)");
			var builds = new List<Build>();
			foreach (var library in ProjectComparison.Libraries(candidates, options, "includes"))
			{
				if (!Wanted(library.AssemblyName))
					continue;
				foreach (var tfm in library.Frameworks.Where(f => chosen is null || f.Equals(chosen)))
				{
					var targetPath = Dotnet.Properties(
						library.Path,
						["TargetPath"],
						$"TargetFramework={tfm.GetShortFolderName()}",
						"Configuration=Release"
					)["TargetPath"];
					if (!File.Exists(targetPath))
					{
						Console.Error.WriteLine(
							$"[api] warning: skipping {library.AssemblyName} ({tfm.GetShortFolderName()}), which has no build output at {targetPath}; build it first with: dotnet build -c Release"
						);
						continue;
					}
					builds.Add(new Build(library.AssemblyName, tfm.GetShortFolderName(), targetPath));
				}
			}
			return builds;
		}

		if (Nupkg.IsPackage(path))
		{
			var frameworks = chosen is null ? Nupkg.Frameworks(path) : [chosen];
			return frameworks.SelectMany(
				f => new NupkgAssemblyProvider(path, f)
					.GetAssemblies(targets)
					.Select(a => new Build(System.IO.Path.GetFileNameWithoutExtension(a.Name), f.GetShortFolderName(), a.FullName))
			).ToList();
		}
		if (Directory.Exists(path))
			return new DirectoryAssemblyProvider(path)
				.GetAssemblies(targets)
				.Select(a => new Build(System.IO.Path.GetFileNameWithoutExtension(a.Name), framework, a.FullName))
				.ToList();
		if (!File.Exists(path))
			throw new FileNotFoundException($"no .dll, directory or .nupkg at {path}");
		return [new Build(System.IO.Path.GetFileNameWithoutExtension(path), framework, path)];
	}

	/// <summary>
	/// Reads every build, and names each API: the assembly's name when all its frameworks have the same API, otherwise the
	/// assembly's name and the framework.
	/// </summary>
	private static List<Assembly> Read(List<Build> builds)
	{
		var assemblies = new List<Assembly>();
		foreach (var group in builds.GroupBy(b => b.AssemblyName, StringComparer.OrdinalIgnoreCase).OrderBy(g => g.Key, StringComparer.Ordinal))
		{
			var read = new List<(Build Build, Nullean.PublicApi.ApiAssembly Api, string Text)>();
			foreach (var build in group)
			{
				try
				{
					var api = Nullean.PublicApi.ApiReader.Read(build.Path);
					read.Add((build, api, Nullean.PublicApi.ApiWriter.Write(api)));
				}
				catch (Exception e) when (e is BadImageFormatException or InvalidOperationException)
				{
					// a native library, or a file that is not an assembly
					Console.Error.WriteLine($"[api] warning: skipping {build.Path}, which is not a .NET assembly");
				}
			}
			if (read.Count == 0)
				continue;
			if (read.Select(r => r.Text).Distinct().Count() == 1)
				assemblies.Add(new Assembly(group.Key, read[0].Api));
			else
				foreach (var (build, api, _) in read)
					assemblies.Add(new Assembly($"{group.Key}.{build.Framework}", api));
		}
		return assemblies;
	}

	/// <summary>The files to write, by path relative to the output directory: <c>MyLib.api</c>, or <c>MyLib/System.Collections.api</c> when split.</summary>
	internal static SortedDictionary<string, string> Files(IEnumerable<Assembly> assemblies, Nullean.PublicApi.ApiSplit split)
	{
		var files = new SortedDictionary<string, string>(StringComparer.Ordinal);
		foreach (var assembly in assemblies)
		{
			if (split == Nullean.PublicApi.ApiSplit.None)
				files[assembly.FileName + ".api"] = Nullean.PublicApi.ApiWriter.Write(assembly.Api);
			else
				foreach (var file in Nullean.PublicApi.ApiWriter.Write(assembly.Api, split))
					files[assembly.FileName + "/" + file.Name + ".api"] = file.Text;
		}
		return files;
	}

	private static void Write(string output, SortedDictionary<string, string> files)
	{
		Directory.CreateDirectory(output);
		foreach (var stale in Existing(output).Keys.Where(k => !files.ContainsKey(k)))
		{
			File.Delete(System.IO.Path.Combine(output, stale));
			Console.WriteLine($"[api] Removed {System.IO.Path.Combine(output, stale)}");
		}
		foreach (var (relative, text) in files)
		{
			var file = System.IO.Path.Combine(output, relative);
			Directory.CreateDirectory(System.IO.Path.GetDirectoryName(file)!);
			var changed = !File.Exists(file) || File.ReadAllText(file) != text;
			if (changed)
				File.WriteAllText(file, text, new UTF8Encoding(false));
			Console.WriteLine($"[api] {(changed ? "Wrote" : "Unchanged")} {file}");
		}
	}

	/// <summary>The .api files under a directory, by relative path with forward slashes; only files this command writes, which start with "assembly ".</summary>
	internal static SortedDictionary<string, string> Existing(string directory)
	{
		var files = new SortedDictionary<string, string>(StringComparer.Ordinal);
		if (!Directory.Exists(directory))
			return files;
		foreach (var file in Directory.EnumerateFiles(directory, "*.api", SearchOption.AllDirectories))
		{
			var text = File.ReadAllText(file);
			if (text.StartsWith("assembly ", StringComparison.Ordinal))
				files[System.IO.Path.GetRelativePath(directory, file).Replace('\\', '/')] = text;
		}
		return files;
	}

	private static string UpdateCommand(
		string? path,
		string split,
		List<string>? target,
		string? framework,
		List<string>? project,
		bool includeTests,
		bool includeUnpackable,
		string directory
	)
	{
		var command = new StringBuilder("assembly-differ api");
		if (path is not null)
			command.Append(' ').Append(path);
		if (split != "none")
			command.Append(" --split ").Append(split);
		foreach (var name in target ?? [])
			command.Append(" -t ").Append(name);
		if (framework is not null)
			command.Append(" --framework ").Append(framework);
		foreach (var file in project ?? [])
			command.Append(" --project ").Append(file);
		if (includeTests)
			command.Append(" --include-tests");
		if (includeUnpackable)
			command.Append(" --include-unpackable");
		return command.Append(" -o ").Append(directory).ToString();
	}
}
