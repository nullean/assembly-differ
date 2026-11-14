#nullable enable
using System;
using System.Collections.Generic;
using System.Linq;
using System.Text;

namespace Nullean.PublicApi;

/// <summary>
/// Writes an <see cref="ApiAssembly"/> as a <c>.api</c> file: an indented tree of namespaces, types and members, one
/// C# declaration per line. A header names the types from other assemblies the API uses, namespace by namespace, so
/// the declarations can use short names; a name two namespaces share is written in full.
/// </summary>
public static class ApiWriter
{
	private const string Indent = "  ";

	/// <summary>The <c>.api</c> text for <paramref name="assembly"/>.</summary>
	public static string Write(ApiAssembly assembly)
	{
		var names = new Names(assembly);
		var builder = new StringBuilder();
		builder.Append("assembly ").Append(assembly.Name).Append('\n');
		// every type from another assembly is listed, a value type in a "using struct" line, so a reader of the file
		// can tell List<int>? from int? without that assembly
		// and an interface a base list names in a "using interface" line, which tells it from a base class
		var namespaces = names
			.External
			.Select(t => t.Namespace)
			.Concat(names.ExternalValueTypes.Select(t => t.Namespace))
			.Concat(names.ExternalInterfaces.Select(t => t.Namespace))
			.Distinct()
			.OrderBy(n => n, StringComparer.Ordinal);
		foreach (var ns in namespaces)
		{
			static string Display((string Namespace, string Path) type) => string.Join(".", type.Path.Split('/').Select(Unbound));

			var references = names
				.External
				.Where(t => t.Namespace == ns && !names.ExternalValueTypes.Contains(t) && !names.ExternalInterfaces.Contains(t))
				.Select(t => Unbound(t.Name))
				.ToList();
			var values = names.ExternalValueTypes.Where(t => t.Namespace == ns).Select(Display).ToList();
			var interfaces = names.ExternalInterfaces.Where(t => t.Namespace == ns).Select(Display).ToList();
			var label = ns.Length == 0 ? "global" : ns;
			if (references.Count > 0)
				builder.Append("using ").Append(label).Append(": ").Append(string.Join(", ", references.OrderBy(n => n, StringComparer.Ordinal))).Append('\n');
			if (values.Count > 0)
				builder
					.Append("using struct ")
					.Append(label)
					.Append(": ")
					.Append(string.Join(", ", values.OrderBy(n => n, StringComparer.Ordinal)))
					.Append('\n');
			if (interfaces.Count > 0)
				builder
					.Append("using interface ")
					.Append(label)
					.Append(": ")
					.Append(string.Join(", ", interfaces.OrderBy(n => n, StringComparer.Ordinal)))
					.Append('\n');
		}
		foreach (var ns in assembly.Types.GroupBy(t => t.Namespace))
		{
			builder.Append('\n').Append("namespace ").Append(ns.Key.Length == 0 ? "global" : ns.Key).Append('\n');
			var first = true;
			foreach (var type in ns)
			{
				if (!first)
					builder.Append('\n');
				first = false;
				new Writer(builder, names).Type(type, 1);
			}
		}
		return builder.ToString();
	}

	/// <summary><c>Dictionary`2</c> as <c>Dictionary&lt;,&gt;</c>.</summary>
	private static string Unbound(string metadataName)
	{
		var arity = TypeReferenceProvider.Arity(metadataName);
		return arity == 0 ? metadataName : NonGeneric(metadataName) + "<" + new string(',', arity - 1) + ">";
	}

	internal static string NonGeneric(string metadataName)
	{
		var tick = metadataName.LastIndexOf('`');
		return tick < 0 ? metadataName : metadataName.Substring(0, tick);
	}

	/// <summary>Decides which top-level types are written by their short name, and which other assemblies' types the header lists.</summary>
	private sealed class Names
	{
		private readonly HashSet<(string Namespace, string Name)> _qualified = [];
		private readonly HashSet<(string Namespace, string Path)> _values = [];
		private readonly HashSet<(string Namespace, string Path)> _interfaces = [];

