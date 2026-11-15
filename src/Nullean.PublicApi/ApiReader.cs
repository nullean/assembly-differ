#nullable enable
using System;
using System.Collections.Generic;
using System.Collections.Immutable;
using System.Globalization;
using System.IO;
using System.Linq;
using System.Reflection;
using System.Reflection.Metadata;
using System.Reflection.PortableExecutable;
using System.Text;

namespace Nullean.PublicApi;

/// <summary>
/// Reads the public API of an assembly from its metadata, without loading it: the types and members code outside it
/// can see, with the base types, interfaces, constraints, nullability, tuple names, default values and attributes
/// their declarations have.
/// </summary>
public sealed class ApiReader
{
	private const string CompilerServices = "System.Runtime.CompilerServices.";

	/// <summary>Attributes the compiler writes to record what C# syntax says; the declarations show that syntax instead.</summary>
	private static readonly HashSet<string> Bookkeeping =
	[
		CompilerServices + "NullableAttribute",
		CompilerServices + "NullableContextAttribute",
		CompilerServices + "NullablePublicOnlyAttribute",
		CompilerServices + "CompilerGeneratedAttribute",
		CompilerServices + "IsReadOnlyAttribute",
		CompilerServices + "IsByRefLikeAttribute",
		CompilerServices + "IsUnmanagedAttribute",
		CompilerServices + "ExtensionAttribute",
		CompilerServices + "TupleElementNamesAttribute",
		CompilerServices + "DynamicAttribute",
		CompilerServices + "NativeIntegerAttribute",
		CompilerServices + "RequiredMemberAttribute",
		CompilerServices + "CompilerFeatureRequiredAttribute",
		CompilerServices + "ScopedRefAttribute",
		CompilerServices + "RequiresLocationAttribute",
		CompilerServices + "AsyncStateMachineAttribute",
		CompilerServices + "IteratorStateMachineAttribute",
		CompilerServices + "AsyncIteratorStateMachineAttribute",
		CompilerServices + "RefSafetyRulesAttribute",
		CompilerServices + "DecimalConstantAttribute",
		CompilerServices + "DateTimeConstantAttribute",
		CompilerServices + "ParamCollectionAttribute",
		CompilerServices + "PreserveBaseOverridesAttribute",
		CompilerServices + "SkipLocalsInitAttribute",
		CompilerServices + "ExtensionMarkerAttribute",
		"System.ParamArrayAttribute",
		"System.Reflection.DefaultMemberAttribute",
		"System.Diagnostics.DebuggerHiddenAttribute",
		"System.Diagnostics.DebuggerStepThroughAttribute",
		"System.Diagnostics.DebuggerNonUserCodeAttribute",
		"System.Diagnostics.DebuggerBrowsableAttribute",
		"System.Diagnostics.DebuggerDisplayAttribute",
		"Microsoft.CodeAnalysis.EmbeddedAttribute",
	];

	private readonly MetadataReader _reader;
	private readonly TypeReferenceProvider _types;

	// owned by Read, which disposes it once the assembly is read
	private readonly EnumCatalog _catalog;

	private ApiReader(MetadataReader reader, TypeReferenceProvider types, EnumCatalog catalog)
	{
		_reader = reader;
		_types = types;
		_catalog = catalog;
	}

	/// <summary>
	/// Whether the assembly was built for a runtime where IntPtr is nint: one targeting .NET 7 or later, or a core
	/// library that declares RuntimeFeature.NumericIntPtr itself.
	/// </summary>
	private static bool NumericIntPtr(MetadataReader reader)
	{
		foreach (var handle in reader.TypeDefinitions)
		{
			var type = reader.GetTypeDefinition(handle);
			if (reader.StringComparer.Equals(type.Name, "RuntimeFeature") && reader.StringComparer.Equals(type.Namespace, "System.Runtime.CompilerServices"))
				return type.GetFields().Any(f => reader.StringComparer.Equals(reader.GetFieldDefinition(f).Name, "NumericIntPtr"));
		}
		var probe = new TypeReferenceProvider(reader);
		foreach (var handle in reader.GetAssemblyDefinition().GetCustomAttributes())
		{
			var attribute = reader.GetCustomAttribute(handle);
			if (attribute.Constructor.Kind != HandleKind.MemberReference)
				continue;
			var parent = reader.GetMemberReference((MemberReferenceHandle)attribute.Constructor).Parent;
			if (parent.Kind != HandleKind.TypeReference || probe.FullName(parent) != "System.Runtime.Versioning.TargetFrameworkAttribute")
				continue;
			var value = attribute.DecodeValue(probe);
			if (value.FixedArguments is [{ Value: string framework }, ..])
				return IsNet7OrLater(framework);
		}
		return false;
	}

	/// <summary><c>.NETCoreApp,Version=v8.0</c> and the like.</summary>
	private static bool IsNet7OrLater(string framework)
	{
		const string prefix = ".NETCoreApp,Version=v";
#pragma warning disable CA1846 // netstandard2.0 cannot parse a span
		return framework.StartsWith(prefix, StringComparison.Ordinal) && Version.TryParse(
			framework.Substring(prefix.Length),
			out var version
		) && version.Major >= 7;
#pragma warning restore CA1846
	}

	/// <summary>
	/// Reads the assembly at <paramref name="path"/>, looking in its folder and the installed .NET runtime for the enums
	/// it refers to.
	/// </summary>
	public static ApiAssembly Read(string path) => Read(path, new ApiReaderOptions());

	/// <summary>Reads the assembly at <paramref name="path"/>, looking for the assemblies it references as <paramref name="options"/> say.</summary>
	public static ApiAssembly Read(string path, ApiReaderOptions options)
	{
		using var stream = File.OpenRead(path);
		var directory = System.IO.Path.GetDirectoryName(System.IO.Path.GetFullPath(path));
		return Read(stream, options, directory is null ? [] : [directory]);
	}

	/// <summary>Reads an assembly from <paramref name="stream"/>, which must be seekable, looking in the installed .NET runtime for the enums it refers to.</summary>
	public static ApiAssembly Read(Stream stream) => Read(stream, new ApiReaderOptions());

	/// <summary>Reads an assembly from <paramref name="stream"/>, which must be seekable, looking for the assemblies it references as <paramref name="options"/> say.</summary>
	public static ApiAssembly Read(Stream stream, ApiReaderOptions options) => Read(stream, options, []);

	private static ApiAssembly Read(Stream stream, ApiReaderOptions options, IReadOnlyList<string> own)
	{
#pragma warning disable CA1510 // netstandard2.0 has no ThrowIfNull
		if (options is null)
			throw new ArgumentNullException(nameof(options));
#pragma warning restore CA1510
		using var pe = new PEReader(stream, PEStreamOptions.LeaveOpen);
		var metadata = pe.GetMetadataReader();
		var directories = own.Concat(options.ReferenceDirectories).Concat(options.SearchRuntime ? RuntimeDirectories() : []).ToList();
		var types = new TypeReferenceProvider(metadata, NumericIntPtr(metadata));
		using var catalog = new EnumCatalog(metadata, types, directories);
		types.Catalog = catalog;
		return new ApiReader(metadata, types, catalog).ReadAssembly();
	}

