using System.Collections.Generic;
using System.IO;
using System.Linq;
using System.Text;
using System.Text.Encodings.Web;
using System.Text.Json;
using Differ.Engine;

namespace Differ.Exporters;

/// <summary>
/// Writes every result as one JSON document, <c>api-changes.json</c>, for tools to read: the suggested version change,
/// the number of breaking changes, and each assembly's changes as a tree.
/// </summary>
public class JsonExporter : IAllComparisonResultsExporter
{
	public string Format { get; } = "json";

	public void Export(AllComparisonResults results, OutputWriterFactory factory)
	{
		using var stream = new MemoryStream();
		using (var json = new Utf8JsonWriter(stream, new JsonWriterOptions { Indented = true, Encoder = JavaScriptEncoder.UnsafeRelaxedJsonEscaping }))
		{
			json.WriteStartObject();
			json.WriteBoolean("firstRelease", false);
			if (results.Baseline is not null)
				json.WriteString("baseline", results.Baseline);
			json.WriteString("suggestedVersionChange", results.SuggestedVersionChange.ToString());
			json.WriteNumber("breakingChanges", results.Comparisons.Sum(c => c.Diff is null ? 0 : CountBreaking(c.Diff)));
			json.WriteStartArray("assemblies");
			foreach (var comparison in results.Comparisons)
				WriteAssembly(json, comparison);
			json.WriteEndArray();
			WriteNames(json, "removedAssemblies", results.Removed.Select(f => f.Name));
			WriteNames(json, "addedAssemblies", results.Added.Select(f => f.Name));
			json.WriteEndObject();
		}

		using var writer = factory.Create("api-changes.json");
		writer.WriteLine(Encoding.UTF8.GetString(stream.ToArray()));
	}

	/// <summary>The document for a first release, which has nothing to compare with.</summary>
	public static string FirstRelease()
	{
		using var stream = new MemoryStream();
		using (var json = new Utf8JsonWriter(stream, new JsonWriterOptions { Indented = true }))
		{
			json.WriteStartObject();
			json.WriteBoolean("firstRelease", true);
			json.WriteString("suggestedVersionChange", SuggestedVersionChange.None.ToString());
			json.WriteNumber("breakingChanges", 0);
			json.WriteStartArray("assemblies");
			json.WriteEndArray();
			json.WriteStartArray("removedAssemblies");
			json.WriteEndArray();
			json.WriteStartArray("addedAssemblies");
			json.WriteEndArray();
			json.WriteEndObject();
		}
		return Encoding.UTF8.GetString(stream.ToArray());
	}

	private static void WriteAssembly(Utf8JsonWriter json, AssemblyComparison comparison)
	{
		json.WriteStartObject();
		json.WriteString("name", comparison.Second.Name);
		json.WriteString("suggestedVersionChange", comparison.SuggestedVersionChange.ToString());
		json.WriteNumber("breakingChanges", comparison.Diff is null ? 0 : CountBreaking(comparison.Diff));
		json.WriteStartArray("changes");
		if (comparison.Diff is not null)
		{
			// the assembly and its module only hold the changes; their own declaration changes are attributes and references
			foreach (var item in comparison.Diff.DeclarationDiffs)
				WriteChange(json, item);
			foreach (var module in comparison.Diff.ChildrenDiffs)
				foreach (var item in module.DeclarationDiffs.Concat(module.ChildrenDiffs))
					WriteChange(json, item);
		}
		json.WriteEndArray();
		json.WriteEndObject();
	}

	private static void WriteChange(Utf8JsonWriter json, IDiffItem item)
	{
		json.WriteStartObject();
		json.WriteString(
			"kind",
			item switch
			{
				IMetadataDiffItem metadata => metadata.MetadataType.ToString(),
				CustomAttributeDiffItem => "Attribute",
				_ => "Declaration",
			}
		);
		json.WriteString("name", item.HumanReadable);
		json.WriteString(
			"change",
			item.DiffType switch
			{
				DiffType.New => "added",
				DiffType.Deleted => "removed",
				_ => "modified",
			}
		);
		json.WriteString("suggestedVersionChange", AssemblyComparison.Classify(item).ToString());
		if (item is MemberDiffItem { AddsObligation: true })
			json.WriteBoolean("addsObligation", true);
		if (item is IMetadataDiffItem { Count: > 0 } parent)
		{
			json.WriteStartArray("changes");
			foreach (var child in parent.DeclarationDiffs.Concat(parent.ChildrenDiffs))
				WriteChange(json, child);
			json.WriteEndArray();
		}
		json.WriteEndObject();
	}

	private static void WriteNames(Utf8JsonWriter json, string property, IEnumerable<string> names)
	{
		json.WriteStartArray(property);
		foreach (var name in names)
			json.WriteStringValue(name);
		json.WriteEndArray();
	}

	/// <summary>
	/// The changes that on their own call for a major version, the count the GitHub comment shows; a modified element
	/// is breaking because of a change inside it, which is counted instead.
	/// </summary>
	public static int CountBreaking(IDiffItem diff)
	{
		var count = 0;
		diff.Visit((item, _) =>
		{
			if (item is not IMetadataDiffItem { DiffType: DiffType.Modified } && AssemblyComparison.Classify(item) == SuggestedVersionChange.Major)
				count++;
		});
		return count;
	}
}
