using System.Text;

namespace Nullean.PublicApi.Tests;

/// <summary>
/// Every field of a model as lines of text, so two models can be compared and the first difference shown. A type
/// reference says whether it is a value type and writes Nullable&lt;T&gt; apart from an annotated <c>T?</c>.
/// </summary>
public static class ModelDump
{
	public static List<string> Lines(ApiAssembly assembly)
	{
		var lines = new List<string> { "assembly " + assembly.Name };
		foreach (var type in assembly.Types)
			Type(type, "", lines);
		return lines;
	}

	private static void Type(ApiType type, string path, List<string> lines)
	{
		var name = path.Length == 0 ? type.Namespace + "." + type.Name : path + "/" + type.Name;
		lines.Add(
			$"type {name} {type.Kind} {type.Accessibility} {type.Modifiers} extendable={type.CanBeExtendedOutside} tps={TypeParameters(type.TypeParameters)} " +
				$"base={(type.BaseType is null ? "-" : Ref(type.BaseType))} interfaces=[{string.Join(", ", type.Interfaces.Select(Ref))}] attributes={Attributes(type.Attributes)}"
		);
		foreach (var member in type.Members)
			lines.Add(name + " :: " + Member(member));
		foreach (var nested in type.NestedTypes)
			Type(nested, name, lines);
	}

	private static string Member(ApiMember member) => member switch
	{
		ApiField f =>
			$"field {f.Name} {f.Accessibility} {Ref(f.Type)} static={f.IsStatic} readonly={f.IsReadOnly} volatile={f.IsVolatile} required={f.IsRequired} constant={f.Constant ?? "-"} {Attributes(f.Attributes)}",
		ApiProperty p =>
			$"property {p.Name} {p.Accessibility} {p.Modifiers} {p.RefKind} {Ref(p.Type)} {Parameters(p.Parameters)} get={Accessor(p.Getter)} set={Accessor(p.Setter)} required={p.IsRequired} {Attributes(p.Attributes)}",
		ApiEvent e => $"event {e.Name} {e.Accessibility} {e.Modifiers} {Ref(e.Type)} {Attributes(e.Attributes)}",
		ApiMethod m =>
			$"method {m.Name} {m.Accessibility} {m.Kind} {m.Modifiers} {m.ReturnRefKind} {Ref(m.ReturnType)} return={Attributes(m.ReturnAttributes)} tps={TypeParameters(m.TypeParameters)} {Parameters(m.Parameters)} extension={m.IsExtension} {Attributes(m.Attributes)}",
		_ => member.ToString()!,
	};

	private static string Accessor(ApiAccessor? accessor) =>
		accessor is null ? "-" : $"{accessor.Accessibility}/init={accessor.IsInit}/readonly={accessor.IsReadOnly}";

	private static string Parameters(IReadOnlyList<ApiParameter> parameters) =>
		"(" + string.Join(
			", ",
			parameters.Select(
				p => $"{Attributes(p.Attributes)}{p.RefKind} params={p.IsParams} scoped={p.IsScoped} {Ref(p.Type)} {p.Name}={p.DefaultValue ?? "-"}"
			)
		) + ")";

	private static string TypeParameters(IReadOnlyList<ApiTypeParameter> parameters) =>
		"<" + string.Join(", ", parameters.Select(p => $"{p.Variance} {p.Name} {p.Constraints} [{string.Join(", ", p.ConstraintTypes.Select(Ref))}]")) + ">";

	private static string Attributes(IReadOnlyList<ApiAttribute> attributes) =>
		attributes.Count == 0
			? ""
			: "[" + string.Join(", ", attributes.Select(a => Ref(a.Type with { IsValueType = false }) + "(" + string.Join(", ", a.Arguments) + ")")) + "]";

	public static string Ref(ApiTypeReference type)
	{
		var builder = new StringBuilder();
		Ref(type, builder);
		return builder.ToString();
	}

	private static void Ref(ApiTypeReference type, StringBuilder builder)
	{
		switch (type)
		{
			case ApiNamedType named:
				// the reader cannot tell what a type it only names as a declaring type is
				builder.Append(named.Namespace).Append(':').Append(Path(named));
				var arguments = AllArguments(named);
				if (arguments.Count > 0)
				{
					builder.Append('<');
					for (var i = 0; i < arguments.Count; i++)
					{
						if (i > 0)
							builder.Append(',');
						Ref(arguments[i], builder);
					}
					builder.Append('>');
				}
				if (named.IsValueType)
					builder.Append("!struct");
				break;
			case ApiTupleType tuple:
				builder.Append('(');
				foreach (var element in tuple.Elements)
				{
					Ref(element.Type, builder);
					builder.Append(' ').Append(element.Name ?? "_").Append(';');
				}
				builder.Append(')');
				break;
			case ApiArrayType array:
				Ref(array.ElementType, builder);
				builder.Append('[').Append(array.Rank).Append(']');
				break;
			case ApiPointerType pointer:
				Ref(pointer.ElementType, builder);
				builder.Append('*');
				break;
			case ApiFunctionPointerType function:
				builder.Append("fn").Append(function.IsUnmanaged ? "unmanaged" : "").Append('(');
				foreach (var parameter in function.ParameterTypes)
				{
					Ref(parameter, builder);
					builder.Append(';');
				}
				builder.Append(")->");
				Ref(function.ReturnType, builder);
				break;
			case ApiTypeParameterReference parameter:
				builder.Append(parameter.IsMethodParameter ? "!!" : "!").Append(parameter.Name);
				break;
			case ApiDynamicType:
				builder.Append("dynamic");
				break;
			case ApiNativeIntegerType native:
				builder.Append(native.IsUnsigned ? "nuint" : "nint");
				break;
			case ApiModifiedType modified:
				builder.Append(modified.IsRequired ? "modreq(" : "modopt(");
				Ref(modified.Modifier, builder);
				builder.Append(')');
				Ref(modified.Type, builder);
				break;
			default:
				builder.Append(type);
				break;
		}
		if (type.IsNullable)
			builder.Append("^?");
	}

	private static string Path(ApiNamedType named) => named.DeclaringType is null ? named.Name : Path(named.DeclaringType) + "/" + named.Name;

	private static List<ApiTypeReference> AllArguments(ApiNamedType named) =>
		named.DeclaringType is null ? [.. named.TypeArguments] : [.. AllArguments(named.DeclaringType), .. named.TypeArguments];
}