	/// <summary>
	/// Where the .NET runtime's assemblies are: the running one's, or, in a native build that has none to point at, the
	/// newest shared runtime of the installed SDK.
	/// </summary>
	private static IEnumerable<string> RuntimeDirectories()
	{
#pragma warning disable IL3000 // empty in a native or single-file build, which the fallback below covers
		var running = typeof(object).Assembly.Location;
#pragma warning restore IL3000
		if (!string.IsNullOrEmpty(running))
		{
			yield return System.IO.Path.GetDirectoryName(running)!;
			yield break;
		}
		var roots = new List<string?> { Environment.GetEnvironmentVariable("DOTNET_ROOT") };
		foreach (var directory in (Environment.GetEnvironmentVariable("PATH") ?? "").Split(System.IO.Path.PathSeparator))
			if (
				directory.Length > 0 &&
				(File.Exists(System.IO.Path.Combine(directory, "dotnet")) || File.Exists(System.IO.Path.Combine(directory, "dotnet.exe")))
			)
				roots.Add(directory);
		foreach (var root in roots.Where(r => !string.IsNullOrEmpty(r)))
		{
			var shared = System.IO.Path.Combine(root!, "shared", "Microsoft.NETCore.App");
			if (!Directory.Exists(shared))
				continue;
			var newest = Directory
				.GetDirectories(shared)
				.Select(d => (Path: d, Version: Version.TryParse(System.IO.Path.GetFileName(d).Split('-')[0], out var v) ? v : new Version()))
				.OrderByDescending(d => d.Version)
				.FirstOrDefault();
			if (newest.Path is not null)
			{
				yield return newest.Path;
				yield break;
			}
		}
	}

	private ApiAssembly ReadAssembly()
	{
		var assembly = _reader.GetAssemblyDefinition();
		var token = assembly.GetAssemblyName().GetPublicKeyToken();
		string? targetFramework = null;
		foreach (var handle in assembly.GetCustomAttributes())
		{
			var attribute = _reader.GetCustomAttribute(handle);
			if (
				AttributeTypeName(attribute) == "System.Runtime.Versioning.TargetFrameworkAttribute" &&
				TryDecode(attribute) is { FixedArguments: [{ Value: string name }, ..] }
			)
				targetFramework = name;
		}
		var types = _reader
			.TypeDefinitions
			.Where(h => _reader.GetTypeDefinition(h).GetDeclaringType().IsNil)
			.Select(h => ReadType(h, null))
			.OfType<ApiType>()
			.OrderBy(t => t.Namespace, StringComparer.Ordinal)
			.ThenBy(t => t.Name, StringComparer.Ordinal)
			.ToList();
		return new ApiAssembly(
			_reader.GetString(assembly.Name),
			assembly.Version,
			token is { Length: > 0 } ? string.Concat(token.Select(b => b.ToString("x2", CultureInfo.InvariantCulture))) : null,
			targetFramework,
			types
		);
	}

	/// <summary>A type, or null when code outside the assembly cannot see it.</summary>
	private ApiType? ReadType(TypeDefinitionHandle handle, ApiType? declaring)
	{
		var type = _reader.GetTypeDefinition(handle);
		var name = _reader.GetString(type.Name);
		// the compiler's own types, such as <PrivateImplementationDetails>, have names C# cannot spell
		if (name.Length > 0 && name[0] == '<')
			return null;
		var accessibility = TypeAccessibility(type.Attributes, declaring);
		if (accessibility is null)
			return null;

		var kind = Kind(type);
		var allTypeParameters = TypeParameterNames(type.GetGenericParameters());
		var context = new TypeContext(allTypeParameters, []);
		var inherited = type.GetDeclaringType().IsNil ? 0 : _reader.GetTypeDefinition(type.GetDeclaringType()).GetGenericParameters().Count;
		var typeContext = NullableContext(type.GetCustomAttributes()) ?? declaring?.NullableContext;

		var modifiers = ApiTypeModifiers.None;
		var flags = type.Attributes;
		var isSealed = (flags & TypeAttributes.Sealed) != 0;
		var isAbstract = (flags & TypeAttributes.Abstract) != 0;
		if (kind == ApiTypeKind.Class)
		{
			if (isSealed && isAbstract)
				modifiers |= ApiTypeModifiers.Static;
			else if (isAbstract)
				modifiers |= ApiTypeModifiers.Abstract;
			else if (isSealed)
				modifiers |= ApiTypeModifiers.Sealed;
			if (type.GetMethods().Any(m => _reader.StringComparer.Equals(_reader.GetMethodDefinition(m).Name, "<Clone>$")))
				modifiers |= ApiTypeModifiers.Record;
		}
		if (kind == ApiTypeKind.Struct)
		{
			if (HasAttribute(type.GetCustomAttributes(), CompilerServices + "IsReadOnlyAttribute"))
				modifiers |= ApiTypeModifiers.ReadOnly;
			if (HasAttribute(type.GetCustomAttributes(), CompilerServices + "IsByRefLikeAttribute"))
				modifiers |= ApiTypeModifiers.Ref;
		}

		var canBeExtended = kind == ApiTypeKind.Interface ||
			(kind == ApiTypeKind.Class && !isSealed && type
				.GetMethods()
				.Select(_reader.GetMethodDefinition)
				.Any(m => _reader.StringComparer.Equals(m.Name, ".ctor") && MemberAccessibility(m.Attributes & MethodAttributes.MemberAccessMask) is not null));

		ApiTypeReference? baseType = null;
		if (kind == ApiTypeKind.Class && !type.BaseType.IsNil && _types.FullName(type.BaseType) != "System.Object")
			baseType = Annotate(_types.FromHandle(type.BaseType, context), type.GetCustomAttributes(), typeContext);
		if (kind == ApiTypeKind.Enum)
			baseType = EnumUnderlyingType(type) is { FullName: not "System.Int32" } underlying ? underlying : null;

		// an internal interface of this assembly is not part of its API
		var interfaces = type
			.GetInterfaceImplementations()
			.Select(_reader.GetInterfaceImplementation)
			.Where(i => IsVisibleDefinition(i.Interface))
			.Select(i => Annotate(_types.FromHandle(i.Interface, context), i.GetCustomAttributes(), typeContext))
			.ToList();

		var shell = new ApiType(
			declaring?.Namespace ?? _reader.GetString(type.Namespace),
			name,
			kind,
			accessibility.Value,
			modifiers,
			ReadTypeParameters(type.GetGenericParameters().Skip(inherited), context, typeContext, kind is ApiTypeKind.Interface or ApiTypeKind.Delegate),
			baseType,
			interfaces,
			Attributes(type.GetCustomAttributes()),
			[],
			[],
			canBeExtended
		)
		{ NullableContext = typeContext };

		var members = kind switch
		{
			ApiTypeKind.Enum => EnumValues(type),
			ApiTypeKind.Delegate =>
				type
					.GetMethods()
					.Select(_reader.GetMethodDefinition)
					.Where(m => _reader.GetString(m.Name) == "Invoke")
					.Select(m => ReadMethod(m, shell, allTypeParameters, typeContext, forceVisible: true))
					.OfType<ApiMember>()
					.ToList(),
			_ => WithExtensions(type, shell, Members(type, shell, allTypeParameters, typeContext), typeContext),
		};
		var nested = type.GetNestedTypes().Select(h => ReadType(h, shell)).OfType<ApiType>().OrderBy(t => t.Name, StringComparer.Ordinal).ToList();
		return shell with { Members = members, NestedTypes = nested };
	}

	/// <summary>Whether a type this assembly defines is public all the way out; a type from elsewhere is assumed to be.</summary>
	private bool IsVisibleDefinition(EntityHandle handle)
	{
		if (handle.Kind == HandleKind.TypeSpecification)
		{
			var blob = _reader.GetBlobReader(_reader.GetTypeSpecification((TypeSpecificationHandle)handle).Signature);
			if (blob.ReadSignatureTypeCode() != SignatureTypeCode.GenericTypeInstance)
				return true;
			blob.ReadSignatureTypeCode();
			handle = blob.ReadTypeHandle();
		}
		if (handle.Kind != HandleKind.TypeDefinition)
			return true;
		for (var definition = _reader.GetTypeDefinition((TypeDefinitionHandle)handle); ; definition = _reader.GetTypeDefinition(definition.GetDeclaringType()))
		{
			var visibility = definition.Attributes & TypeAttributes.VisibilityMask;
			if (visibility is not (TypeAttributes.Public or TypeAttributes.NestedPublic))
				return false;
			if (definition.GetDeclaringType().IsNil)
				return true;
		}
	}