		public Names(ApiAssembly assembly)
		{
			var own = new HashSet<(string, string)>();
			foreach (var type in assembly.Types)
				own.Add((type.Namespace, type.Name));
			var used = new HashSet<(string Namespace, string Name)>(own);
			foreach (var type in assembly.Types)
				Collect(type, used);
			foreach (var clash in used.GroupBy(t => t.Name).Where(g => g.Count() > 1))
				foreach (var type in clash)
					_qualified.Add(type);
			External = used.Where(t => !own.Contains(t)).ToList();
			ExternalValueTypes = new HashSet<(string Namespace, string Path)>(_values.Where(v => !own.Contains((v.Namespace, v.Path.Split('/')[0]))));
			ExternalInterfaces = new HashSet<(string Namespace, string Path)>(_interfaces.Where(v => !own.Contains((v.Namespace, v.Path.Split('/')[0]))));
		}

		/// <summary>The top-level types from other assemblies, by namespace and metadata name.</summary>
		public IReadOnlyList<(string Namespace, string Name)> External { get; }

		/// <summary>The value types from other assemblies, by namespace and nesting path: <c>Outer`1/Inner</c>.</summary>
		public HashSet<(string Namespace, string Path)> ExternalValueTypes { get; }

		/// <summary>The interfaces from other assemblies that a base list names.</summary>
		public HashSet<(string Namespace, string Path)> ExternalInterfaces { get; }

		public bool IsQualified(string ns, string name) => _qualified.Contains((ns, name));

		internal static string Path(ApiNamedType named) => named.DeclaringType is null ? named.Name : Path(named.DeclaringType) + "/" + named.Name;

		private void Collect(ApiType type, HashSet<(string, string)> used)
		{
			foreach (var attribute in type.Attributes)
				Collect(attribute.Type, used);
			if (type.BaseType is { } baseType)
				Collect(baseType, used);
			foreach (var item in type.Interfaces)
			{
				Collect(item, used);
				if (item is ApiNamedType named)
					_interfaces.Add((named.Namespace, Path(named)));
			}
			foreach (var parameter in type.TypeParameters)
				Collect(parameter, used);
			foreach (var member in type.Members)
			{
				foreach (var attribute in member.Attributes)
					Collect(attribute.Type, used);
				switch (member)
				{
					case ApiField field:
						Collect(field.Type, used);
						break;
					case ApiProperty property:
						Collect(property.Type, used);
						foreach (var parameter in property.Parameters)
							Collect(parameter, used);
						break;
					case ApiEvent @event:
						Collect(@event.Type, used);
						break;
					case ApiMethod method:
						Collect(method.ReturnType, used);
						foreach (var attribute in method.ReturnAttributes)
							Collect(attribute.Type, used);
						foreach (var parameter in method.TypeParameters)
							Collect(parameter, used);
						foreach (var parameter in method.Parameters)
							Collect(parameter, used);
						break;
				}
			}
			foreach (var nested in type.NestedTypes)
				Collect(nested, used);
		}

		private void Collect(ApiTypeParameter parameter, HashSet<(string, string)> used)
		{
			foreach (var constraint in parameter.ConstraintTypes)
				Collect(constraint, used);
		}

		private void Collect(ApiParameter parameter, HashSet<(string, string)> used)
		{
			Collect(parameter.Type, used);
			foreach (var attribute in parameter.Attributes)
				Collect(attribute.Type, used);
		}

		private void Collect(ApiTypeReference type, HashSet<(string, string)> used)
		{
			switch (type)
			{
				case ApiNamedType named:
					var outermost = named;
					while (outermost.DeclaringType is not null)
						outermost = outermost.DeclaringType;
					// Nullable<T> is written T?
					if (Keyword(outermost) is null && !named.IsNullableValueType)
					{
						used.Add((outermost.Namespace, outermost.Name));
						if (named.IsValueType)
							_values.Add((named.Namespace, Path(named)));
					}
					for (var current = named; current is not null; current = current.DeclaringType)
						foreach (var argument in current.TypeArguments)
							Collect(argument, used);
					break;
				case ApiTupleType tuple:
					foreach (var element in tuple.Elements)
						Collect(element.Type, used);
					break;
				case ApiArrayType array:
					Collect(array.ElementType, used);
					break;
				case ApiPointerType pointer:
					Collect(pointer.ElementType, used);
					break;
				case ApiFunctionPointerType function:
					Collect(function.ReturnType, used);
					foreach (var parameter in function.ParameterTypes)
						Collect(parameter, used);
					break;
				case ApiModifiedType modified:
					Collect(modified.Type, used);
					Collect(modified.Modifier, used);
					break;
			}
		}
	}

