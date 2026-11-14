using System;
using System.Linq;

namespace Nullean.PublicApi.Diff;

/// <summary>The version change semantic versioning calls for.</summary>
public enum SuggestedVersionChange
{
	/// <summary>The API is the same.</summary>
	None = 1,
	/// <summary>Nothing callers use changed, such as compiler-generated attributes or assembly references.</summary>
	Patch = 2,
	/// <summary>Something was added that existing code does not have to act on.</summary>
	Minor = 3,
	/// <summary>Code built against the old version can break.</summary>
	Major = 4
}

/// <summary>Classifies differences by the version change each calls for.</summary>
public static class VersionChange
{
	/// <summary>The version change one difference calls for on its own; the reports count the <c>Major</c> ones as breaking.</summary>
	public static SuggestedVersionChange Classify(IDiffItem item)
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