	private ApiTypeKind Kind(TypeDefinition type)
	{
		if ((type.Attributes & TypeAttributes.Interface) != 0)
			return ApiTypeKind.Interface;
		var baseName = type.BaseType.IsNil ? string.Empty : _types.FullName(type.BaseType);
		var ownName = _reader.GetString(type.Namespace) + "." + _reader.GetString(type.Name);
		return baseName switch
		{
			"System.Enum" => ApiTypeKind.Enum,
			"System.ValueType" when ownName != "System.Enum" => ApiTypeKind.Struct,
			"System.MulticastDelegate" => ApiTypeKind.Delegate,
			_ => ApiTypeKind.Class,
		};
	}

	private static ApiAccessibility? TypeAccessibility(TypeAttributes attributes, ApiType? declaring)
	{
		var visibility = attributes & TypeAttributes.VisibilityMask;
		if (declaring is null)
			return visibility == TypeAttributes.Public ? ApiAccessibility.Public : null;
		return visibility switch
		{
			TypeAttributes.NestedPublic => ApiAccessibility.Public,
			// a protected type is only reachable from a type that derives from its declaring type
			TypeAttributes.NestedFamily when declaring.CanBeExtendedOutside => ApiAccessibility.Protected,
			TypeAttributes.NestedFamORAssem when declaring.CanBeExtendedOutside => ApiAccessibility.ProtectedInternal,
			_ => null,
		};
	}

	private static ApiAccessibility? MemberAccessibility(MethodAttributes access) => access switch
	{
		MethodAttributes.Public => ApiAccessibility.Public,
		MethodAttributes.Family => ApiAccessibility.Protected,
		MethodAttributes.FamORAssem => ApiAccessibility.ProtectedInternal,
		_ => null,
	};

	/// <summary>A member's accessibility, or null when it is not visible from outside: protected needs a type others can derive from.</summary>
	private static ApiAccessibility? Visible(ApiAccessibility? accessibility, ApiType declaring) =>
		accessibility is ApiAccessibility.Public || (accessibility is not null && declaring.CanBeExtendedOutside) ? accessibility : null;

	private ApiNamedType? EnumUnderlyingType(TypeDefinition type)
	{
		foreach (var handle in type.GetFields())
		{
			var field = _reader.GetFieldDefinition(handle);
			if ((field.Attributes & FieldAttributes.Static) == 0)
				return field.DecodeSignature(_types, TypeContext.None) as ApiNamedType;
		}
		return null;
	}

	private List<ApiMember> EnumValues(TypeDefinition type)
	{
		var values = new List<ApiMember>();
		var underlying = EnumUnderlyingType(type) ?? TypeReferenceProvider.Primitive(PrimitiveTypeCode.Int32);
		foreach (var handle in type.GetFields())
		{
			var field = _reader.GetFieldDefinition(handle);
			if ((field.Attributes & FieldAttributes.Literal) == 0 || (field.Attributes & FieldAttributes.Public) != FieldAttributes.Public)
				continue;
			values.Add(
				new ApiField(
					_reader.GetString(field.Name),
					ApiAccessibility.Public,
					Attributes(field.GetCustomAttributes()),
					underlying,
					true,
					false,
					false,
					false,
					Constant(field.GetDefaultValue())
				)
			);
		}
		return values;
	}

	private List<ApiMember> Members(TypeDefinition type, ApiType declaring, ImmutableArray<string> typeParameters, byte? typeContext)
	{
		var members = new List<ApiMember>();
		var accessors = new HashSet<MethodDefinitionHandle>();
		var context = new TypeContext(typeParameters, []);
		var isStruct = declaring.Kind == ApiTypeKind.Struct;

		foreach (var handle in type.GetFields())
		{
			var field = _reader.GetFieldDefinition(handle);
			var accessibility = Visible(FieldAccessibility(field.Attributes), declaring);
			if (accessibility is null)
				continue;
			var rawType = field.DecodeSignature(_types, context);
			var isVolatile = false;
			while (rawType is ApiModifiedType modified)
			{
				isVolatile |= modified.Modifier.FullName == CompilerServices + "IsVolatile";
				if (modified.Modifier.FullName != CompilerServices + "IsVolatile")
					break;
				rawType = modified.Type;
			}
			var fieldType = Annotate(rawType, field.GetCustomAttributes(), typeContext);
			var constant = (field.Attributes & FieldAttributes.Literal) != 0
				? EnumConstant(rawType, field.GetDefaultValue())
				: DecimalConstant(field.GetCustomAttributes());
			members.Add(
				new ApiField(
					_reader.GetString(field.Name),
					accessibility.Value,
					Attributes(field.GetCustomAttributes()),
					fieldType,
					(field.Attributes & FieldAttributes.Static) != 0,
					constant is null && (field.Attributes & FieldAttributes.InitOnly) != 0,
					isVolatile,
					HasAttribute(field.GetCustomAttributes(), CompilerServices + "RequiredMemberAttribute"),
					constant
				)
			);
		}

		foreach (var handle in type.GetProperties())
		{
			var property = _reader.GetPropertyDefinition(handle);
			var propertyAccessors = property.GetAccessors();
			if (!propertyAccessors.Getter.IsNil)
				accessors.Add(propertyAccessors.Getter);
			if (!propertyAccessors.Setter.IsNil)
				accessors.Add(propertyAccessors.Setter);
			if (ReadProperty(property, propertyAccessors, declaring, context, typeContext, isStruct) is { } read)
				members.Add(read);
		}

		foreach (var handle in type.GetEvents())
		{
			var @event = _reader.GetEventDefinition(handle);
			var eventAccessors = @event.GetAccessors();
			if (!eventAccessors.Adder.IsNil)
				accessors.Add(eventAccessors.Adder);
			if (!eventAccessors.Remover.IsNil)
				accessors.Add(eventAccessors.Remover);
			if (eventAccessors.Adder.IsNil)
				continue;
			var adder = _reader.GetMethodDefinition(eventAccessors.Adder);
			var accessibility = Visible(MemberAccessibility(adder.Attributes & MethodAttributes.MemberAccessMask), declaring);
			if (accessibility is null)
				continue;
			members.Add(
				new ApiEvent(
					_reader.GetString(@event.Name),
					accessibility.Value,
					Attributes(@event.GetCustomAttributes()),
					Modifiers(adder, declaring),
					Annotate(_types.FromHandle(@event.Type, context), @event.GetCustomAttributes(), typeContext)
				)
			);
		}

		foreach (var handle in type.GetMethods())
		{
			if (accessors.Contains(handle))
				continue;
			if (ReadMethod(_reader.GetMethodDefinition(handle), declaring, typeParameters, typeContext, forceVisible: false) is { } method)
				members.Add(method);
		}

		return members.OrderBy(Order).ThenBy(m => m.Name, StringComparer.Ordinal).ThenBy(ParameterKey, StringComparer.Ordinal).ToList();
	}

	private static int Order(ApiMember member) => member switch
	{
		ApiMethod { Kind: ApiMethodKind.Constructor } => 0,
		ApiField => 1,
		ApiProperty => 2,
		ApiEvent => 3,
		ApiMethod { Kind: ApiMethodKind.Method } => 4,
		ApiMethod { Kind: ApiMethodKind.Operator } => 5,
		ApiExtension => 7,
		_ => 6,
	};

