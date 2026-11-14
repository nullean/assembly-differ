#nullable enable
using System;
using System.Collections.Generic;

namespace Nullean.PublicApi;

/// <summary>
/// The public API of an assembly: the types and members code outside it can see, with what a C# declaration says
/// about them, such as base types, interfaces, constraints, nullability and default values.
/// </summary>
/// <param name="Name">The assembly's simple name.</param>
/// <param name="Version">The assembly version.</param>
/// <param name="PublicKeyToken">The public key token as lowercase hex, or null for an assembly without a strong name.</param>
/// <param name="TargetFramework">The framework the assembly was built for, such as <c>.NETCoreApp,Version=v10.0</c>, if it says.</param>
/// <param name="Types">The visible top-level types, ordered by namespace and name; nested types are under their declaring type.</param>
public sealed record ApiAssembly(string Name, Version Version, string? PublicKeyToken, string? TargetFramework, IReadOnlyList<ApiType> Types);

/// <summary>The kind of a type declaration.</summary>
public enum ApiTypeKind
{
	/// <summary>A class, including a record class.</summary>
	Class,

	/// <summary>A struct, including a record struct.</summary>
	Struct,

	/// <summary>An interface.</summary>
	Interface,

	/// <summary>An enum.</summary>
	Enum,

	/// <summary>A delegate.</summary>
	Delegate,
}

/// <summary>Who can see a type or member.</summary>
public enum ApiAccessibility
{
	/// <summary><c>public</c>.</summary>
	Public,

	/// <summary><c>protected</c>.</summary>
	Protected,

	/// <summary><c>protected internal</c>.</summary>
	ProtectedInternal,
}

/// <summary>The modifiers of a type declaration.</summary>
[Flags]
public enum ApiTypeModifiers
{
	/// <summary>None.</summary>
	None = 0,

	/// <summary><c>abstract</c>.</summary>
	Abstract = 1,

	/// <summary><c>sealed</c>.</summary>
	Sealed = 2,

	/// <summary><c>static</c>: a class that is abstract and sealed in metadata.</summary>
	Static = 4,

	/// <summary><c>readonly</c>, on a struct.</summary>
	ReadOnly = 8,

	/// <summary><c>ref</c>, on a struct that can only live on the stack.</summary>
	Ref = 16,

	/// <summary><c>record</c>: the compiler generated its equality and cloning members.</summary>
	Record = 32,
}

/// <summary>A type declaration and its visible members.</summary>
/// <param name="Namespace">The namespace, empty for the global namespace; a nested type has its declaring type's.</param>
/// <param name="Name">The name in metadata, with the number of its own type parameters: <c>Box`1</c>.</param>
/// <param name="Kind">Class, struct, interface, enum or delegate.</param>
/// <param name="Accessibility">Who can see it.</param>
/// <param name="Modifiers">Its modifiers.</param>
/// <param name="TypeParameters">Its own type parameters; a nested type does not repeat its declaring type's.</param>
/// <param name="BaseType">The base class, or the underlying type of an enum; null for <c>object</c>, a struct, an interface or a delegate.</param>
/// <param name="Interfaces">The interfaces it implements or, for an interface, extends; metadata lists inherited ones too.</param>
/// <param name="Attributes">The attributes on it that say something about the API.</param>
/// <param name="Members">The visible members, ordered by kind, name and parameters; an enum's values in declaration order; a delegate's <c>Invoke</c> method.</param>
/// <param name="NestedTypes">The visible types declared inside it.</param>
/// <param name="CanBeExtendedOutside">
/// Whether code outside the assembly can implement it or derive from it: an interface, or a class that is not sealed
/// or static and has a public or protected constructor.
/// </param>
public sealed record ApiType(
	string Namespace,
	string Name,
	ApiTypeKind Kind,
	ApiAccessibility Accessibility,
	ApiTypeModifiers Modifiers,
	IReadOnlyList<ApiTypeParameter> TypeParameters,
	ApiTypeReference? BaseType,
	IReadOnlyList<ApiTypeReference> Interfaces,
	IReadOnlyList<ApiAttribute> Attributes,
	IReadOnlyList<ApiMember> Members,
	IReadOnlyList<ApiType> NestedTypes,
	bool CanBeExtendedOutside
)
{
	/// <summary>The nullable context the compiler gave the type's members, which a nested type inherits.</summary>
	internal byte? NullableContext { get; init; }
}

/// <summary>The variance of a type parameter.</summary>
public enum ApiVariance
{
	/// <summary>Invariant.</summary>
	None,

	/// <summary><c>out</c>.</summary>
	Out,

	/// <summary><c>in</c>.</summary>
	In,
}

