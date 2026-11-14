# Nullean.PublicApi.Diff

Compares the public API of two versions of a .NET assembly: what was added, removed or changed, the version bump
semantic versioning calls for, and which of the changes affect code that uses the assembly.

```csharp
using Nullean.PublicApi.Diff;

var diff = ApiDiff.Compare("old/MyLib.dll", "new/MyLib.dll");
if (diff is null)
	return; // the same API

diff.Visit((item, depth) =>
	Console.WriteLine($"{new string(' ', depth * 2)}{item.DiffType} {item.HumanReadable} ({VersionChange.Classify(item)})"));
```

- `ApiDiff.Compare` takes two paths, or two models read with [Nullean.PublicApi](https://www.nuget.org/packages/Nullean.PublicApi).
- `VersionChange.Classify` says whether a difference calls for a patch, minor or major bump.
- `UsedApi.Read` and `UsageFilter.Apply` narrow a diff down to the changes some code is affected by.

Targets netstandard2.0 and net10.0. It is the comparison behind [assembly-differ](https://github.com/nullean/assembly-differ).