	private static List<ApiMember> Ordered(IEnumerable<ApiMember> members) =>
		members.OrderBy(Order).ThenBy(m => m.Name, StringComparer.Ordinal).ThenBy(ParameterKey, StringComparer.Ordinal).ToList();

	/// <summary>
	/// A static class's C# 14 extension blocks, which the compiler writes as nested grouping types (<c>&lt;G&gt;$…</c>):
	/// their members as declared, under a marker type (<c>&lt;M&gt;$…</c>) that holds the receiver, and as static methods
	/// in the class that implement them. The blocks take the place of those methods.
	/// </summary>
	private List<ApiMember> WithExtensions(TypeDefinition container, ApiType shell, List<ApiMember> members, byte? typeContext)
	{
		var blocks = new List<ApiExtension>();
		foreach (var groupingHandle in container.GetNestedTypes())
		{
			var grouping = _reader.GetTypeDefinition(groupingHandle);
			if (
				!_reader.GetString(grouping.Name).StartsWith(
					"<G>$",
					StringComparison.Ordinal
				) || (grouping.Attributes & TypeAttributes.VisibilityMask) != TypeAttributes.NestedPublic
			)
				continue;
			blocks.AddRange(Extensions(grouping, shell, typeContext));
		}
		if (blocks.Count == 0)
			return members;

		var implementations = new HashSet<string>(blocks.SelectMany(Implementations), StringComparer.Ordinal);
		var kept = members.Where(
			m => m is not ApiMethod { Modifiers: var modifiers } method || (modifiers & ApiMemberModifiers.Static) == 0 ||
				!implementations.Contains(Signature(method.Name, method.Parameters.Select(p => p.Type)))
		);
		return Ordered(kept.Concat(blocks));
	}

	private IEnumerable<ApiExtension> Extensions(TypeDefinition grouping, ApiType container, byte? typeContext)
	{
		var groupingContext = NullableContext(grouping.GetCustomAttributes()) ?? typeContext;
		var shell = container with
		{
			Name = _reader.GetString(grouping.Name),
			Kind = ApiTypeKind.Class,
			Modifiers = ApiTypeModifiers.Sealed,
			CanBeExtendedOutside = false,
		};

		// each marker is one block: its <Extension>$ method holds the receiver, and its type parameters have the names
		// the source gave them, where the grouping type's are the compiler's own
		var blocks = new Dictionary<string, (ApiParameter Receiver, ImmutableArray<string> Names, List<ApiTypeParameter> TypeParameters, IReadOnlyList<ApiAttribute> Attributes)>(
			StringComparer.Ordinal
		);
		foreach (var markerHandle in grouping.GetNestedTypes())
		{
			var marker = _reader.GetTypeDefinition(markerHandle);
			var names = TypeParameterNames(marker.GetGenericParameters());
			var context = new TypeContext(names, []);
			foreach (var methodHandle in marker.GetMethods())
			{
				var method = _reader.GetMethodDefinition(methodHandle);
				if (!_reader.StringComparer.Equals(method.Name, "<Extension>$"))
					continue;
				var signature = method.DecodeSignature(_types, context);
				var parameters = ReadParameters(method, signature.ParameterTypes, NullableContext(method.GetCustomAttributes()) ?? groupingContext);
				// a receiver without a name has no parameter row
				var named = false;
				foreach (var parameterHandle in method.GetParameters())
					named |= _reader.GetParameter(parameterHandle).SequenceNumber == 1;
				if (parameters.Count == 1)
					blocks[_reader.GetString(marker.Name)] =
						(named ? parameters[0] : parameters[0] with { Name = "" }, names, ReadTypeParameters(
							marker.GetGenericParameters(),
							context,
							groupingContext,
							false
						), Attributes(marker.GetCustomAttributes()));
			}
		}

		var byMarker = blocks.Keys.ToDictionary(k => k, _ => new List<ApiMember>(), StringComparer.Ordinal);
		var accessors = new HashSet<MethodDefinitionHandle>();
		foreach (var propertyHandle in grouping.GetProperties())
		{
			var property = _reader.GetPropertyDefinition(propertyHandle);
			var propertyAccessors = property.GetAccessors();
			if (!propertyAccessors.Getter.IsNil)
				accessors.Add(propertyAccessors.Getter);
			if (!propertyAccessors.Setter.IsNil)
				accessors.Add(propertyAccessors.Setter);
			var accessor = propertyAccessors.Getter.IsNil ? propertyAccessors.Setter : propertyAccessors.Getter;
			if (
				accessor.IsNil || Marker(_reader.GetMethodDefinition(accessor).GetCustomAttributes()) is not { } markerName ||
				!byMarker.TryGetValue(markerName, out var list)
			)
				continue;
			var context = new TypeContext(blocks[markerName].Names, []);
			if (ReadProperty(property, propertyAccessors, shell, context, groupingContext, false) is { } read)
				list.Add(read);
		}
		foreach (var methodHandle in grouping.GetMethods())
		{
			if (accessors.Contains(methodHandle))
				continue;
			var method = _reader.GetMethodDefinition(methodHandle);
			if (Marker(method.GetCustomAttributes()) is not { } markerName || !byMarker.TryGetValue(markerName, out var list))
				continue;
			if (ReadMethod(method, shell, blocks[markerName].Names, groupingContext, forceVisible: false) is { } read)
				list.Add(read);
		}

		// netstandard2.0's KeyValuePair has no Deconstruct
		foreach (var pair in blocks)
			if (byMarker[pair.Key].Count > 0)
				yield return new ApiExtension(
					"extension",
					ApiAccessibility.Public,
					pair.Value.Attributes,
					pair.Value.TypeParameters,
					pair.Value.Receiver,
					Ordered(byMarker[pair.Key])
				);
	}

	/// <summary>The marker an extension member belongs to, named by its ExtensionMarkerAttribute.</summary>
	private string? Marker(IEnumerable<CustomAttributeHandle> attributes)
	{
		foreach (var handle in attributes)
		{
			var attribute = _reader.GetCustomAttribute(handle);
			if (
				AttributeTypeName(attribute) == CompilerServices + "ExtensionMarkerAttribute" &&
				TryDecode(attribute) is { FixedArguments: [{ Value: string name }] }
			)
				return name;
		}
		return null;
	}

	/// <summary>
	/// The static methods the compiler implements a block's members with: a method by its name, an accessor by
	/// get_ or set_ and the property's name; an instance member takes the receiver first.
	/// </summary>
	private static IEnumerable<string> Implementations(ApiExtension block)
	{
		foreach (var member in block.Members)
		{
			var isStatic = member switch
			{
				ApiMethod m => (m.Modifiers & ApiMemberModifiers.Static) != 0,
				ApiProperty p => (p.Modifiers & ApiMemberModifiers.Static) != 0,
				_ => true,
			};
			IEnumerable<ApiTypeReference> receiver = isStatic ? [] : [block.Receiver.Type];
			switch (member)
			{
				case ApiMethod method:
					yield return Signature(method.Name, receiver.Concat(method.Parameters.Select(p => p.Type)));
					break;
				case ApiProperty property:
					var index = property.Parameters.Select(p => p.Type).ToList();
					if (property.Getter is not null)
						yield return Signature("get_" + property.Name, receiver.Concat(index));
					if (property.Setter is not null)
						yield return Signature("set_" + property.Name, receiver.Concat(index).Append(property.Type));
					break;
			}
		}
	}

	/// <summary>A method's name and parameter types, with type parameters by name whether a type or a method declares them.</summary>
	private static string Signature(string name, IEnumerable<ApiTypeReference> parameters) =>
		name + "(" + string.Join(",", parameters.Select(p => TypeKey(p).Replace("!!", "!"))) + ")";

