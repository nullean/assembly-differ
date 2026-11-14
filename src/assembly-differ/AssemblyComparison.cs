using System;
using System.Collections.Generic;
using System.IO;
using System.Linq;

namespace Differ;

public class AssemblyComparison
{
	public AssemblyComparison(FileInfo first, FileInfo second)
	{
		First = first ?? throw new ArgumentNullException(nameof(first));
		Second = second ?? throw new ArgumentNullException(nameof(second));
	}

	public FileInfo First { get; }
	public FileInfo Second { get; }
	public IMetadataDiffItem Diff { get; set; }

	/// <summary>
	/// The version change the differences call for, by semantic versioning: <c>Major</c> when anything breaks, including
	/// a new abstract or required member, <c>Minor</c>
	/// when API is only added or changed compatibly, <c>Patch</c> when only assembly references changed, and <c>None</c>
	/// when nothing changed.
	/// </summary>
	public SuggestedVersionChange SuggestedVersionChange => Diff == null ? SuggestedVersionChange.None : VersionChange.Classify(Diff);
}

public class AllComparisonResults
{
	public List<AssemblyComparison> Comparisons { get; }
	public SuggestedVersionChange PreventVersionChange { get; }

	public AllComparisonResults(List<AssemblyComparison> results, SuggestedVersionChange preventChange) =>
		(Comparisons, PreventVersionChange) = (results, preventChange);

	/// <summary>The release the old version is, when a command resolved one, such as <c>1.2.0</c>.</summary>
	public string? Baseline { get; init; }

	/// <summary>Assemblies only the old version has, which count as removed API.</summary>
	public IReadOnlyList<FileInfo> Removed { get; init; } = [];

	/// <summary>Assemblies only the new version has, which count as added API.</summary>
	public IReadOnlyList<FileInfo> Added { get; init; } = [];

	public SuggestedVersionChange SuggestedVersionChange
	{
		get
		{
			var change = Comparisons.Select(c => c.SuggestedVersionChange).DefaultIfEmpty(SuggestedVersionChange.None).Max();
			if (Removed.Count > 0)
				return SuggestedVersionChange.Major;
			if (Added.Count > 0 && change < SuggestedVersionChange.Minor)
				return SuggestedVersionChange.Minor;
			return change;
		}
	}
}
