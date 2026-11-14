// Reads which types and members of a package's assemblies other assemblies reference, from their metadata.
#nullable enable
using System;
using System.Collections.Generic;
using System.Collections.Immutable;
using System.IO;
using System.Linq;
using System.Reflection.Metadata;
using System.Reflection.PortableExecutable;

namespace Differ.Engine;

/// <summary>
/// The API of some assemblies that other code uses: types by metadata full name (<c>Lib.Outer/Inner</c>), and per type
/// the member names called on it. A property or event counts as used when one of its accessors is.
/// </summary>
public sealed class UsedApi
{
	public HashSet<string> Types { get; } = new(StringComparer.Ordinal);

	public Dictionary<string, HashSet<string>> Members { get; } = new(StringComparer.Ordinal);

	public bool UsesMember(string type, string member) => Members.TryGetValue(type, out var members) && members.Contains(member);

	/// <summary>What <paramref name="assemblies"/> reference of the assemblies named <paramref name="packageAssemblies"/>.</summary>
	public static UsedApi Read(IEnumerable<string> assemblies, IEnumerable<string> packageAssemblies)
	{
		var used = new UsedApi();
		var names = new HashSet<string>(packageAssemblies, StringComparer.OrdinalIgnoreCase);
		foreach (var path in assemblies)
		{
			using var stream = File.OpenRead(path);
			using var pe = new PEReader(stream);
			if (!pe.HasMetadata)
				continue;
			used.Add(pe.GetMetadataReader(), names);
		}
		return used;
	}

	private void Add(MetadataReader reader, HashSet<string> packageAssemblies)
	{
		var types = new TypeNames(reader, packageAssemblies);
		foreach (var handle in reader.TypeReferences)
			if (types.Of(handle) is { } type)
				Types.Add(type);

		foreach (var handle in reader.MemberReferences)
		{
			var member = reader.GetMemberReference(handle);
			var type = member.Parent.Kind switch
			{
				HandleKind.TypeReference => types.Of((TypeReferenceHandle)member.Parent),
				HandleKind.TypeSpecification => reader.GetTypeSpecification((TypeSpecificationHandle)member.Parent).DecodeSignature(types, null),
				_ => null,
			};
			if (type is null)
				continue;
			Types.Add(type);
			if (!Members.TryGetValue(type, out var members))
				Members[type] = members = new HashSet<string>(StringComparer.Ordinal);
			var name = reader.GetString(member.Name);
			members.Add(name);
			foreach (var prefix in Accessors)
				if (name.StartsWith(prefix, StringComparison.Ordinal))
					members.Add(name[prefix.Length..]);
		}
	}

	private static readonly string[] Accessors = ["get_", "set_", "add_", "remove_"];

	/// <summary>
	/// Names the types a signature or reference points at, when they live in one of the package's assemblies; for a
	/// generic instance, its generic type.
	/// </summary>
	private sealed class TypeNames(MetadataReader reader, HashSet<string> packageAssemblies) : ISignatureTypeProvider<string?, object?>
	{
		public string? Of(TypeReferenceHandle handle)
		{
			var type = reader.GetTypeReference(handle);
			var name = reader.GetString(type.Name);
			switch (type.ResolutionScope.Kind)
			{
				case HandleKind.TypeReference:
					return Of((TypeReferenceHandle)type.ResolutionScope) is { } declaring ? declaring + "/" + name : null;
				case HandleKind.AssemblyReference:
					var assembly = reader.GetString(reader.GetAssemblyReference((AssemblyReferenceHandle)type.ResolutionScope).Name);
					if (!packageAssemblies.Contains(assembly))
						return null;
					var space = reader.GetString(type.Namespace);
					return space.Length == 0 ? name : space + "." + name;
				default:
					return null;
			}
		}

		public string? GetTypeFromReference(MetadataReader metadata, TypeReferenceHandle handle, byte rawTypeKind) => Of(handle);

		public string? GetGenericInstantiation(string? genericType, ImmutableArray<string?> typeArguments) => genericType;

		public string? GetTypeFromDefinition(MetadataReader metadata, TypeDefinitionHandle handle, byte rawTypeKind) => null;

		public string? GetTypeFromSpecification(MetadataReader metadata, object? genericContext, TypeSpecificationHandle handle, byte rawTypeKind) => null;

		public string? GetPrimitiveType(PrimitiveTypeCode typeCode) => null;

		public string? GetSZArrayType(string? elementType) => null;

		public string? GetArrayType(string? elementType, System.Reflection.Metadata.ArrayShape shape) => null;

		public string? GetByReferenceType(string? elementType) => null;

		public string? GetPointerType(string? elementType) => null;

		public string? GetPinnedType(string? elementType) => null;

		public string? GetModifiedType(string? modifier, string? unmodifiedType, bool isRequired) => unmodifiedType;

		public string? GetFunctionPointerType(MethodSignature<string?> signature) => null;

		public string? GetGenericTypeParameter(object? genericContext, int index) => null;

		public string? GetGenericMethodParameter(object? genericContext, int index) => null;
	}
}

/// <summary>Narrows a diff down to the changes that code using <see cref="UsedApi"/> is affected by.</summary>
public static class UsageFilter
{
	/// <summary>
	/// Keeps the changes to used types: their removed and changed members that are used, new abstract or required
	/// members, which every implementer or initializer has to act on, and their own declaration changes. New types and
	/// other new members cannot be in use yet, and assembly-level changes are not about the API. Members match by name,
	/// so a change to one overload shows when another overload is used. Returns null when nothing is left.
	/// </summary>
	public static IMetadataDiffItem? Apply(IMetadataDiffItem assembly, UsedApi used)
	{
		var modules = new List<IMetadataDiffItem>();
		foreach (var module in assembly.ChildrenDiffs.OfType<ModuleDiffItem>())
		{
			var types = module.ChildrenDiffs.OfType<TypeDiffItem>().Select(t => Type(t, used)).OfType<IMetadataDiffItem>().ToList();
			if (types.Count > 0)
				modules.Add(new ModuleDiffItem(true, true, module.Name, null, types));
		}
		return modules.Count == 0 ? null : new AssemblyDiffItem(true, true, ((MetadataDiffItem)assembly).Name, null, modules);
	}

	private static TypeDiffItem? Type(TypeDiffItem type, UsedApi used)
	{
		var nested = type.ChildrenDiffs.OfType<TypeDiffItem>().Select(t => Type(t, used)).OfType<IMetadataDiffItem>().ToList();
		var isUsed = type.Key is { } key && used.Types.Contains(key);
		if (!isUsed || type.DiffType == DiffType.New)
			return nested.Count == 0 ? null : new TypeDiffItem(true, true, type.Name, null, nested) { Key = type.Key };
		if (type.DiffType == DiffType.Deleted)
			return type;

		var members = type
			.ChildrenDiffs
			.OfType<MemberDiffItem>()
			.Where(m => m.DiffType == DiffType.New ? m.AddsObligation : m.Key is { } member && used.UsesMember(type.Key!, member))
			.Cast<IMetadataDiffItem>()
			.Concat(nested)
			.ToList();
		// the compiler moves its own attributes, such as NullableContextAttribute, whenever members change
		var declarations = type
			.DeclarationDiffs
			.Where(d => d is not CustomAttributeDiffItem a || !a.HumanReadable.StartsWith("System.Runtime.CompilerServices.", StringComparison.Ordinal))
			.ToList();
		if (members.Count == 0 && declarations.Count == 0)
			return null;
		return new TypeDiffItem(true, true, type.Name, declarations, members) { Key = type.Key };
	}
}
