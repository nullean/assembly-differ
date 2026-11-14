#nullable enable
using System;
using System.Collections.Generic;
using System.Collections.Immutable;
using System.Globalization;
using System.Linq;
using System.Reflection.Metadata;

namespace Nullean.PublicApi;

/// <summary>A by-reference type, which only a parameter or return type has; the reader turns it into an <see cref="ApiRefKind"/>.</summary>
internal sealed record ApiByReferenceType(ApiTypeReference ElementType) : ApiTypeReference;

/// <summary>The type parameter names a signature's <c>!0</c> and <c>!!0</c> stand for.</summary>
internal readonly record struct TypeContext(ImmutableArray<string> TypeParameters, ImmutableArray<string> MethodParameters)
{
	public static readonly TypeContext None = new([], []);
}

/// <summary>Decodes signatures and attribute blobs into <see cref="ApiTypeReference"/>s.</summary>
internal sealed class TypeReferenceProvider(
	MetadataReader reader
) : ISignatureTypeProvider<ApiTypeReference, TypeContext>, ICustomAttributeTypeProvider<ApiTypeReference>
{
	private readonly Dictionary<EntityHandle, ApiNamedType> _named = [];

	public ApiTypeReference GetPrimitiveType(PrimitiveTypeCode typeCode) => Primitive(typeCode);

	// the records are immutable, so every reference to int or string shares one, made once
	private static readonly ApiNamedType?[] Primitives = BuildPrimitives();

	private static ApiNamedType?[] BuildPrimitives()
	{
		var primitives = new ApiNamedType?[64];
		foreach (var code in new[]
		{
			PrimitiveTypeCode.Boolean,
			PrimitiveTypeCode.Byte,
			PrimitiveTypeCode.SByte,
			PrimitiveTypeCode.Char,
			PrimitiveTypeCode.Int16,
			PrimitiveTypeCode.UInt16,
			PrimitiveTypeCode.Int32,
			PrimitiveTypeCode.UInt32,
			PrimitiveTypeCode.Int64,
			PrimitiveTypeCode.UInt64,
			PrimitiveTypeCode.Single,
			PrimitiveTypeCode.Double,
			PrimitiveTypeCode.IntPtr,
			PrimitiveTypeCode.UIntPtr,
			PrimitiveTypeCode.Object,
			PrimitiveTypeCode.String,
			PrimitiveTypeCode.TypedReference,
			PrimitiveTypeCode.Void,
		})
			primitives[(int)code] = NewPrimitive(code);
		return primitives;
	}

	internal static ApiNamedType Primitive(PrimitiveTypeCode typeCode) =>
		(int)typeCode < Primitives.Length && Primitives[(int)typeCode] is { } primitive ? primitive : NewPrimitive(typeCode);

	private static ApiNamedType NewPrimitive(PrimitiveTypeCode typeCode) => typeCode switch
	{
		PrimitiveTypeCode.Boolean => InSystem("Boolean", true),
		PrimitiveTypeCode.Byte => InSystem("Byte", true),
		PrimitiveTypeCode.SByte => InSystem("SByte", true),
		PrimitiveTypeCode.Char => InSystem("Char", true),
		PrimitiveTypeCode.Int16 => InSystem("Int16", true),
		PrimitiveTypeCode.UInt16 => InSystem("UInt16", true),
		PrimitiveTypeCode.Int32 => InSystem("Int32", true),
		PrimitiveTypeCode.UInt32 => InSystem("UInt32", true),
		PrimitiveTypeCode.Int64 => InSystem("Int64", true),
		PrimitiveTypeCode.UInt64 => InSystem("UInt64", true),
		PrimitiveTypeCode.Single => InSystem("Single", true),
		PrimitiveTypeCode.Double => InSystem("Double", true),
		PrimitiveTypeCode.IntPtr => InSystem("IntPtr", true),
		PrimitiveTypeCode.UIntPtr => InSystem("UIntPtr", true),
		PrimitiveTypeCode.Object => InSystem("Object", false),
		PrimitiveTypeCode.String => InSystem("String", false),
		PrimitiveTypeCode.TypedReference => InSystem("TypedReference", true),
		PrimitiveTypeCode.Void => InSystem("Void", true),
		_ => throw new ArgumentOutOfRangeException(nameof(typeCode), typeCode, null),
	};

	private static ApiNamedType InSystem(string name, bool isValueType) => new("System", name, null, [], isValueType);

	public ApiTypeReference GetTypeFromDefinition(MetadataReader metadata, TypeDefinitionHandle handle, byte rawTypeKind) =>
		Definition(handle, rawTypeKind == (byte)SignatureTypeKind.ValueType ? true : rawTypeKind == (byte)SignatureTypeKind.Class ? false : null);

	public ApiTypeReference GetTypeFromReference(MetadataReader metadata, TypeReferenceHandle handle, byte rawTypeKind) =>
		Reference(handle, rawTypeKind == (byte)SignatureTypeKind.ValueType);

	/// <summary>A type definition, which is a value type when its base type says so if the signature does not.</summary>
	internal ApiNamedType Definition(TypeDefinitionHandle handle, bool? isValueType = null)
	{
		var definition = reader.GetTypeDefinition(handle);
		var declaring = definition.GetDeclaringType();
		var valueType = isValueType ?? IsValueType(definition);
		if (_named.TryGetValue(handle, out var known) && known.IsValueType == valueType)
			return known;
		var named = new ApiNamedType(
			declaring.IsNil ? reader.GetString(definition.Namespace) : Definition(declaring).Namespace,
			reader.GetString(definition.Name),
			declaring.IsNil ? null : Definition(declaring),
			[],
			valueType
		);
		_named[handle] = named;
		return named;
	}

	private bool IsValueType(TypeDefinition definition)
	{
		if (definition.BaseType.IsNil)
			return false;
		var baseName = FullName(definition.BaseType);
		var ownName = reader.GetString(definition.Namespace) + "." + reader.GetString(definition.Name);
		return baseName == "System.Enum" || (baseName == "System.ValueType" && ownName != "System.Enum");
	}

	/// <summary>The namespace-qualified name of a type definition or reference, empty for anything else.</summary>
	internal string FullName(EntityHandle handle) => handle.Kind switch
	{
		HandleKind.TypeDefinition => Definition((TypeDefinitionHandle)handle).FullName,
		HandleKind.TypeReference => Reference((TypeReferenceHandle)handle, false).FullName,
		_ => string.Empty,
	};

	internal ApiNamedType Reference(TypeReferenceHandle handle, bool isValueType)
	{
		var reference = reader.GetTypeReference(handle);
		var scope = reference.ResolutionScope;
		var declaring = scope.Kind == HandleKind.TypeReference ? Reference((TypeReferenceHandle)scope, false) : null;
		return new ApiNamedType(declaring?.Namespace ?? reader.GetString(reference.Namespace), reader.GetString(reference.Name), declaring, [], isValueType);
	}

	public ApiTypeReference GetTypeFromSpecification(MetadataReader metadata, TypeContext genericContext, TypeSpecificationHandle handle, byte rawTypeKind) =>
		metadata.GetTypeSpecification(handle).DecodeSignature(this, genericContext);

	/// <summary>A type definition, reference or specification, as a base type, interface, constraint or event type names it.</summary>
	internal ApiTypeReference FromHandle(EntityHandle handle, TypeContext context) => handle.Kind switch
	{
		HandleKind.TypeDefinition => Definition((TypeDefinitionHandle)handle),
		HandleKind.TypeReference => Reference((TypeReferenceHandle)handle, false),
		HandleKind.TypeSpecification => reader.GetTypeSpecification((TypeSpecificationHandle)handle).DecodeSignature(this, context),
		_ => throw new BadImageFormatException($"not a type: {handle.Kind}"),
	};

	public ApiTypeReference GetGenericInstantiation(ApiTypeReference genericType, ImmutableArray<ApiTypeReference> typeArguments)
	{
		if (genericType is not ApiNamedType named)
			return genericType;
		var index = 0;
		return Instantiate(named, typeArguments, ref index);
	}

	/// <summary>Hands each type in a nesting chain its own share of a generic instantiation's arguments, outermost first.</summary>
	private static ApiNamedType Instantiate(ApiNamedType named, ImmutableArray<ApiTypeReference> arguments, ref int index)
	{
		var declaring = named.DeclaringType is null ? null : Instantiate(named.DeclaringType, arguments, ref index);
		var arity = Math.Min(Arity(named.Name), arguments.Length - index);
		var own = arguments.Skip(index).Take(arity).ToList();
		index += arity;
		return named with { DeclaringType = declaring, TypeArguments = own };
	}

	internal static int Arity(string metadataName)
	{
		var tick = metadataName.LastIndexOf('`');
#pragma warning disable CA1846 // netstandard2.0 cannot parse a span
		return tick >= 0 && int.TryParse(metadataName.Substring(tick + 1), NumberStyles.None, CultureInfo.InvariantCulture, out var arity) ? arity : 0;
#pragma warning restore CA1846
	}

	public ApiTypeReference GetSZArrayType(ApiTypeReference elementType) => new ApiArrayType(elementType, 1);

	public ApiTypeReference GetArrayType(ApiTypeReference elementType, ArrayShape shape) => new ApiArrayType(elementType, shape.Rank);

	public ApiTypeReference GetByReferenceType(ApiTypeReference elementType) => new ApiByReferenceType(elementType);

	public ApiTypeReference GetPointerType(ApiTypeReference elementType) => new ApiPointerType(elementType);

	public ApiTypeReference GetPinnedType(ApiTypeReference elementType) => elementType;

	public ApiTypeReference GetModifiedType(ApiTypeReference modifier, ApiTypeReference unmodifiedType, bool isRequired) =>
		modifier is ApiNamedType named ? new ApiModifiedType(unmodifiedType, named, isRequired) : unmodifiedType;

	public ApiTypeReference GetFunctionPointerType(MethodSignature<ApiTypeReference> signature) =>
		new ApiFunctionPointerType(signature.ReturnType, signature.ParameterTypes, signature.Header.CallingConvention != SignatureCallingConvention.Default);

	public ApiTypeReference GetGenericTypeParameter(TypeContext genericContext, int index) =>
		new ApiTypeParameterReference(index < genericContext.TypeParameters.Length ? genericContext.TypeParameters[index] : "!" + index, false);

	public ApiTypeReference GetGenericMethodParameter(TypeContext genericContext, int index) =>
		new ApiTypeParameterReference(index < genericContext.MethodParameters.Length ? genericContext.MethodParameters[index] : "!!" + index, true);

	// attribute blobs

	public ApiTypeReference GetSystemType() => new ApiNamedType("System", "Type", null, [], false);

	public bool IsSystemType(ApiTypeReference type) => type is ApiNamedType { FullName: "System.Type" };

	public ApiTypeReference GetTypeFromSerializedName(string name)
	{
		// Namespace.Outer+Inner, Assembly, Version=...; generic arguments are rare in attributes and kept in the name
		var comma = name.IndexOf(',');
		var typeName = comma >= 0 && name.IndexOf('[') is var bracket && (bracket < 0 || bracket > comma) ? name.Substring(0, comma) : name;
		var parts = typeName.Split('+');
		var dot = parts[0].LastIndexOf('.');
		ApiNamedType named = new(dot < 0 ? string.Empty : parts[0].Substring(0, dot), parts[0].Substring(dot + 1), null, [], false);
		foreach (var nested in parts.Skip(1))
			named = new ApiNamedType(named.Namespace, nested, named, [], false);
		return named;
	}

	private Dictionary<string, PrimitiveTypeCode>? _enumTypes;

	public PrimitiveTypeCode GetUnderlyingEnumType(ApiTypeReference type)
	{
		// an enum this assembly defines says; one from elsewhere is assumed to be an int, which nearly all are
		if (_enumTypes is null)
		{
			_enumTypes = new Dictionary<string, PrimitiveTypeCode>(StringComparer.Ordinal);
			foreach (var handle in reader.TypeDefinitions)
				if (Definition(handle) is { IsValueType: true } definition)
					foreach (var fieldHandle in reader.GetTypeDefinition(handle).GetFields())
					{
						var field = reader.GetFieldDefinition(fieldHandle);
						if (
							(field.Attributes & System.Reflection.FieldAttributes.Static) == 0 &&
							field.DecodeSignature(this, TypeContext.None) is ApiNamedType { Namespace: "System" } underlying
						)
						{
							_enumTypes[definition.FullName] = underlying.Name switch
							{
								"Byte" => PrimitiveTypeCode.Byte,
								"SByte" => PrimitiveTypeCode.SByte,
								"Int16" => PrimitiveTypeCode.Int16,
								"UInt16" => PrimitiveTypeCode.UInt16,
								"UInt32" => PrimitiveTypeCode.UInt32,
								"Int64" => PrimitiveTypeCode.Int64,
								"UInt64" => PrimitiveTypeCode.UInt64,
								"Char" => PrimitiveTypeCode.Char,
								"Boolean" => PrimitiveTypeCode.Boolean,
								_ => PrimitiveTypeCode.Int32,
							};
							break;
						}
					}
		}
		return type is ApiNamedType named && _enumTypes.TryGetValue(named.FullName, out var code) ? code : PrimitiveTypeCode.Int32;
	}
}