	/// <summary>Tells overloads apart in the order: their parameter types.</summary>
	private static string ParameterKey(ApiMember member) => member switch
	{
		ApiExtension extension => TypeKey(extension.Receiver.Type) + ";" + extension.TypeParameters.Count,
		ApiMethod method => string.Join(",", method.Parameters.Select(p => p.RefKind + " " + TypeKey(p.Type))) + ";" + method.TypeParameters.Count,
		ApiProperty property => string.Join(",", property.Parameters.Select(p => TypeKey(p.Type))),
		_ => string.Empty,
	};

	internal static string TypeKey(ApiTypeReference type) => type switch
	{
		ApiNamedType named =>
			(named.DeclaringType is null ? named.Namespace + "." + named.Name : TypeKey(named.DeclaringType) + "/" + named.Name) +
				(named.TypeArguments.Count == 0 ? string.Empty : "<" + string.Join(",", named.TypeArguments.Select(TypeKey)) + ">"),
		ApiTupleType tuple => "(" + string.Join(",", tuple.Elements.Select(e => TypeKey(e.Type))) + ")",
		ApiArrayType array => TypeKey(array.ElementType) + "[" + new string(',', array.Rank - 1) + "]",
		ApiPointerType pointer => TypeKey(pointer.ElementType) + "*",
		ApiByReferenceType reference => TypeKey(reference.ElementType) + "&",
		ApiFunctionPointerType function => "delegate*<" + string.Join(",", function.ParameterTypes.Append(function.ReturnType).Select(TypeKey)) + ">",
		ApiTypeParameterReference parameter => (parameter.IsMethodParameter ? "!!" : "!") + parameter.Name,
		ApiDynamicType => "System.Object",
		ApiNativeIntegerType native => native.IsUnsigned ? "System.UIntPtr" : "System.IntPtr",
		ApiModifiedType modified => TypeKey(modified.Type),
		_ => type.ToString(),
	};

	private static ApiAccessibility? FieldAccessibility(FieldAttributes attributes) => (attributes & FieldAttributes.FieldAccessMask) switch
	{
		FieldAttributes.Public => ApiAccessibility.Public,
		FieldAttributes.Family => ApiAccessibility.Protected,
		FieldAttributes.FamORAssem => ApiAccessibility.ProtectedInternal,
		_ => null,
	};

	private ApiProperty? ReadProperty(
		PropertyDefinition property,
		PropertyAccessors propertyAccessors,
		ApiType declaring,
		TypeContext context,
		byte? typeContext,
		bool isStruct
	)
	{
		MethodDefinition? getter = propertyAccessors.Getter.IsNil ? null : _reader.GetMethodDefinition(propertyAccessors.Getter);
		MethodDefinition? setter = propertyAccessors.Setter.IsNil ? null : _reader.GetMethodDefinition(propertyAccessors.Setter);
		var getAccess = getter is { } g ? Visible(MemberAccessibility(g.Attributes & MethodAttributes.MemberAccessMask), declaring) : null;
		var setAccess = setter is { } s ? Visible(MemberAccessibility(s.Attributes & MethodAttributes.MemberAccessMask), declaring) : null;
		if (getAccess is null && setAccess is null)
			return null;
		var accessibility = Most(getAccess, setAccess);
		var primary = getAccess is not null ? getter!.Value : setter!.Value;

		var signature = property.DecodeSignature(_types, context);
		var rawType = signature.ReturnType;
		var refKind = ApiRefKind.None;
		var hasInModifier = false;
		var propertyOffset = 0;
		while (rawType is ApiModifiedType { Modifier.FullName: "System.Runtime.InteropServices.InAttribute" } readOnlyReturn)
		{
			hasInModifier = true;
			rawType = readOnlyReturn.Type;
			propertyOffset++;
		}
		var isByReference = rawType is ApiByReferenceType;
		if (rawType is ApiByReferenceType reference)
		{
			rawType = reference.ElementType;
			refKind = hasInModifier ||
				(getter is { } byRefGetter && HasAttribute(ReturnParameterAttributes(byRefGetter), CompilerServices + "IsReadOnlyAttribute"))
				? ApiRefKind.RefReadOnly
				: ApiRefKind.Ref;
		}
		var memberContext = (getter is { } ng ? NullableContext(ng.GetCustomAttributes()) : null) ?? typeContext;
		// the property's type falls back to its type's context; the getter's context is for the getter's own parameters
		var propertyType = Annotate(rawType, property.GetCustomAttributes(), typeContext, propertyOffset + (isByReference ? 1 : 0));

		// an indexer's parameters are the getter's, or all but the setter's last
		IReadOnlyList<ApiParameter> parameters = [];
		if (signature.ParameterTypes.Length > 0)
		{
			var source = getter ?? setter!.Value;
			var sourceSignature = source.DecodeSignature(_types, context);
			parameters = ReadParameters(source, sourceSignature.ParameterTypes.Take(signature.ParameterTypes.Length).ToImmutableArray(), memberContext);
		}

		ApiAccessor? Accessor(MethodDefinition? method, ApiAccessibility? access, bool isSetter)
		{
			if (method is not { } m || access is null)
				return null;
			var isInit = isSetter &&
				m.DecodeSignature(_types, context).ReturnType is ApiModifiedType { Modifier.FullName: CompilerServices + "IsExternalInit" };
			var isReadOnly = isStruct && !isSetter && HasAttribute(m.GetCustomAttributes(), CompilerServices + "IsReadOnlyAttribute");
			return new ApiAccessor(access.Value, isInit, isReadOnly);
		}

		return new ApiProperty(
			_reader.GetString(property.Name),
			accessibility,
			Attributes(property.GetCustomAttributes()),
			Modifiers(primary, declaring),
			propertyType,
			refKind,
			parameters,
			Accessor(getter, getAccess, false),
			Accessor(setter, setAccess, true),
			HasAttribute(property.GetCustomAttributes(), CompilerServices + "RequiredMemberAttribute")
		);
	}

	private static ApiAccessibility Most(ApiAccessibility? first, ApiAccessibility? second)
	{
		if (first == ApiAccessibility.Public || second == ApiAccessibility.Public)
			return ApiAccessibility.Public;
		if (first == ApiAccessibility.ProtectedInternal || second == ApiAccessibility.ProtectedInternal)
			return ApiAccessibility.ProtectedInternal;
		return ApiAccessibility.Protected;
	}

