using System.Text;
using AwesomeAssertions;
using Nullean.PublicApi.Diff;
using Nullean.Snapshots;

namespace Nullean.PublicApi.Tests;

/// <summary>Each scenario is an old and a new version of a library; the snapshot is every change, classified.</summary>
public class ComparisonTests
{
	public static IEnumerable<(string Name, string Old, string New)> Scenarios()
	{
		yield return ("member removed", "public class A { public void M() { } public void Keep() { } }", "public class A { public void Keep() { } }");
		yield return ("member added", "public class A { }", "public class A { public void M() { } }");
		yield return ("obsolete member removed", "public class A { [System.Obsolete] public void M() { } }", "public class A { }");
		yield return ("interface member added", "public interface I { void M(); }", "public interface I { void M(); void N(); }");
		yield return ("interface member added with a body", "public interface I { void M(); }", "public interface I { void M(); void N() { } }");
		yield return ("interface member added to a declared non-extension point", "[InternalImplementationOnly] public interface I { void M(); } public class InternalImplementationOnlyAttribute : System.Attribute { }", "[InternalImplementationOnly] public interface I { void M(); void N(); } public class InternalImplementationOnlyAttribute : System.Attribute { }");
		yield return ("base interface added to an interface", "public interface IBase { void B(); } public interface I { void M(); }", "public interface IBase { void B(); } public interface I : IBase { void M(); }");
		yield return ("empty base interface added", "public interface IMarker { } public interface I { void M(); }", "public interface IMarker { } public interface I : IMarker { void M(); }");
		yield return ("interface removed from a class", "public class A : System.IDisposable { public void Dispose() { } }", "public class A { public void Dispose() { } }");
		yield return ("abstract member added", "public abstract class A { public abstract void M(); }", "public abstract class A { public abstract void M(); public abstract void N(); }");
		yield return ("abstract member added to a class nobody can derive from", "public abstract class A { internal A() { } public abstract void M(); }", "public abstract class A { internal A() { } public abstract void M(); public abstract void N(); }");
		yield return ("base class removed", "public class Shape { } public class Widget : Shape { }", "public class Shape { } public class Widget { }");
		yield return ("base class inserted", "public class Shape { } public class Mid : Shape { } public class Widget : Shape { }", "public class Shape { } public class Mid : Shape { } public class Widget : Mid { }");
		yield return ("member moved to a base class", "public class Shape { } public class Widget : Shape { public void Draw() { } }", "public class Shape { public void Draw() { } } public class Widget : Shape { }");
		yield return ("override removed", "public class A { public override string ToString() => \"\"; }", "public class A { }");
		yield return ("parameter renamed", "public class A { public void M(int count) { } }", "public class A { public void M(int total) { } }");
		yield return ("optional parameter added", "public class A { public void M(int a) { } }", "public class A { public void M(int a, int b = 0) { } }");
		yield return ("default value changed", "public class A { public void M(int a = 1) { } }", "public class A { public void M(int a = 2) { } }");
		yield return ("sealed added", "public class A { }", "public sealed class A { }");
		yield return ("virtual removed", "public class A { public virtual void M() { } }", "public class A { public void M() { } }");
		yield return ("return type changed", "public class A { public int M() => 0; }", "public class A { public long M() => 0; }");
		yield return ("nullability changed", "public class A { public string M() => \"\"; }", "public class A { public string? M() => null; }");
		yield return ("tuple names changed", "public class A { public (int a, int b) M() => default; }", "public class A { public (int x, int y) M() => default; }");
		yield return ("const value changed", "public class A { public const int Max = 1; }", "public class A { public const int Max = 2; }");
		yield return ("enum value changed", "public enum E { A = 1, B = 2 }", "public enum E { A = 1, B = 3 }");
		yield return ("required property added", "public class A { }", "public class A { public required string Name { get; init; } }");
		yield return ("setter became init", "public class A { public int X { get; set; } }", "public class A { public int X { get; init; } }");
		yield return ("obsolete added", "public class A { public void M() { } }", "public class A { [System.Obsolete(\"use N\")] public void M() { } }");
		yield return ("obsolete error added", "public class A { public void M() { } }", "public class A { [System.Obsolete(\"use N\", true)] public void M() { } }");
		yield return ("type removed", "public class A { } public class B { }", "public class A { }");
		yield return ("type added", "public class A { }", "public class A { } public class B { }");
		yield return ("constraint added", "public class A { public void M<T>() { } }", "public class A { public void M<T>() where T : class { } }");
		yield return ("made static", "public class A { public void M() { } }", "public class A { public static void M() { } }");
		yield return ("accessibility reduced", "public class A { public void M() { } }", "public class A { protected void M() { } }");
		yield return ("nothing changed", "public class A { public void M() { } }", "public class A { public void M() { } }");
	}

