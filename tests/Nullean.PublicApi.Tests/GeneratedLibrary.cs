using System.Text;

namespace Nullean.PublicApi.Tests;

/// <summary>
/// Writes a C# library from a sequence of choices, one decision per number, so CsCheck can generate libraries and shrink
/// a failing one: fewer and smaller numbers give fewer types, members and features. Every library compiles; one that
/// does not is a bug in this generator. It covers what the reader and the file must get right: kinds and modifiers,
/// generics with constraints and variance, base classes and interfaces, nullability, tuples, dynamic, ref kinds,
/// params, defaults, constants, required and init, accessors, events, operators, conversions, indexers, extension
/// methods, records, nested and protected types, and default interface members.
/// </summary>
public sealed class GeneratedLibrary
{
	private readonly uint[] _choices;
	private int _next;
	private readonly StringBuilder _source = new();
	private readonly List<TypeInfo> _types = [];
	private int _names;
	private string _namespace = "Lib";

	// what the type being written already has, which a second one of would clash
	private bool _hasConstructor, _hasOperator, _hasIndexer, _hasExtra;

	private GeneratedLibrary(uint[] choices) => _choices = choices;

	/// <summary>The C# source the choices describe.</summary>
	public static string Source(uint[] choices)
	{
		var library = new GeneratedLibrary(choices);
		library.Write();
		return library._source.ToString();
	}

	private enum Kind
	{
		Class,
		AbstractClass,
		SealedClass,
		StaticClass,
		Struct,
		ReadOnlyStruct,
		Interface,
		Enum,
		Delegate,
		Record
	}

	private enum Constraint
	{
		None,
		Class,
		NullableClass,
		Struct,
		NotNull,
		Unmanaged
	}

	private sealed record TypeParameter(string Name, Constraint Constraint, bool New, bool Comparable, string Variance);

	private sealed record TypeInfo(string Namespace, string Name, Kind Kind, List<TypeParameter> TypeParameters, bool Marker, bool HasStaticAbstract)
	{
		public bool IsValueType => Kind is Kind.Struct or Kind.ReadOnlyStruct or Kind.Enum;
	}

	/// <summary>The next choice below <paramref name="count"/>; 0 once the choices run out, so a short sequence is a small library.</summary>
	private int Next(int count) => _next < _choices.Length ? (int)(_choices[_next++] % (uint)count) : 0;

	private bool Chance(int outOf) => Next(outOf) == 1;

	private string Name(string prefix) => prefix + _names++;

	private void Write()
	{
		_source.AppendLine("#nullable enable").AppendLine("using System;").AppendLine("using System.Collections.Generic;");
		var namespaces = 1 + Next(3);
		for (var n = 0; n < namespaces; n++)
		{
			_namespace = "Lib" + (n == 0 ? "" : ".N" + n);
			_source.AppendLine($"namespace {_namespace}").AppendLine("{");
			var types = 1 + Next(5);
			for (var t = 0; t < types; t++)
				Type();
			if (n == 0 && Chance(2))
				Extensions();
			_source.AppendLine("}");
		}
	}

	// types

