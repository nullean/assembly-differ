using System.Diagnostics;
using System.Reflection;
using System.Text;

namespace Differ.Tests;

/// <summary>What one run of the tool printed and returned.</summary>
public sealed record CliResult(int ExitCode, string Out, string Error);

/// <summary>Runs the tool built from this checkout as a process, the way the action, the container and builds do.</summary>
public static class Cli
{
	private static readonly string ToolPath = typeof(Cli).Assembly.GetCustomAttributes<AssemblyMetadataAttribute>().Single(a => a.Key == "ToolPath").Value!;

	public static Task<CliResult> RunAsync(params string[] args) => RunToolAsync(ToolPath, args);

	/// <summary>Runs another build of the tool, such as a previous release, given the path to its dll.</summary>
	public static async Task<CliResult> RunToolAsync(string toolPath, params string[] args)
	{
		if (!File.Exists(toolPath))
			throw new FileNotFoundException($"build the tool first, {toolPath} does not exist");

		var start = new ProcessStartInfo("dotnet")
		{
			RedirectStandardOutput = true,
			RedirectStandardError = true,
			UseShellExecute = false,
			StandardOutputEncoding = Encoding.UTF8,
			StandardErrorEncoding = Encoding.UTF8,
		};
		start.ArgumentList.Add("exec");
		start.ArgumentList.Add(toolPath);
		foreach (var arg in args)
			start.ArgumentList.Add(arg);

		using var process = Process.Start(start)!;
		var stdout = process.StandardOutput.ReadToEndAsync();
		var stderr = process.StandardError.ReadToEndAsync();
		using var timeout = new CancellationTokenSource(TimeSpan.FromMinutes(2));
		await process.WaitForExitAsync(timeout.Token);
		return new CliResult(process.ExitCode, await stdout, await stderr);
	}
}
