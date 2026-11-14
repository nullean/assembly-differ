using System;
using System.IO;

namespace Differ;

/// <summary>
/// The run's own scratch folder, <c>$TMPDIR/assembly-differ/&lt;run id&gt;</c>, where packages unpack and assemblies are
/// copied to be compared. It is deleted when the run ends, unless ASSEMBLY_DIFFER_KEEP_SCRATCH is set. Downloads are not
/// kept here: NuGet caches them in its global packages folder, as dotnet restore does.
/// </summary>
internal static class Scratch
{
	private static readonly Lazy<string> RootPath = new(
		() => Directory.CreateDirectory(Path.Combine(Path.GetTempPath(), "assembly-differ", Guid.NewGuid().ToString("N"))).FullName
	);

	/// <summary>A folder in the scratch folder, created.</summary>
	public static string Folder(params string[] parts) => Directory.CreateDirectory(Path.Combine([RootPath.Value, .. parts])).FullName;

	public static void Delete()
	{
		if (!RootPath.IsValueCreated)
			return;
		if (Environment.GetEnvironmentVariable("ASSEMBLY_DIFFER_KEEP_SCRATCH") is { Length: > 0 })
		{
			Console.Error.WriteLine($"[diff] kept the scratch folder {RootPath.Value}");
			return;
		}
		try
		{
			Directory.Delete(RootPath.Value, recursive: true);
		}
		catch (Exception e) when (e is IOException or UnauthorizedAccessException)
		{
			// a file still open; the system cleans its temporary folder eventually
		}
	}
}
