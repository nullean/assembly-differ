using System.Text;

namespace Differ.Tests;

/// <summary>
/// Generates an old and a new version of a library that covers much more of C# than <see cref="Api"/>: classes,
/// structs, interfaces, enums and delegates, generics with variance, nested types, overloads, by-ref parameters,
/// indexers, events, operators, explicit interface implementations, attributes and every accessibility. The new
/// version is the old one with some types and members added, removed or changed in one detail, so the two differ the
/// way releases do. A seed always produces the same pair, which is what lets the corpus snapshots stand still.
/// </summary>
public static class RichApi
{
	public static (string Old, string New) Pair(int seed)
	{
		var random = new Random(seed);
		var types = Enumerable.Range(0, random.Next(2, 6)).Select(i => TypeSpec.Create(random, i)).ToList();
		var oldTypes = types.Select(t => random.NextDouble() < 0.85 ? t : null).ToList();
		var newTypes = oldTypes.Select(
			(t, i) => t is null ? (random.NextDouble() < 0.3 ? types[i].Mutate(random) : null) : random.NextDouble() < 0.1 ? null : t.Mutate(random)
		).ToList();
		return (Render(oldTypes), Render(newTypes));
	}

	private static string Render(IEnumerable<TypeSpec?> types)
	{
		var source = new StringBuilder();
		source.AppendLine("#nullable enable");
		source.AppendLine("namespace Lib;");
		source.AppendLine("public interface ISample { int Value { get; } void Run(); }");
		foreach (var type in types)
			if (type is not null)
				type.Render(source);
		return source.ToString();
	}

	private static T Pick<T>(this Random random, params T[] values) => values[random.Next(values.Length)];

	internal enum TypeKind
	{
		Class,
		AbstractClass,
		SealedClass,
		StaticClass,
		Struct,
		Interface,
		Enum,
		Delegate,
	}

	private static readonly string[] AllAccess = ["public", "protected", "internal", "private", "protected internal", "private protected"];
	private static readonly string[] ValueTypeAccess = ["public", "internal", "private"];
	private static readonly string[] TypeAccess = ["public", "internal"];
	private static readonly string[] NestedTypeAccess = ["public", "protected", "internal", "private", "protected internal"];

	private static readonly string[] MemberTypes =
	[
		"int",
		"long",
		"string",
		"string?",
		"int?",
		"int[]",
		"int[,]",
		"object",
		"bool",
		"double",
		"System.DateTime",
		"System.Collections.Generic.List<int>",
		"System.Collections.Generic.Dictionary<string, System.Collections.Generic.List<int>>",
		"(int, string)",
		"System.Func<int, string>",
		"System.Collections.Generic.IEnumerable<string>",
		"nint",
		"System.Threading.CancellationToken",
		"T",
		"T[]",
		"System.Collections.Generic.List<T>",
	];

	private static readonly string[] ParameterModifiers = ["", "", "", "ref ", "out ", "in "];
	private static readonly string[] ParameterNames = ["a", "b", "value", "index"];
	private static readonly string[] Attributes =
	[
		"",
		"",
		"",
		"[System.Obsolete] ",
		"[System.Obsolete(\"gone\")] ",
		"[System.ComponentModel.EditorBrowsable(System.ComponentModel.EditorBrowsableState.Never)] "
	];

