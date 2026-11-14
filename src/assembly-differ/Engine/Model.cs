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

namespace Differ.Engine;

/// <summary>An attribute, named by its constructor: <c>System.ObsoleteAttribute::.ctor(System.String)</c>.</summary>
internal sealed record AttributeModel(string Signature);

internal sealed record AssemblyModel(string FullName, IReadOnlyList<AttributeModel> Attributes, IReadOnlyList<ModuleModel> Modules);

internal sealed record ModuleModel(
	string Name,
	IReadOnlyList<AttributeModel> Attributes,
	IReadOnlyList<string> AssemblyReferences,
	IReadOnlyList<TypeModel> Types
);

internal sealed record TypeModel(
	string FullName,
	string DisplayName,
	TypeAttributes Flags,
	IReadOnlyList<AttributeModel> Attributes,
	IReadOnlyList<FieldModel> Fields,
	IReadOnlyList<PropertyModel> Properties,
	IReadOnlyList<MethodModel> Methods,
	IReadOnlyList<EventModel> Events,
	IReadOnlyList<TypeModel> NestedTypes
)
{
	public TypeAttributes Visibility => Flags & TypeAttributes.VisibilityMask;
}

/// <summary>A member's name and type as the reports show them: <c>Draw()</c> and <c>Void</c>.</summary>
internal readonly record struct Display(string Name, string? Type);

internal sealed record MethodModel(
	string Signature,
	MethodAttributes Flags,
	string ReturnTypeFullName,
	Display Display,
	IReadOnlyList<string> ParameterNames,
	IReadOnlyList<AttributeModel> Attributes
)
{
	public bool IsApi => Access is MethodAttributes.Public or MethodAttributes.FamORAssem or MethodAttributes.Family;

	public MethodAttributes Access => Flags & MethodAttributes.MemberAccessMask;

	public bool IsVirtual => (Flags & MethodAttributes.Virtual) != 0;

	public bool IsStatic => (Flags & MethodAttributes.Static) != 0;

	public bool IsAbstract => (Flags & MethodAttributes.Abstract) != 0;
}

internal sealed record FieldModel(string Name, FieldAttributes Flags, string TypeFullName, Display Display, IReadOnlyList<AttributeModel> Attributes)
{
	public FieldAttributes Access => Flags & FieldAttributes.FieldAccessMask;

	public bool IsApi => Access is FieldAttributes.Public or FieldAttributes.FamORAssem or FieldAttributes.Family;

	public bool IsStatic => (Flags & FieldAttributes.Static) != 0;

	public bool AddsObligation => Attributes.Any(Obligations.IsRequired);
}

internal sealed record PropertyModel(
	string Name,
	string TypeFullName,
	Display Display,
	IReadOnlyList<AttributeModel> Attributes,
	MethodModel? Get,
	MethodModel? Set
)
{
	public bool AddsObligation => Get is { IsAbstract: true } || Set is { IsAbstract: true } || Attributes.Any(Obligations.IsRequired);
}

internal static class Obligations
{
	public static bool IsRequired(AttributeModel attribute) => attribute.Signature == "System.Runtime.CompilerServices.RequiredMemberAttribute::.ctor()";
}

internal sealed record EventModel(string Name, Display Display, IReadOnlyList<AttributeModel> Attributes, MethodModel? Add, MethodModel? Remove)
{
	public bool AddsObligation => Add is { IsAbstract: true } || Remove is { IsAbstract: true };
}

/// <summary>Reads the parts of an assembly the comparison looks at, in the order the metadata tables hold them.</summary>
internal sealed class ModelReader
{
	private readonly MetadataReader _reader;
	private readonly ShapeProvider _shapes;
	private readonly HashSet<MethodDefinitionHandle> _overriding = [];

	private ModelReader(MetadataReader reader)
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

	public static AssemblyModel Read(string path)
	{
		using var stream = File.OpenRead(path);
		using var pe = new PEReader(stream);
		var reader = pe.GetMetadataReader();
		return new ModelReader(reader).ReadAssembly();
	}

	private AssemblyModel ReadAssembly()
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
		return new AssemblyModel(fullName, Attributes(assembly.GetCustomAttributes()), [ReadModule()]);
	}

	private ModuleModel ReadModule()
	{
		var module = _reader.GetModuleDefinition();
		var references = _reader.AssemblyReferences.Select(h => ReferenceFullName(_reader.GetAssemblyReference(h))).ToList();
		var types = _reader.TypeDefinitions.Where(h => _reader.GetTypeDefinition(h).GetDeclaringType().IsNil).Select(ReadType).ToList();
		return new ModuleModel(_reader.GetString(module.Name), Attributes(_reader.GetCustomAttributes(EntityHandle.ModuleDefinition)), references, types);
	}

	private TypeModel ReadType(TypeDefinitionHandle handle)
	{
		var type = _reader.GetTypeDefinition(handle);
		var shape = _shapes.Definition(handle);
		var typeParameters = GenericParameterNames(type.GetGenericParameters());
		var context = new GenericContext(typeParameters, []);

		var accessors = new HashSet<MethodDefinitionHandle>();
		var properties = new List<PropertyModel>();
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
				new PropertyModel(
					name,
					propertyType.FullName,
					new Display(MemberName(name, explicitImplementation), propertyType.DisplayName),
					Attributes(property.GetCustomAttributes()),
					propertyAccessors.Getter.IsNil ? null : ReadMethod(propertyAccessors.Getter, shape, typeParameters),
					propertyAccessors.Setter.IsNil ? null : ReadMethod(propertyAccessors.Setter, shape, typeParameters)
				)
			);
		}

		var events = new List<EventModel>();
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
				new EventModel(
					name,
					new Display(MemberName(name, explicitImplementation), eventType.DisplayName),
					Attributes(@event.GetCustomAttributes()),
					eventAccessors.Adder.IsNil ? null : ReadMethod(eventAccessors.Adder, shape, typeParameters),
					eventAccessors.Remover.IsNil ? null : ReadMethod(eventAccessors.Remover, shape, typeParameters)
				)
			);
		}

		var fields = new List<FieldModel>();
		foreach (var fieldHandle in type.GetFields())
		{
			var field = _reader.GetFieldDefinition(fieldHandle);
			var fieldType = field.DecodeSignature(_shapes, context);
			var name = _reader.GetString(field.Name);
			fields.Add(
				new FieldModel(
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

		return new TypeModel(
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

	private MethodModel ReadMethod(MethodDefinitionHandle handle, NamedShape declaringType, ImmutableArray<string> typeParameters)
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

		return new MethodModel(
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

	private List<AttributeModel> Attributes(CustomAttributeHandleCollection attributes) =>
		attributes.Select(h => new AttributeModel(ConstructorSignature(_reader.GetCustomAttribute(h).Constructor))).ToList();

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
