// The comparison is a port of JustAssembly (https://github.com/telerik/JustAssembly), Copyright Telerik, licensed
// under the Apache License 2.0, and reproduces its reports. Names follow JustDecompile's C# writer from the same project.
#nullable enable
using System.Collections.Immutable;
using System.Globalization;
using System.Linq;
using System.Reflection.Metadata;
using System.Text;

namespace Differ.Engine;

/// <summary>
/// A type as a signature spells it. Every shape renders two ways: <see cref="FullName"/>, the spelling Mono.Cecil
/// gives a type, which orders members and names attribute constructors; and <see cref="DisplayName"/>, the C# spelling
/// the reports show, with short names and generic arguments in angle brackets.
/// </summary>
internal abstract record Shape
{
	public abstract string FullName { get; }

	public abstract string DisplayName { get; }
}

internal sealed record NamedShape(string Namespace, string Name, NamedShape? DeclaringType) : Shape
{
	public override string FullName => DeclaringType is not null ? DeclaringType.FullName + "/" + Name : Namespace.Length == 0 ? Name : Namespace + "." + Name;

	public override string DisplayName => Names.Identifier(Names.NonGeneric(Name));
}

internal sealed record GenericInstanceShape(Shape Element, ImmutableArray<Shape> Arguments) : Shape
{
	public override string FullName => Element.FullName + "<" + string.Join(",", Arguments.Select(a => a.FullName)) + ">";

	public override string DisplayName =>
		Names.Identifier(Names.NonGeneric(Element is NamedShape named ? named.Name : Element.DisplayName)) + "<" + string.Join(
			", ",
			Arguments.Select(a => a.DisplayName)
		) + ">";
}

internal sealed record GenericParameterShape(string Name) : Shape
{
	public override string FullName => Name;

	public override string DisplayName => Names.Identifier(Names.NonGeneric(Name));
}

internal sealed record SzArrayShape(Shape Element) : Shape
{
	public override string FullName => Element.FullName + "[]";

	public override string DisplayName => Element.DisplayName + "[]";
}

internal sealed record ArrayShape(Shape Element, System.Reflection.Metadata.ArrayShape Bounds) : Shape
{
	public override string FullName
	{
		get
		{
			// Mono.Cecil writes a dimension with a known bound as lower...upper, and one without as nothing
			var suffix = new StringBuilder("[");
			for (var i = 0; i < Bounds.Rank; i++)
			{
				if (i > 0)
					suffix.Append(',');
				int? lower = i < Bounds.LowerBounds.Length ? Bounds.LowerBounds[i] : null;
				int? upper = i < Bounds.Sizes.Length ? (lower ?? 0) + Bounds.Sizes[i] - 1 : null;
				if (lower.HasValue || upper.HasValue)
					suffix.Append(lower?.ToString(CultureInfo.InvariantCulture)).Append("...").Append(upper?.ToString(CultureInfo.InvariantCulture));
			}
			return Element.FullName + suffix.Append(']');
		}
	}

	public override string DisplayName => Element.DisplayName + "[" + new string(',', Bounds.Rank - 1) + "]";
}

internal sealed record ByReferenceShape(Shape Element) : Shape
{
	public override string FullName => Element.FullName + "&";

	public override string DisplayName => Element.DisplayName + "&";
}

internal sealed record PointerShape(Shape Element) : Shape
{
	public override string FullName => Element.FullName + "*";

	public override string DisplayName => Element.DisplayName + "*";
}

internal sealed record PinnedShape(Shape Element) : Shape
{
	public override string FullName => Element.FullName + " pinned";

	public override string DisplayName => Element.DisplayName;
}

internal sealed record ModifiedShape(Shape Element, Shape Modifier, bool IsRequired) : Shape
{
	public override string FullName => Element.FullName + (IsRequired ? " modreq(" : " modopt(") + Modifier.FullName + ")";

	public override string DisplayName => Element.DisplayName;
}

internal sealed record FunctionPointerShape(MethodSignature<Shape> Signature) : Shape
{
	public override string FullName =>
		"method " + Signature.ReturnType.FullName + " *(" + string.Join(",", Signature.ParameterTypes.Select(p => p.FullName)) + ")";