/// <summary>The constraints on a type parameter that are not types.</summary>
[Flags]
public enum ApiConstraints
{
	/// <summary>None.</summary>
	None = 0,

	/// <summary><c>class</c>.</summary>
	Class = 1,

	/// <summary><c>class?</c>.</summary>
	NullableClass = 2,

	/// <summary><c>struct</c>.</summary>
	Struct = 4,

	/// <summary><c>unmanaged</c>.</summary>
	Unmanaged = 8,

	/// <summary><c>notnull</c>.</summary>
	NotNull = 16,

	/// <summary><c>new()</c>.</summary>
	New = 32,

	/// <summary><c>allows ref struct</c>.</summary>
	AllowsRefStruct = 64,
}

/// <summary>A type parameter.</summary>
/// <param name="Name">Its name.</param>
/// <param name="Variance">Its variance, on an interface or delegate.</param>
/// <param name="Constraints">Its constraints that are not types.</param>
/// <param name="ConstraintTypes">The types it is constrained to.</param>
public sealed record ApiTypeParameter(string Name, ApiVariance Variance, ApiConstraints Constraints, IReadOnlyList<ApiTypeReference> ConstraintTypes);

/// <summary>An attribute, with the arguments it was applied with.</summary>
/// <param name="Type">The attribute's type.</param>
/// <param name="Arguments">The arguments as C# literals, named ones as <c>Name = value</c>; empty when they cannot be read.</param>
[System.Diagnostics.CodeAnalysis.SuppressMessage("Naming", "CA1711", Justification = "It describes an attribute.")]
public sealed record ApiAttribute(ApiNamedType Type, IReadOnlyList<string> Arguments);

/// <summary>A member of a type.</summary>
public abstract record ApiMember
{
	private protected ApiMember(string name, ApiAccessibility accessibility, IReadOnlyList<ApiAttribute> attributes)
	{
		Name = name;
		Accessibility = accessibility;
		Attributes = attributes;
	}

	/// <summary>The name in metadata: <c>Draw</c>, <c>.ctor</c>, <c>op_Addition</c> or <c>Item</c> for an indexer.</summary>
	public string Name { get; }

	/// <summary>Who can see it.</summary>
	public ApiAccessibility Accessibility { get; }

	/// <summary>The attributes on it that say something about the API.</summary>
	public IReadOnlyList<ApiAttribute> Attributes { get; }
}

/// <summary>How a method, property, event or accessor takes part in inheritance.</summary>
[Flags]
public enum ApiMemberModifiers
{
	/// <summary>None.</summary>
	None = 0,

	/// <summary><c>static</c>.</summary>
	Static = 1,

	/// <summary><c>abstract</c>; in an interface, a member without a default implementation.</summary>
	Abstract = 2,

	/// <summary><c>virtual</c>; in an interface, a member with a default implementation.</summary>
	Virtual = 4,

	/// <summary><c>override</c>.</summary>
	Override = 8,

	/// <summary><c>sealed</c>, on an override.</summary>
	Sealed = 16,

	/// <summary><c>readonly</c>, on a struct member.</summary>
	ReadOnly = 32,
}

/// <summary>A field, or a value of an enum.</summary>
/// <param name="Name">The field's name.</param>
/// <param name="Accessibility">Who can see it.</param>
/// <param name="Attributes">The attributes on it.</param>
/// <param name="Type">The field's type.</param>
/// <param name="IsStatic">Whether it is static; a constant is.</param>
/// <param name="IsReadOnly">Whether it is <c>readonly</c>.</param>
/// <param name="IsVolatile">Whether it is <c>volatile</c>.</param>
/// <param name="IsRequired">Whether it is <c>required</c>.</param>
/// <param name="Constant">The value of a constant or an enum value, as a C# literal; null for a field that is not one.</param>
public sealed record ApiField(
	string Name,
	ApiAccessibility Accessibility,
	IReadOnlyList<ApiAttribute> Attributes,
	ApiTypeReference Type,
	bool IsStatic,
	bool IsReadOnly,
	bool IsVolatile,
	bool IsRequired,
	string? Constant
) : ApiMember(Name, Accessibility, Attributes);

/// <summary>What kind of method a method is.</summary>
public enum ApiMethodKind
{
	/// <summary>An ordinary method.</summary>
	Method,

	/// <summary>An instance constructor.</summary>
	Constructor,

	/// <summary>A user-defined operator, such as <c>op_Addition</c>.</summary>
	Operator,

	/// <summary>A user-defined conversion: <c>op_Implicit</c> or <c>op_Explicit</c>.</summary>
	Conversion,
}

/// <summary>How a parameter or return value is passed.</summary>
public enum ApiRefKind
{
	/// <summary>By value.</summary>
	None,

	/// <summary><c>ref</c>.</summary>
	Ref,

