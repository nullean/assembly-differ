// The comparison is a port of JustAssembly (https://github.com/telerik/JustAssembly), Copyright Telerik, licensed
// under the Apache License 2.0, and reproduces its reports. Names follow JustDecompile's C# writer from the same project.
#nullable enable
using System;
using System.Collections.Generic;
using System.Collections.Immutable;
using System.IO;
using System.Linq;
using System.Reflection;
using System.Reflection.Metadata;
using System.Reflection.PortableExecutable;
using System.Text;

namespace Nullean.PublicApi;

/// <summary>An attribute, named by its constructor: <c>System.ObsoleteAttribute::.ctor(System.String)</c>.</summary>
/// <param name="Signature">The constructor's declaring type and parameter types.</param>
[System.Diagnostics.CodeAnalysis.SuppressMessage("Naming", "CA1711", Justification = "It describes an attribute.")]
public sealed record ApiAttribute(string Signature);

/// <summary>An assembly's public API, as the metadata holds it.</summary>
/// <param name="FullName">The assembly's display name: name, version, culture and public key token.</param>
/// <param name="Attributes">The attributes on the assembly.</param>
/// <param name="Modules">The assembly's module; a .NET assembly has one.</param>
public sealed record ApiAssembly(string FullName, IReadOnlyList<ApiAttribute> Attributes, IReadOnlyList<ApiModule> Modules);

/// <summary>A module and the types declared in it.</summary>
/// <param name="Name">The module's file name.</param>
/// <param name="Attributes">The attributes on the module.</param>
/// <param name="AssemblyReferences">The display names of the assemblies the module references.</param>
/// <param name="Types">The top-level types, of every visibility; nested types are under their declaring type.</param>
public sealed record ApiModule(string Name, IReadOnlyList<ApiAttribute> Attributes, IReadOnlyList<string> AssemblyReferences, IReadOnlyList<ApiType> Types);

/// <summary>A type and its members, of every visibility.</summary>
/// <param name="FullName">The name in metadata: <c>Lib.Outer/Inner</c> or <c>Lib.Box`1</c>.</param>
/// <param name="DisplayName">The name the reports show: <c>Lib.Box&lt;T&gt;</c>.</param>
/// <param name="Flags">The type's metadata attributes.</param>
/// <param name="Attributes">The attributes on the type.</param>
/// <param name="Fields">The fields.</param>
/// <param name="Properties">The properties.</param>
/// <param name="Methods">The methods, without property and event accessors.</param>
/// <param name="Events">The events.</param>
/// <param name="NestedTypes">The types declared inside this one.</param>
public sealed record ApiType(
	string FullName,
	string DisplayName,
	TypeAttributes Flags,
	IReadOnlyList<ApiAttribute> Attributes,
	IReadOnlyList<ApiField> Fields,
	IReadOnlyList<ApiProperty> Properties,
	IReadOnlyList<ApiMethod> Methods,
	IReadOnlyList<ApiEvent> Events,
	IReadOnlyList<ApiType> NestedTypes
)
{
	/// <summary>The visibility bits of <see cref="Flags"/>.</summary>
	public TypeAttributes Visibility => Flags & TypeAttributes.VisibilityMask;
}

/// <summary>A member's name and type as the reports show them: <c>Draw()</c> and <c>Void</c>.</summary>
/// <param name="Name">The member's name, with parameters for a method.</param>
/// <param name="Type">The member's type, or a method's return type; null for a constructor.</param>
public readonly record struct Display(string Name, string? Type);

