using AwesomeAssertions;

namespace Differ.Tests;

/// <summary>Runs the NuGet providers against packages on nuget.org, whose published versions never change.</summary>
public class NuGetProviderTests
{
	[Test]
	[NotInParallel(nameof(NuGetProviderTests))]
	public async Task Previous_nuget_and_nuget_for_one_version_unpack_apart()
	{
		// both sides name 0.14.0: previous-nuget unpacks 0.13.0, nuget unpacks 0.14.0, which changed Proc's API.
		// Run twice, so a folder the first run left behind is in place for the second.
		for (var run = 0; run < 2; run++)
		{
			var result = await Cli.RunAsync("diff", "previous-nuget|Proc|0.14.0|netstandard2.0", "nuget|Proc|0.14.0|netstandard2.0", "-t", "proc");

			result.ExitCode.Should().Be(0, result.Error);
			result.Out.Should().Contain("[diff] Difference found").And.NotContain("[diff] No diff between");
		}
	}
}