	internal sealed record TypeSpec(
		int Index,
		TypeKind Kind,
		string Access,
		string Generic,
		string Attribute,
		bool Explicit,
		string Underlying,
		IReadOnlyList<MemberSpec?> Members
	)
	{
		public static TypeSpec Create(Random random, int index)
		{
			var kind = (TypeKind)random.Next(0, 8);
			var generic = kind switch
			{
				TypeKind.Interface => random.Pick("", "<T>", "<out T>", "<in T>"),
				TypeKind.Enum or TypeKind.StaticClass => "",
				_ => random.Pick("", "", "<T>"),
			};
			var members = Enumerable.Range(0, random.Next(1, 9)).Select(i => (MemberSpec?)MemberSpec.Create(random, i)).ToList();
			return new TypeSpec(
				index,
				kind,
				random.Pick(TypeAccess),
				generic,
				random.Pick(Attributes),
				random.NextDouble() < 0.5,
				random.Pick("int", "long", "byte"),
				members
			);
		}

		public TypeSpec Mutate(Random random)
		{
			var members = Members.Select(
				(m, position) => m is null
					? (random.NextDouble() < 0.15 ? MemberSpec.Create(random, position) : null)
					: random.NextDouble() < 0.12 ? null : random.NextDouble() < 0.35 ? m.Mutate(random) : m
			).ToList();
			if (random.NextDouble() < 0.25)
				members.Add(MemberSpec.Create(random, Members.Count + random.Next(0, 3)));
			var type = this with { Members = members };
			return random.Next(0, 10) switch
			{
				0 => type with { Access = random.Pick(TypeAccess) },
				1 => type with { Attribute = random.Pick(Attributes) },
				2 => type with { Explicit = !Explicit },
				3 when Kind == TypeKind.Enum => type with { Underlying = random.Pick("int", "long", "byte") },
				4 when Kind == TypeKind.Interface => type with { Generic = random.Pick("<T>", "<out T>", "<in T>") },
				_ => type,
			};
		}

		private string Name => Kind switch
		{
			TypeKind.Interface => $"I{Index}",
			TypeKind.Enum => $"E{Index}",
			TypeKind.Delegate => $"D{Index}",
			TypeKind.Struct => $"S{Index}",
			_ => $"C{Index}",
		};

		private bool IsGeneric => Generic != "";

		private string SelfType => IsGeneric ? $"{Name}<T>" : Name;

		public void Render(StringBuilder source)
		{
			switch (Kind)
			{
				case TypeKind.Enum:
					source.AppendLine($"{Attribute}{Access} enum {Name} : {Underlying}");
					source.AppendLine("{");
					foreach (var member in Members.Where(m => m is not null))
						source.AppendLine($"\tV{member!.Index}{(member.Static ? $" = {member.Index * 10}" : "")},");
					source.AppendLine("}");
					return;
				case TypeKind.Delegate:
					var first = Members.FirstOrDefault(m => m is not null);
					var returnType = first is null ? "void" : TypeFor(first.Type);
					source.AppendLine($"{Attribute}{Access} delegate {returnType} {Name}{Generic}({(first is null ? "" : first.Parameters(this))});");
					return;
			}

			var keyword = Kind switch
			{
				TypeKind.AbstractClass => "abstract class",
				TypeKind.SealedClass => "sealed class",
				TypeKind.StaticClass => "static class",
				TypeKind.Struct => "struct",
				TypeKind.Interface => "interface",
				_ => "class",
			};
			var implements = Explicit && Kind is TypeKind.Class or TypeKind.SealedClass or TypeKind.AbstractClass or TypeKind.Struct ? " : ISample" : "";
			source.AppendLine($"{Attribute}{Access} {keyword} {Name}{Generic}{implements}");
			source.AppendLine("{");
			if (implements != "")
			{
				source.AppendLine("\tint ISample.Value => 0;");
				source.AppendLine("\tvoid ISample.Run() { }");
			}
			foreach (var member in Members)
				if (member is not null)
					member.Render(source, this);
			source.AppendLine("}");
		}

		public string TypeFor(int index)
		{
			var type = MemberTypes[index % MemberTypes.Length];
			// a type without a type parameter cannot use T, and a variant one keeps T out of its members altogether
			return Generic.Contains("out ") || Generic.Contains("in ")
				? (type.Contains('T') && !type.Contains("System") || type.Contains("<T>") ? "int" : type)
				: !IsGeneric && type.Contains('T') && !type.Contains("System") ? "int" : !IsGeneric && type.Contains("<T>") ? "int" : type;
		}

		public bool IsValueLike => Kind is TypeKind.Struct or TypeKind.StaticClass;

		public bool CanBeVirtual => Kind is TypeKind.Class or TypeKind.AbstractClass;

		public string AccessFor(string access) => Kind switch
		{
			TypeKind.Struct or TypeKind.StaticClass => ValueTypeAccess.Contains(access) ? access : "public",
			TypeKind.SealedClass => access.Contains("protected") ? "public" : access,
			_ => access,
		};

		public string Self => SelfType;

		public string TypeName => Name;
	}