	internal static string? Keyword(ApiNamedType type) =>
		type is { Namespace: "System", DeclaringType: null, TypeArguments.Count: 0 }
			? type.Name switch
			{
				"Boolean" => "bool",
				"Byte" => "byte",
				"SByte" => "sbyte",
				"Char" => "char",
				"Int16" => "short",
				"UInt16" => "ushort",
				"Int32" => "int",
				"UInt32" => "uint",
				"Int64" => "long",
				"UInt64" => "ulong",
				"Single" => "float",
				"Double" => "double",
				"Decimal" => "decimal",
				"String" => "string",
				"Object" => "object",
				"Void" => "void",
				_ => null,
			}
			: null;

	private sealed class Writer(StringBuilder builder, Names names)
	{
		public void Type(ApiType type, int depth)
		{
			var invoke = type.Kind == ApiTypeKind.Delegate ? type.Members.OfType<ApiMethod>().FirstOrDefault() : null;
			if (invoke is not null)
				Attributes(invoke.ReturnAttributes, depth, "return");
			Attributes(type.Attributes, depth, null);
			Line(depth);
			builder.Append(Access(type.Accessibility)).Append(' ');
			if (invoke is not null)
			{
				builder.Append("delegate ").Append(RefKind(invoke.ReturnRefKind)).Append(Reference(invoke.ReturnType)).Append(' ').Append(Declared(type.Name));
				TypeParameters(type.TypeParameters);
				Parameters(invoke.Parameters, false);
				Constraints(type.TypeParameters);
				builder.Append('\n');
				return;
			}
			var modifiers = type.Modifiers;
			if ((modifiers & ApiTypeModifiers.Static) != 0)
				builder.Append("static ");
			if ((modifiers & ApiTypeModifiers.Abstract) != 0)
				builder.Append("abstract ");
			if ((modifiers & ApiTypeModifiers.Sealed) != 0)
				builder.Append("sealed ");
			if ((modifiers & ApiTypeModifiers.ReadOnly) != 0)
				builder.Append("readonly ");
			if ((modifiers & ApiTypeModifiers.Ref) != 0)
				builder.Append("ref ");
			builder.Append(type.Kind switch
			{
				ApiTypeKind.Class when (modifiers & ApiTypeModifiers.Record) != 0 => "record",
				ApiTypeKind.Class => "class",
				ApiTypeKind.Struct => "struct",
				ApiTypeKind.Interface => "interface",
				_ => "enum",
			});
			builder.Append(' ').Append(Declared(type.Name));
			TypeParameters(type.TypeParameters);
			var bases = new List<ApiTypeReference>();
			if (type.BaseType is { } baseType)
				bases.Add(baseType);
			bases.AddRange(type.Interfaces);
			if (bases.Count > 0)
				builder.Append(" : ").Append(string.Join(", ", bases.Select(Reference)));
			Constraints(type.TypeParameters);
			builder.Append('\n');

			foreach (var member in type.Members)
				Member(member, type, depth + 1);
			foreach (var nested in type.NestedTypes)
				Type(nested, depth + 1);
		}

