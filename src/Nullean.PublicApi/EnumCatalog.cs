#nullable enable
using System;
using System.Collections.Generic;
using System.Globalization;
using System.IO;
using System.Linq;
using System.Reflection;
using System.Reflection.Metadata;
using System.Reflection.PortableExecutable;

namespace Nullean.PublicApi;

/// <summary>How <see cref="ApiReader"/> finds what an assembly only refers to.</summary>
public sealed class ApiReaderOptions
{
	/// <summary>
	/// Folders to look in for the assemblies an assembly references, after its own folder, such as a build's
	/// dependencies. The reader uses them to write an enum's value by name: <c>StringComparison.Ordinal</c> rather than
	/// <c>(StringComparison)4</c>.
	/// </summary>
	public IReadOnlyList<string> ReferenceDirectories { get; init; } = [];

	/// <summary>Whether to look in the installed .NET runtime too, for the framework's own enums. On by default.</summary>
	public bool SearchRuntime { get; init; } = true;
}

/// <summary>An enum's underlying type, its values, and whether it is a set of flags.</summary>
internal sealed record EnumInfo(PrimitiveTypeCode Underlying, IReadOnlyList<(string Name, ulong Value)> Values, bool IsFlags);

/// <summary>
/// Finds enums, in the assembly being read or in the ones it references, by looking for those in the folders it is
/// given and following type forwarders, as System.Runtime's to System.Private.CoreLib.
/// </summary>
internal sealed class EnumCatalog(MetadataReader own, TypeReferenceProvider types, IReadOnlyList<string> directories) : IDisposable
{
	private readonly Dictionary<string, MetadataReader?> _assemblies = new(StringComparer.OrdinalIgnoreCase);
	private readonly List<PEReader> _open = [];
	private readonly Dictionary<string, EnumInfo?> _enums = new(StringComparer.Ordinal);

	/// <summary>The enum <paramref name="type"/> names, or null when it is not one, or cannot be found.</summary>
	public EnumInfo? Find(ApiNamedType type)
	{
		var key = (types.AssemblyOf(type) ?? "") + "|" + type.FullName;
		if (_enums.TryGetValue(key, out var known))
			return known;
		var found = Find(own, type, types.AssemblyOf(type), 0);
		_enums[key] = found;
		return found;
	}

	private EnumInfo? Find(MetadataReader reader, ApiNamedType type, string? assembly, int depth)
	{
		if (depth > 4)
			return null;
		// a reference to another assembly is looked up there; the assembly's own types here
		if (ReferenceEquals(reader, own) && assembly is not null)
			return Open(assembly) is { } referenced ? Find(referenced, type, null, depth + 1) : null;

		var path = Path(type);
		// the assembly being read is asked about many enums, and indexed once; another is asked about few, and searched
		if (ReferenceEquals(reader, own) ? Index(reader).TryGetValue(path, out var definition) : TryFind(reader, type, out definition))
			return Read(reader, reader.GetTypeDefinition(definition));
		// a forwarder sends the type on to the assembly that has it now
		foreach (var handle in reader.ExportedTypes)
		{
			var exported = reader.GetExportedType(handle);
			if (!exported.IsForwarder || exported.Implementation.Kind != HandleKind.AssemblyReference)
				continue;
			var name = reader.GetString(exported.Namespace).Length == 0
				? reader.GetString(exported.Name)
				: reader.GetString(exported.Namespace) + "." + reader.GetString(exported.Name);
			if (name != path.Split('/')[0])
				continue;
			var target = reader.GetString(reader.GetAssemblyReference((AssemblyReferenceHandle)exported.Implementation).Name);
			return Open(target) is { } forwarded ? Find(forwarded, type, null, depth + 1) : null;
		}
		return null;
	}

	private readonly Dictionary<MetadataReader, Dictionary<string, TypeDefinitionHandle>> _indexes = [];

