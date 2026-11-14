#nullable enable
using System.Collections.Generic;
using System.IO;
using System.Linq;
using System.Text;
using System.Text.Encodings.Web;
using System.Text.Json;
using Nullean.PublicApi;

namespace Differ.ApiFiles;

/// <summary>
/// The API as JSON: the same tree as a .api file, with every part of each declaration as a field, and the declaration
/// itself as C#, for tools and agents that would otherwise parse the text.
/// </summary>
internal static class ApiJson
{
	public static string Write(IReadOnlyList<ApiAssembly> assemblies)
	{
		using var stream = new MemoryStream();
		using (var json = new Utf8JsonWriter(stream, new JsonWriterOptions { Indented = true, Encoder = JavaScriptEncoder.UnsafeRelaxedJsonEscaping }))
		{
			json.WriteStartArray();
			foreach (var assembly in assemblies)
			{
				json.WriteStartObject();
				json.WriteString("assembly", assembly.Name);
				json.WriteString("version", assembly.Version.ToString());
				if (assembly.TargetFramework is not null)
					json.WriteString("targetFramework", assembly.TargetFramework);
				json.WriteStartArray("types");
				foreach (var type in assembly.Types)
					Type(json, type);
				json.WriteEndArray();
				json.WriteEndObject();
			}
			json.WriteEndArray();
		}
		return Encoding.UTF8.GetString(stream.ToArray());
	}

	private static void Type(Utf8JsonWriter json, ApiType type)
	{
		json.WriteStartObject();
		json.WriteString("declaration", ApiWriter.Declaration(type));
		json.WriteString("namespace", type.Namespace);
		json.WriteString("name", type.Name);
		json.WriteString("kind", type.Kind.ToString().ToLowerInvariant());
		json.WriteString("accessibility", Access(type.Accessibility));
		Words(json, "modifiers", type.Modifiers.ToString());
		json.WriteBoolean("canBeExtendedOutside", type.CanBeExtendedOutside);
		TypeParameters(json, type.TypeParameters, type);
		if (type.BaseType is not null)
			json.WriteString("baseType", ApiWriter.Reference(type.BaseType, type));
		Strings(json, "interfaces", type.Interfaces.Select(i => ApiWriter.Reference(i, type)));
		Attributes(json, type.Attributes, type);
		json.WriteStartArray("members");
		if (type.Kind != ApiTypeKind.Delegate)
			foreach (var member in type.Members)
				Member(json, member, type);
		json.WriteEndArray();
		json.WriteStartArray("nestedTypes");
		foreach (var nested in type.NestedTypes)
			Type(json, nested);
		json.WriteEndArray();
		json.WriteEndObject();
	}

	private static void Member(Utf8JsonWriter json, ApiMember member, ApiType type)
	{
		json.WriteStartObject();
		json.WriteString("declaration", ApiWriter.Declaration(member, type));
		json.WriteString("name", member.Name);
		json.WriteString("accessibility", Access(member.Accessibility));
		switch (member)
		{
			case ApiField field:
				json.WriteString("kind", type.Kind == ApiTypeKind.Enum ? "value" : field.Constant is null ? "field" : "const");
				json.WriteString("type", ApiWriter.Reference(field.Type, type));
				json.WriteBoolean("static", field.IsStatic);
				json.WriteBoolean("readonly", field.IsReadOnly);
				json.WriteBoolean("required", field.IsRequired);
				if (field.Constant is not null)
					json.WriteString("value", field.Constant);
				break;
			case ApiProperty property:
				json.WriteString("kind", property.Parameters.Count > 0 ? "indexer" : "property");
				Words(json, "modifiers", property.Modifiers.ToString());
				json.WriteString("type", ApiWriter.Reference(property.Type, type));
				Parameters(json, property.Parameters, type);
				if (property.Getter is { } getter)
					json.WriteString("get", Access(getter.Accessibility));
				if (property.Setter is { } setter)
					json.WriteString(setter.IsInit ? "init" : "set", Access(setter.Accessibility));
				json.WriteBoolean("required", property.IsRequired);
				break;
			case ApiEvent @event:
				json.WriteString("kind", "event");
				Words(json, "modifiers", @event.Modifiers.ToString());
				json.WriteString("type", ApiWriter.Reference(@event.Type, type));
				break;
			case ApiMethod method:
				json.WriteString("kind", method.Kind.ToString().ToLowerInvariant());
				Words(json, "modifiers", method.Modifiers.ToString());
				json.WriteString("returnType", ApiWriter.Reference(method.ReturnType, type));
				if (method.ReturnRefKind != ApiRefKind.None)
					json.WriteString("returnRef", RefKind(method.ReturnRefKind));
				TypeParameters(json, method.TypeParameters, type);
				Parameters(json, method.Parameters, type);
				json.WriteBoolean("extension", method.IsExtension);
				break;
		}
		Attributes(json, member.Attributes, type);
		json.WriteEndObject();
	}

