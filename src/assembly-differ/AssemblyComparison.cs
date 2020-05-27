using System;
using System.Collections.Generic;
using System.IO;
using System.Linq;
using JustAssembly.Core;
using Mono.Cecil;

namespace Differ
{
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
				if (Diff == null) return SuggestedVersionChange.Patch;

				if (Diff.IsBreakingChange) return SuggestedVersionChange.Major;

				var differences = Diff.ChildrenDiffs.Concat(Diff.DeclarationDiffs).ToList();
				if (differences.Count == 0)
					return SuggestedVersionChange.Patch;

				var anyDeleted = differences.Any(diff => diff.DiffType == DiffType.Deleted);
				var anyModified = differences.Any(diff => diff.DiffType == DiffType.Modified);
				if (anyDeleted || anyModified)
					return SuggestedVersionChange.Major;

				var anyNew = differences.Any(diff => diff.DiffType == DiffType.New);
				return anyNew ? SuggestedVersionChange.Minor : SuggestedVersionChange.Patch;
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
			Comparisons.Count == 0
				? SuggestedVersionChange.Patch
				: Comparisons
					.Select(c => c.SuggestedVersionChange)
					.OrderByDescending(c=>c)
					.First();
	}
}
