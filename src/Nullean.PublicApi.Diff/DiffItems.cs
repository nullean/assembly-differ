// The comparison is a port of JustAssembly (https://github.com/telerik/JustAssembly), Copyright Telerik, licensed
// under the Apache License 2.0, and reproduces its reports. Names follow JustDecompile's C# writer from the same project.
#nullable enable
using System;
using System.Collections.Generic;
using System.IO;
using System.Linq;
using System.Xml;

using Nullean.PublicApi;

namespace Nullean.PublicApi.Diff;

/// <summary>Whether an element only exists in the old assembly, only in the new one, or in both but changed.</summary>
public enum DiffType : byte
{
	/// <summary>The element is only in the old assembly.</summary>
	Deleted,

	/// <summary>The element is in both assemblies and differs.</summary>
	Modified,

	/// <summary>The element is only in the new assembly.</summary>
	New,
}

/// <summary>The kind of metadata element a <see cref="IMetadataDiffItem"/> describes; its name is the XML element name.</summary>
public enum MetadataType : byte
{
	/// <summary>An assembly.</summary>
	Assembly,

	/// <summary>A module of an assembly.</summary>
	Module,

	/// <summary>A type.</summary>
	Type,

	/// <summary>A field.</summary>
	Field,

	/// <summary>A method, constructor or accessor.</summary>
	Method,

	/// <summary>A property.</summary>
	Property,

	/// <summary>An event.</summary>
	Event,

	/// <summary>A reference to another assembly.</summary>
	AssemblyReference,
}

/// <summary>One difference between an old and a new assembly.</summary>
public interface IDiffItem
{
	/// <summary>Whether the element was deleted, added or changed.</summary>
	DiffType DiffType { get; }

	/// <summary>The difference and the ones nested in it as indented XML, in the format JustAssembly writes.</summary>
	string ToXml();

	/// <summary>What the reports show: an element's name, or a sentence that says what changed.</summary>
	string HumanReadable { get; }

	/// <summary>Whether code compiled against the old assembly can break on the new one.</summary>
	bool IsBreakingChange { get; }

	/// <summary>Visits this and nested differences, with the depth each one is at.</summary>
	void Visit(Action<IDiffItem, int> visit, int depth = 0);
}

/// <summary>A difference in a metadata element, which holds the differences in its declaration and in its children.</summary>
public interface IMetadataDiffItem : IDiffItem
{
	/// <summary>The changes to the element's own declaration, such as its visibility or attributes.</summary>
	ICollection<IDiffItem> DeclarationDiffs { get; }

	/// <summary>The differences in the elements it contains, such as a type's members.</summary>
	ICollection<IMetadataDiffItem> ChildrenDiffs { get; }

	/// <summary>The number of declaration and child differences.</summary>
	int Count { get; }

	/// <summary>The kind of element.</summary>
	MetadataType MetadataType { get; }
}

/// <summary>The base of every difference.</summary>
public abstract class DiffItem : IDiffItem
{
	private protected DiffItem(DiffType diffType) => DiffType = diffType;

	/// <inheritdoc />
	public DiffType DiffType { get; }

	/// <inheritdoc />
	public string ToXml()
	{
		using var stringWriter = new StringWriter();
		using (var xmlWriter = new XmlTextWriter(stringWriter) { Formatting = Formatting.Indented })
			WriteXml(xmlWriter);
		return stringWriter.ToString();
	}

	/// <inheritdoc />
	public abstract string HumanReadable { get; }

	/// <inheritdoc />
	public abstract bool IsBreakingChange { get; }

	/// <inheritdoc />
	public abstract void Visit(Action<IDiffItem, int> visit, int depth = 0);

	internal virtual void WriteXml(XmlWriter writer)
	{
		writer.WriteStartElement("DiffItem");
		writer.WriteAttributeString("DiffType", DiffType.ToString());
		writer.WriteString(HumanReadable);
		writer.WriteEndElement();
	}
}

/// <summary>A difference in the declaration of a member, such as its visibility, that has no children.</summary>
public abstract class DeclarationDiffItem : DiffItem
{
	private protected DeclarationDiffItem() : base(DiffType.Modified)
	{ }

	/// <inheritdoc />
	public override void Visit(Action<IDiffItem, int> visit, int depth = 0) => visit(this, depth);
}

/// <summary>The member is more or less visible than it was, for example <c>protected</c> where it was <c>public</c>.</summary>
public sealed class VisibilityChangedDiffItem : DeclarationDiffItem
{
	private readonly bool _reduced;

	internal VisibilityChangedDiffItem(bool reduced)
	{
		_reduced = reduced;
	}