/// <summary>A method, constructor or accessor.</summary>
/// <param name="Name">The name in metadata: <c>Draw</c> or <c>.ctor</c>.</param>
/// <param name="Signature">The name with the parameter types, which tells overloads apart.</param>
/// <param name="Flags">The method's metadata attributes.</param>
/// <param name="ReturnTypeFullName">The return type's full name.</param>
/// <param name="Display">The name and return type as the reports show them.</param>
/// <param name="ParameterNames">The parameter names, in order.</param>
/// <param name="Attributes">The attributes on the method.</param>
public sealed record ApiMethod(
	string Name,
	string Signature,
	MethodAttributes Flags,
	string ReturnTypeFullName,
	Display Display,
	IReadOnlyList<string> ParameterNames,
	IReadOnlyList<ApiAttribute> Attributes
)
{
	/// <summary>Whether code outside the assembly can call it: public or protected.</summary>
	public bool IsApi => Access is MethodAttributes.Public or MethodAttributes.FamORAssem or MethodAttributes.Family;

	/// <summary>The access bits of <see cref="Flags"/>.</summary>
	public MethodAttributes Access => Flags & MethodAttributes.MemberAccessMask;

	/// <summary>Whether the method is virtual.</summary>
	public bool IsVirtual => (Flags & MethodAttributes.Virtual) != 0;

	/// <summary>Whether the method is static.</summary>
	public bool IsStatic => (Flags & MethodAttributes.Static) != 0;

	/// <summary>Whether the method is abstract, which includes an interface method without a default implementation.</summary>
	public bool IsAbstract => (Flags & MethodAttributes.Abstract) != 0;
}

/// <summary>A field.</summary>
/// <param name="Name">The field's name.</param>
/// <param name="Flags">The field's metadata attributes.</param>
/// <param name="TypeFullName">The field type's full name.</param>
/// <param name="Display">The name and type as the reports show them.</param>
/// <param name="Attributes">The attributes on the field.</param>
public sealed record ApiField(string Name, FieldAttributes Flags, string TypeFullName, Display Display, IReadOnlyList<ApiAttribute> Attributes)
{
	/// <summary>The access bits of <see cref="Flags"/>.</summary>
	public FieldAttributes Access => Flags & FieldAttributes.FieldAccessMask;

	/// <summary>Whether code outside the assembly can use it: public or protected.</summary>
	public bool IsApi => Access is FieldAttributes.Public or FieldAttributes.FamORAssem or FieldAttributes.Family;

	/// <summary>Whether the field is static.</summary>
	public bool IsStatic => (Flags & FieldAttributes.Static) != 0;

	/// <summary>Whether the field is <c>required</c>, so every object initializer has to set it.</summary>
	public bool AddsObligation => Attributes.Any(Obligations.IsRequired);
}

/// <summary>A property and its accessors.</summary>
/// <param name="Name">The property's name.</param>
/// <param name="TypeFullName">The property type's full name.</param>
/// <param name="Display">The name and type as the reports show them.</param>
/// <param name="Attributes">The attributes on the property.</param>
/// <param name="Get">The getter, if it has one.</param>
/// <param name="Set">The setter or init accessor, if it has one.</param>
public sealed record ApiProperty(string Name, string TypeFullName, Display Display, IReadOnlyList<ApiAttribute> Attributes, ApiMethod? Get, ApiMethod? Set)
{
	/// <summary>Whether a new property of this shape asks something of existing code: it is abstract or <c>required</c>.</summary>
	public bool AddsObligation => Get is { IsAbstract: true } || Set is { IsAbstract: true } || Attributes.Any(Obligations.IsRequired);
}

internal static class Obligations
{
	public static bool IsRequired(ApiAttribute attribute) => attribute.Signature == "System.Runtime.CompilerServices.RequiredMemberAttribute::.ctor()";
}

/// <summary>An event and its accessors.</summary>
/// <param name="Name">The event's name.</param>
/// <param name="Display">The name and handler type as the reports show them.</param>
/// <param name="Attributes">The attributes on the event.</param>
/// <param name="Add">The add accessor.</param>
/// <param name="Remove">The remove accessor.</param>
public sealed record ApiEvent(string Name, Display Display, IReadOnlyList<ApiAttribute> Attributes, ApiMethod? Add, ApiMethod? Remove)
{
	/// <summary>Whether the event is abstract, so every implementer has to add it.</summary>
	public bool AddsObligation => Add is { IsAbstract: true } || Remove is { IsAbstract: true };
}

/// <summary>Reads the parts of an assembly the comparison looks at, in the order the metadata tables hold them.</summary>
public sealed class ApiReader
{
	private readonly MetadataReader _reader;
	private readonly ShapeProvider _shapes;
	private readonly HashSet<MethodDefinitionHandle> _overriding = [];

