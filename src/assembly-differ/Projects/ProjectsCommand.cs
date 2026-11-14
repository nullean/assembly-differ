using System;
using System.Collections.Generic;
using System.IO;
using Nullean.Argh;

namespace Differ.Projects;

internal sealed class ProjectsCommand
{
	/// <summary>
	/// Compares every library project in the current directory with the latest release of its package. Runs when no
	/// command is given.
	/// </summary>
	/// <remarks>
	/// It reads the one solution in the directory, or else the project files in it, and skips test projects and projects
	/// with IsPackable set to false. It compares each project's Release build, for its newest target framework, with the
	/// latest stable release of its package on the feeds your NuGet.config lists. It does not build: run
	/// dotnet build -c Release first, and a project without build output is skipped with a warning. A project whose
	/// package has no release yet is reported and skipped. It needs the .NET SDK, to read the projects.
	/// </remarks>
	/// <param name="project">Compare this project file, whatever kind it is. Repeat it for more than one.</param>
	/// <param name="includeTests">Compare test projects too.</param>
	/// <param name="includeUnpackable">Compare projects with IsPackable set to false too.</param>
	/// <param name="report">How to report the comparison.</param>
	/// <example>dotnet build -c Release &amp;&amp; assembly-differ</example>
	/// <example>assembly-differ --project src/MyLib/MyLib.csproj --fail-on major</example>
	[DefaultCommand]
	[CommandName("projects")]
	public int Projects(List<string>? project = null, bool includeTests = false, bool includeUnpackable = false, [AsParameters] ReportOptions? report = null)
	{
		try
		{
			return ProjectComparison.Run(
				Directory.GetCurrentDirectory(),
				new ProjectComparison.Options(project ?? [], includeTests, includeUnpackable),
				report ?? new ReportOptions()
			);
		}
		catch (Exception e) when (e is Dotnet.SdkException or ArgumentException)
		{
			Console.Error.WriteLine($"[diff] {e.Message}");
			return 1;
		}
	}
}
