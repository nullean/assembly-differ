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

	/// <summary>Runs the tool in <paramref name="workingDirectory"/>, with extra environment variables.</summary>
	public static Task<CliResult> RunInAsync(string workingDirectory, IReadOnlyDictionary<string, string> environment, params string[] args) =>
		RunToolAsync(ToolPath, args, workingDirectory, environment);

	/// <summary>Runs another build of the tool, such as a previous release, given the path to its dll.</summary>
	public static Task<CliResult> RunToolAsync(string toolPath, params string[] args) => RunToolAsync(toolPath, args, null, null);

	private static async Task<CliResult> RunToolAsync(
		string toolPath,
		string[] args,
		string? workingDirectory,
		IReadOnlyDictionary<string, string>? environment
	)
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
		if (workingDirectory is not null)
			start.WorkingDirectory = workingDirectory;
		foreach (var (name, value) in environment ?? new Dictionary<string, string>())
			start.Environment[name] = value;
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
