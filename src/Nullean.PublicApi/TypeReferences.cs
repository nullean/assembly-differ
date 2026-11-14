#nullable enable
using System.Collections.Generic;

namespace Nullean.PublicApi;

/// <summary>A type as a declaration spells it: <c>List&lt;string?&gt;</c>, <c>int[]</c>, <c>T</c> or <c>(int x, int y)</c>.</summary>
public abstract record ApiTypeReference
{
	private protected ApiTypeReference()
	{ }

	/// <summary>Whether it carries a nullable annotation: <c>string?</c>. A nullable value type is <see cref="ApiNamedType"/> <c>System.Nullable`1</c>.</summary>
	public bool IsNullable { get; init; }
}

/// <summary>A class, struct, interface, enum or delegate, with its type arguments if it is generic.</summary>
/// <param name="Namespace">The namespace, empty for the global namespace; a nested type has its declaring type's.</param>
/// <param name="Name">The name in metadata, with the number of its own type parameters: <c>List`1</c>.</param>
/// <param name="DeclaringType">The type it is nested in, with that type's arguments.</param>
/// <param name="TypeArguments">Its own type arguments; a nested type's declaring type holds the rest.</param>
/// <param name="IsValueType">Whether it is a value type.</param>
public sealed record ApiNamedType(
	string Namespace,
	string Name,
	ApiNamedType? DeclaringType,
	IReadOnlyList<ApiTypeReference> TypeArguments,
	bool IsValueType
) : ApiTypeReference
{
	/// <summary>The name in metadata with its namespace and declaring types: <c>Lib.Outer`1/Inner</c>.</summary>
	public string FullName => DeclaringType is not null ? DeclaringType.FullName + "/" + Name : Namespace.Length == 0 ? Name : Namespace + "." + Name;

	/// <summary>Whether this is <c>System.Nullable`1</c>, which declarations spell <c>T?</c>.</summary>
	public bool IsNullableValueType => DeclaringType is null && Namespace == "System" && Name == "Nullable`1" && TypeArguments.Count == 1;

	/// <summary>Whether this is the <c>System.ValueTuple</c> a tuple is made of.</summary>
	internal bool IsValueTuple => DeclaringType is null && Namespace == "System" && Name.StartsWith("ValueTuple`", System.StringComparison.Ordinal);
}

/// <summary>A tuple: <c>(int x, int y)</c>, a <c>System.ValueTuple</c> whose elements may have names.</summary>
/// <param name="Elements">The elements, more than seven of them for a long tuple.</param>
public sealed record ApiTupleType(IReadOnlyList<ApiTupleElement> Elements) : ApiTypeReference;

/// <summary>An element of a tuple.</summary>
/// <param name="Type">The element's type.</param>
/// <param name="Name">Its name, null when it has none.</param>
public sealed record ApiTupleElement(ApiTypeReference Type, string? Name);

/// <summary>An array: <c>int[]</c> or <c>int[,]</c>.</summary>
/// <param name="ElementType">The type of its elements.</param>
/// <param name="Rank">The number of dimensions.</param>
public sealed record ApiArrayType(ApiTypeReference ElementType, int Rank) : ApiTypeReference;

/// <summary>A pointer: <c>int*</c>.</summary>
/// <param name="ElementType">The type it points to.</param>
public sealed record ApiPointerType(ApiTypeReference ElementType) : ApiTypeReference;

/// <summary>A function pointer: <c>delegate*&lt;int, void&gt;</c>.</summary>
/// <param name="ReturnType">The return type.</param>
/// <param name="ParameterTypes">The parameter types.</param>
/// <param name="IsUnmanaged">Whether it uses an unmanaged calling convention.</param>
public sealed record ApiFunctionPointerType(ApiTypeReference ReturnType, IReadOnlyList<ApiTypeReference> ParameterTypes, bool IsUnmanaged) : ApiTypeReference;

/// <summary>A type parameter: <c>T</c>.</summary>
/// <param name="Name">Its name.</param>
/// <param name="IsMethodParameter">Whether a method declares it, rather than a type.</param>
public sealed record ApiTypeParameterReference(string Name, bool IsMethodParameter) : ApiTypeReference;

/// <summary><c>dynamic</c>, which metadata stores as <c>object</c>.</summary>
public sealed record ApiDynamicType() : ApiTypeReference;

/// <summary><c>nint</c> or <c>nuint</c>, where metadata marks an <c>IntPtr</c> or <c>UIntPtr</c> as one.</summary>
/// <param name="IsUnsigned">Whether it is <c>nuint</c>.</param>
public sealed record ApiNativeIntegerType(bool IsUnsigned) : ApiTypeReference;

/// <summary>A type with a custom modifier the reader does not turn into C#, such as <c>modreq(IsConst)</c>.</summary>
/// <param name="Type">The modified type.</param>
/// <param name="Modifier">The modifier type.</param>
/// <param name="IsRequired">Whether it is <c>modreq</c>, rather than <c>modopt</c>.</param>
public sealed record ApiModifiedType(ApiTypeReference Type, ApiNamedType Modifier, bool IsRequired) : ApiTypeReference;