	private ApiReader(MetadataReader reader)
	{
		_reader = reader;
		_shapes = new ShapeProvider(reader);
		foreach (var type in reader.TypeDefinitions)
			foreach (var handle in reader.GetTypeDefinition(type).GetMethodImplementations())
			{
				var body = reader.GetMethodImplementation(handle).MethodBody;
				if (body.Kind == HandleKind.MethodDefinition)
					_overriding.Add((MethodDefinitionHandle)body);
			}
	}

	/// <summary>Reads the assembly at <paramref name="path"/>.</summary>
	public static ApiAssembly Read(string path)
	{
		using var stream = File.OpenRead(path);
		return Read(stream);
	}

	/// <summary>Reads an assembly from <paramref name="stream"/>, which must be seekable.</summary>
	public static ApiAssembly Read(Stream stream)
	{
		using var pe = new PEReader(stream, PEStreamOptions.LeaveOpen);
		var reader = pe.GetMetadataReader();
		return new ApiReader(reader).ReadAssembly();
	}

	private ApiAssembly ReadAssembly()
	{
		var assembly = _reader.GetAssemblyDefinition();
		var name = assembly.GetAssemblyName();
		var fullName = AssemblyFullName(
			_reader.GetString(assembly.Name),
			assembly.Version,
			_reader.GetString(assembly.Culture),
			name.GetPublicKeyToken(),
			(assembly.Flags & AssemblyFlags.Retargetable) != 0
		);
		return new ApiAssembly(fullName, Attributes(assembly.GetCustomAttributes()), [ReadModule()]);
	}

	private ApiModule ReadModule()
	{
		var module = _reader.GetModuleDefinition();
		var references = _reader.AssemblyReferences.Select(h => ReferenceFullName(_reader.GetAssemblyReference(h))).ToList();
		var types = _reader.TypeDefinitions.Where(h => _reader.GetTypeDefinition(h).GetDeclaringType().IsNil).Select(ReadType).ToList();
		return new ApiModule(_reader.GetString(module.Name), Attributes(_reader.GetCustomAttributes(EntityHandle.ModuleDefinition)), references, types);
	}

	private ApiType ReadType(TypeDefinitionHandle handle)
	{
		var type = _reader.GetTypeDefinition(handle);
		var shape = _shapes.Definition(handle);
		var typeParameters = GenericParameterNames(type.GetGenericParameters());
		var context = new GenericContext(typeParameters, []);

		var accessors = new HashSet<MethodDefinitionHandle>();
		var properties = new List<ApiProperty>();
		foreach (var propertyHandle in type.GetProperties())
		{
			var property = _reader.GetPropertyDefinition(propertyHandle);
			var propertyAccessors = property.GetAccessors();
			if (!propertyAccessors.Getter.IsNil)
				accessors.Add(propertyAccessors.Getter);
			if (!propertyAccessors.Setter.IsNil)
				accessors.Add(propertyAccessors.Setter);
			var propertyType = property.DecodeSignature(_shapes, context).ReturnType;
			var name = _reader.GetString(property.Name);
			var explicitImplementation = IsExplicit(propertyAccessors.Getter) || IsExplicit(propertyAccessors.Setter);
			properties.Add(
				new ApiProperty(
					name,
					propertyType.FullName,
					new Display(MemberName(name, explicitImplementation), propertyType.DisplayName),
					Attributes(property.GetCustomAttributes()),
					propertyAccessors.Getter.IsNil ? null : ReadMethod(propertyAccessors.Getter, shape, typeParameters),
					propertyAccessors.Setter.IsNil ? null : ReadMethod(propertyAccessors.Setter, shape, typeParameters)
				)
			);
		}

		var events = new List<ApiEvent>();
		foreach (var eventHandle in type.GetEvents())
		{
			var @event = _reader.GetEventDefinition(eventHandle);
			var eventAccessors = @event.GetAccessors();
			if (!eventAccessors.Adder.IsNil)
				accessors.Add(eventAccessors.Adder);
			if (!eventAccessors.Remover.IsNil)
				accessors.Add(eventAccessors.Remover);
			var name = _reader.GetString(@event.Name);
			var eventType = _shapes.FromTypeHandle(@event.Type, context);
			var explicitImplementation = IsExplicit(eventAccessors.Adder) || IsExplicit(eventAccessors.Remover);
			events.Add(
				new ApiEvent(
					name,
					new Display(MemberName(name, explicitImplementation), eventType.DisplayName),
					Attributes(@event.GetCustomAttributes()),
					eventAccessors.Adder.IsNil ? null : ReadMethod(eventAccessors.Adder, shape, typeParameters),
					eventAccessors.Remover.IsNil ? null : ReadMethod(eventAccessors.Remover, shape, typeParameters)
				)
			);
		}

		var fields = new List<ApiField>();
		foreach (var fieldHandle in type.GetFields())
		{
			var field = _reader.GetFieldDefinition(fieldHandle);
			var fieldType = field.DecodeSignature(_shapes, context);
			var name = _reader.GetString(field.Name);
			fields.Add(
				new ApiField(
					name,
					field.Attributes,
					fieldType.FullName,
					new Display(MemberName(name, false), fieldType.DisplayName),
					Attributes(field.GetCustomAttributes())
				)
			);
		}

		var methods = type.GetMethods().Where(m => !accessors.Contains(m)).Select(m => ReadMethod(m, shape, typeParameters)).ToList();
		var nested = type.GetNestedTypes().Select(ReadType).ToList();

		return new ApiType(
			shape.FullName,
			_reader.GetString(type.Namespace) + "." + TypeDisplayName(type, typeParameters),
			type.Attributes,
			Attributes(type.GetCustomAttributes()),
			fields,
			properties,
			methods,
			events,
			nested
		);
	}

