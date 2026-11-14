using System;
using System.Collections.Generic;
using System.IO;
using System.Linq;
using Differ.Engine;

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
	public SuggestedVersionChange SuggestedVersionChange => Diff == null ? SuggestedVersionChange.None : Classify(Diff);

	/// <summary>The version change one difference calls for on its own; the comment counts the <c>Major</c> ones as breaking.</summary>
	internal static SuggestedVersionChange Classify(IDiffItem item)
	{
		switch (item)
		{
			// a reference the new assembly dropped is not part of its API
			case AssemblyReferenceDiffItem:
				return SuggestedVersionChange.Patch;
			// the compiler moves its own attributes, such as NullableContextAttribute, whenever members change
			case CustomAttributeDiffItem attribute when attribute.HumanReadable.StartsWith("System.Runtime.CompilerServices.", StringComparison.Ordinal):
				return SuggestedVersionChange.Patch;
			// adding or removing an attribute, such as [Obsolete], does not break a caller
			case CustomAttributeDiffItem:
				return SuggestedVersionChange.Minor;
			case IMetadataDiffItem metadata when metadata.DiffType == DiffType.Deleted:
				return SuggestedVersionChange.Major;
			// an abstract or required member is new work for every implementer or caller
			case MemberDiffItem { DiffType: DiffType.New, AddsObligation: true }:
				return SuggestedVersionChange.Major;
			case IMetadataDiffItem metadata when metadata.DiffType == DiffType.New:
				return SuggestedVersionChange.Minor;
			case IMetadataDiffItem metadata:
				var change = SuggestedVersionChange.Patch;
				foreach (var child in metadata.DeclarationDiffs.Concat(metadata.ChildrenDiffs))
				{
					var childChange = Classify(child);
					if (childChange > change)
						change = childChange;
				}
				return change;
			default:
				return item.IsBreakingChange ? SuggestedVersionChange.Major : SuggestedVersionChange.Minor;
		}
	}
}

public enum SuggestedVersionChange
{
	None = 1,
	Patch = 2,
	Minor = 3,
	Major = 4
}

public class AllComparisonResults
{
	public List<AssemblyComparison> Comparisons { get; }
	public SuggestedVersionChange PreventVersionChange { get; }

	public AllComparisonResults(List<AssemblyComparison> results, SuggestedVersionChange preventChange) =>
		(Comparisons, PreventVersionChange) = (results, preventChange);

	public SuggestedVersionChange SuggestedVersionChange =>
		Comparisons.Count == 0 ? SuggestedVersionChange.None : Comparisons.Select(c => c.SuggestedVersionChange).OrderByDescending(c => c).First();
}
