# Nullean.PublicApi

Reads the public API of a .NET assembly from its metadata, without loading it, and writes it as a `.api` file: an
indented tree of namespaces, types and members, one C# declaration per line.

```csharp
using Nullean.PublicApi;

var api = ApiReader.Read("bin/Release/net10.0/MyLib.dll");
File.WriteAllText("MyLib.api", ApiWriter.Write(api));
```

```
assembly MyLib
using System: IComparable<>, IDisposable, ObsoleteAttribute

namespace MyLib.Drawing
  public interface IShape : IDisposable
    double Area { get; }
    void Scale(double factor) { … }

  public sealed class Widget : Shape, IComparable<Widget>
    public Widget(string name, int size = 1)
    public required string? Name { get; init; }
    [Obsolete("use Draw(Canvas)")]
    public void Draw()
```

The model holds what a declaration says: base types and interfaces, type parameter constraints and variance,
nullability, tuple element names, `ref`, `out`, `in` and `params`, default and constant values, `required`, `init`,
and the attributes that are about the API rather than the compiler's bookkeeping. The header names the types from
other assemblies the API uses, so declarations use short names; a name two namespaces share is written in full.

Targets netstandard2.0 and net10.0. It is the reader behind [assembly-differ](https://github.com/nullean/assembly-differ).
