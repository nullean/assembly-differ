# Nullean.PublicApi

Reads the public API of a .NET assembly from its metadata, without loading it, and writes it as a `.api` file: an
indented tree of namespaces, types and members, one C# declaration per line.

```csharp
using Nullean.PublicApi;

var api = ApiReader.Read("bin/Release/net10.0/MyLib.dll");
File.WriteAllText("MyLib.api", ApiWriter.Write(api));

// and back: the same model, with the line each type and member is on
var document = ApiParser.Parse(File.ReadAllText("MyLib.api"));
```

```
assembly MyLib
using System: ObsoleteAttribute
using interface System: IComparable<>, IDisposable

namespace MyLib.Drawing
  interface IShape : IDisposable
    double Area { get; }
    void Scale(double factor) { … }

  sealed class Widget : Shape, IComparable<Widget>
    Widget(string name, int size = 1)
    required string? Name { get; init; }
    [Obsolete("use Draw(Canvas)")]
    void Draw()
```

The model holds what a declaration says: base types and interfaces, type parameter constraints and variance,
nullability, tuple element names, `ref`, `out`, `in` and `params`, default and constant values, `required`, `init`,
and the attributes that are about the API rather than the compiler's bookkeeping. Everything in the file is visible from outside, so `public` goes without saying and only `protected` is written.

The header names every type from another assembly the API uses, namespace by namespace, so declarations use short
names; a name two namespaces share is written in full. `using struct` lists the value types, so `DateTime?` reads back
as a nullable value type and `string?` as an annotated reference, and `using interface` the interfaces a base list
names, so a reader can tell a base class from an interface without the other assembly. `ApiParser` reads the file back
into the same model the reader produced from the assembly.

C# 14 extension blocks are written as the source declares them, with their members under the receiver; the static
methods the compiler writes for those members are left out, since they are the block's and not the class's:

```
  static class TextExtensions
    static int Old(this string s)
    extension<T>(List<T> list) where T : struct
      T? FirstOrNone()
    extension(string self)
      bool IsBlank { get; }
      static string Make(int n)
```

Targets netstandard2.0 and net10.0. It is the reader behind [assembly-differ](https://github.com/nullean/assembly-differ).
