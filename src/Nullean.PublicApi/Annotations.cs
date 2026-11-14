#nullable enable
using System.Collections.Generic;
using System.Linq;

namespace Nullean.PublicApi;

/// <summary>
/// What the compiler's attributes say about a type that its signature cannot: which <c>object</c> is <c>dynamic</c>,
/// which <c>IntPtr</c> is <c>nint</c>, which references are nullable and what tuple elements are called. Each list of
/// flags runs over the type in pre-order, the way the C# compiler writes it.
/// </summary>
internal sealed record Annotations(
	IReadOnlyList<bool>? Dynamic,
	IReadOnlyList<bool>? NativeIntegers,
	IReadOnlyList<byte>? Nullable,
	IReadOnlyList<string?>? TupleNames
)
{
	public static readonly Annotations None = new(null, null, null, null);

	/// <summary>Applies the annotations, after skipping <paramref name="dynamicOffset"/> flags for a by-reference wrapper.</summary>
	public ApiTypeReference Apply(ApiTypeReference type, int dynamicOffset = 0)
	{
		if (Dynamic is { Count: > 0 })
		{
			var index = dynamicOffset;
			type = ApplyDynamic(type, ref index);
		}
		if (NativeIntegers is not null)
		{
			var index = 0;
			type = ApplyNativeIntegers(type, ref index);
		}
		if (Nullable is { Count: > 0 })
		{
			var index = 0;
			type = ApplyNullable(type, ref index);
		}
		var tupleIndex = 0;
		return ApplyTuples(type, ref tupleIndex);
	}

	private ApiTypeReference ApplyDynamic(ApiTypeReference type, ref int index)
	{
		// every type consumes a flag, a custom modifier too
		var isDynamic = index < Dynamic!.Count && Dynamic[index];
		index++;
		switch (type)
		{
			case ApiNamedType { FullName: "System.Object", TypeArguments.Count: 0 } when isDynamic:
				return new ApiDynamicType();
			case ApiNamedType named:
				return MapArguments(named, ref index, ApplyDynamic);
			case ApiArrayType array:
				return array with { ElementType = ApplyDynamic(array.ElementType, ref index) };
			case ApiPointerType pointer:
				return pointer with { ElementType = ApplyDynamic(pointer.ElementType, ref index) };
			case ApiByReferenceType reference:
				return reference with { ElementType = ApplyDynamic(reference.ElementType, ref index) };
			case ApiModifiedType modified:
				return modified with { Type = ApplyDynamic(modified.Type, ref index) };
			default:
				return type;
		}
	}

	private ApiTypeReference ApplyNativeIntegers(ApiTypeReference type, ref int index)
	{
		switch (type)
		{
			case ApiNamedType { FullName: "System.IntPtr" or "System.UIntPtr" } native:
				{
					// no flags means every one of them is native
					var isNative = NativeIntegers!.Count == 0 || (index < NativeIntegers.Count && NativeIntegers[index]);
					index++;
					return isNative ? new ApiNativeIntegerType(native.Name == "UIntPtr") : native;
				}
			case ApiNamedType named:
				return MapArguments(named, ref index, ApplyNativeIntegers);
			case ApiArrayType array:
				return array with { ElementType = ApplyNativeIntegers(array.ElementType, ref index) };
			case ApiPointerType pointer:
				return pointer with { ElementType = ApplyNativeIntegers(pointer.ElementType, ref index) };
			case ApiByReferenceType reference:
				return reference with { ElementType = ApplyNativeIntegers(reference.ElementType, ref index) };
			case ApiModifiedType modified:
				return modified with { Type = ApplyNativeIntegers(modified.Type, ref index) };
			default:
				return type;
		}
	}

	private ApiTypeReference ApplyNullable(ApiTypeReference type, ref int index)
	{
		// a reference type, an array and a type parameter have a flag; so does a generic value type other than
		// Nullable<T>, whose flag is always 0; another value type has none. A single flag stands for all of them.
		switch (type)
		{
			case ApiNamedType named:
				{
					var annotated = false;
					if (!named.IsValueType)
						annotated = Next(ref index) == 2;
					else if (!named.IsNullableValueType && HasTypeArguments(named))
						Next(ref index);
					return MapArguments(named, ref index, ApplyNullable) with { IsNullable = annotated };
				}
			case ApiTypeParameterReference parameter:
				return parameter with { IsNullable = Next(ref index) == 2 };
			case ApiArrayType array:
				{
					var annotated = Next(ref index) == 2;
					return array with { ElementType = ApplyNullable(array.ElementType, ref index), IsNullable = annotated };
				}
			case ApiDynamicType dynamic:
				return dynamic with { IsNullable = Next(ref index) == 2 };
			case ApiPointerType pointer:
				return pointer with { ElementType = ApplyNullable(pointer.ElementType, ref index) };
			case ApiByReferenceType reference:
				return reference with { ElementType = ApplyNullable(reference.ElementType, ref index) };
			case ApiModifiedType modified:
				return modified with { Type = ApplyNullable(modified.Type, ref index) };
			case ApiFunctionPointerType function:
				{
					var returnType = ApplyNullable(function.ReturnType, ref index);
					var parameters = new List<ApiTypeReference>();
					foreach (var parameter in function.ParameterTypes)
						parameters.Add(ApplyNullable(parameter, ref index));
					return function with { ReturnType = returnType, ParameterTypes = parameters };
				}
			default:
				return type;
		}
	}

	private static bool HasTypeArguments(ApiNamedType named) =>
		named.TypeArguments.Count > 0 || (named.DeclaringType is { } declaring && HasTypeArguments(declaring));

	private byte Next(ref int index)
	{
		var flag = Nullable!.Count == 1 ? Nullable[0] : index < Nullable.Count ? Nullable[index] : (byte)0;
		index++;
		return flag;
	}

	private ApiTypeReference ApplyTuples(ApiTypeReference type, ref int index)
	{
		switch (type)
		{
			// ValueTuple`8 is only a tuple when its eighth argument holds the rest of one
			case ApiNamedType { IsValueTuple: true } tuple when tuple.TypeArguments.Count < 8 || tuple.TypeArguments[7] is ApiNamedType { IsValueTuple: true }:
				{
					var elements = TupleElements(tuple);
					var names = new string?[elements.Count];
					for (var i = 0; i < elements.Count; i++)
						names[i] = TupleNames is not null && index + i < TupleNames.Count ? TupleNames[index + i] : null;
					index += elements.Count;
					var mapped = new List<ApiTupleElement>(elements.Count);
					for (var i = 0; i < elements.Count; i++)
						mapped.Add(new ApiTupleElement(ApplyTuples(elements[i], ref index), names[i]));
					return new ApiTupleType(mapped) { IsNullable = tuple.IsNullable };
				}
			case ApiNamedType named:
				return MapArguments(named, ref index, ApplyTuples);
			case ApiArrayType array:
				return array with { ElementType = ApplyTuples(array.ElementType, ref index) };
			case ApiPointerType pointer:
				return pointer with { ElementType = ApplyTuples(pointer.ElementType, ref index) };
			case ApiByReferenceType reference:
				return reference with { ElementType = ApplyTuples(reference.ElementType, ref index) };
			case ApiModifiedType modified:
				return modified with { Type = ApplyTuples(modified.Type, ref index) };
			default:
				return type;
		}
	}

	/// <summary>A tuple's elements: the eighth argument of a long one holds the rest.</summary>
	private static List<ApiTypeReference> TupleElements(ApiNamedType tuple)
	{
		var elements = new List<ApiTypeReference>();
		var current = tuple;
		while (true)
		{
			if (current.TypeArguments.Count == 8 && current.TypeArguments[7] is ApiNamedType { IsValueTuple: true } rest)
			{
				elements.AddRange(current.TypeArguments.Take(7));
				current = rest;
				continue;
			}
			elements.AddRange(current.TypeArguments);
			return elements;
		}
	}

	private delegate ApiTypeReference Map(ApiTypeReference type, ref int index);

	/// <summary>Maps the type arguments of a type and the types it is nested in, outermost first.</summary>
	private static ApiNamedType MapArguments(ApiNamedType named, ref int index, Map map)
	{
		var declaring = named.DeclaringType is null ? null : MapArguments(named.DeclaringType, ref index, map);
		if (declaring is null && named.TypeArguments.Count == 0)
			return named;
		var arguments = new List<ApiTypeReference>(named.TypeArguments.Count);
		foreach (var argument in named.TypeArguments)
			arguments.Add(map(argument, ref index));
		return named with { DeclaringType = declaring, TypeArguments = arguments };
	}
}