	private void Type()
	{
		var kind = (Kind)Next(10);
		var name = Name("T");
		var arity = kind is Kind.Enum || Chance(3) == false ? 0 : 1 + Next(2);
		var typeParameters = Enumerable.Range(0, arity).Select(i => TypeParameterOf(kind, name + "P" + i)).ToList();
		var marker = kind == Kind.Interface && Chance(2);
		// variance only where nothing uses the type parameter the wrong way round: an interface without members
		if (!marker)
			typeParameters = typeParameters.Select(p => p with { Variance = "" }).ToList();
		var info = new TypeInfo(_namespace, name, kind, typeParameters, marker, false);
		_hasConstructor = _hasOperator = _hasIndexer = _hasExtra = false;
		var context = new Context(typeParameters, [], kind);

		var attributes = Chance(6) ? "[Obsolete(\"use another\")] " : "";
		switch (kind)
		{
			case Kind.Enum:
				{
					_source.Append($"  {attributes}public enum {name}{(Chance(3) ? " : byte" : "")} {{ ");
					var values = 1 + Next(4);
					for (var v = 0; v < values; v++)
						_source.Append($"V{v}{(Chance(3) ? " = " + (v * 3) : "")}, ");
					_source.AppendLine("}");
					_types.Add(info);
					return;
				}
			case Kind.Delegate:
				_source.AppendLine(
					$"  {attributes}public delegate {ReturnType(context)} {name}{TypeParameterList(typeParameters)}({Parameters(context, allowDefaults: false)}){Constraints(typeParameters)};"
				);
				_types.Add(info);
				return;
		}

		var bases = Bases(kind, info);
		var keyword = kind switch
		{
			Kind.AbstractClass => "abstract class",
			Kind.SealedClass => "sealed class",
			Kind.StaticClass => "static class",
			Kind.Struct => "struct",
			Kind.ReadOnlyStruct => "readonly struct",
			Kind.Interface => "interface",
			Kind.Record => Chance(2) ? "sealed record" : "record",
			_ => "class",
		};
		_hasExtra = kind == Kind.Record && Chance(2);
		var primary = kind == Kind.Record ? $"(int Id, string? Label{(_hasExtra ? ", " + Type(context, valueOnly: false) + " Extra" : "")})" : "";
		_source.AppendLine($"  {attributes}public {keyword} {name}{TypeParameterList(typeParameters)}{primary}{bases}{Constraints(typeParameters)}").AppendLine(
			"  {"
		);
		var hasStaticAbstract = false;
		if (!marker)
		{
			if (kind is Kind.Class or Kind.AbstractClass or Kind.SealedClass)
				_source.AppendLine($"    {(kind == Kind.AbstractClass ? "protected" : "public")} {name}() {{ }}");
			var members = 1 + Next(6);
			for (var m = 0; m < members; m++)
				hasStaticAbstract |= Member(kind, name, context);
			if (kind is Kind.Class or Kind.AbstractClass && Chance(3))
				_source.AppendLine($"    {(Chance(2) ? "public" : "protected")} class {Name("Nested")} {{ public int Value {{ get; set; }} }}");
		}
		_source.AppendLine("  }");
		_types.Add(info with { HasStaticAbstract = hasStaticAbstract });
	}

	private TypeParameter TypeParameterOf(Kind kind, string name)
	{
		var constraint = (Constraint)Next(6);
		var allowsNew = constraint is Constraint.None or Constraint.Class && Chance(3);
		var comparable = !allowsNew && Chance(3);
		var variance = kind is Kind.Interface && constraint is Constraint.None or Constraint.Class && !comparable
			? Next(3) switch
			{
				1 => "out ",
				2 => "in ",
				_ => ""
			}
			: "";
		return new TypeParameter(name, constraint, allowsNew, comparable, variance);
	}

	private static string TypeParameterList(List<TypeParameter> parameters) =>
		parameters.Count == 0 ? "" : "<" + string.Join(", ", parameters.Select(p => p.Variance + p.Name)) + ">";

	private static string Constraints(List<TypeParameter> parameters)
	{
		var builder = new StringBuilder();
		foreach (var p in parameters)
		{
			var parts = new List<string>();
			parts.AddRange(p.Constraint switch
			{
				Constraint.Class => ["class"],
				Constraint.NullableClass => ["class?"],
				Constraint.Struct => ["struct"],
				Constraint.NotNull => ["notnull"],
				Constraint.Unmanaged => ["unmanaged"],
				_ => [],
			});
			if (p.Comparable)
				parts.Add($"IComparable<{p.Name}>");
			if (p.New)
				parts.Add("new()");
			if (parts.Count > 0)
				builder.Append($" where {p.Name} : {string.Join(", ", parts)}");
		}
		return builder.ToString();
	}