		private void Member(ApiMember member, ApiType type, int depth)
		{
			if (member is ApiMethod { ReturnAttributes: var returnAttributes })
				Attributes(returnAttributes, depth, "return");
			// metadata names an indexer by the attribute that renamed it in source
			if (member is ApiProperty { Parameters.Count: > 0, Name: not "Item" } indexer)
			{
				Line(depth);
				builder.Append("[IndexerName(").Append(ApiReader.Constant(indexer.Name)).Append(")]\n");
			}
			Attributes(member.Attributes, depth, null);
			Line(depth);
			var inInterface = type.Kind == ApiTypeKind.Interface;
			switch (member)
			{
				case ApiField field when type.Kind == ApiTypeKind.Enum:
					builder.Append(field.Name).Append(" = ").Append(field.Constant);
					break;
				case ApiField field:
					builder.Append(Access(field.Accessibility)).Append(' ');
					if (field.Constant is not null)
						builder.Append("const ");
					else if (field.IsStatic)
						builder.Append("static ");
					if (field.IsReadOnly)
						builder.Append("readonly ");
					if (field.IsVolatile)
						builder.Append("volatile ");
					if (field.IsRequired)
						builder.Append("required ");
					builder.Append(Reference(field.Type)).Append(' ').Append(field.Name);
					if (field.Constant is not null)
						builder.Append(" = ").Append(field.Constant);
					break;
				case ApiProperty property:
					MemberStart(property.Accessibility, property.Modifiers, inInterface);
					if (property.IsRequired)
						builder.Append("required ");
					builder.Append(RefKind(property.RefKind)).Append(Reference(property.Type)).Append(' ');
					if (property.Parameters.Count > 0)
					{
						builder.Append("this[");
						ParameterList(property.Parameters, false);
						builder.Append(']');
					}
					else
						builder.Append(property.Name);
					builder.Append(" {");
					Accessor(property.Getter, property.Accessibility, "get");
					Accessor(property.Setter, property.Accessibility, property.Setter is { IsInit: true } ? "init" : "set");
					builder.Append(" }");
					if (inInterface && HasBody(property.Modifiers))
						builder.Append(" { … }");
					break;
				case ApiEvent @event:
					MemberStart(@event.Accessibility, @event.Modifiers, inInterface);
					builder.Append("event ").Append(Reference(@event.Type)).Append(' ').Append(@event.Name);
					if (inInterface && HasBody(@event.Modifiers))
						builder.Append(" { … }");
					break;
				case ApiMethod { Kind: ApiMethodKind.Constructor } constructor:
					builder.Append(Access(constructor.Accessibility)).Append(' ').Append(Declared(type.Name));
					Parameters(constructor.Parameters, false);
					break;
				case ApiMethod method:
					MemberStart(method.Accessibility, method.Modifiers, inInterface);
					var returnType = RefKind(method.ReturnRefKind) + Reference(method.ReturnType);
					switch (method.Kind)
					{
						case ApiMethodKind.Conversion:
							builder.Append(method.Name == "op_Implicit" ? "implicit" : "explicit").Append(" operator ").Append(returnType);
							break;
						case ApiMethodKind.Operator:
							builder.Append(returnType).Append(" operator ").Append(Operator(method.Name));
							break;
						default:
							builder.Append(returnType).Append(' ').Append(method.Name);
							TypeParameters(method.TypeParameters);
							break;
					}
					Parameters(method.Parameters, method.IsExtension);
					Constraints(method.TypeParameters);
					if (inInterface && HasBody(method.Modifiers))
						builder.Append(" { … }");
					break;
			}
			builder.Append('\n');
		}

		private void MemberStart(ApiAccessibility accessibility, ApiMemberModifiers modifiers, bool inInterface)
		{
			// an interface member is public and abstract unless it says otherwise; a default implementation shows a body
			if (!inInterface || accessibility != ApiAccessibility.Public)
				builder.Append(Access(accessibility)).Append(' ');
			if ((modifiers & ApiMemberModifiers.Static) != 0)
				builder.Append("static ");
			if (inInterface)
			{
				if ((modifiers & ApiMemberModifiers.Static) != 0 && (modifiers & ApiMemberModifiers.Abstract) != 0)
					builder.Append("abstract ");
				// an instance member with a body that implementers cannot override
				if ((modifiers & (ApiMemberModifiers.Static | ApiMemberModifiers.Abstract | ApiMemberModifiers.Virtual)) == 0)
					builder.Append("sealed ");
				return;
			}
			if ((modifiers & ApiMemberModifiers.Sealed) != 0)
				builder.Append("sealed ");
			if ((modifiers & ApiMemberModifiers.Abstract) != 0)
				builder.Append("abstract ");
			if ((modifiers & ApiMemberModifiers.Virtual) != 0)
				builder.Append("virtual ");
			if ((modifiers & ApiMemberModifiers.Override) != 0)
				builder.Append("override ");
			if ((modifiers & ApiMemberModifiers.ReadOnly) != 0)
				builder.Append("readonly ");
		}

		/// <summary>An interface member with a default implementation: virtual, or sealed, which is neither virtual nor abstract.</summary>
		private static bool HasBody(ApiMemberModifiers modifiers) =>
			(modifiers & ApiMemberModifiers.Virtual) != 0 || (modifiers & (ApiMemberModifiers.Static | ApiMemberModifiers.Abstract)) == 0;

		private void Accessor(ApiAccessor? accessor, ApiAccessibility propertyAccessibility, string keyword)
		{
			if (accessor is null)
				return;
			builder.Append(' ');
			if (accessor.Accessibility != propertyAccessibility)
				builder.Append(Access(accessor.Accessibility)).Append(' ');
			if (accessor.IsReadOnly)
				builder.Append("readonly ");
			builder.Append(keyword).Append(';');
		}

