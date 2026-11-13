// The comparison is a port of JustAssembly (https://github.com/telerik/JustAssembly), Copyright Telerik, licensed
// under the Apache License 2.0, and reproduces its reports. Names follow JustDecompile's C# writer from the same project.
#nullable enable
using System;
using System.Collections.Generic;
using System.Linq;
using System.Reflection;

namespace Differ.Engine;

/// <summary>Compares the public API of two assemblies.</summary>
public static class ApiDiff
{
	/// <summary>The differences between two assemblies' public API, or <c>null</c> when there are none.</summary>
	public static IMetadataDiffItem? Compare(string oldAssemblyPath, string newAssemblyPath)
	{
		var oldAssembly = ModelReader.Read(oldAssemblyPath);
		var newAssembly = ModelReader.Read(newAssemblyPath);
		var declarationDiffs = Comparers.Attributes(oldAssembly.Attributes, newAssembly.Attributes);
		var childrenDiffs = Comparers.Modules.Differences(oldAssembly.Modules, newAssembly.Modules);
		if (declarationDiffs.Count == 0 && childrenDiffs.Count == 0)
			return null;
		return new AssemblyDiffItem(true, true, newAssembly.FullName, declarationDiffs, childrenDiffs.Cast<IMetadataDiffItem>());
	}
}

/// <summary>
/// Pairs the elements of two lists, each sorted by a key, and reports the ones only one side has and the pairs that
/// differ. Only API elements are reported: an element neither side exposes is skipped.
/// </summary>
internal abstract class Comparer<T> where T : class
{
	public List<IDiffItem> Differences(IEnumerable<T> oldElements, IEnumerable<T> newElements)
	{
		var oldSorted = new List<T>(oldElements);
		oldSorted.Sort(Compare);
		var newSorted = new List<T>(newElements);
		newSorted.Sort(Compare);

		var result = new List<IDiffItem>();
		int oldIndex = 0, newIndex = 0;
		while (oldIndex < oldSorted.Count && newIndex < newSorted.Count)
		{
			var oldElement = oldSorted[oldIndex];
			var newElement = newSorted[newIndex];
			var order = Compare(oldElement, newElement);
			if (order < 0)
			{
				oldIndex++;
				if (IsApi(oldElement))
					result.Add(Missing(oldElement));
			}
			else if (order > 0)
			{
				newIndex++;
				if (IsApi(newElement) && Added(newElement) is { } added)
					result.Add(added);
			}
			else
			{
				oldIndex++;
				newIndex++;
				if ((IsApi(oldElement) || IsApi(newElement)) && Changed(oldElement, newElement) is { } changed)
					result.Add(changed);
			}
		}
		for (; oldIndex < oldSorted.Count; oldIndex++)
			if (IsApi(oldSorted[oldIndex]))
				result.Add(Missing(oldSorted[oldIndex]));
		for (; newIndex < newSorted.Count; newIndex++)
			if (IsApi(newSorted[newIndex]) && Added(newSorted[newIndex]) is { } added)
				result.Add(added);
		return result;
	}

	protected abstract int Compare(T x, T y);

	protected abstract bool IsApi(T element);

	protected abstract IDiffItem Missing(T element);

	protected abstract IDiffItem? Added(T element);

	protected abstract IDiffItem? Changed(T oldElement, T newElement);

	/// <summary>The culture-aware ordering <see cref="string.CompareTo(string)"/> gives, which the reports have always used.</summary>
#pragma warning disable CA1309 // ordinal would reorder the reports
	protected static int Order(string x, string y) => string.Compare(x, y, StringComparison.CurrentCulture);
#pragma warning restore CA1309
}

internal static class Comparers
{
	public static readonly ModuleComparer Modules = new();

	public static List<IDiffItem> Attributes(IEnumerable<AttributeModel> oldAttributes, IEnumerable<AttributeModel> newAttributes) =>
		AttributeComparer.Instance.Differences(oldAttributes, newAttributes);

	/// <summary>Public is the most visible; a positive result means the new element is more visible.</summary>
	public static int CompareVisibility(int oldVisibility, int newVisibility) => newVisibility - oldVisibility;

	public static int Visibility(TypeAttributes visibility) => visibility switch
	{
		TypeAttributes.Public or TypeAttributes.NestedPublic => 3,
		TypeAttributes.NestedFamily or TypeAttributes.NestedFamORAssem => 2,
		TypeAttributes.NotPublic or TypeAttributes.NestedAssembly or TypeAttributes.NestedFamANDAssem => 1,
		_ => 0,
	};