	/// <summary>Each assembly's types by full name, built once: an assembly such as CoreLib is asked about many enums.</summary>
	private Dictionary<string, TypeDefinitionHandle> Index(MetadataReader reader)
	{
		if (_indexes.TryGetValue(reader, out var index))
			return index;
		index = new Dictionary<string, TypeDefinitionHandle>(StringComparer.Ordinal);
		foreach (var handle in reader.TypeDefinitions)
			index[FullName(reader, handle)] = handle;
		return _indexes[reader] = index;
	}

	/// <summary>A type by namespace and name, nested ones through their declaring types, compared in place.</summary>
	private static bool TryFind(MetadataReader reader, ApiNamedType type, out TypeDefinitionHandle found)
	{
		found = default;
		var chain = new List<ApiNamedType>();
		for (var current = type; current is not null; current = current.DeclaringType)
			chain.Insert(0, current);
		foreach (var handle in reader.TypeDefinitions)
		{
			var definition = reader.GetTypeDefinition(handle);
			if (
				!definition.GetDeclaringType().IsNil || !reader.StringComparer.Equals(definition.Name, chain[0].Name) ||
				!reader.StringComparer.Equals(definition.Namespace, chain[0].Namespace)
			)
				continue;
			found = handle;
			for (var i = 1; i < chain.Count && !found.IsNil; i++)
			{
				var nested = default(TypeDefinitionHandle);
				foreach (var candidate in reader.GetTypeDefinition(found).GetNestedTypes())
					if (reader.StringComparer.Equals(reader.GetTypeDefinition(candidate).Name, chain[i].Name))
						nested = candidate;
				found = nested;
			}
			return !found.IsNil;
		}
		return false;
	}

	private static string Path(ApiNamedType type) =>
		type.DeclaringType is null ? (type.Namespace.Length == 0 ? type.Name : type.Namespace + "." + type.Name) : Path(type.DeclaringType) + "/" + type.Name;

	private static string FullName(MetadataReader reader, TypeDefinitionHandle handle)
	{
		var definition = reader.GetTypeDefinition(handle);
		var declaring = definition.GetDeclaringType();
		if (!declaring.IsNil)
			return FullName(reader, declaring) + "/" + reader.GetString(definition.Name);
		var ns = reader.GetString(definition.Namespace);
		return ns.Length == 0 ? reader.GetString(definition.Name) : ns + "." + reader.GetString(definition.Name);
	}

	private static EnumInfo? Read(MetadataReader reader, TypeDefinition definition)
	{
		var provider = new TypeReferenceProvider(reader);
		if (definition.BaseType.IsNil || provider.FullName(definition.BaseType) != "System.Enum")
			return null;
		var underlying = PrimitiveTypeCode.Int32;
		var values = new List<(string, ulong)>();
		foreach (var handle in definition.GetFields())
		{
			var field = reader.GetFieldDefinition(handle);
			if ((field.Attributes & FieldAttributes.Static) == 0)
			{
				if (field.DecodeSignature(provider, TypeContext.None) is ApiNamedType { Namespace: "System" } type)
					underlying = Underlying(type.Name);
				continue;
			}
			if ((field.Attributes & FieldAttributes.Literal) != 0 && Value(reader, field.GetDefaultValue()) is { } value)
				values.Add((reader.GetString(field.Name), value));
		}
		var isFlags = definition.GetCustomAttributes().Any(h =>
		{
			var attribute = reader.GetCustomAttribute(h);
			return attribute.Constructor.Kind == HandleKind.MemberReference && provider.FullName(
				reader.GetMemberReference((MemberReferenceHandle)attribute.Constructor).Parent
			) == "System.FlagsAttribute" || attribute.Constructor.Kind == HandleKind.MethodDefinition && provider.FullName(
				reader.GetMethodDefinition((MethodDefinitionHandle)attribute.Constructor).GetDeclaringType()
			) == "System.FlagsAttribute";
		});
		return new EnumInfo(underlying, values, isFlags);
	}