		private void Parameters(IReadOnlyList<ApiParameter> parameters, bool isExtension)
		{
			builder.Append('(');
			ParameterList(parameters, isExtension);
			builder.Append(')');
		}

		private void ParameterList(IReadOnlyList<ApiParameter> parameters, bool isExtension)
		{
			for (var i = 0; i < parameters.Count; i++)
			{
				var parameter = parameters[i];
				if (i > 0)
					builder.Append(", ");
				foreach (var attribute in parameter.Attributes)
					builder.Append(Attribute(attribute, null)).Append(' ');
				if (i == 0 && isExtension)
					builder.Append("this ");
				if (parameter.IsScoped)
					builder.Append("scoped ");
				if (parameter.IsParams)
					builder.Append("params ");
				builder.Append(parameter.RefKind switch
				{
					ApiRefKind.Ref => "ref ",
					ApiRefKind.Out => "out ",
					ApiRefKind.In => "in ",
					ApiRefKind.RefReadOnly => "ref readonly ",
					_ => string.Empty,
				});
				builder.Append(Reference(parameter.Type)).Append(' ').Append(parameter.Name);
				if (parameter.DefaultValue is not null)
					builder.Append(" = ").Append(parameter.DefaultValue);
			}
		}

		private void TypeParameters(IReadOnlyList<ApiTypeParameter> parameters)
		{
			if (parameters.Count == 0)
				return;
			builder.Append('<');
			builder.Append(
				string.Join(
					", ",
					parameters.Select(
						p => (p.Variance switch
						{
							ApiVariance.Out => "out ",
							ApiVariance.In => "in ",
							_ => string.Empty
						}) + p.Name
					)
				)
			);
			builder.Append('>');
		}

		private void Constraints(IReadOnlyList<ApiTypeParameter> parameters)
		{
			foreach (var parameter in parameters)
			{
				var constraints = new List<string>();
				var flags = parameter.Constraints;
				if ((flags & ApiConstraints.Class) != 0)
					constraints.Add("class");
				if ((flags & ApiConstraints.NullableClass) != 0)
					constraints.Add("class?");
				if ((flags & ApiConstraints.Struct) != 0)
					constraints.Add("struct");
				if ((flags & ApiConstraints.Unmanaged) != 0)
					constraints.Add("unmanaged");
				if ((flags & ApiConstraints.NotNull) != 0)
					constraints.Add("notnull");
				constraints.AddRange(parameter.ConstraintTypes.Select(Reference));
				if ((flags & ApiConstraints.New) != 0)
					constraints.Add("new()");
				if ((flags & ApiConstraints.AllowsRefStruct) != 0)
					constraints.Add("allows ref struct");
				if (constraints.Count > 0)
					builder.Append(" where ").Append(parameter.Name).Append(" : ").Append(string.Join(", ", constraints));
			}
		}

		private void Attributes(IReadOnlyList<ApiAttribute> attributes, int depth, string? target)
		{
			foreach (var attribute in attributes)
			{
				Line(depth);
				builder.Append(Attribute(attribute, target)).Append('\n');
			}
		}

		private string Attribute(ApiAttribute attribute, string? target)
		{
			var name = Reference(attribute.Type);
			if (name.EndsWith("Attribute", StringComparison.Ordinal) && name.Length > "Attribute".Length)
				name = name.Substring(0, name.Length - "Attribute".Length);
			var arguments = attribute.Arguments.Count == 0 ? string.Empty : "(" + string.Join(", ", attribute.Arguments) + ")";
			return "[" + (target is null ? string.Empty : target + ": ") + name + arguments + "]";
		}

		private void Line(int depth)
		{
			for (var i = 0; i < depth; i++)
				builder.Append(Indent);
		}

		/// <summary>A declared type's or constructor's name: <c>Box`1</c> as <c>Box</c>.</summary>
		private static string Declared(string metadataName) => NonGeneric(metadataName);

		private static string Access(ApiAccessibility accessibility) => accessibility switch
		{
			ApiAccessibility.Public => "public",
			ApiAccessibility.Protected => "protected",
			_ => "protected internal",
		};

		private static string RefKind(ApiRefKind refKind) => refKind switch
		{
			ApiRefKind.Ref => "ref ",
			ApiRefKind.RefReadOnly => "ref readonly ",
			_ => string.Empty,
		};

