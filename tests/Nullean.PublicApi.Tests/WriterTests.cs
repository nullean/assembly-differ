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
				public static bool operator ==(Signatures? a, Signatures? b) => true;
				public static bool operator !=(Signatures? a, Signatures? b) => false;
				public override bool Equals(object? obj) => false;
				public override int GetHashCode() => 0;
			}
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
	public void Without_nullable_annotations() =>
		Compiler.Api("namespace Lib; public class Oblivious { public string Name(string? value) => value!; }", nullable: false).Should().MatchSnapshot();
}