	public override string DisplayName => "method";
}

/// <summary>The generic parameter names a signature's <c>!0</c> and <c>!!0</c> stand for, where they are known.</summary>
internal readonly record struct GenericContext(ImmutableArray<string> TypeParameters, ImmutableArray<string> MethodParameters)
{
	public static readonly GenericContext None = new([], []);
}

/// <summary>Decodes signatures into <see cref="Shape"/>s.</summary>
internal sealed class ShapeProvider(MetadataReader reader) : ISignatureTypeProvider<Shape, GenericContext>
{
	public Shape GetPrimitiveType(PrimitiveTypeCode typeCode) =>
		new NamedShape(
			"System",
			typeCode switch
			{
				PrimitiveTypeCode.Boolean => "Boolean",
				PrimitiveTypeCode.Byte => "Byte",
				PrimitiveTypeCode.SByte => "SByte",
				PrimitiveTypeCode.Char => "Char",
				PrimitiveTypeCode.Int16 => "Int16",
				PrimitiveTypeCode.UInt16 => "UInt16",
				PrimitiveTypeCode.Int32 => "Int32",
				PrimitiveTypeCode.UInt32 => "UInt32",
				PrimitiveTypeCode.Int64 => "Int64",
				PrimitiveTypeCode.UInt64 => "UInt64",
				PrimitiveTypeCode.Single => "Single",
				PrimitiveTypeCode.Double => "Double",
				PrimitiveTypeCode.IntPtr => "IntPtr",
				PrimitiveTypeCode.UIntPtr => "UIntPtr",
				PrimitiveTypeCode.Object => "Object",
				PrimitiveTypeCode.String => "String",
				PrimitiveTypeCode.TypedReference => "TypedReference",
				_ => "Void",
			},
			null
		);

	public Shape GetTypeFromDefinition(MetadataReader metadata, TypeDefinitionHandle handle, byte rawTypeKind) => Definition(handle);

	public Shape GetTypeFromReference(MetadataReader metadata, TypeReferenceHandle handle, byte rawTypeKind) => Reference(handle);

	public Shape GetTypeFromSpecification(MetadataReader metadata, GenericContext genericContext, TypeSpecificationHandle handle, byte rawTypeKind) =>
		metadata.GetTypeSpecification(handle).DecodeSignature(this, genericContext);

	public Shape GetSZArrayType(Shape elementType) => new SzArrayShape(elementType);

	public Shape GetArrayType(Shape elementType, System.Reflection.Metadata.ArrayShape shape) => new ArrayShape(elementType, shape);

	public Shape GetByReferenceType(Shape elementType) => new ByReferenceShape(elementType);

	public Shape GetPointerType(Shape elementType) => new PointerShape(elementType);

	public Shape GetPinnedType(Shape elementType) => new PinnedShape(elementType);

	public Shape GetModifiedType(Shape modifier, Shape unmodifiedType, bool isRequired) => new ModifiedShape(unmodifiedType, modifier, isRequired);

	public Shape GetFunctionPointerType(MethodSignature<Shape> signature) => new FunctionPointerShape(signature);

	public Shape GetGenericInstantiation(Shape genericType, ImmutableArray<Shape> typeArguments) => new GenericInstanceShape(genericType, typeArguments);

	public Shape GetGenericTypeParameter(GenericContext genericContext, int index) =>
		new GenericParameterShape(index < genericContext.TypeParameters.Length ? genericContext.TypeParameters[index] : "!" + index);

	public Shape GetGenericMethodParameter(GenericContext genericContext, int index) =>
		new GenericParameterShape(index < genericContext.MethodParameters.Length ? genericContext.MethodParameters[index] : "!!" + index);

	public NamedShape Definition(TypeDefinitionHandle handle)
	{
		var type = reader.GetTypeDefinition(handle);
		var declaring = type.GetDeclaringType();
		return new NamedShape(reader.GetString(type.Namespace), reader.GetString(type.Name), declaring.IsNil ? null : Definition(declaring));
	}