	/// <summary><c>out</c>.</summary>
	Out,

	/// <summary><c>in</c>.</summary>
	In,

	/// <summary><c>ref readonly</c>.</summary>
	RefReadOnly,
}

/// <summary>A method, constructor, operator or conversion.</summary>
/// <param name="Name">The name in metadata.</param>
/// <param name="Accessibility">Who can see it.</param>
/// <param name="Attributes">The attributes on it.</param>
/// <param name="Kind">Method, constructor, operator or conversion.</param>
/// <param name="Modifiers">How it takes part in inheritance.</param>
/// <param name="ReturnType">The return type; <c>void</c> for a constructor.</param>
/// <param name="ReturnRefKind">Whether it returns by reference.</param>
/// <param name="ReturnAttributes">The attributes on the return value.</param>
/// <param name="TypeParameters">Its type parameters.</param>
/// <param name="Parameters">Its parameters.</param>
/// <param name="IsExtension">Whether it is an extension method, whose first parameter is <c>this</c>.</param>
public sealed record ApiMethod(
	string Name,
	ApiAccessibility Accessibility,
	IReadOnlyList<ApiAttribute> Attributes,
	ApiMethodKind Kind,
	ApiMemberModifiers Modifiers,
	ApiTypeReference ReturnType,
	ApiRefKind ReturnRefKind,
	IReadOnlyList<ApiAttribute> ReturnAttributes,
	IReadOnlyList<ApiTypeParameter> TypeParameters,
	IReadOnlyList<ApiParameter> Parameters,
	bool IsExtension
) : ApiMember(Name, Accessibility, Attributes);

/// <summary>A parameter of a method, indexer or delegate.</summary>
/// <param name="Name">Its name, which callers can pass it by.</param>
/// <param name="Type">Its type, without the reference a <c>ref</c>, <c>out</c> or <c>in</c> adds.</param>
/// <param name="RefKind">How it is passed.</param>
/// <param name="IsParams">Whether it is <c>params</c>.</param>
/// <param name="IsScoped">Whether it is <c>scoped</c>.</param>
/// <param name="DefaultValue">Its default value as a C# literal, <c>default</c> when it has no constant; null when it is not optional.</param>
/// <param name="Attributes">The attributes on it.</param>
public sealed record ApiParameter(
	string Name,
	ApiTypeReference Type,
	ApiRefKind RefKind,
	bool IsParams,
	bool IsScoped,
	string? DefaultValue,
	IReadOnlyList<ApiAttribute> Attributes
);

/// <summary>A property's or indexer's accessor that code outside the assembly can call.</summary>
/// <param name="Accessibility">Who can call it.</param>
/// <param name="IsInit">Whether a setter is <c>init</c>.</param>
/// <param name="IsReadOnly">Whether a struct's getter is <c>readonly</c>.</param>
public sealed record ApiAccessor(ApiAccessibility Accessibility, bool IsInit, bool IsReadOnly);

/// <summary>A property or indexer.</summary>
/// <param name="Name">The name in metadata; an indexer's is usually <c>Item</c>.</param>
/// <param name="Accessibility">The accessibility of its most visible accessor.</param>
/// <param name="Attributes">The attributes on it.</param>
/// <param name="Modifiers">How it takes part in inheritance.</param>
/// <param name="Type">Its type.</param>
/// <param name="RefKind">Whether it returns by reference.</param>
/// <param name="Parameters">An indexer's parameters; empty for a property.</param>
/// <param name="Getter">The getter, if code outside the assembly can call one.</param>
/// <param name="Setter">The setter or init accessor, if code outside the assembly can call one.</param>
/// <param name="IsRequired">Whether it is <c>required</c>.</param>
public sealed record ApiProperty(
	string Name,
	ApiAccessibility Accessibility,
	IReadOnlyList<ApiAttribute> Attributes,
	ApiMemberModifiers Modifiers,
	ApiTypeReference Type,
	ApiRefKind RefKind,
	IReadOnlyList<ApiParameter> Parameters,
	ApiAccessor? Getter,
	ApiAccessor? Setter,
	bool IsRequired
) : ApiMember(Name, Accessibility, Attributes);

/// <summary>An event.</summary>
/// <param name="Name">Its name.</param>
/// <param name="Accessibility">Who can see it.</param>
/// <param name="Attributes">The attributes on it.</param>
/// <param name="Modifiers">How it takes part in inheritance.</param>
/// <param name="Type">Its delegate type.</param>
public sealed record ApiEvent(
	string Name,
	ApiAccessibility Accessibility,
	IReadOnlyList<ApiAttribute> Attributes,
	ApiMemberModifiers Modifiers,
	ApiTypeReference Type
) : ApiMember(Name, Accessibility, Attributes);
