// The comparison is a port of JustAssembly (https://github.com/telerik/JustAssembly), Copyright Telerik, licensed
// under the Apache License 2.0, and reproduces its reports. Names follow JustDecompile's C# writer from the same project.
#nullable enable
using System;
using System.Collections.Generic;
using System.IO;
using System.Linq;
using System.Xml;

namespace Differ.Engine;

/// <summary>Whether an element only exists in the old assembly, only in the new one, or in both but changed.</summary>
public enum DiffType : byte
{
	Deleted,
	Modified,
	New,
}

/// <summary>The kind of metadata element a <see cref="IMetadataDiffItem"/> describes; its name is the XML element name.</summary>
public enum MetadataType : byte
{
	Assembly,
	Module,
	Type,
	Field,
	Method,
	Property,
	Event,
	AssemblyReference,
}

/// <summary>One difference between an old and a new assembly.</summary>
public interface IDiffItem
{
	DiffType DiffType { get; }

	string ToXml();

	string HumanReadable { get; }

	bool IsBreakingChange { get; }

	/// <summary>Visits this and nested differences, with the depth each one is at.</summary>
	void Visit(Action<IDiffItem, int> visit, int depth = 0);
}

/// <summary>A difference in a metadata element, which holds the differences in its declaration and in its children.</summary>
public interface IMetadataDiffItem : IDiffItem
{
	ICollection<IDiffItem> DeclarationDiffs { get; }

	ICollection<IMetadataDiffItem> ChildrenDiffs { get; }

	int Count { get; }

	MetadataType MetadataType { get; }
}

public abstract class DiffItem(DiffType diffType) : IDiffItem
{
	public DiffType DiffType { get; } = diffType;

	public string ToXml()
	{
		using var stringWriter = new StringWriter();
		using (var xmlWriter = new XmlTextWriter(stringWriter) { Formatting = Formatting.Indented })
			WriteXml(xmlWriter);
		return stringWriter.ToString();
	}

	public abstract string HumanReadable { get; }

	public abstract bool IsBreakingChange { get; }

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
public abstract class DeclarationDiffItem() : DiffItem(DiffType.Modified)
{
	public override void Visit(Action<IDiffItem, int> visit, int depth = 0) => visit(this, depth);
}

public sealed class VisibilityChangedDiffItem(bool reduced) : DeclarationDiffItem
{
	public override string HumanReadable => $"Member is {(reduced ? "less" : "more")} visible.";

	public override bool IsBreakingChange => reduced;
}

public sealed class StaticFlagChangedDiffItem(bool isNewMemberStatic) : DeclarationDiffItem
{
	public override string HumanReadable => $"Member changed to {(isNewMemberStatic ? "static" : "instance")}.";

	public override bool IsBreakingChange => true;
}

public sealed class VirtualFlagChangedDiffItem(bool isNewMethodVirtual) : DeclarationDiffItem
{
	public override string HumanReadable => $"Method changed to {(isNewMethodVirtual ? string.Empty : "non-")}virtual.";

	public override bool IsBreakingChange => !isNewMethodVirtual;
}

public sealed class MemberTypeDiffItem(string? oldType, string? newType) : DeclarationDiffItem
{
	public override string HumanReadable => $"Member type changed from {oldType} to {newType}.";

	public override bool IsBreakingChange => true;
}

public sealed class ParameterDiffItem(string oldName, string newName) : DeclarationDiffItem
{
	public override string HumanReadable => $"Parameter name changed from {oldName} to {newName}.";

	public override bool IsBreakingChange => true;
}

/// <summary>An attribute that is only on the old or only on the new element, named by its constructor.</summary>
public sealed class CustomAttributeDiffItem(DiffType diffType, string constructorSignature) : DiffItem(diffType)
{
	public override string HumanReadable => constructorSignature;

	public override bool IsBreakingChange => DiffType == DiffType.Deleted;

	public override void Visit(Action<IDiffItem, int> visit, int depth = 0) => visit(this, depth);

	internal override void WriteXml(XmlWriter writer)
	{
		writer.WriteStartElement("CustomAttribute");
		writer.WriteAttributeString("Name", constructorSignature);
		writer.WriteAttributeString("DiffType", DiffType.ToString());
		writer.WriteEndElement();
	}
}

/// <summary>A difference in a metadata element: an assembly, module, type, member or assembly reference.</summary>
public abstract class MetadataDiffItem : DiffItem, IMetadataDiffItem
{
	private bool? _isBreakingChange;

	protected MetadataDiffItem(
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
	public string? Key { get; init; }

	public ICollection<IDiffItem> DeclarationDiffs { get; }

	public ICollection<IMetadataDiffItem> ChildrenDiffs { get; }

	public int Count => DeclarationDiffs.Count + ChildrenDiffs.Count;

	public abstract MetadataType MetadataType { get; }

	public override string HumanReadable => Name;

	public override bool IsBreakingChange =>
		_isBreakingChange ??= DiffType != DiffType.Modified
			? DiffType == DiffType.Deleted
			: DeclarationDiffs.Concat(ChildrenDiffs).Any(item => item.IsBreakingChange);

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

public sealed class AssemblyDiffItem(
	bool hasOld,
	bool hasNew,
	string name,
	IEnumerable<IDiffItem>? declarationDiffs,
	IEnumerable<IMetadataDiffItem>? childrenDiffs
) : MetadataDiffItem(hasOld, hasNew, name, declarationDiffs, childrenDiffs)
{
	public override MetadataType MetadataType => MetadataType.Assembly;
}

public sealed class ModuleDiffItem(
	bool hasOld,
	bool hasNew,
	string name,
	IEnumerable<IDiffItem>? declarationDiffs,
	IEnumerable<IMetadataDiffItem>? childrenDiffs
) : MetadataDiffItem(hasOld, hasNew, name, declarationDiffs, childrenDiffs)
{
	public override MetadataType MetadataType => MetadataType.Module;
}

public sealed class TypeDiffItem(
	bool hasOld,
	bool hasNew,
	string name,
	IEnumerable<IDiffItem>? declarationDiffs,
	IEnumerable<IMetadataDiffItem>? childrenDiffs
) : MetadataDiffItem(hasOld, hasNew, name, declarationDiffs, childrenDiffs)
{
	public override MetadataType MetadataType => MetadataType.Type;
}

public sealed class MemberDiffItem(
	MetadataType metadataType,
	bool hasOld,
	bool hasNew,
	string name,
	IEnumerable<IDiffItem>? declarationDiffs,
	IEnumerable<IMetadataDiffItem>? childrenDiffs
) : MetadataDiffItem(hasOld, hasNew, name, declarationDiffs, childrenDiffs)
{
	public override MetadataType MetadataType { get; } = metadataType;

	/// <summary>
	/// A new member that existing code has to act on: an abstract one, such as an interface member without a default
	/// implementation, which every implementer must add, or a required one, which every object initializer must set.
	/// </summary>
	public bool AddsObligation { get; init; }
}

/// <summary>An assembly reference the new module no longer has. Reports do not count these as API changes.</summary>
public sealed class AssemblyReferenceDiffItem(string name) : MetadataDiffItem(true, false, name, null, null)
{
	public override MetadataType MetadataType => MetadataType.AssemblyReference;
}