	private ApiMethod? ReadMethod(MethodDefinition method, ApiType declaring, ImmutableArray<string> typeParameters, byte? typeContext, bool forceVisible)
	{
		var name = _reader.GetString(method.Name);
		if (name == ".cctor")
			return null;
		var accessibility = forceVisible
			? ApiAccessibility.Public
			: Visible(MemberAccessibility(method.Attributes & MethodAttributes.MemberAccessMask), declaring);
		if (accessibility is null)
			return null;

		var methodTypeParameters = TypeParameterNames(method.GetGenericParameters());
		var context = new TypeContext(typeParameters, methodTypeParameters);
		var signature = method.DecodeSignature(_types, context);
		var methodContext = NullableContext(method.GetCustomAttributes()) ?? typeContext;

		var special = (method.Attributes & MethodAttributes.SpecialName) != 0;
		var kind = name == ".ctor"
			? ApiMethodKind.Constructor
			: special && name is "op_Implicit" or "op_Explicit"
				? ApiMethodKind.Conversion
				: special && name.StartsWith("op_", StringComparison.Ordinal) ? ApiMethodKind.Operator : ApiMethodKind.Method;

		var returnAttributes = ReturnParameterAttributes(method);
		var rawReturn = signature.ReturnType;
		var returnRefKind = ApiRefKind.None;
		var returnOffset = 0;
		var returnHasInModifier = false;
		while (rawReturn is ApiModifiedType { Modifier.FullName: "System.Runtime.InteropServices.InAttribute" } readOnlyReturn)
		{
			returnHasInModifier = true;
			rawReturn = readOnlyReturn.Type;
			returnOffset++;
		}
		if (rawReturn is ApiByReferenceType reference)
		{
			rawReturn = reference.ElementType;
			returnOffset++;
			returnRefKind = returnHasInModifier || HasAttribute(returnAttributes, CompilerServices + "IsReadOnlyAttribute")
				? ApiRefKind.RefReadOnly
				: ApiRefKind.Ref;
		}

		return new ApiMethod(
			name,
			accessibility.Value,
			Attributes(method.GetCustomAttributes()),
			kind,
			Modifiers(method, declaring),
			Annotate(rawReturn, returnAttributes, methodContext, returnOffset),
			returnRefKind,
			Attributes(returnAttributes),
			ReadTypeParameters(method.GetGenericParameters(), context, methodContext, false),
			ReadParameters(method, signature.ParameterTypes, methodContext),
			HasAttribute(method.GetCustomAttributes(), CompilerServices + "ExtensionAttribute")
		);
	}

	[System.Diagnostics.CodeAnalysis.SuppressMessage("Performance", "CA1859", Justification = "a method without a return parameter row has no collection to return")]
	private IEnumerable<CustomAttributeHandle> ReturnParameterAttributes(MethodDefinition method)
	{
		foreach (var handle in method.GetParameters())
		{
			var parameter = _reader.GetParameter(handle);
			if (parameter.SequenceNumber == 0)
				return parameter.GetCustomAttributes();
		}
		return [];
	}

	private ApiMemberModifiers Modifiers(MethodDefinition method, ApiType declaring)
	{
		var flags = method.Attributes;
		var modifiers = ApiMemberModifiers.None;
		if ((flags & MethodAttributes.Static) != 0)
			modifiers |= ApiMemberModifiers.Static;
		var isVirtual = (flags & MethodAttributes.Virtual) != 0;
		var isAbstract = (flags & MethodAttributes.Abstract) != 0;
		var isNewSlot = (flags & MethodAttributes.NewSlot) != 0;
		var isFinal = (flags & MethodAttributes.Final) != 0;
		if (declaring.Kind == ApiTypeKind.Interface)
		{
			if (isAbstract)
				modifiers |= ApiMemberModifiers.Abstract;
			else if (isVirtual)
				modifiers |= ApiMemberModifiers.Virtual;
		}
		else if (isVirtual)
		{
			// a covariant return override is a new slot that the runtime is told overrides the base one
			if (isNewSlot && HasAttribute(method.GetCustomAttributes(), CompilerServices + "PreserveBaseOverridesAttribute"))
				isNewSlot = false;
			if (!isNewSlot)
				modifiers |= ApiMemberModifiers.Override | (isFinal ? ApiMemberModifiers.Sealed : 0) | (isAbstract ? ApiMemberModifiers.Abstract : 0);
			else if (isAbstract)
				modifiers |= ApiMemberModifiers.Abstract;
			// a final new slot is how C# implements an interface member without making it virtual
			else if (!isFinal)
				modifiers |= ApiMemberModifiers.Virtual;
		}
		if (
			declaring.Kind == ApiTypeKind.Struct && (declaring.Modifiers & ApiTypeModifiers.ReadOnly) == 0 && HasAttribute(
				method.GetCustomAttributes(),
				CompilerServices + "IsReadOnlyAttribute"
			)
		)
			modifiers |= ApiMemberModifiers.ReadOnly;
		return modifiers;
	}

	[System.Diagnostics.CodeAnalysis.SuppressMessage("Performance", "CA1859", Justification = "a method without parameters shares one empty list")]
	private IReadOnlyList<ApiParameter> ReadParameters(MethodDefinition method, ImmutableArray<ApiTypeReference> types, byte? methodContext)
	{
		var rows = new Parameter?[types.Length];
		foreach (var handle in method.GetParameters())
		{
			var parameter = _reader.GetParameter(handle);
			if (parameter.SequenceNumber > 0 && parameter.SequenceNumber <= rows.Length)
				rows[parameter.SequenceNumber - 1] = parameter;
		}

		if (types.Length == 0)
			return [];
		var parameters = new List<ApiParameter>(types.Length);
		for (var i = 0; i < types.Length; i++)
		{
			var row = rows[i];
			// typed as the interface: a bare [] would take the struct collection's type, and a default one has no reader
			IEnumerable<CustomAttributeHandle> attributes = row is { } withAttributes
				? withAttributes.GetCustomAttributes()
				: Array.Empty<CustomAttributeHandle>();
			var rawType = types[i];
			var refKind = ApiRefKind.None;
			var offset = 0;
			var hasInModifier = false;
			// a custom modifier takes a dynamic flag of its own, as the reference does
			while (rawType is ApiModifiedType { Modifier.FullName: "System.Runtime.InteropServices.InAttribute" } readOnlyParameter)
			{
				hasInModifier = true;
				rawType = readOnlyParameter.Type;
				offset++;
			}
			if (rawType is ApiByReferenceType reference)
			{
				rawType = reference.ElementType;
				offset++;
				var flags = row?.Attributes ?? ParameterAttributes.None;
				refKind = HasAttribute(attributes, CompilerServices + "RequiresLocationAttribute")
					? ApiRefKind.RefReadOnly
					: hasInModifier || HasAttribute(attributes, CompilerServices + "IsReadOnlyAttribute")
						? ApiRefKind.In
						: (flags & ParameterAttributes.Out) != 0 && (flags & ParameterAttributes.In) == 0 ? ApiRefKind.Out : ApiRefKind.Ref;
			}
			parameters.Add(
				new ApiParameter(
					row is { } named ? _reader.GetString(named.Name) : "arg" + i,
					Annotate(rawType, attributes, methodContext, offset),
					refKind,
					HasAttribute(attributes, "System.ParamArrayAttribute") || HasAttribute(attributes, CompilerServices + "ParamCollectionAttribute"),
					HasAttribute(attributes, CompilerServices + "ScopedRefAttribute"),
					row is { } optional ? DefaultValue(optional, rawType) : null,
					Attributes(attributes)
				)
			);
		}
		return parameters;
	}

	private string? DefaultValue(Parameter parameter, ApiTypeReference type)
	{
		if ((parameter.Attributes & ParameterAttributes.HasDefault) != 0)
		{
			var constant = Constant(parameter.GetDefaultValue());
			if (type is ApiNamedType { IsValueType: true } valueType && !IsBuiltIn(valueType) && valueType.FullName != "System.Decimal")
			{
				// a struct's default is written as null; an enum's value by its name
				if (constant is null or "null")
					return "default";
				if (valueType.IsNullableValueType)
					return valueType.TypeArguments[0] is ApiNamedType { IsValueType: true } inner && !IsBuiltIn(inner) &&
						RawConstant(parameter.GetDefaultValue()) is { } wrapped ? _catalog.Render(inner, wrapped) : constant;
				return RawConstant(parameter.GetDefaultValue()) is { } raw ? _catalog.Render(valueType, raw) : constant;
			}
			return constant ?? "default";
		}
		if ((parameter.Attributes & ParameterAttributes.Optional) == 0)
			return null;
		return DecimalConstant(parameter.GetCustomAttributes()) ?? "default";
	}

