using System;
using System.Collections.Generic;
using System.ComponentModel;
using System.Diagnostics;
using System.Text;
using System.Text.Json;

namespace Differ.Projects;

/// <summary>Runs the .NET SDK: <c>dotnet sln list</c>, <c>dotnet msbuild -getProperty</c> and <c>dotnet build</c>.</summary>
internal static class Dotnet
{
	public sealed class SdkException(string message) : Exception(message);

	public static (int ExitCode, string Output) Run(params string[] args)
	{
		var start = new ProcessStartInfo("dotnet")
		{
			RedirectStandardOutput = true,
			RedirectStandardError = true,
			UseShellExecute = false,
			StandardOutputEncoding = Encoding.UTF8,
			StandardErrorEncoding = Encoding.UTF8,
		};
		foreach (var arg in args)
			start.ArgumentList.Add(arg);
		start.Environment["DOTNET_NOLOGO"] = "1";
		start.Environment["DOTNET_CLI_TELEMETRY_OPTOUT"] = "1";
		try
		{
			using var process = Process.Start(start)!;
			var error = process.StandardError.ReadToEndAsync();
			var output = process.StandardOutput.ReadToEnd();
			process.WaitForExit();
			return (process.ExitCode, output + error.Result);
		}
		catch (Win32Exception)
		{
			throw new SdkException("comparing projects needs the .NET SDK on the PATH; dotnet was not found");
		}
	}

	/// <summary>The projects a solution lists, as paths relative to the solution's directory.</summary>
	public static IReadOnlyList<string> SolutionProjects(string solution)
	{
		var (exitCode, output) = Run("sln", solution, "list");
		if (exitCode != 0)
			throw new SdkException($"dotnet sln {solution} list failed:{Environment.NewLine}{output}");
		var projects = new List<string>();
		foreach (var line in output.Split('\n', StringSplitOptions.TrimEntries | StringSplitOptions.RemoveEmptyEntries))
			if (line.EndsWith("proj", StringComparison.OrdinalIgnoreCase))
				projects.Add(line);
		return projects;
	}

	/// <summary>MSBuild properties of a project, evaluated with the global properties given.</summary>
	public static Dictionary<string, string> Properties(string project, IEnumerable<string> names, params string[] globalProperties)
	{
		var args = new List<string> { "msbuild", project, "-nologo" };
		foreach (var name in names)
			args.Add($"-getProperty:{name}");
		foreach (var property in globalProperties)
			args.Add($"-p:{property}");
		var (exitCode, output) = Run([.. args]);
		if (exitCode != 0)
			throw new SdkException($"dotnet msbuild could not evaluate {project}:{Environment.NewLine}{output}");

		var properties = new Dictionary<string, string>(StringComparer.OrdinalIgnoreCase);
		var json = output.Trim();
		if (json.StartsWith('{'))
		{
			using var document = JsonDocument.Parse(json);
			foreach (var property in document.RootElement.GetProperty("Properties").EnumerateObject())
				properties[property.Name] = property.Value.GetString() ?? "";
		}
		else
		{
			// with a single -getProperty, msbuild prints the bare value
			foreach (var name in names)
				properties[name] = json;
		}
		return properties;
	}
}