	/// <inheritdoc />
	public override string HumanReadable => $"Member is {(_reduced ? "less" : "more")} visible.";

	/// <inheritdoc />
	public override bool IsBreakingChange => _reduced;
}

/// <summary>The member changed from instance to static or back.</summary>
public sealed class StaticFlagChangedDiffItem : DeclarationDiffItem
{
	private readonly bool _isNewMemberStatic;

	internal StaticFlagChangedDiffItem(bool isNewMemberStatic)
	{
		_isNewMemberStatic = isNewMemberStatic;
	}

	/// <inheritdoc />
	public override string HumanReadable => $"Member changed to {(_isNewMemberStatic ? "static" : "instance")}.";

	/// <inheritdoc />
	public override bool IsBreakingChange => true;
}

/// <summary>The method became virtual, or stopped being virtual.</summary>
public sealed class VirtualFlagChangedDiffItem : DeclarationDiffItem
{
	private readonly bool _isNewMethodVirtual;

	internal VirtualFlagChangedDiffItem(bool isNewMethodVirtual)
	{
		_isNewMethodVirtual = isNewMethodVirtual;
	}

	/// <inheritdoc />
	public override string HumanReadable => $"Method changed to {(_isNewMethodVirtual ? string.Empty : "non-")}virtual.";

	/// <inheritdoc />
	public override bool IsBreakingChange => !_isNewMethodVirtual;
}

/// <summary>The member's type changed: a field's or property's type, or a method's return type.</summary>
public sealed class MemberTypeDiffItem : DeclarationDiffItem
{
	private readonly string? _oldType;
	private readonly string? _newType;

	internal MemberTypeDiffItem(string? oldType, string? newType)
	{
		_oldType = oldType;
		_newType = newType;
	}

	/// <inheritdoc />
	public override string HumanReadable => $"Member type changed from {_oldType} to {_newType}.";

	/// <inheritdoc />
	public override bool IsBreakingChange => true;
}

/// <summary>A parameter was renamed, which breaks callers that pass it by name.</summary>
public sealed class ParameterDiffItem : DeclarationDiffItem
{
	private readonly string _oldName;
	private readonly string _newName;

	internal ParameterDiffItem(string oldName, string newName)
	{
		_oldName = oldName;
		_newName = newName;
	}

	/// <inheritdoc />
	public override string HumanReadable => $"Parameter name changed from {_oldName} to {_newName}.";

	/// <inheritdoc />
	public override bool IsBreakingChange => true;
}

/// <summary>An attribute that is only on the old or only on the new element, named by its constructor.</summary>
public sealed class CustomAttributeDiffItem : DiffItem
{
	private readonly string _constructorSignature;

	internal CustomAttributeDiffItem(DiffType diffType, string constructorSignature) : base(diffType) => _constructorSignature = constructorSignature;

	/// <inheritdoc />
	public override string HumanReadable => _constructorSignature;

	/// <inheritdoc />
	public override bool IsBreakingChange => DiffType == DiffType.Deleted;

	/// <inheritdoc />
	public override void Visit(Action<IDiffItem, int> visit, int depth = 0) => visit(this, depth);

	internal override void WriteXml(XmlWriter writer)
	{
		writer.WriteStartElement("CustomAttribute");
		writer.WriteAttributeString("Name", _constructorSignature);
		writer.WriteAttributeString("DiffType", DiffType.ToString());
		writer.WriteEndElement();
	}
}

/// <summary>A difference in a metadata element: an assembly, module, type, member or assembly reference.</summary>
public abstract class MetadataDiffItem : DiffItem, IMetadataDiffItem
{
	private bool? _isBreakingChange;

	private protected MetadataDiffItem(
		bool hasOld,
		bool hasNew,
		string name,
		IEnumerable<IDiffItem>? declarationDiffs,
		IEnumerable<IMetadataDiffItem>? childrenDiffs
	) : base(!hasNew ? DiffType.Deleted : !hasOld ? DiffType.New : DiffType.Modified)
	{
		Name = name;
		DeclarationDiffs = (declarationDiffs ?? []).ToList();
		ChildrenDiffs = (childrenDiffs ?? []).ToList();
	}

	/// <summary>The name the reports show for the element.</summary>
	public string Name { get; }

	/// <summary>
	/// The element's name in metadata: a type's full name, such as <c>Lib.Outer/Inner</c> or <c>Lib.Box`1</c>, or a
	/// member's name, such as <c>Draw</c> or <c>.ctor</c>. Matches the names in a referencing assembly's metadata.
	/// </summary>
	public string? Key { get; internal init; }

	/// <inheritdoc />
	public ICollection<IDiffItem> DeclarationDiffs { get; }