	// distinct per member index, so overloads that differ only in this parameter never collide
	private static readonly string[] DistinctTypes =
	[
		"byte",
		"short",
		"sbyte",
		"ushort",
		"uint",
		"ulong",
		"char",
		"float",
		"decimal",
		"System.Guid",
		"System.TimeSpan",
		"System.Uri"
	];
	private static readonly string[] Operators = ["+", "-", "*", "/", "%", "&", "|", "^"];

	internal enum MemberKind
	{
		Method,
		GenericMethod,
		Property,
		Field,
		Event,
		Constructor,
		Indexer,
		Operator,
		Nested,
	}

	internal sealed record MemberSpec(
		int Index,
		MemberKind Kind,
		string Access,
		bool Static,
		bool Virtual,
		int Type,
		int ParameterType,
		string ParameterModifier,
		string ParameterName,
		int ParameterCount,
		string Accessors,
		string Attribute,
		bool Required
	)
	{
		public static MemberSpec Create(Random random, int index) =>
			new(
				index,
				(MemberKind)random.Next(0, 9),
				random.Pick(AllAccess),
				random.NextDouble() < 0.2,
				random.NextDouble() < 0.25,
				random.Next(0, MemberTypes.Length),
				random.Next(0, MemberTypes.Length),
				random.Pick(ParameterModifiers),
				random.Pick(ParameterNames),
				random.Next(0, 3),
				random.Pick("get; set;", "get;", "get; init;", "get; private set;"),
				random.Pick(Attributes),
				random.NextDouble() < 0.15
			);

		public MemberSpec Mutate(Random random) => random.Next(0, 10) switch
		{
			0 => this with { Access = random.Pick(AllAccess) },
			1 => this with { Static = !Static },
			2 => this with { Virtual = !Virtual },
			3 => this with { Type = random.Next(0, MemberTypes.Length) },
			4 => this with { ParameterType = random.Next(0, MemberTypes.Length) },
			5 => this with { ParameterModifier = random.Pick(ParameterModifiers) },
			6 => this with { ParameterName = random.Pick(ParameterNames) },
			7 => this with { ParameterCount = random.Next(0, 3) },
			8 => this with { Accessors = random.Pick("get; set;", "get;", "get; init;", "get; private set;") },
			_ => this with { Attribute = random.Pick(Attributes), Required = !Required },
		};

		public string Parameters(TypeSpec type, bool allowModifiers = true)
		{
			var parameters = new List<string>();
			for (var i = 0; i < ParameterCount; i++)
			{
				var modifier = allowModifiers && i == 0 ? ParameterModifier : "";
				var name = i == 0 ? ParameterName : $"p{i}";
				parameters.Add($"{modifier}{type.TypeFor(ParameterType + i)} {name}");
			}
			return string.Join(", ", parameters);
		}

		private bool HasOut => ParameterCount > 0 && ParameterModifier == "out ";

		public void Render(StringBuilder source, TypeSpec type)
		{
			var isInterface = type.Kind == TypeKind.Interface;
			var isStatic = type.Kind == TypeKind.StaticClass || Static && !isInterface;
			var access = isInterface ? "" : type.AccessFor(Access) + " ";
			var abstractMember = type.Kind == TypeKind.AbstractClass && Virtual && !isStatic && !access.StartsWith("private ", StringComparison.Ordinal);
			var virtualMember = !abstractMember && type.CanBeVirtual && Virtual && !isStatic && !access.StartsWith("private ", StringComparison.Ordinal);
			var modifiers = (isStatic ? "static " : "") + (abstractMember ? "abstract " : "") + (virtualMember ? "virtual " : "");
			var memberType = type.TypeFor(Type);
			var body = isInterface || abstractMember ? ";" : " => throw null!;";
			var prefix = $"\t{Attribute}{access}{modifiers}";

			switch (Kind)
			{
				case MemberKind.Method:
					source.AppendLine($"{prefix}{memberType} M{Index}({Parameters(type)}){body}");
					break;
				case MemberKind.GenericMethod:
					source.AppendLine($"{prefix}U G{Index}<U>(U {ParameterName}{(ParameterCount > 1 ? $", {type.TypeFor(ParameterType)} p1" : "")}){body}");
					break;
				case MemberKind.Property:
					{
						var accessors = isInterface || abstractMember ? Accessors.Replace("private set;", "") : Accessors;
						var required = Required && !isStatic && !isInterface && !abstractMember &&
							(accessors.Contains("set;") || accessors.Contains("init;")) && !accessors.Contains("private") && access == "public ";
						if (isStatic && accessors.Contains("init;"))
							accessors = "get; set;";
						// a private setter needs a property that is more visible than private
						if (accessors.Contains("private set;") && access is "private " or "")
							accessors = "get; set;";
						source.AppendLine($"{prefix}{(required ? "required " : "")}{memberType} P{Index} {{ {accessors} }}");
						break;
					}
				case MemberKind.Field when !isInterface:
					{
						var constant = Static && Virtual && memberType is "int" or "long" or "string";
						var field = constant
							? $"const {memberType} F{Index} = default!;"
							: $"{(isStatic ? "static " : "")}{(Virtual ? "readonly " : "")}{memberType} F{Index};";
						source.AppendLine($"\t{Attribute}{access}{field}");
						break;
					}
				case MemberKind.Event:
					source.AppendLine($"{prefix}event System.EventHandler{(isInterface ? "" : "?")} Ev{Index};");
					break;
				case MemberKind.Constructor when type.Kind is not (TypeKind.StaticClass or TypeKind.Interface) && !HasOut:
					{
						// the last parameter's type is distinct per member index, which keeps the overloads apart
						var ctorAccess = type.Kind == TypeKind.Struct && ParameterCount == 0 ? "public " : access;
						var distinct = $"{DistinctTypes[Index % DistinctTypes.Length]} ctor{Index}";
						var ctorParameters = ParameterCount == 0
							? (type.Kind == TypeKind.Struct ? "" : distinct)
							: $"{Parameters(type, allowModifiers: false)}, {distinct}";
						if (ParameterCount == 0 && type.Kind == TypeKind.Struct && Index != 0)
							break;
						source.AppendLine($"\t{Attribute}{ctorAccess}{type.TypeName}({ctorParameters}) {{ }}");
						break;
					}
					break;
				case MemberKind.Indexer when type.Kind is not (TypeKind.StaticClass or TypeKind.Interface):
					source.AppendLine(
						$"\t{Attribute}{access}{(virtualMember ? "virtual " : "")}{memberType} this[{type.TypeFor(ParameterType)} i, {DistinctTypes[Index % DistinctTypes.Length]} at{Index}] {{ get => throw null!; set {{ }} }}"
					);
					break;
				case MemberKind.Operator when type.Kind is not (TypeKind.StaticClass or TypeKind.Interface) && Index < Operators.Length:
					source.AppendLine(
						$"\t{Attribute}public static {type.Self} operator {Operators[Index]}({type.Self} left, {(Static ? "int" : type.Self)} right) => left;"
					);
					break;
				case MemberKind.Nested when type.Kind is not TypeKind.Interface:
					{
						var nestedAccess = type.Kind is TypeKind.Struct or TypeKind.StaticClass or TypeKind.SealedClass
							? (Access.Contains("protected") ? "public" : Access)
							: NestedTypeAccess.Contains(Access) ? Access : "public";
						source.AppendLine(
							$"\t{Attribute}{nestedAccess} {(Static ? "static " : "")}class N{Index} {{ public {(Static ? "static " : "")}{memberType} X{Index}() => throw null!; }}"
						);
						break;
					}
			}
		}
	}
}