	private ApiMethod ReadMethod(MethodDefinitionHandle handle, NamedShape declaringType, ImmutableArray<string> typeParameters)
	{
		var method = _reader.GetMethodDefinition(handle);
		var methodParameters = GenericParameterNames(method.GetGenericParameters());
		var signature = method.DecodeSignature(_shapes, new GenericContext(typeParameters, methodParameters));
		var name = _reader.GetString(method.Name);

		var parameterNames = Enumerable.Repeat(string.Empty, signature.ParameterTypes.Length).ToArray();
		foreach (var parameterHandle in method.GetParameters())
		{
			var parameter = _reader.GetParameter(parameterHandle);
			if (parameter.SequenceNumber > 0 && parameter.SequenceNumber <= parameterNames.Length)
				parameterNames[parameter.SequenceNumber - 1] = _reader.GetString(parameter.Name);
		}

		var isConstructor = (method.Attributes & MethodAttributes.RTSpecialName) != 0 && (method.Attributes & MethodAttributes.SpecialName) != 0 &&
			name is ".ctor" or ".cctor";
		var display = new StringBuilder(Names.DottedIdentifier(Names.NonGeneric(isConstructor ? declaringType.Name : name)));
		if (methodParameters.Length > 0)
			display.Append('<').Append(string.Join(", ", methodParameters.Select(Names.Identifier))).Append('>');
		display.Append('(').Append(string.Join(", ", signature.ParameterTypes.Select(p => p.DisplayName))).Append(')');

		return new ApiMethod(
			name,
			declaringType.FullName + "::" + name + "(" + string.Join(",", signature.ParameterTypes.Select(p => p.FullName)) + ")",
			method.Attributes,
			signature.ReturnType.FullName,
			new Display(display.ToString(), isConstructor ? null : signature.ReturnType.DisplayName),
			parameterNames,
			Attributes(method.GetCustomAttributes())
		);
	}

	private string TypeDisplayName(TypeDefinition type, ImmutableArray<string> typeParameters)
	{
		var display = new StringBuilder(Names.Identifier(Names.NonGeneric(_reader.GetString(type.Name))));
		// a nested type repeats its declaring type's parameters first; only its own are written
		var declaring = type.GetDeclaringType();
		var inherited = declaring.IsNil ? 0 : _reader.GetTypeDefinition(declaring).GetGenericParameters().Count;
		var parameters = type.GetGenericParameters();
		if (inherited < parameters.Count)
		{
			display.Append('<');
			for (var i = inherited; i < parameters.Count; i++)
			{
				var parameter = _reader.GetGenericParameter(parameters[i]);
				var variance = parameter.Attributes & GenericParameterAttributes.VarianceMask;
				if (variance == GenericParameterAttributes.Covariant)
					display.Append("out ");
				if (variance == GenericParameterAttributes.Contravariant)
					display.Append("in ");
				display.Append(Names.Identifier(typeParameters[i]));
				if (i != parameters.Count - 1)
					display.Append(", ");
			}
			display.Append('>');
		}
		return display.ToString();
	}

