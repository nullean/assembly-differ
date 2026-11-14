#nullable enable
using System;
using System.Collections.Generic;
using System.Linq;

using Nullean.PublicApi;

namespace Nullean.PublicApi.Diff;

/// <summary>Who a change breaks.</summary>
[Flags]
public enum ApiAudience
{
	/// <summary>Nobody.</summary>
	None = 0,

	/// <summary>Code that calls the API, creates its types or reads its members.</summary>
	Callers = 1,

	/// <summary>Code that implements one of its interfaces.</summary>
	Implementers = 2,

	/// <summary>Code that derives from one of its classes or overrides one of its members.</summary>
	Inheritors = 4,
}

/// <summary>How a change breaks the code it breaks.</summary>
[Flags]
public enum ApiCompatibility
{
	/// <summary>It does not.</summary>
	None = 0,

	/// <summary>Code compiled against the old version fails against the new one, or behaves differently.</summary>
	Binary = 1,

	/// <summary>Code that compiled against the old version no longer compiles against the new one.</summary>
	Source = 2,
}

/// <summary>Whether a type or member was added or removed, or one of its parts changed.</summary>
public enum ApiChangeKind
{
	/// <summary>Only the new version has it.</summary>
	Added,

	/// <summary>Only the old version has it.</summary>
	Removed,

	/// <summary>Both have it, and a part of it differs.</summary>
	Changed,
}

/// <summary>One difference between two versions of an API.</summary>
/// <param name="Path">Where: the namespace, the types, and the member, such as <c>Lib.Drawing</c>, <c>Widget</c>, <c>Draw(Canvas)</c>.</param>
/// <param name="Kind">Added, removed or changed.</param>
/// <param name="Part">What changed, for a change: <c>return type</c>, <c>base type</c>, <c>parameter 1 name</c>; empty for an addition or removal.</param>
/// <param name="Old">The old declaration or part, as C#; null for an addition.</param>
/// <param name="New">The new declaration or part, as C#; null for a removal.</param>
/// <param name="Breaks">Who it breaks.</param>
/// <param name="Compatibility">How it breaks them.</param>
/// <param name="Counts">
/// Whether the break asks for a major version: false when it only breaks implementers or inheritors of a type that is
/// declared or configured not to be an extension point.
/// </param>
/// <param name="Note">Why it is classified the way it is, when that is not obvious; null otherwise.</param>
public sealed record ApiChange(
	IReadOnlyList<string> Path,
	ApiChangeKind Kind,
	string Part,
	string? Old,
	string? New,
	ApiAudience Breaks,
	ApiCompatibility Compatibility,
	bool Counts,
	string? Note
)
{
	/// <summary>The type or member in the old version, which a parsed file knows the line of.</summary>
	public object? OldNode { get; init; }

	/// <summary>The type or member in the new version.</summary>
	public object? NewNode { get; init; }

	/// <summary>The old version of the type the change is in, which locates an addition in the old file.</summary>
	public object? OldScope { get; init; }

	/// <summary>The version change this change calls for on its own.</summary>
	public SuggestedVersionChange Level { get; init; }

	/// <summary>The path written out: <c>Lib.Drawing › Widget › Draw(Canvas)</c>.</summary>
	public string Where => string.Join(" › ", Path);
}

/// <summary>How <see cref="ApiComparison"/> decides what counts as an extension point.</summary>
public sealed class ApiComparisonOptions
{
	/// <summary>
	/// Types that are not extension points, so changes that only break their implementers or inheritors do not ask for
	/// a major version: full names, where <c>*</c> stands for any run of characters, such as <c>Lib.Internal.*</c>.
	/// </summary>
	public IReadOnlyList<string> NotExtensionPoints { get; init; } = [];
}

/// <summary>The differences between two versions of an API, and the version change they call for.</summary>
public sealed class ApiComparisonResult
{
	internal ApiComparisonResult(IReadOnlyList<ApiChange> changes) => Changes = changes;

	/// <summary>Every difference, in the order of the new version's types and members.</summary>
	public IReadOnlyList<ApiChange> Changes { get; }

	/// <summary>The changes that ask for a major version.</summary>
	public IEnumerable<ApiChange> Breaking => Changes.Where(c => c.Level == SuggestedVersionChange.Major);

	/// <summary>
	/// The version change semantic versioning calls for. Below 1.0, where anything may change, a break asks for a
	/// minor version: pass the version the changes are measured from as <paramref name="baseline"/>.
	/// </summary>
	public SuggestedVersionChange Suggest(Version? baseline = null)
	{
		var level = Changes.Count == 0 ? SuggestedVersionChange.None : Changes.Max(c => c.Level);
		return level == SuggestedVersionChange.Major && baseline is { Major: 0 } ? SuggestedVersionChange.Minor : level;
	}
}
