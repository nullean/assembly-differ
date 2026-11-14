# Nullean.PublicApi

Reads the public API of a .NET assembly with `System.Reflection.Metadata`, without loading it: its types, members,
signatures and attributes, as a model of records.

```csharp
using Nullean.PublicApi;

var assembly = ApiReader.Read("bin/Release/net10.0/MyLib.dll");
foreach (var type in assembly.Modules[0].Types)
	Console.WriteLine(type.DisplayName);
```

Targets netstandard2.0 and net10.0. It is the reader behind [assembly-differ](https://github.com/nullean/assembly-differ);
[Nullean.PublicApi.Diff](https://www.nuget.org/packages/Nullean.PublicApi.Diff) compares two of these models.
