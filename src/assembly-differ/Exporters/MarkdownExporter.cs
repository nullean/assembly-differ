using System.Collections.Generic;
using System.IO;
using System.Linq;
using System.Text.RegularExpressions;
using Differ.Engine;

namespace Differ.Exporters;

/// <summary>
/// Writes each changed type as a heading, with a line or a sub-heading for every change to it or to one of its members.
/// </summary>
public partial class MarkdownExporter : IAssemblyComparisonExporter
{
	public string Format { get; } = "markdown";

	public void Export(AssemblyComparison assemblyComparison, OutputWriterFactory factory)
	{
		var name = assemblyComparison.First.Name;
		using var writer = factory.Create(Path.ChangeExtension(name, "md"));

		writer.WriteLine($"## API Changes: `{Path.GetFileNameWithoutExtension(name)}`");
		writer.WriteLine();

		foreach (var module in assemblyComparison.Diff.ChildrenDiffs)
			foreach (var type in module.ChildrenDiffs.OfType<TypeDiffItem>())
				WriteType(writer, type);
	}

	private static void WriteType(OutputWriter writer, TypeDiffItem type)
	{
		switch (type.DiffType)
		{
			case DiffType.New:
				writer.WriteLine($"## `{type.Name}` is new");
				return;
			case DiffType.Deleted:
				writer.WriteLine($"## `{type.Name}` is deleted");
				return;
		}

		var declarations = type.DeclarationDiffs.Select(Describe).ToList();
		var members = type.ChildrenDiffs.Where(c => c is not TypeDiffItem).ToList();
		if (declarations.Count > 0 || members.Count > 0)
		{
			writer.WriteLine($"## `{type.Name}`");
			foreach (var declaration in declarations)
				writer.WriteLine(declaration);
			foreach (var member in members)
				WriteMember(writer, member);
		}

		foreach (var nested in type.ChildrenDiffs.OfType<TypeDiffItem>())
			WriteType(writer, nested);
	}

	private static void WriteMember(OutputWriter writer, IMetadataDiffItem member)
	{
		var name = ((MetadataDiffItem)member).Name;
		switch (member.DiffType)
		{
			case DiffType.New:
				writer.WriteLine($"### `{name}` is added");
				return;
			case DiffType.Deleted:
				writer.WriteLine($"### `{name}` is deleted");
				return;
		}

		writer.WriteLine($"### `{name}`");
		foreach (var declaration in member.DeclarationDiffs)
			writer.WriteLine(Describe(declaration));
		// a property's or event's accessors: get, set, add and remove
		foreach (var accessor in member.ChildrenDiffs.Cast<MetadataDiffItem>())
		{
			if (accessor.DiffType != DiffType.Modified)
			{
				writer.WriteLine($"`{accessor.Name}` is {(accessor.DiffType == DiffType.New ? "added" : "deleted")}.");
				continue;
			}
			foreach (var declaration in accessor.DeclarationDiffs)
				writer.WriteLine($"`{accessor.Name}`: {Describe(declaration)}");
		}
	}

	private static string Describe(IDiffItem item) => item switch
	{
		CustomAttributeDiffItem attribute => $"Attribute `{attribute.HumanReadable}` is {(attribute.DiffType == DiffType.New ? "added" : "deleted")}.",
		_ => ChangedFrom().Replace(item.HumanReadable, "changed from `$1` to `$2`."),
	};

	[GeneratedRegex("changed from (.*?) to (.*).")]
	private static partial Regex ChangedFrom();
}
