using System;
using System.Collections.Generic;
using System.IO;
using System.Linq;
using JustAssembly.Core;
using Mono.Cecil;

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

	public SuggestedVersionChange SuggestedVersionChange
	{
		get
		{
			if (Diff == null)
				return SuggestedVersionChange.Patch;

			if (Diff.IsBreakingChange)
				return SuggestedVersionChange.Major;

			var differences = Changes(Diff).ToList();
			if (differences.Count == 0)
				return SuggestedVersionChange.Patch;

			var anyDeleted = differences.Any(diff => diff.DiffType == DiffType.Deleted);
			var anyModified = differences.Any(diff => diff.DiffType == DiffType.Modified);
			var anyBreakingAddition = differences.Any(diff => diff.DiffType == DiffType.New && IsBreakingAddition(diff));
			if (anyDeleted || anyModified || anyBreakingAddition)
				return SuggestedVersionChange.Major;

			var anyNew = differences.Any(diff => diff.DiffType == DiffType.New);
			return anyNew ? SuggestedVersionChange.Minor : SuggestedVersionChange.Patch;
		}
	}

	// Additions that break existing code: a new abstract member, on an interface (without a default implementation)
	// or an abstract class, has to be implemented by every type deriving from it, and a new instance field of a
	// struct has to be assigned by code that initializes the struct field by field.
	private static bool IsBreakingAddition(IDiffItem diff) => diff switch
	{
		IMetadataDiffItem<MethodDefinition> method => method.NewElement?.IsAbstract == true,
		IMetadataDiffItem<PropertyDefinition> property => property.NewElement?.GetMethod?.IsAbstract == true
			|| property.NewElement?.SetMethod?.IsAbstract == true,
		IMetadataDiffItem<EventDefinition> @event => @event.NewElement?.AddMethod?.IsAbstract == true,
		IMetadataDiffItem<FieldDefinition> field => field.NewElement is { IsStatic: false, DeclaringType.IsValueType: true },
		_ => false
	};

	// The changes under a diff item: a module or type that is Modified only because members were added
	// to or deleted from it isn't a change of its own, so look through it to what changed inside it.
	private static IEnumerable<IDiffItem> Changes(IMetadataDiffItem item)
	{
		foreach (var declarationDiff in item.DeclarationDiffs)
			yield return declarationDiff;

		foreach (var child in item.ChildrenDiffs)
		{
			// A Modified item with nothing under it still counts as a modification
			if (child.DiffType != DiffType.Modified || child.DeclarationDiffs.Count + child.ChildrenDiffs.Count == 0)
			{
				yield return child;
				continue;
			}

			foreach (var change in Changes(child))
				yield return change;
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
		Comparisons.Count == 0 ? SuggestedVersionChange.Patch : Comparisons.Select(c => c.SuggestedVersionChange).OrderByDescending(c => c).First();
}