	public static int Visibility(MethodAttributes access) => access switch
	{
		MethodAttributes.Public => 3,
		MethodAttributes.Family or MethodAttributes.FamORAssem => 2,
		MethodAttributes.FamANDAssem or MethodAttributes.Assembly => 1,
		_ => 0,
	};

	public static int Visibility(FieldAttributes access) => access switch
	{
		FieldAttributes.Public => 3,
		FieldAttributes.Family or FieldAttributes.FamORAssem => 2,
		FieldAttributes.FamANDAssem or FieldAttributes.Assembly => 1,
		_ => 0,
	};

	public static IEnumerable<IDiffItem> VisibilityChange(int result)
	{
		if (result != 0)
			yield return new VisibilityChangedDiffItem(result < 0);
	}
}

internal sealed class AttributeComparer : Comparer<AttributeModel>
{
	public static readonly AttributeComparer Instance = new();

	protected override int Compare(AttributeModel x, AttributeModel y) => Order(x.Signature, y.Signature);

	protected override bool IsApi(AttributeModel element) => true;

	protected override IDiffItem Missing(AttributeModel element) => new CustomAttributeDiffItem(DiffType.Deleted, element.Signature);

	protected override IDiffItem Added(AttributeModel element) => new CustomAttributeDiffItem(DiffType.New, element.Signature);

	protected override IDiffItem? Changed(AttributeModel oldElement, AttributeModel newElement) => null;
}

internal sealed class ReferenceComparer : Comparer<string>
{
	public static readonly ReferenceComparer Instance = new();

	protected override int Compare(string x, string y) => Order(x, y);

	protected override bool IsApi(string element) => true;

	protected override IDiffItem Missing(string element) => new AssemblyReferenceDiffItem(element);

	// a new or unchanged reference is not reported
	protected override IDiffItem? Added(string element) => null;

	protected override IDiffItem? Changed(string oldElement, string newElement) => null;
}

internal sealed class ModuleComparer : Comparer<ModuleModel>
{
	protected override int Compare(ModuleModel x, ModuleModel y) => Order(x.Name, y.Name);

	protected override bool IsApi(ModuleModel element) => element.Types.Any(t => t.Visibility == TypeAttributes.Public);

	protected override IDiffItem Missing(ModuleModel element) => new ModuleDiffItem(true, false, element.Name, null, null);

	protected override IDiffItem Added(ModuleModel element) => new ModuleDiffItem(false, true, element.Name, null, null);

	protected override IDiffItem? Changed(ModuleModel oldElement, ModuleModel newElement)
	{
		var declarationDiffs = Comparers
			.Attributes(oldElement.Attributes, newElement.Attributes)
			.Concat(ReferenceComparer.Instance.Differences(oldElement.AssemblyReferences, newElement.AssemblyReferences))
			.ToList();
		var childrenDiffs = TypeComparer.Instance.Differences(oldElement.Types, newElement.Types);
		if (declarationDiffs.Count == 0 && childrenDiffs.Count == 0)
			return null;
		return new ModuleDiffItem(true, true, newElement.Name, declarationDiffs, childrenDiffs.Cast<IMetadataDiffItem>());
	}
}

internal sealed partial class TypeComparer : Comparer<TypeModel>
{
	public static readonly TypeComparer Instance = new();

	protected override int Compare(TypeModel x, TypeModel y) => Order(x.FullName, y.FullName);

	protected override bool IsApi(TypeModel element) =>
		element.Visibility is TypeAttributes.Public or TypeAttributes.NestedPublic or TypeAttributes.NestedFamily or TypeAttributes.NestedFamORAssem;

	protected override IDiffItem Missing(TypeModel element) => new TypeDiffItem(true, false, element.DisplayName, null, null);

	protected override IDiffItem Added(TypeModel element) => new TypeDiffItem(false, true, element.DisplayName, null, null);