	internal static PrimitiveTypeCode Underlying(string name) => name switch
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

	private static ulong? Value(MetadataReader reader, ConstantHandle handle)
	{
		if (handle.IsNil)
			return null;
		var constant = reader.GetConstant(handle);
		var blob = reader.GetBlobReader(constant.Value);
		return constant.TypeCode switch
		{
			ConstantTypeCode.SByte => unchecked((ulong)blob.ReadSByte()),
			ConstantTypeCode.Byte => blob.ReadByte(),
			ConstantTypeCode.Int16 => unchecked((ulong)blob.ReadInt16()),
			ConstantTypeCode.UInt16 => blob.ReadUInt16(),
			ConstantTypeCode.Int32 => unchecked((ulong)blob.ReadInt32()),
			ConstantTypeCode.UInt32 => blob.ReadUInt32(),
			ConstantTypeCode.Int64 => unchecked((ulong)blob.ReadInt64()),
			ConstantTypeCode.UInt64 => blob.ReadUInt64(),
			ConstantTypeCode.Char => blob.ReadChar(),
			ConstantTypeCode.Boolean => blob.ReadBoolean() ? 1UL : 0UL,
			_ => null,
		};
	}

	/// <summary>An assembly from the folders, by name; null when none of them has it.</summary>
	private MetadataReader? Open(string name)
	{
		if (_assemblies.TryGetValue(name, out var known))
			return known;
		MetadataReader? reader = null;
		foreach (var directory in directories)
		{
			var file = System.IO.Path.Combine(directory, name + ".dll");
			if (!File.Exists(file))
				continue;
			try
			{
				// only the metadata is read, not a whole CoreLib
				var pe = new PEReader(File.OpenRead(file), PEStreamOptions.PrefetchMetadata);
				if (!pe.HasMetadata)
					continue;
				_open.Add(pe);
				reader = pe.GetMetadataReader();
				break;
			}
			catch (Exception e) when (e is BadImageFormatException or IOException or UnauthorizedAccessException)
			{
				// not an assembly, or not readable: look further
			}
		}
		_assemblies[name] = reader;
		return reader;
	}

	/// <summary>
	/// A value of <paramref name="type"/> as C#: its member, <c>StringComparison.Ordinal</c>; flags joined,
	/// <c>FileShare.Read | FileShare.Write</c>; or a cast of the number when the enum cannot be found or has no such
	/// member, <c>(StringComparison)42</c>.
	/// </summary>
	public string Render(ApiNamedType type, object value)
	{
		var display = Display(type);
		var number = Number(value);
		var cast = "(" + display + ")" + Convert.ToString(value, CultureInfo.InvariantCulture);
		if (number is not { } bits || Find(type) is not { } info)
			return cast;
		foreach (var (name, member) in info.Values)
			if (member == bits)
				return display + "." + name;
		if (!info.IsFlags || bits == 0)
			return cast;
		var names = new List<string>();
		var left = bits;
		foreach (var (name, member) in info.Values.Where(v => v.Value != 0).OrderByDescending(v => v.Value))
		{
			if ((left & member) == member)
			{
				names.Add(display + "." + name);
				left &= ~member;
			}
		}
		return left == 0 ? string.Join(" | ", Enumerable.Reverse(names)) : cast;
	}

	private static string Display(ApiNamedType type) => (type.DeclaringType is null ? "" : Display(type.DeclaringType) + ".") + ApiWriter.NonGeneric(type.Name);

	private static ulong? Number(object value) => value switch
	{
		sbyte v => unchecked((ulong)v),
		byte v => v,
		short v => unchecked((ulong)v),
		ushort v => v,
		int v => unchecked((ulong)v),
		uint v => v,
		long v => unchecked((ulong)v),
		ulong v => v,
		char v => v,
		bool v => v ? 1UL : 0UL,
		_ => null,
	};

	public void Dispose()
	{
		foreach (var pe in _open)
			pe.Dispose();
	}
}