	private static void Parameters(Utf8JsonWriter json, IReadOnlyList<ApiParameter> parameters, ApiType type)
	{
		json.WriteStartArray("parameters");
		foreach (var parameter in parameters)
		{
			json.WriteStartObject();
			json.WriteString("name", parameter.Name);
			json.WriteString("type", ApiWriter.Reference(parameter.Type, type));
			if (parameter.RefKind != ApiRefKind.None)
				json.WriteString("ref", RefKind(parameter.RefKind));
			if (parameter.IsParams)
				json.WriteBoolean("params", true);
			if (parameter.IsScoped)
				json.WriteBoolean("scoped", true);
			if (parameter.DefaultValue is not null)
				json.WriteString("default", parameter.DefaultValue);
			Attributes(json, parameter.Attributes, type);
			json.WriteEndObject();
		}
		json.WriteEndArray();
	}

	private static void TypeParameters(Utf8JsonWriter json, IReadOnlyList<ApiTypeParameter> parameters, ApiType type)
	{
		json.WriteStartArray("typeParameters");
		foreach (var parameter in parameters)
		{
			json.WriteStartObject();
			json.WriteString("name", parameter.Name);
			if (parameter.Variance != ApiVariance.None)
				json.WriteString("variance", parameter.Variance.ToString().ToLowerInvariant());
			Words(json, "constraints", parameter.Constraints.ToString());
			Strings(json, "constraintTypes", parameter.ConstraintTypes.Select(t => ApiWriter.Reference(t, type)));
			json.WriteEndObject();
		}
		json.WriteEndArray();
	}

	private static void Attributes(Utf8JsonWriter json, IReadOnlyList<ApiAttribute> attributes, ApiType type) =>
		Strings(
			json,
			"attributes",
			attributes.Select(a => ApiWriter.Reference(a.Type, type) + (a.Arguments.Count == 0 ? "" : "(" + string.Join(", ", a.Arguments) + ")"))
		);

	private static void Strings(Utf8JsonWriter json, string name, IEnumerable<string> values)
	{
		json.WriteStartArray(name);
		foreach (var value in values)
			json.WriteStringValue(value);
		json.WriteEndArray();
	}

	/// <summary>A flags enum's names, lowercase: "Abstract, Sealed" as ["abstract", "sealed"], and None as [].</summary>
	private static void Words(Utf8JsonWriter json, string name, string flags) =>
		Strings(json, name, flags == "None" ? [] : flags.Split(", ").Select(f => f.ToLowerInvariant()));

	private static string Access(ApiAccessibility accessibility) => accessibility switch
	{
		ApiAccessibility.Public => "public",
		ApiAccessibility.Protected => "protected",
		_ => "protected internal"
	};

	private static string RefKind(ApiRefKind refKind) => refKind switch
	{
		ApiRefKind.Ref => "ref",
		ApiRefKind.Out => "out",
		ApiRefKind.In => "in",
		_ => "ref readonly"
	};
}