	protected override IDiffItem? Changed(TypeModel oldElement, TypeModel newElement)
	{
		var declarationDiffs = Comparers
			.Attributes(oldElement.Attributes, newElement.Attributes)
			.Concat(Comparers.VisibilityChange(TypeVisibilityChange(oldElement, newElement)))
			.ToList();
		var childrenDiffs = FieldComparer
			.Instance
			.Differences(oldElement.Fields, newElement.Fields)
			.Concat(PropertyComparer.Instance.Differences(oldElement.Properties, newElement.Properties))
			.Concat(MethodComparer.Instance.Differences(oldElement.Methods, newElement.Methods))
			.Concat(EventComparer.Instance.Differences(oldElement.Events, newElement.Events))
			.Concat(Instance.Differences(oldElement.NestedTypes, newElement.NestedTypes))
			.ToList();
		if (declarationDiffs.Count == 0 && childrenDiffs.Count == 0)
			return null;
		return new TypeDiffItem(true, true, newElement.DisplayName, declarationDiffs, childrenDiffs.Cast<IMetadataDiffItem>());
	}
}

internal sealed partial class TypeComparer
{
	/// <summary>
	/// JustAssembly subtracted the two visibilities as byte-backed enum values, which wraps a negative result around to
	/// a positive one. A type that became less visible is therefore reported as more visible, and not as breaking;
	/// the reports keep that until it is fixed on purpose.
	/// </summary>
	private static int TypeVisibilityChange(TypeModel oldElement, TypeModel newElement) =>
		(byte)(Comparers.Visibility(newElement.Visibility) - Comparers.Visibility(oldElement.Visibility));
}

internal sealed class MethodComparer : Comparer<MethodModel>
{
	public static readonly MethodComparer Instance = new();

	protected override int Compare(MethodModel x, MethodModel y) => Order(x.Signature, y.Signature);

	protected override bool IsApi(MethodModel element) => element.IsApi;

	protected override IDiffItem Missing(MethodModel element) => new MemberDiffItem(MetadataType.Method, true, false, element.Display.Name, null, null);

	protected override IDiffItem Added(MethodModel element) => new MemberDiffItem(MetadataType.Method, false, true, element.Display.Name, null, null);

	protected override IDiffItem? Changed(MethodModel oldElement, MethodModel newElement)
	{
		var declarationDiffs = DeclarationDiffs(oldElement, newElement).ToList();
		if (declarationDiffs.Count == 0)
			return null;
		return new MemberDiffItem(MetadataType.Method, true, true, newElement.Display.Name, declarationDiffs, null);
	}

	public static IEnumerable<IDiffItem> DeclarationDiffs(MethodModel oldMethod, MethodModel newMethod)
	{
		foreach (var item in Comparers.Attributes(oldMethod.Attributes, newMethod.Attributes))
			yield return item;
		foreach (var item in Comparers.VisibilityChange(
			Comparers.CompareVisibility(Comparers.Visibility(oldMethod.Access), Comparers.Visibility(newMethod.Access))
		))
			yield return item;
		if (oldMethod.IsVirtual != newMethod.IsVirtual)
			yield return new VirtualFlagChangedDiffItem(newMethod.IsVirtual);
		if (oldMethod.IsStatic != newMethod.IsStatic)
			yield return new StaticFlagChangedDiffItem(newMethod.IsStatic);
		if (oldMethod.ReturnTypeFullName != newMethod.ReturnTypeFullName)
			yield return new MemberTypeDiffItem(oldMethod.Display.Type, newMethod.Display.Type);
		for (var i = 0; i < oldMethod.ParameterNames.Count; i++)
			if (oldMethod.ParameterNames[i] != newMethod.ParameterNames[i])
				yield return new ParameterDiffItem(oldMethod.ParameterNames[i], newMethod.ParameterNames[i]);
	}

	/// <summary>
	/// A property's or event's accessor: <c>get</c>, <c>set</c>, <c>add</c> or <c>remove</c>. One only one side has is
	/// reported without details; a changed type is the property's to report.
	/// </summary>
	public static IMetadataDiffItem? Accessor(string name, MethodModel? oldAccessor, MethodModel? newAccessor)
	{
		if (oldAccessor is null && newAccessor is null)
			return null;
		if (oldAccessor is null || newAccessor is null)
			return (oldAccessor ?? newAccessor)!.IsApi
				? new MemberDiffItem(MetadataType.Method, oldAccessor is not null, newAccessor is not null, name, null, null)
				: null;
		if (!oldAccessor.IsApi && !newAccessor.IsApi)
			return null;
		var declarationDiffs = DeclarationDiffs(oldAccessor, newAccessor).Where(item => item is not MemberTypeDiffItem).ToList();
		if (declarationDiffs.Count == 0)
			return null;
		return new MemberDiffItem(MetadataType.Method, true, true, name, declarationDiffs, null);
	}
}