	public NamedShape Reference(TypeReferenceHandle handle)
	{
		var type = reader.GetTypeReference(handle);
		var declaring = type.ResolutionScope.Kind == HandleKind.TypeReference ? Reference((TypeReferenceHandle)type.ResolutionScope) : null;
		return new NamedShape(reader.GetString(type.Namespace), reader.GetString(type.Name), declaring);
	}

	public Shape FromTypeHandle(EntityHandle handle, GenericContext context) => handle.Kind switch
	{
		HandleKind.TypeDefinition => Definition((TypeDefinitionHandle)handle),
		HandleKind.TypeReference => Reference((TypeReferenceHandle)handle),
		_ => GetTypeFromSpecification(reader, context, (TypeSpecificationHandle)handle, 0),
	};
}

/// <summary>The C# spelling JustDecompile gives names, which the reports have always used.</summary>
internal static class Names
{
	/// <summary>Drops the generic arity suffix: <c>List`1</c> becomes <c>List</c>.</summary>
	public static string NonGeneric(string name)
	{
		var index = name.IndexOf('`');
		return index > 0 ? name[..index] : name;
	}

	/// <summary>Writes each character a C# identifier cannot hold as <c>u</c> and its four hex digits.</summary>
	public static string Identifier(string identifier)
	{
		if (identifier.Length == 0)
			return identifier;
		var builder = new StringBuilder();
		builder.Append(IsFirstCharacter(identifier[0]) ? identifier[0].ToString() : Escape(identifier[0]));
		for (var i = 1; i < identifier.Length; i++)
			builder.Append(IsCharacter(identifier[i]) ? identifier[i].ToString() : Escape(identifier[i]));
		return builder.ToString();
	}

	/// <summary>
	/// A dotted member name, such as an explicit interface implementation's, with each part made a valid identifier;
	/// a part that holds a generic argument list keeps its angle brackets and commas.
	/// </summary>
	public static string DottedIdentifier(string name)
	{
		var builder = new StringBuilder();
		var parts = name.Split('.');
		for (var i = 0; i < parts.Length; i++)
		{
			if (i > 0)
				builder.Append('.');
			if (!NormalizeGenericSymbols(parts[i], builder))
				builder.Append(Identifier(parts[i]));
		}
		return builder.ToString();
	}

	private static bool NormalizeGenericSymbols(string name, StringBuilder builder)
	{
		var left = name.Split('<');
		var right = name.Split('>');
		if (left.Length > 1)
		{
			NormalizeGenericTokens(left, '<', builder);
			return true;
		}
		if (right.Length > 1)
		{
			NormalizeGenericTokens(right, '>', builder);
			return true;
		}
		return false;
	}

	private static void NormalizeGenericTokens(string[] tokens, char symbol, StringBuilder builder)
	{
		for (var i = 0; i < tokens.Length; i++)
		{
			if (i > 0)
				builder.Append(symbol);
			if (NormalizeGenericSymbols(tokens[i], builder))
				continue;
			var arguments = tokens[i].Split(',');
			for (var j = 0; j < arguments.Length; j++)
			{
				if (j > 0)
					builder.Append(", ");
				builder.Append(Identifier(arguments[j]));
			}
		}
	}

	private static string Escape(char character) => "u" + ((int)character).ToString("x4", CultureInfo.InvariantCulture);

	private static bool IsFirstCharacter(char character) =>
		character == '_' ||
			char.GetUnicodeCategory(
				character
			) is UnicodeCategory.LowercaseLetter or UnicodeCategory.UppercaseLetter or UnicodeCategory.TitlecaseLetter or UnicodeCategory.ModifierLetter or UnicodeCategory.OtherLetter or UnicodeCategory.LetterNumber;

	private static bool IsCharacter(char character) =>
		IsFirstCharacter(character) ||
			char.GetUnicodeCategory(
				character
			) is UnicodeCategory.NonSpacingMark or UnicodeCategory.SpacingCombiningMark or UnicodeCategory.DecimalDigitNumber or UnicodeCategory.ConnectorPunctuation or UnicodeCategory.Format;
}