	[Test]
	[MethodDataSource(nameof(Scenarios))]
	public void Classifies(string name, string oldSource, string newSource) =>
		Report(Compare(oldSource, newSource)).Should().MatchSnapshot(name: name.Replace(' ', '-'));

	[Test]
	public void Configured_not_an_extension_point_does_not_count()
	{
		var result = Compare(
			"namespace Lib.Internal { public interface I { void M(); } }",
			"namespace Lib.Internal { public interface I { void M(); void N(); } }",
			new ApiComparisonOptions { NotExtensionPoints = ["Lib.Internal.*"] }
		);
		Report(result).Should().MatchSnapshot();
	}

	[Test]
	public void IntPtr_read_as_nint_is_no_change()
	{
		const string source = "public class A { public System.IntPtr M(System.UIntPtr value) => default; }";
		var result = Compare(source, "[assembly: System.Runtime.Versioning.TargetFramework(\".NETCoreApp,Version=v11.0\")]\n" + source);
		result.Changes.Should().BeEmpty();
	}

	[Test]
	public void A_break_below_one_asks_for_a_minor_version()
	{
		var result = Compare("public class A { public void M() { } }", "public class A { }");
		result.Suggest(new Version(1, 2, 0)).Should().Be(SuggestedVersionChange.Major);
		result.Suggest(new Version(0, 9, 0)).Should().Be(SuggestedVersionChange.Minor);
	}

	[Test]
	public void An_assembly_compared_with_its_own_api_file_has_no_changes()
	{
		var read = ApiReader.Read(RoslynOracleTests.FrameworkPath("System.Collections.Immutable"));
		var parsed = ApiParser.Parse(ApiWriter.Write(read)).Assembly;
		ApiComparison.Compare(read, parsed).Changes.Should().BeEmpty();
	}

	private static ApiComparisonResult Compare(string oldSource, string newSource, ApiComparisonOptions? options = null)
	{
		using var oldStream = Compiler.Compile(oldSource);
		using var newStream = Compiler.Compile(newSource);
		return ApiComparison.Compare(ApiReader.Read(oldStream), ApiReader.Read(newStream), options);
	}

	private static string Report(ApiComparisonResult result)
	{
		var builder = new StringBuilder();
		foreach (var change in result.Changes)
		{
			builder.Append(change.Where);
			if (change.Part.Length > 0)
				builder.Append(" › ").Append(change.Part);
			builder.Append("  ").Append(change.Kind.ToString().ToLowerInvariant());
			builder.Append(": ").Append(change.Old ?? "-").Append(" → ").Append(change.New ?? "-");
			builder.Append("  [").Append(change.Level);
			if (change.Breaks != ApiAudience.None)
				builder.Append(", breaks ").Append(change.Breaks).Append(' ').Append(change.Compatibility).Append(change.Counts ? "" : ", does not count");
			builder.Append(']');
			if (change.Note is not null)
				builder.Append(" (").Append(change.Note).Append(')');
			builder.Append('\n');
		}
		builder.Append("suggested: ").Append(result.Suggest()).Append('\n');
		return builder.ToString();
	}
}