		public string Reference(ApiTypeReference type)
		{
			var nullable = type.IsNullable ? "?" : string.Empty;
			switch (type)
			{
				case ApiNamedType { IsNullableValueType: true } nullableValue:
					return Reference(nullableValue.TypeArguments[0]) + "?";
				case ApiNamedType named:
					{
						if (Keyword(named) is { } keyword)
							return keyword + nullable;
						string prefix;
						if (named.DeclaringType is not null)
							prefix = Reference(named.DeclaringType with { IsNullable = false }) + ".";
						else if (names.IsQualified(named.Namespace, named.Name))
							prefix = named.Namespace.Length == 0 ? "global::" : named.Namespace + ".";
						else
							prefix = string.Empty;
						var arguments = named.TypeArguments.Count == 0 ? string.Empty : "<" + string.Join(", ", named.TypeArguments.Select(Reference)) + ">";
						return prefix + NonGeneric(named.Name) + arguments + nullable;
					}
				// C# has no syntax for a tuple of one
				case ApiTupleType { Elements: [var single] } tuple:
					return "System.ValueTuple<" + Reference(single.Type) + ">" + nullable;
				case ApiTupleType tuple:
					return "(" + string.Join(", ", tuple.Elements.Select(e => Reference(e.Type) + (e.Name is null ? string.Empty : " " + e.Name))) + ")" +
						nullable;
				case ApiArrayType:
					{
						// C# writes an array of arrays outermost rank first: int[][,] holds int[,]
						var ranks = new StringBuilder();
						var current = type;
						while (current is ApiArrayType level)
						{
							ranks.Append('[').Append(',', level.Rank - 1).Append(']');
							if (level.IsNullable)
								ranks.Append('?');
							current = level.ElementType;
						}
						return Reference(current) + ranks;
					}
				case ApiPointerType pointer:
					return Reference(pointer.ElementType) + "*";
				case ApiFunctionPointerType function:
					return "delegate*" + (function.IsUnmanaged ? " unmanaged" : string.Empty) + "<" + string.Join(
						", ",
						function.ParameterTypes.Append(function.ReturnType).Select(Reference)
					) + ">";
				case ApiTypeParameterReference parameter:
					return parameter.Name + nullable;
				case ApiDynamicType:
					return "dynamic" + nullable;
				case ApiNativeIntegerType native:
					return native.IsUnsigned ? "nuint" : "nint";
				case ApiModifiedType modified:
					return (modified.IsRequired ? "modreq(" : "modopt(") + Reference(modified.Modifier) + ") " + Reference(modified.Type);
				case ApiByReferenceType reference:
					return "ref " + Reference(reference.ElementType);
				default:
					return type.ToString() ?? string.Empty;
			}
		}

		private static string Operator(string name) => name switch
		{
			"op_Addition" => "+",
			"op_Subtraction" => "-",
			"op_Multiply" => "*",
			"op_Division" => "/",
			"op_Modulus" => "%",
			"op_BitwiseAnd" => "&",
			"op_BitwiseOr" => "|",
			"op_ExclusiveOr" => "^",
			"op_LeftShift" => "<<",
			"op_RightShift" => ">>",
			"op_UnsignedRightShift" => ">>>",
			"op_Equality" => "==",
			"op_Inequality" => "!=",
			"op_LessThan" => "<",
			"op_GreaterThan" => ">",
			"op_LessThanOrEqual" => "<=",
			"op_GreaterThanOrEqual" => ">=",
			"op_UnaryPlus" => "+",
			"op_UnaryNegation" => "-",
			"op_LogicalNot" => "!",
			"op_OnesComplement" => "~",
			"op_Increment" => "++",
			"op_Decrement" => "--",
			"op_True" => "true",
			"op_False" => "false",
			"op_CheckedAddition" => "checked +",
			"op_CheckedSubtraction" => "checked -",
			"op_CheckedMultiply" => "checked *",
			"op_CheckedDivision" => "checked /",
			"op_CheckedUnaryNegation" => "checked -",
			"op_CheckedIncrement" => "checked ++",
			"op_CheckedDecrement" => "checked --",
			"op_AdditionAssignment" => "+=",
			"op_SubtractionAssignment" => "-=",
			"op_MultiplicationAssignment" => "*=",
			"op_DivisionAssignment" => "/=",
			_ => name,
		};
	}
}