	/// <summary>A type argument that meets a type parameter's constraints.</summary>
	private static string ArgumentFor(TypeParameter parameter) => parameter.Constraint switch
	{
		Constraint.Class when parameter.New => "object",
		Constraint.Class or Constraint.NullableClass => "string",
		Constraint.None when parameter.New => "int",
		_ => "int",
	};

	/// <summary>A type with arguments that meet its constraints, in full, since one namespace cannot see another's types.</summary>
	private static string Closed(TypeInfo type) =>
		"global::" + type.Namespace + "." + type.Name +
			(type.TypeParameters.Count == 0 ? "" : "<" + string.Join(", ", type.TypeParameters.Select(ArgumentFor)) + ">");

	private string Bases(Kind kind, TypeInfo self)
	{
		var bases = new List<string>();
		if (kind is Kind.Class or Kind.AbstractClass or Kind.SealedClass && Chance(2))
		{
			// a base declared earlier, so there is no cycle; an abstract one only under an abstract class, which need not implement it
			var candidates = _types.Where(t => t.Kind is Kind.Class || (t.Kind == Kind.AbstractClass && kind == Kind.AbstractClass)).ToList();
			if (candidates.Count > 0)
				bases.Add(Closed(candidates[Next(candidates.Count)]));
		}
		if (kind is Kind.Interface && Chance(2))
		{
			// a marker interface stays one: it only extends markers
			var candidates = _types.Where(t => t.Kind == Kind.Interface && (t.Marker || !self.Marker)).ToList();
			if (candidates.Count > 0)
				bases.Add(Closed(candidates[Next(candidates.Count)]));
		}
		if (kind is Kind.Class or Kind.SealedClass or Kind.Struct or Kind.ReadOnlyStruct or Kind.Record && Chance(2))
		{
			// only interfaces with nothing to implement
			var markers = _types.Where(t => t.Kind == Kind.Interface && t.Marker).ToList();
			if (markers.Count > 0)
				bases.Add(Closed(markers[Next(markers.Count)]));
		}
		return bases.Count == 0 ? "" : " : " + string.Join(", ", bases.Distinct());
	}

	// members

	private sealed record Context(List<TypeParameter> TypeParameters, List<TypeParameter> MethodParameters, Kind Kind)
	{
		public IEnumerable<TypeParameter> All => TypeParameters.Concat(MethodParameters);
	}