internal sealed class FieldComparer : Comparer<FieldModel>
{
	public static readonly FieldComparer Instance = new();

	protected override int Compare(FieldModel x, FieldModel y) => Order(x.Name, y.Name);

	protected override bool IsApi(FieldModel element) => element.IsApi;

	protected override IDiffItem Missing(FieldModel element) => new MemberDiffItem(MetadataType.Field, true, false, element.Display.Name, null, null);

	protected override IDiffItem Added(FieldModel element) => new MemberDiffItem(MetadataType.Field, false, true, element.Display.Name, null, null);

	protected override IDiffItem? Changed(FieldModel oldElement, FieldModel newElement)
	{
		var declarationDiffs = Comparers
			.Attributes(oldElement.Attributes, newElement.Attributes)
			.Concat(Comparers.VisibilityChange(Comparers.CompareVisibility(Comparers.Visibility(oldElement.Access), Comparers.Visibility(newElement.Access))))
			.ToList();
		if (oldElement.IsStatic != newElement.IsStatic)
			declarationDiffs.Add(new StaticFlagChangedDiffItem(newElement.IsStatic));
		if (oldElement.TypeFullName != newElement.TypeFullName)
			declarationDiffs.Add(new MemberTypeDiffItem(oldElement.Display.Type, newElement.Display.Type));
		if (declarationDiffs.Count == 0)
			return null;
		return new MemberDiffItem(MetadataType.Field, true, true, newElement.Display.Name, declarationDiffs, null);
	}
}

internal sealed class PropertyComparer : Comparer<PropertyModel>
{
	public static readonly PropertyComparer Instance = new();

	protected override int Compare(PropertyModel x, PropertyModel y) => Order(x.Name, y.Name);

	protected override bool IsApi(PropertyModel element) => element.Get is { IsApi: true } || element.Set is { IsApi: true };

	protected override IDiffItem Missing(PropertyModel element) => new MemberDiffItem(MetadataType.Property, true, false, element.Display.Name, null, null);

	protected override IDiffItem Added(PropertyModel element) => new MemberDiffItem(MetadataType.Property, false, true, element.Display.Name, null, null);

	protected override IDiffItem? Changed(PropertyModel oldElement, PropertyModel newElement)
	{
		var declarationDiffs = Comparers.Attributes(oldElement.Attributes, newElement.Attributes);
		if (oldElement.TypeFullName != newElement.TypeFullName)
			declarationDiffs.Add(new MemberTypeDiffItem(oldElement.Display.Type, newElement.Display.Type));
		var childrenDiffs = new List<IMetadataDiffItem>(2);
		if (MethodComparer.Accessor("get", oldElement.Get, newElement.Get) is { } get)
			childrenDiffs.Add(get);
		if (MethodComparer.Accessor("set", oldElement.Set, newElement.Set) is { } set)
			childrenDiffs.Add(set);
		if (declarationDiffs.Count == 0 && childrenDiffs.Count == 0)
			return null;
		return new MemberDiffItem(MetadataType.Property, true, true, newElement.Display.Name, declarationDiffs, childrenDiffs);
	}
}

internal sealed class EventComparer : Comparer<EventModel>
{
	public static readonly EventComparer Instance = new();

	protected override int Compare(EventModel x, EventModel y) => Order(x.Name, y.Name);

	protected override bool IsApi(EventModel element) => element.Add is { IsApi: true } || element.Remove is { IsApi: true };

	protected override IDiffItem Missing(EventModel element) => new MemberDiffItem(MetadataType.Event, true, false, element.Display.Name, null, null);

	protected override IDiffItem Added(EventModel element) => new MemberDiffItem(MetadataType.Event, false, true, element.Display.Name, null, null);

	protected override IDiffItem? Changed(EventModel oldElement, EventModel newElement)
	{
		var declarationDiffs = Comparers.Attributes(oldElement.Attributes, newElement.Attributes);
		var childrenDiffs = new List<IMetadataDiffItem>(2);
		if (MethodComparer.Accessor("add", oldElement.Add, newElement.Add) is { } add)
			childrenDiffs.Add(add);
		if (MethodComparer.Accessor("remove", oldElement.Remove, newElement.Remove) is { } remove)
			childrenDiffs.Add(remove);
		if (declarationDiffs.Count == 0 && childrenDiffs.Count == 0)
			return null;
		return new MemberDiffItem(MetadataType.Event, true, true, newElement.Display.Name, declarationDiffs, childrenDiffs);
	}
}