	/// <inheritdoc />
	public ICollection<IMetadataDiffItem> ChildrenDiffs { get; }

	/// <inheritdoc />
	public int Count => DeclarationDiffs.Count + ChildrenDiffs.Count;

	/// <inheritdoc />
	public abstract MetadataType MetadataType { get; }

	/// <inheritdoc />
	public override string HumanReadable => Name;

	/// <inheritdoc />
	public override bool IsBreakingChange =>
		_isBreakingChange ??= DiffType != DiffType.Modified
			? DiffType == DiffType.Deleted
			: DeclarationDiffs.Concat(ChildrenDiffs).Any(item => item.IsBreakingChange);

	/// <inheritdoc />
	public override void Visit(Action<IDiffItem, int> visit, int depth = 0)
	{
		visit(this, depth);
		foreach (var diffItem in DeclarationDiffs)
			diffItem.Visit(visit, depth + 1);
		foreach (var item in ChildrenDiffs.OrderBy(c => c.Count).ThenBy(c => c.HumanReadable))
			item.Visit(visit, depth + 1);
	}

	internal override void WriteXml(XmlWriter writer)
	{
		writer.WriteStartElement(MetadataType.ToString());
		writer.WriteAttributeString("Name", Name);
		writer.WriteAttributeString("DiffType", DiffType.ToString());
		if (DeclarationDiffs.Count > 0)
		{
			writer.WriteStartElement("DeclarationDiffs");
			foreach (var item in DeclarationDiffs)
				((DiffItem)item).WriteXml(writer);
			writer.WriteEndElement();
		}
		foreach (var item in ChildrenDiffs)
			((DiffItem)item).WriteXml(writer);
		writer.WriteEndElement();
	}
}

/// <summary>A difference in an assembly, which holds the module differences.</summary>
public sealed class AssemblyDiffItem : MetadataDiffItem
{
	internal AssemblyDiffItem(
		bool hasOld,
		bool hasNew,
		string name,
		IEnumerable<IDiffItem>? declarationDiffs,
		IEnumerable<IMetadataDiffItem>? childrenDiffs
	) : base(hasOld, hasNew, name, declarationDiffs, childrenDiffs)
	{ }

	/// <inheritdoc />
	public override MetadataType MetadataType => MetadataType.Assembly;
}

/// <summary>A difference in a module, which holds the type and assembly reference differences.</summary>
public sealed class ModuleDiffItem : MetadataDiffItem
{
	internal ModuleDiffItem(
		bool hasOld,
		bool hasNew,
		string name,
		IEnumerable<IDiffItem>? declarationDiffs,
		IEnumerable<IMetadataDiffItem>? childrenDiffs
	) : base(hasOld, hasNew, name, declarationDiffs, childrenDiffs)
	{ }

	/// <inheritdoc />
	public override MetadataType MetadataType => MetadataType.Module;
}

/// <summary>A difference in a type, which holds the member and nested type differences.</summary>
public sealed class TypeDiffItem : MetadataDiffItem
{
	internal TypeDiffItem(
		bool hasOld,
		bool hasNew,
		string name,
		IEnumerable<IDiffItem>? declarationDiffs,
		IEnumerable<IMetadataDiffItem>? childrenDiffs
	) : base(hasOld, hasNew, name, declarationDiffs, childrenDiffs)
	{ }

	/// <inheritdoc />
	public override MetadataType MetadataType => MetadataType.Type;
}

/// <summary>A difference in a field, method, property or event, or in a property's or event's accessor.</summary>
public sealed class MemberDiffItem : MetadataDiffItem
{
	internal MemberDiffItem(
		MetadataType metadataType,
		bool hasOld,
		bool hasNew,
		string name,
		IEnumerable<IDiffItem>? declarationDiffs,
		IEnumerable<IMetadataDiffItem>? childrenDiffs
	) : base(hasOld, hasNew, name, declarationDiffs, childrenDiffs) => MetadataType = metadataType;

	/// <inheritdoc />
	public override MetadataType MetadataType { get; }

	/// <summary>
	/// A new member that existing code has to act on: an abstract one, such as an interface member without a default
	/// implementation, which every implementer must add, or a required one, which every object initializer must set.
	/// </summary>
	public bool AddsObligation { get; internal init; }
}

/// <summary>An assembly reference the new module no longer has. Reports do not count these as API changes.</summary>
public sealed class AssemblyReferenceDiffItem : MetadataDiffItem
{
	internal AssemblyReferenceDiffItem(string name) : base(true, false, name, null, null)
	{ }

	/// <inheritdoc />
	public override MetadataType MetadataType => MetadataType.AssemblyReference;
}