	/// <summary>One member; whether it is a static abstract interface member, which keeps the interface out of type arguments.</summary>
	private bool Member(Kind kind, string typeName, Context context)
	{
		var isStatic = kind == Kind.StaticClass;
		var inInterface = kind == Kind.Interface;
		// a sealed record cannot have virtual members, so records get none
		var canBeVirtual = kind is Kind.Class or Kind.AbstractClass;
		var access = kind is Kind.Class or Kind.AbstractClass && Chance(4) ? "protected " : "public ";
		if (inInterface)
			access = "";
		var self = typeName + TypeParameterList(context.TypeParameters).Replace("out ", "").Replace("in ", "");
		var readOnly = kind == Kind.ReadOnlyStruct;
		var readOnlyMember = kind == Kind.Struct && Chance(3);

		// interfaces get methods more often: that is where default bodies and static abstract members are
		switch (inInterface && Chance(2) ? 3 : Next(9))
		{
			case 0 when !inInterface:
				{
					var type = Type(context, valueOnly: false);
					if (Chance(3) && !readOnly)
					{
						var (constType, value) = Next(4) switch
						{
							1 => ("string", "\"text\""),
							2 => ("double", "1.5"),
							3 => ("long", "9L"),
							_ => ("int", "7")
						};
						_source.AppendLine($"    {access}const {constType} {Name("C")} = {value};");
					}
					else
						_source.AppendLine(
							$"    {access}{(isStatic || Chance(3) ? "static " : "")}{(readOnly || Chance(2) ? "readonly " : "")}{type} {Name("F")};"
						);
					return false;
				}
			case 1 or 2:
				{
					var type = Type(context, valueOnly: false);
					var modifier = isStatic ? "static " : canBeVirtual && Chance(3) ? (kind == Kind.AbstractClass && Chance(2) ? "abstract " : "virtual ") : "";
					string accessors;
					if (inInterface || isStatic)
						accessors = Chance(2) ? "{ get; }" : "{ get; set; }";
					else if (modifier == "abstract ")
						accessors = "{ get; }";
					else if (readOnly)
						accessors = Chance(2) ? "{ get; }" : "{ get; init; }";
					else
						accessors = Next(4) switch
						{
							1 => "{ get; init; }",
							2 => kind is Kind.Class or Kind.AbstractClass && access == "public " ? "{ get; protected set; }" : "{ get; }",
							3 => "{ get; }",
							_ => "{ get; set; }"
						};
					var required = !isStatic && !inInterface && access == "public " &&
						kind is Kind.Class or Kind.SealedClass or Kind.Record && accessors.Contains("init") && Chance(2) ? "required " : "";
					_source.AppendLine($"    {access}{modifier}{required}{type} {Name("P")} {accessors}");
					return false;
				}
			case 3:
				{
					var methodParameters = Chance(3) ? [TypeParameterOf(Kind.Class, Name("M"))] : new List<TypeParameter>();
					var scope = context with { MethodParameters = methodParameters };
					var body = " => throw null!;";
					var modifier = isStatic ? "static " : "";
					var staticAbstract = false;
					if (inInterface)
					{
						switch (Next(4))
						{
							case 1:
								body = " { throw null!; }";
								break;
							case 2 when methodParameters.Count == 0:
								modifier = "static abstract ";
								body = ";";
								staticAbstract = true;
								break;
							default:
								body = ";";
								break;
						}
					}
					else if (canBeVirtual && Chance(3))
					{
						modifier = kind == Kind.AbstractClass && Chance(2) ? "abstract " : "virtual ";
						if (modifier == "abstract ")
							body = ";";
					}
					else if (readOnlyMember && !isStatic)
						modifier = "readonly ";
					var returnType = Chance(6) ? "ref readonly int" : ReturnType(scope);
					_source.AppendLine(
						$"    {access}{modifier}{returnType} {Name("M")}{TypeParameterList(methodParameters)}({Parameters(scope, allowDefaults: true)}){Constraints(methodParameters)}{body}"
					);
					return staticAbstract;
				}
			case 4 when !isStatic && !inInterface && kind is not (Kind.AbstractClass or Kind.Record) && !_hasConstructor:
				{
					_hasConstructor = true;
					// never parameterless: a class has that one already
					var parameters = Parameters(context, allowDefaults: true);
					_source.AppendLine($"    public {typeName}(long seed{(parameters.Length > 0 ? ", " + parameters : "")}) => throw null!;");
					return false;
				}
			case 5 when kind != Kind.ReadOnlyStruct:
				{
					var eventType = Next(4) switch
					{
						1 => "Action<int>",
						2 => "EventHandler",
						3 => "Func<string?>",
						_ => "Action"
					};
					_source.AppendLine($"    {access}{(isStatic ? "static " : "")}event {eventType}? {Name("E")};");
					return false;
				}
			case 6 when !isStatic && !inInterface && !_hasOperator:
				_hasOperator = true;
				_source.AppendLine($"    public static {self} operator +({self} left, {self} right) => throw null!;");
				if (Chance(2))
					_source.AppendLine($"    public static {(Chance(2) ? "implicit" : "explicit")} operator int({self} value) => 0;");
				return false;
			case 7 when !isStatic && !_hasIndexer:
				_hasIndexer = true;
				_source.AppendLine(
					$"    {access}{Type(context, valueOnly: false)} this[{Type(context, valueOnly: true)} key{(Chance(2) ? ", string other" : "")}] {(inInterface ? "{ get; }" : "{ get => throw null!; set { } }")}"
				);
				return false;
			default:
				_source.AppendLine(
					$"    {access}{(isStatic ? "static " : "")}{(readOnlyMember && !isStatic ? "readonly " : "")}void {Name("M")}({Parameters(context, allowDefaults: !inInterface)}){(inInterface ? ";" : " => throw null!;")}"
				);
				return false;
		}
	}

