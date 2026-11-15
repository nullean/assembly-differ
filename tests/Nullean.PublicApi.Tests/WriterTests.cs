using AwesomeAssertions;
using Nullean.Snapshots;

namespace Nullean.PublicApi.Tests;

/// <summary>Each case is C# source, and the snapshot is the <c>.api</c> file of the assembly it compiles to.</summary>
public class WriterTests
{
	public static IEnumerable<(string Name, string Source)> Cases()
	{
		yield return ("types", """
			namespace Lib.Drawing;
			public interface IShape : System.IDisposable
			{
				double Area { get; }
				void Draw(Canvas canvas);
				void Scale(double factor) { }
				static abstract IShape Create();
			}
			public abstract class Shape
			{
				protected Shape() { }
				public abstract double Area { get; }
				public virtual void Draw(Canvas canvas) { }
				public int Layer { get; protected set; }
				public const int MaxLayer = 10;
				public static readonly string Default = "";
				public event System.EventHandler<System.EventArgs>? Changed;
				internal void Hidden() { }
				private protected void AlsoHidden() { }
			}
			public sealed class Widget : Shape, System.IComparable<Widget>
			{
				public Widget(string name, int size = 1) { Name = name; }
				public required string? Name { get; init; }
				public override double Area => 0;
				public sealed override void Draw(Canvas canvas) { }
				public int CompareTo(Widget? other) => 0;
				protected void Unreachable() { }
			}
			public sealed class Canvas;
			public static class Widgets
			{
				[System.Obsolete("use Widget")] public static Widget? Default { get; }
				public static int Count(this Widget widget) => 0;
			}
			public abstract class Renderer
			{
				internal Renderer() { }
				public abstract void Render(IShape shape);
				protected abstract void Unreachable();
			}
			public enum Color : byte { Red, Green = 5, Blue }
			[System.Flags] public enum Options { None = 0, A = 1, B = 2 }
			public delegate T Factory<out T>(string name) where T : class;
			public readonly struct Point(int x, int y)
			{
				public int X { get; } = x;
				public int Y { get; } = y;
				public static Point operator +(Point a, Point b) => a;
				public static implicit operator Point(int value) => default;
			}
			public ref struct Span2 { public int Length; }
			public record Person(string Name, int Age);
			public sealed record Employee(string Name, int Age, string Team) : Person(Name, Age);
			public class Animal { public virtual Animal Clone() => new(); }
			public class Dog : Animal { public override Dog Clone() => new(); }
			public class Outer<T>
			{
				public class Inner<U> { public T? First; public U? Second; }
				protected class Family { }
			}
			""");
		yield return ("signatures", """
			using System;
			using System.Collections.Generic;
			// built for .NET 11, where IntPtr is nint, as the SDK says in every build
			[assembly: System.Runtime.Versioning.TargetFramework(".NETCoreApp,Version=v11.0")]
			namespace Lib;
			public class Signatures
			{
				public void Refs(ref int a, out int b, in int c, ref readonly int d, params int[] rest) { b = 0; }
				public void Nullability(string? a, List<string?> b, Dictionary<string, List<int?>?> c, string?[]? d, int[][,] jagged) { }
				public (int x, string? name) Tuple() => default;
				public List<(int a, (string b, int c) inner)> NestedTuples() => [];
				public dynamic Dynamic(List<dynamic> list) => list;
				public nint Native(nuint value) => 0;
				public ref readonly int RefReturn() => throw null!;
				public T Generic<T, U>(U value) where T : struct, IComparable<T> where U : class?, new() => default;
				public void NotNull<T>(T value) where T : notnull { }
				public void Unmanaged<T>() where T : unmanaged { }
				public void Defaults(string s = "a\"b", char c = 'x', double d = 1.5, decimal m = 2.5m, DayOfWeek day = DayOfWeek.Monday, object? o = null, DateTime when = default) { }
				public void Scoped(scoped ref int value) { }
				public int this[int index, string key] { get => 0; set { } }
				public unsafe void Pointers(int* p, delegate*<int, void> f) { }
				public volatile int Flag;
				public unsafe delegate*<int, void> Callback;
				public static bool operator ==(Signatures? a, Signatures? b) => true;
				public static bool operator !=(Signatures? a, Signatures? b) => false;
				public override bool Equals(object? obj) => false;
				public override int GetHashCode() => 0;
			}
			""");
		yield return ("enums", """
			using System;
			using System.ComponentModel;
			using System.IO;
			namespace Lib;
			public enum Size : byte { Small = 1, Large = 200 }
			[Flags] public enum Access { None = 0, Read = 1, Write = 2, Execute = 4, ReadWrite = Read | Write }
			[AttributeUsage(AttributeTargets.Method | AttributeTargets.Property)]
			public sealed class SizedAttribute(Size size) : Attribute { public Size Size { get; } = size; }
			public class Files
			{
				public void Open(FileShare share = FileShare.Read | FileShare.Delete, StringComparison comparison = StringComparison.OrdinalIgnoreCase, Size size = Size.Large, Access access = Access.ReadWrite | Access.Execute, Access none = Access.None, Access odd = (Access)64, Size? maybe = Size.Small) { }
				public const Access Default = Access.Read;
				[EditorBrowsable(EditorBrowsableState.Never)] [Sized(Size.Large)] public void Hidden() { }
			}
			""");
		yield return ("extensions", """
			using System.Collections.Generic;
			namespace Lib;
			public static class TextExtensions
			{
				public static int Old(this string s) => 0;
				extension(string self)
				{
					public bool IsBlank => self.Length == 0;
					public int WordCount() => 0;
					public static string Make(int n) => "";
					public static string Empty2 => "";
				}
				extension(string? maybe)
				{
					public bool IsMissing => maybe is null;
				}
				extension(string)
				{
					public static string Twice(string value) => value + value;
				}
				extension<T>(List<T> list) where T : struct
				{
					public T? FirstOrNone() => null;
					public int Size { get => 0; set { } }
				}
				extension(ref int value)
				{
					public void Bump() => value++;
				}
			}
			""");
		yield return ("modern", """
			using System;
			using System.Collections.Generic;
			using System.Diagnostics.CodeAnalysis;
			using System.Runtime.CompilerServices;
			namespace Lib;
			public struct Money
			{
				public decimal Amount;
				public static Money operator +(Money a, Money b) => a;
				public static Money operator checked +(Money a, Money b) => a;
				public void operator +=(Money other) { }
				public void operator checked +=(Money other) { }
				public void operator %=(Money other) { }
				public void operator >>>=(int count) { }
				public void operator ++() { }
				public void operator --() { }
				public void operator checked --() { }
				public static Money operator ++(Money m) => m;
				public static explicit operator int(Money m) => 0;
				public static explicit operator checked int(Money m) => 0;
				public int Auto { get; set; }
				public int Setter { get => 0; readonly set { } }
				public readonly int Whole { get => 0; set { } }
				public int Mutating { get => 0; set { } }
				public readonly int Sum() => 0;
			}
			[InlineArray(4)]
			public struct Buffer4 { private int _element; }
			public ref struct Cursor : IDisposable { public void Dispose() { } }
			public class Api
			{
				public void Log(params ReadOnlySpan<string> parts) { }
				public void Keep(scoped ReadOnlySpan<string> parts) { }
				public void Add(params IEnumerable<int> values) { }
				public void Take<T>(T value) where T : allows ref struct { }
				[OverloadResolutionPriority(1)] public void Prefer(ReadOnlySpan<char> s) { }
				public void Prefer(string s) { }
				[Experimental("LIB001")] public void Trial() { }
				public ref readonly int Peek(ref readonly int value) => ref value;
				public int Field { get => field; set => field = value; }
			}
			public partial class Parts
			{
				public partial int Size { get; }
				public partial int Size { get => 0; }
				public partial event Action? Changed;
				public partial event Action? Changed { add { } remove { } }
			}
			public interface ICounter { static abstract int Zero { get; } static virtual int One => 1; static int Two() => 2; void Tick() { } }
			public record struct Point(int X, int Y);
			public class Primary(int seed) { public int Seed => seed; }
			""");
		yield return ("names", """
			namespace Lib.A { public class Range { } public class Task { } }
			namespace Lib.B
			{
				public class Uses
				{
					public System.Range Bcl() => default;
					public Lib.A.Range Own() => new();
					public System.Threading.Tasks.Task Run() => null!;
					public Lib.A.Task Mine() => new();
					public System.Collections.Generic.Dictionary<string, System.IO.Stream> Map() => [];
				}
			}
			public class InGlobal { }
			""");
	}

	[Test]
	[MethodDataSource(nameof(Cases))]
	public void Writes(string name, string source) => Compiler.Api(source).Should().MatchSnapshot(name: name);

	[Test]
	public void An_enum_that_cannot_be_found_is_a_cast()
	{
		using var stream = Compiler.Compile("namespace Lib; public class A { public void M(System.StringComparison c = System.StringComparison.Ordinal) { } }");
		ApiWriter.Write(ApiReader.Read(stream, new ApiReaderOptions { SearchRuntime = false })).Should().Contain("StringComparison c = (StringComparison)4");
	}

	[Test]
	public void Without_nullable_annotations() =>
		Compiler.Api("namespace Lib; public class Oblivious { public string Name(string? value) => value!; }", nullable: false).Should().MatchSnapshot();
}
