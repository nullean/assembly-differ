namespace Differ.Tests;

/// <summary>
/// Named pairs of an old and a new version of one library. The characterization tests diff every pair, so each
/// name stands for one kind of API change.
/// </summary>
public static class Scenarios
{
	private const string Base =
		"""
		namespace Lib;
		public class Widget
		{
			public int Size { get; set; }
			public void Draw() { }
			public string Name(int index) => "";
		}
		public interface IShape { double Area(); }
		""";

	public static readonly IReadOnlyDictionary<string, (string Old, string New)> All = new Dictionary<string, (string, string)>
	{
		["unchanged"] = (Base, Base),
		["method-added"] = (Base, Base.Replace("public void Draw() { }", "public void Draw() { }\n\tpublic void Erase() { }")),
		["method-removed"] = (Base, Base.Replace("public void Draw() { }", "")),
		["type-added"] = (Base, Base + "\npublic sealed class Gadget { public void Run() { } }\n"),
		["type-removed"] = (Base, Base.Replace("public interface IShape { double Area(); }", "")),
		["property-type-changed"] = (Base, Base.Replace("public int Size", "public long Size")),
		["parameter-added"] = (Base, Base.Replace("Name(int index)", "Name(int index, bool upper)")),
		["visibility-reduced"] = (Base, Base.Replace("public void Draw()", "internal void Draw()")),
		["interface-member-added"] = (Base, Base.Replace("double Area();", "double Area(); double Perimeter();")),
		["required-property-added"] = (Base, Base.Replace("public void Draw() { }", "public void Draw() { }\n\tpublic required string Label { get; init; }")),
		["added-and-removed"] = (Base, Base.Replace("public void Draw() { }", "public void Paint() { }")),
	};

	public static IEnumerable<string> Names() => All.Keys;

	public static Fixture Create(string name)
	{
		var (oldSource, newSource) = All[name];
		return Fixture.Create(oldSource, newSource);
	}
}