	private void Extensions()
	{
		_source.AppendLine("  public static class Extensions").AppendLine("  {");
		var count = 1 + Next(3);
		for (var i = 0; i < count; i++)
		{
			var target = Type(new Context([], [], Kind.StaticClass), valueOnly: false);
			// an extension method cannot extend dynamic
			if (target.StartsWith("dynamic", StringComparison.Ordinal))
				target = "string";
			_source.AppendLine($"    public static {ReturnType(new Context([], [], Kind.StaticClass))} {Name("X")}(this {target} self) => throw null!;");
		}
		_source.AppendLine("  }");
	}

	private string ReturnType(Context context) => Chance(4) ? "void" : Type(context, valueOnly: false);

	private string Parameters(Context context, bool allowDefaults)
	{
		var count = Next(4);
		var parameters = new List<string>();
		var optional = false;
		for (var i = 0; i < count; i++)
		{
			var name = "p" + i;
			var refKind = Next(8) switch
			{
				1 => "ref ",
				2 => "out ",
				3 => "in ",
				4 => "ref readonly ",
				_ => ""
			};
			if (refKind.Length > 0 && !optional)
			{
				parameters.Add($"{refKind}{Type(context, valueOnly: false)} {name}");
				continue;
			}
			// defaults only for types that have literals, and only at the end
			if (allowDefaults && (optional || Chance(3)))
			{
				var (type, value) = Next(5) switch
				{
					1 => ("string", "\"a\""),
					2 => ("string?", "null"),
					3 => ("double", "2.5"),
					4 => ("int?", "null"),
					_ => ("int", "3")
				};
				parameters.Add($"{type} {name} = {value}");
				optional = true;
				continue;
			}
			if (optional)
				break;
			parameters.Add($"{Type(context, valueOnly: false)} {name}");
		}
		if (!optional && Chance(5))
			parameters.Add($"params {(Chance(2) ? "int" : "string?")}[] rest");
		return string.Join(", ", parameters);
	}

	// type expressions

	private string Type(Context context, bool valueOnly, int depth = 0)
	{
		var type = Next(depth > 1 ? 5 : 12) switch
		{
			1 => "string",
			2 => "bool",
			3 => "double",
			4 => "object",
			5 when !valueOnly => $"List<{Type(context, false, depth + 1)}>",
			6 when !valueOnly => $"Dictionary<string, {Type(context, false, depth + 1)}>",
			7 when !valueOnly => $"{Type(context, false, depth + 1)}[{(Chance(4) ? "," : "")}]",
			8 => $"({Type(context, false, depth + 1)} first, {Type(context, false, depth + 1)}{(Chance(2) ? " second" : "")})",
			9 when context.All.Any() => context.All.ElementAt(Next(context.All.Count())).Name,
			10 when _types.Any(UsableAsArgument) => Closed(_types.Where(UsableAsArgument).ElementAt(Next(_types.Count(UsableAsArgument)))),
			11 when !valueOnly => Next(3) switch
			{
				1 => "dynamic",
				2 => "nint",
				_ => "Func<int, string?>"
			},
			_ => "int",
		};
		// a ? on a reference type annotates it; on a value type, or a struct type parameter, it is Nullable<T>
		if (Chance(4) && type is not ("dynamic" or "object" or "nint") && !type.EndsWith('?'))
			type += "?";
		return type;
	}

	private static bool UsableAsArgument(TypeInfo type) => type.Kind is not (Kind.StaticClass or Kind.Delegate) && !type.HasStaticAbstract;
}
