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

## Who a change breaks

`ApiComparison.Compare` compares two models read with [Nullean.PublicApi](https://www.nuget.org/packages/Nullean.PublicApi),
from assemblies or `.api` files, and says for each change who it breaks (callers, implementers or inheritors), whether
it breaks compiled code, source or both, and the version change it calls for:

```csharp
var result = ApiComparison.Compare(ApiReader.Read("old/MyLib.dll"), ApiReader.Read("new/MyLib.dll"));
foreach (var change in result.Changes)
	Console.WriteLine($"{change.Where}  {change.Kind} {change.Part}  {change.Old} → {change.New}  {change.Level}");
Console.WriteLine(result.Suggest(baseline: new Version(1, 4, 0)));
```

```
Lib › Widget › Draw(Canvas)  Removed   public void Draw(Canvas canvas) →   Major
Lib › IShape › Resize(double)  Added    → void Resize(double factor)  Major
Lib › A › M(int)  Changed parameter 1 name  count → total  Major
Lib › Widget  Changed base type  Shape → Mid  Minor
Major
```

`Breaks`, `Compatibility` and `Note` say why: the removal breaks callers, binary and source; the new interface member
breaks implementers; the renamed parameter breaks callers that pass it by name, in source only; `Mid` derives from
`Shape`, so `Shape` is still a base.

A break that only affects implementers or inheritors does not ask for a major version when nobody outside can implement
or derive from the type, or when the type is declared (an attribute named `InternalImplementationOnlyAttribute`) or
configured (`ApiComparisonOptions.NotExtensionPoints`) not to be an extension point; it is still listed. Below 1.0 a
break asks for a minor version.

## The JustAssembly-compatible comparison

- `ApiDiff.Compare` takes two paths and returns the report assembly-differ's commands print.
- `VersionChange.Classify` says whether a difference calls for a patch, minor or major bump.
- `UsedApi.Read` and `UsageFilter.Apply` narrow a diff down to the changes some code is affected by.

Targets netstandard2.0 and net10.0. It is the comparison behind [assembly-differ](https://github.com/nullean/assembly-differ).