	/// <summary>A property, event or field name; an explicit implementation's dots separate identifiers.</summary>
	private static string MemberName(string name, bool explicitImplementation)
	{
		name = Names.NonGeneric(name);
		return explicitImplementation ? Names.DottedIdentifier(name) : Names.Identifier(name);
	}

	private bool IsExplicit(MethodDefinitionHandle accessor) =>
		!accessor.IsNil && _overriding.Contains(accessor) && (_reader.GetMethodDefinition(
			accessor
		).Attributes & MethodAttributes.MemberAccessMask) == MethodAttributes.Private;

	private ImmutableArray<string> GenericParameterNames(GenericParameterHandleCollection parameters) =>
		[.. parameters.Select(p => _reader.GetString(_reader.GetGenericParameter(p).Name))];

	private List<ApiAttribute> Attributes(CustomAttributeHandleCollection attributes) =>
		attributes.Select(h => new ApiAttribute(ConstructorSignature(_reader.GetCustomAttribute(h).Constructor))).ToList();

	private string ConstructorSignature(EntityHandle constructor)
	{
		if (constructor.Kind == HandleKind.MethodDefinition)
		{
			var method = _reader.GetMethodDefinition((MethodDefinitionHandle)constructor);
			var declaring = method.GetDeclaringType();
			var typeParameters = GenericParameterNames(_reader.GetTypeDefinition(declaring).GetGenericParameters());
			var signature = method.DecodeSignature(_shapes, new GenericContext(typeParameters, GenericParameterNames(method.GetGenericParameters())));
			return _shapes.Definition(declaring).FullName + "::" + _reader.GetString(method.Name) + Parameters(signature);
		}

		var reference = _reader.GetMemberReference((MemberReferenceHandle)constructor);
		var parent = reference.Parent.Kind switch
		{
			HandleKind.TypeDefinition or HandleKind.TypeReference or HandleKind.TypeSpecification =>
				_shapes.FromTypeHandle(reference.Parent, GenericContext.None).FullName,
			_ => string.Empty,
		};
		return parent + "::" + _reader.GetString(reference.Name) + Parameters(reference.DecodeMethodSignature(_shapes, GenericContext.None));
	}

	private static string Parameters(MethodSignature<Shape> signature) => "(" + string.Join(",", signature.ParameterTypes.Select(p => p.FullName)) + ")";

	private string ReferenceFullName(AssemblyReference reference)
	{
		var publicKeyOrToken = _reader.GetBlobBytes(reference.PublicKeyOrToken);
		byte[]? token = publicKeyOrToken;
		if ((reference.Flags & AssemblyFlags.PublicKey) != 0 && publicKeyOrToken.Length > 0)
		{
			var name = new AssemblyName();
			name.SetPublicKey(publicKeyOrToken);
			token = name.GetPublicKeyToken();
		}
		return AssemblyFullName(
			_reader.GetString(reference.Name),
			reference.Version,
			_reader.GetString(reference.Culture),
			token,
			(reference.Flags & AssemblyFlags.Retargetable) != 0
		);
	}

	/// <summary>An assembly's display name the way Mono.Cecil writes it.</summary>
	private static string AssemblyFullName(string name, Version version, string culture, byte[]? publicKeyToken, bool retargetable)
	{
		var builder = new StringBuilder(name);
		builder.Append(", Version=").Append(version.ToString(4));
		builder.Append(", Culture=").Append(culture.Length == 0 ? "neutral" : culture);
		builder.Append(", PublicKeyToken=");
		if (publicKeyToken is { Length: > 0 })
			foreach (var b in publicKeyToken)
				builder.Append(b.ToString("x2", System.Globalization.CultureInfo.InvariantCulture));
		else
			builder.Append("null");
		if (retargetable)
			builder.Append(", Retargetable=Yes");
		return builder.ToString();
	}
}