	private List<ApiTypeParameter> ReadTypeParameters(IEnumerable<GenericParameterHandle> handles, TypeContext context, byte? nullableContext, bool hasVariance)
	{
		var parameters = new List<ApiTypeParameter>();
		foreach (var handle in handles)
		{
			var parameter = _reader.GetGenericParameter(handle);
			var flags = parameter.Attributes;
			var variance = (flags & GenericParameterAttributes.VarianceMask) switch
			{
				GenericParameterAttributes.Covariant when hasVariance => ApiVariance.Out,
				GenericParameterAttributes.Contravariant when hasVariance => ApiVariance.In,
				_ => ApiVariance.None,
			};
			var constraints = ApiConstraints.None;
			var nullable = Nullable(parameter.GetCustomAttributes()) is [var single] ? single : nullableContext;
			var isUnmanaged = HasAttribute(parameter.GetCustomAttributes(), CompilerServices + "IsUnmanagedAttribute");
			var isStruct = (flags & GenericParameterAttributes.NotNullableValueTypeConstraint) != 0;
			if ((flags & GenericParameterAttributes.ReferenceTypeConstraint) != 0)
				constraints |= nullable == 2 ? ApiConstraints.NullableClass : ApiConstraints.Class;
			if (isStruct)
				constraints |= isUnmanaged ? ApiConstraints.Unmanaged : ApiConstraints.Struct;
			if ((flags & GenericParameterAttributes.DefaultConstructorConstraint) != 0 && !isStruct)
				constraints |= ApiConstraints.New;
			// AllowByRefLike, which netstandard2.0's enum does not name
			if (((int)flags & 0x20) != 0)
				constraints |= ApiConstraints.AllowsRefStruct;

			var constraintTypes = new List<ApiTypeReference>();
			foreach (var constraintHandle in parameter.GetConstraints())
			{
				var constraint = _reader.GetGenericParameterConstraint(constraintHandle);
				var constraintType = _types.FromHandle(constraint.Type, context);
				while (constraintType is ApiModifiedType modified)
					constraintType = modified.Type;
				// struct and unmanaged write System.ValueType as a constraint too
				if (isStruct && constraintType is ApiNamedType { FullName: "System.ValueType" })
					continue;
				constraintTypes.Add(Annotate(constraintType, constraint.GetCustomAttributes(), nullableContext));
			}
			if (nullable == 1 && (constraints & (ApiConstraints.Class | ApiConstraints.NullableClass | ApiConstraints.Struct | ApiConstraints.Unmanaged)) == 0)
				constraints |= ApiConstraints.NotNull;
			parameters.Add(new ApiTypeParameter(_reader.GetString(parameter.Name), variance, constraints, constraintTypes));
		}
		return parameters;
	}

	private ImmutableArray<string> TypeParameterNames(GenericParameterHandleCollection handles) =>
		[.. handles.Select(h => _reader.GetString(_reader.GetGenericParameter(h).Name))];

	// annotations

	private ApiTypeReference Annotate(ApiTypeReference type, IEnumerable<CustomAttributeHandle> attributes, byte? nullableContext, int dynamicOffset = 0)
	{
		IReadOnlyList<bool>? dynamic = null;
		IReadOnlyList<bool>? nativeIntegers = null;
		IReadOnlyList<byte>? nullable = null;
		IReadOnlyList<string?>? tupleNames = null;
		foreach (var handle in attributes)
		{
			var attribute = _reader.GetCustomAttribute(handle);
			switch (AttributeTypeName(attribute))
			{
				case CompilerServices + "DynamicAttribute":
					dynamic = Flags(attribute) ?? [true];
					break;
				case CompilerServices + "NativeIntegerAttribute":
					nativeIntegers = Flags(attribute) ?? [];
					break;
				case CompilerServices + "NullableAttribute":
					nullable = NullableFlags(attribute);
					break;
				case CompilerServices + "TupleElementNamesAttribute":
					tupleNames = TryDecode(attribute) is { FixedArguments: [{ Value: ImmutableArray<CustomAttributeTypedArgument<ApiTypeReference>> names }] }
						? names.Select(n => n.Value as string).ToList()
						: null;
					break;
			}
		}
		if (nullable is null && nullableContext is { } contextFlag)
			nullable = [contextFlag];
		return new Annotations(dynamic, nativeIntegers, nullable, tupleNames).Apply(type, dynamicOffset);
	}

	private List<bool>? Flags(CustomAttribute attribute) =>
		TryDecode(attribute) is { FixedArguments: [{ Value: ImmutableArray<CustomAttributeTypedArgument<ApiTypeReference>> flags }] }
			? flags.Select(f => f.Value is true).ToList()
			: null;

	private byte? NullableContext(IEnumerable<CustomAttributeHandle> attributes)
	{
		foreach (var handle in attributes)
		{
			var attribute = _reader.GetCustomAttribute(handle);
			if (AttributeTypeName(attribute) == CompilerServices + "NullableContextAttribute")
			{
				var blob = _reader.GetBlobReader(attribute.Value);
				return blob.Length == 5 && blob.ReadUInt16() == 1 ? blob.ReadByte() : null;
			}
		}
		return null;
	}

	private byte[]? Nullable(IEnumerable<CustomAttributeHandle> attributes)
	{
		foreach (var handle in attributes)
		{
			var attribute = _reader.GetCustomAttribute(handle);
			if (AttributeTypeName(attribute) == CompilerServices + "NullableAttribute")
				return NullableFlags(attribute);
		}
		return null;
	}

	/// <summary>
	/// A NullableAttribute's flags, read from its blob: the prolog, then a byte or an array of them. Nearly every member has
	/// one, so it is not decoded the general way.
	/// </summary>
	private byte[]? NullableFlags(CustomAttribute attribute)
	{
		var blob = _reader.GetBlobReader(attribute.Value);
		if (blob.Length < 5 || blob.ReadUInt16() != 1)
			return null;
		// prolog, one byte, the named argument count
		if (blob.Length == 5)
			return [blob.ReadByte()];
		var count = blob.ReadInt32();
		return count < 0 || count > blob.RemainingBytes ? null : blob.ReadBytes(count);
	}

	// attributes

	private bool HasAttribute(IEnumerable<CustomAttributeHandle> attributes, string fullName)
	{
		foreach (var handle in attributes)
			if (AttributeTypeName(_reader.GetCustomAttribute(handle)) == fullName)
				return true;
		return false;
	}

	// an assembly applies the same few attributes many times: name each constructor's type once
	private readonly Dictionary<EntityHandle, ApiNamedType?> _attributeTypes = [];
	private readonly Dictionary<EntityHandle, string> _attributeTypeNames = [];

	private string AttributeTypeName(CustomAttribute attribute)
	{
		if (!_attributeTypeNames.TryGetValue(attribute.Constructor, out var name))
			_attributeTypeNames[attribute.Constructor] = name = AttributeType(attribute)?.FullName ?? string.Empty;
		return name;
	}

	private ApiNamedType? AttributeType(CustomAttribute attribute)
	{
		if (!_attributeTypes.TryGetValue(attribute.Constructor, out var type))
			_attributeTypes[attribute.Constructor] = type = ResolveAttributeType(attribute);
		return type;
	}

	private ApiNamedType? ResolveAttributeType(CustomAttribute attribute)
	{
		var constructor = attribute.Constructor;
		EntityHandle parent = constructor.Kind switch
		{
			HandleKind.MethodDefinition => _reader.GetMethodDefinition((MethodDefinitionHandle)constructor).GetDeclaringType(),
			HandleKind.MemberReference => _reader.GetMemberReference((MemberReferenceHandle)constructor).Parent,
			_ => default,
		};
		return parent.Kind switch
		{
			HandleKind.TypeDefinition => _types.Definition((TypeDefinitionHandle)parent),
			HandleKind.TypeReference => _types.Reference((TypeReferenceHandle)parent, false),
			HandleKind.TypeSpecification => _types.FromHandle(parent, TypeContext.None) as ApiNamedType,
			_ => null,
		};
	}

	private CustomAttributeValue<ApiTypeReference>? TryDecode(CustomAttribute attribute)
	{
		try
		{
			return attribute.DecodeValue(_types);
		}
		catch (Exception e) when (e is BadImageFormatException or ArgumentOutOfRangeException or InvalidCastException)
		{
			return null;
		}
	}

	[System.Diagnostics.CodeAnalysis.SuppressMessage("Performance", "CA1859", Justification = "a declaration without attributes shares one empty list")]
	private IReadOnlyList<ApiAttribute> Attributes(IEnumerable<CustomAttributeHandle> attributes)
	{
		var result = new List<ApiAttribute>();
		foreach (var handle in attributes)
		{
			var attribute = _reader.GetCustomAttribute(handle);
			if (AttributeType(attribute) is not { } type || Bookkeeping.Contains(type.FullName))
				continue;
			var value = TryDecode(attribute);
			// the compiler marks what older compilers must not use, such as a ref struct, with an error
			if (
				type.FullName == "System.ObsoleteAttribute" && value is { FixedArguments: [{ Value: string message }, { Value: true }] } && message.EndsWith(
					"not supported in this version of your compiler.",
					StringComparison.Ordinal
				)
			)
				continue;
			var arguments = new List<string>();
			if (value is { } decoded)
			{
				arguments.AddRange(decoded.FixedArguments.Select(Literal));
				arguments.AddRange(
					decoded.NamedArguments.Select(a => a.Name + " = " + Literal(new CustomAttributeTypedArgument<ApiTypeReference>(a.Type, a.Value)))
				);
			}
			result.Add(new ApiAttribute(type, arguments));
		}
		// most declarations have none, and share one empty list
		return result.Count switch
		{
			0 => [],
			1 => result,
			_ => result.OrderBy(a => a.Type.FullName, StringComparer.Ordinal).ThenBy(a => string.Join(",", a.Arguments), StringComparer.Ordinal).ToList(),
		};
	}

	private string Literal(CustomAttributeTypedArgument<ApiTypeReference> argument) => argument.Value switch
	{
		null => "null",
		ImmutableArray<CustomAttributeTypedArgument<ApiTypeReference>> items => "new[] { " + string.Join(", ", items.Select(Literal)) + " }",
		ApiTypeReference type => "typeof(" + TypeKey(type) + ")",
		// an enum's value by its name, where the enum can be found
		var value when argument.Type is ApiNamedType named && !IsBuiltIn(named) => _catalog.Render(named, value),
		var value => Constant(value) ?? "null",
	};

	private static bool IsBuiltIn(ApiNamedType type) =>
		type is
		{
			Namespace: "System",
			DeclaringType: null,
			Name: "Boolean" or "Byte" or "SByte" or "Char" or "Int16" or "UInt16" or "Int32" or "UInt32" or "Int64" or "UInt64" or "Single" or "Double" or "String" or "Object" or "Type",
		};

	// constants

	private string? Constant(ConstantHandle handle) => handle.IsNil ? null : Constant(RawConstant(handle));

	private object? RawConstant(ConstantHandle handle)
	{
		var constant = _reader.GetConstant(handle);
		var blob = _reader.GetBlobReader(constant.Value);
		object? value = constant.TypeCode switch
		{
			ConstantTypeCode.Boolean => blob.ReadBoolean(),
			ConstantTypeCode.Char => blob.ReadChar(),
			ConstantTypeCode.SByte => blob.ReadSByte(),
			ConstantTypeCode.Byte => blob.ReadByte(),
			ConstantTypeCode.Int16 => blob.ReadInt16(),
			ConstantTypeCode.UInt16 => blob.ReadUInt16(),
			ConstantTypeCode.Int32 => blob.ReadInt32(),
			ConstantTypeCode.UInt32 => blob.ReadUInt32(),
			ConstantTypeCode.Int64 => blob.ReadInt64(),
			ConstantTypeCode.UInt64 => blob.ReadUInt64(),
			ConstantTypeCode.Single => blob.ReadSingle(),
			ConstantTypeCode.Double => blob.ReadDouble(),
			ConstantTypeCode.String => blob.ReadUTF16(blob.Length),
			_ => null,
		};
		return value;
	}

	/// <summary>A constant of an enum type by name, where the enum can be found; the number otherwise.</summary>
	private string? EnumConstant(ApiTypeReference type, ConstantHandle handle) =>
		handle.IsNil
			? null
			: type is ApiNamedType { IsValueType: true } named && !IsBuiltIn(named) && named.FullName != "System.Decimal" && RawConstant(handle) is { } value
				? _catalog.Render(named, value)
				: Constant(handle);

	private string? DecimalConstant(IEnumerable<CustomAttributeHandle> attributes)
	{
		foreach (var handle in attributes)
		{
			var attribute = _reader.GetCustomAttribute(handle);
			if (AttributeTypeName(attribute) != CompilerServices + "DecimalConstantAttribute" || TryDecode(attribute) is not { FixedArguments.Length: 5 } value)
				continue;
			var scale = Convert.ToByte(value.FixedArguments[0].Value, CultureInfo.InvariantCulture);
			var negative = Convert.ToByte(value.FixedArguments[1].Value, CultureInfo.InvariantCulture) != 0;
			// the attribute takes its three parts as int or as uint
			static int Part(object? part) => part is uint unsigned ? unchecked((int)unsigned) : Convert.ToInt32(part, CultureInfo.InvariantCulture);

			var hi = Part(value.FixedArguments[2].Value);
			var mid = Part(value.FixedArguments[3].Value);
			var lo = Part(value.FixedArguments[4].Value);
			return new decimal(lo, mid, hi, negative, scale).ToString(CultureInfo.InvariantCulture) + "m";
		}
		return null;
	}

	/// <summary>A constant as a C# literal.</summary>
	internal static string? Constant(object? value) => value switch
	{
		null => "null",
		bool b => b ? "true" : "false",
		string s => Quote(s, '"'),
		char c => Quote(c.ToString(), '\''),
		float f =>
			float.IsNaN(f)
				? "float.NaN"
				: float.IsPositiveInfinity(f)
					? "float.PositiveInfinity"
					: float.IsNegativeInfinity(f) ? "float.NegativeInfinity" : f.ToString("R", CultureInfo.InvariantCulture) + "f",
		double d =>
			double.IsNaN(d)
				? "double.NaN"
				: double.IsPositiveInfinity(d)
					? "double.PositiveInfinity"
					: double.IsNegativeInfinity(d) ? "double.NegativeInfinity" : d.ToString("R", CultureInfo.InvariantCulture) + "d",
		long l => l.ToString(CultureInfo.InvariantCulture) + "L",
		ulong ul => ul.ToString(CultureInfo.InvariantCulture) + "UL",
		uint ui => ui.ToString(CultureInfo.InvariantCulture) + "U",
		IFormattable formattable => formattable.ToString(null, CultureInfo.InvariantCulture),
		_ => value.ToString(),
	};

	private static string Quote(string text, char quote)
	{
		var builder = new StringBuilder().Append(quote);
		foreach (var c in text)
			builder.Append(c switch
			{
				'\\' => "\\\\",
				'\n' => "\\n",
				'\r' => "\\r",
				'\t' => "\\t",
				'\0' => "\\0",
				_ when c == quote => "\\" + quote,
				_ when char.IsControl(c) => "\\u" + ((int)c).ToString("x4", CultureInfo.InvariantCulture),
				_ => c.ToString(),
			});
		return builder.Append(quote).ToString();
	}
}
