using System.Collections.Immutable;
using System.Text;
using AwesomeAssertions;
using Microsoft.CodeAnalysis;
using Microsoft.CodeAnalysis.CSharp;

namespace Nullean.PublicApi.Tests;

/// <summary>
/// Reads an assembly with <see cref="ApiReader"/> and with Roslyn, and checks that every signature agrees: the types
/// with their nullability, tuple names, <c>dynamic</c> and ref kinds, the base types and interfaces. Roslyn's reading
/// of metadata is what C# code compiled against the assembly sees, so it is the reference.
/// </summary>
public class RoslynOracleTests
{
	public static IEnumerable<string> FrameworkAssemblies() =>
		["System.Collections.Immutable", "System.Text.Json", "System.Linq", "System.Private.CoreLib", "System.Net.Http", "System.Memory"];

	[Test]
	[MethodDataSource(nameof(FrameworkAssemblies))]
	public void Agrees_with_Roslyn_on_a_framework_assembly(string name)
	{
		var path = ((string)AppContext.GetData("TRUSTED_PLATFORM_ASSEMBLIES")!).Split(Path.PathSeparator).First(
			p => Path.GetFileNameWithoutExtension(p) == name
		);
		Compare(File.ReadAllBytes(path));
	}

	[Test]
	[MethodDataSource(typeof(WriterTests), nameof(WriterTests.Cases))]
	public void Agrees_with_Roslyn_on_a_case(string name, string source)
	{
		using var stream = Compiler.Compile(source);
		Compare(stream.ToArray());
	}

	private static void Compare(byte[] image)
	{
		var assembly = ApiReader.Read(new MemoryStream(image));
		var ours = Ours(assembly);
		var theirs = Theirs(image, assembly.Name);
		var missing = theirs.Keys.Except(ours.Keys).Order(StringComparer.Ordinal).Take(20).ToList();
		var extra = ours.Keys.Except(theirs.Keys).Order(StringComparer.Ordinal).Take(20).ToList();
		var different = theirs
			.Where(p => ours.TryGetValue(p.Key, out var value) && value != p.Value)
			.Take(20)
			.Select(p => $"{p.Key}{Environment.NewLine}  roslyn: {p.Value}{Environment.NewLine}  ours:   {ours[p.Key]}")
			.ToList();
		missing.Should().BeEmpty("Roslyn sees these and the reader does not");
		extra.Should().BeEmpty("the reader sees these and Roslyn does not");
		different.Should().BeEmpty();
		theirs.Count.Should().BeGreaterThan(0);
	}

	// the reader's side

	private static Dictionary<string, string> Ours(ApiAssembly assembly)
	{
		var result = new Dictionary<string, string>(StringComparer.Ordinal);
		foreach (var type in assembly.Types)
			Ours(type, null, result);
		return result;
	}

	private static void Ours(ApiType type, string? declaring, Dictionary<string, string> result)
	{
		var name = declaring is null ? (type.Namespace.Length == 0 ? type.Name : type.Namespace + "." + type.Name) : declaring + "/" + type.Name;
		var bases = new List<string>();
		if (type.Kind == ApiTypeKind.Class && type.BaseType is { } baseType)
			bases.Add(Render(baseType));
		bases.AddRange(type.Interfaces.Select(Render).Order(StringComparer.Ordinal));
		var typeFacts = new List<string> { Access(type.Accessibility), type.Kind.ToString().ToLowerInvariant() };
		if (type.Kind == ApiTypeKind.Class)
		{
			if ((type.Modifiers & ApiTypeModifiers.Static) != 0)
				typeFacts.Add("static");
			if ((type.Modifiers & ApiTypeModifiers.Abstract) != 0)
				typeFacts.Add("abstract");
			if ((type.Modifiers & ApiTypeModifiers.Sealed) != 0)
				typeFacts.Add("sealed");
			if ((type.Modifiers & ApiTypeModifiers.Record) != 0)
				typeFacts.Add("record");
		}
		if ((type.Modifiers & ApiTypeModifiers.ReadOnly) != 0)
			typeFacts.Add("readonly");
		if ((type.Modifiers & ApiTypeModifiers.Ref) != 0)
			typeFacts.Add("ref");
		result[name] = string.Join(" ", typeFacts) + TypeParameters(type.TypeParameters) + " : " + string.Join(", ", bases);
		if (type.Kind is ApiTypeKind.Enum or ApiTypeKind.Delegate)
			return;
		foreach (var member in type.Members)
		{
			switch (member)
			{
				case ApiField field:
					{
						var facts = new List<string> { Access(field.Accessibility) };
						if (field.Constant is not null)
							facts.Add("const");
						else
						{
							if (field.IsStatic)
								facts.Add("static");
							if (field.IsReadOnly)
								facts.Add("readonly");
							if (field.IsVolatile)
								facts.Add("volatile");
						}
						if (field.IsRequired)
							facts.Add("required");
						result[name + "::" + field.Name] = string.Join(" ", facts) + " " + Render(field.Type);
						break;
					}
				case ApiProperty property:
					{
						var facts = new List<string> { Access(property.Accessibility), Modifiers(property.Modifiers) };
						if (property.IsRequired)
							facts.Add("required");
						if (property.Getter is { } get)
							facts.Add("get:" + Access(get.Accessibility));
						if (property.Setter is { } set)
							facts.Add((set.IsInit ? "init:" : "set:") + Access(set.Accessibility));
						result[name + "::" + property.Name + Key(property.Parameters.Select(p => p.Type))] = string.Join(" ", facts) + " " + RefKind(
							property.RefKind
						) + Render(property.Type) + Parameters(property.Parameters);
						break;
					}
				case ApiEvent @event:
					result[name + "::" + @event.Name] = Access(@event.Accessibility) + " " + Modifiers(@event.Modifiers) + " " + Render(@event.Type);
					break;
				case ApiMethod method:
					result[name + "::" + method.Name + "`" + method.TypeParameters.Count + Key(
						method.Parameters.Select(p => p.Type),
						method.Parameters.Select(p => p.RefKind)
					)] = Access(method.Accessibility) + " " + (IsFinalizer(method) ? "[finalizer]" : Modifiers(method.Modifiers)) +
						(method.IsExtension ? " extension" : "") + TypeParameters(method.TypeParameters) + " " + RefKind(method.ReturnRefKind) + Render(
						method.ReturnType
					) + Parameters(method.Parameters);
					break;
			}
		}
		foreach (var nested in type.NestedTypes)
			Ours(nested, name, result);
	}

	private static string Parameters(IEnumerable<ApiParameter> parameters) =>
		"(" + string.Join(
			", ",
			parameters.Select(
				p => (p.IsParams ? "params " : "") + (p.IsScoped && p.RefKind != ApiRefKind.Out ? "scoped " : "") + RefKind(p.RefKind) + Render(p.Type) +
					" " + p.Name + (p.DefaultValue is null ? "" : " = ?")
			)
		) + ")";

	private static bool IsFinalizer(ApiMethod method) =>
		method is { Name: "Finalize", Parameters.Count: 0, TypeParameters.Count: 0, Accessibility: ApiAccessibility.Protected } && (method.Modifiers &
			(ApiMemberModifiers.Virtual | ApiMemberModifiers.Override)) != 0;

	private static string Access(ApiAccessibility accessibility) => accessibility switch
	{
		ApiAccessibility.Public => "public",
		ApiAccessibility.Protected => "protected",
		_ => "protected-internal"
	};

	private static string Modifiers(ApiMemberModifiers modifiers)
	{
		var words = new List<string>();
		if ((modifiers & ApiMemberModifiers.Static) != 0)
			words.Add("static");
		if ((modifiers & ApiMemberModifiers.Abstract) != 0)
			words.Add("abstract");
		if ((modifiers & ApiMemberModifiers.Virtual) != 0)
			words.Add("virtual");
		if ((modifiers & ApiMemberModifiers.Override) != 0)
			words.Add("override");
		if ((modifiers & ApiMemberModifiers.Sealed) != 0)
			words.Add("sealed");
		return "[" + string.Join(" ", words) + "]";
	}

	private static string TypeParameters(IReadOnlyList<ApiTypeParameter> parameters) =>
		parameters.Count == 0
			? ""
			: "<" + string.Join(", ", parameters.Select(p =>
			{
				var words = new List<string>();
				if (p.Variance != ApiVariance.None)
					words.Add(p.Variance.ToString().ToLowerInvariant());
				words.Add(p.Name);
				var c = p.Constraints;
				if ((c & ApiConstraints.Class) != 0)
					words.Add("class");
				if ((c & ApiConstraints.NullableClass) != 0)
					words.Add("class?");
				if ((c & ApiConstraints.Struct) != 0)
					words.Add("struct");
				if ((c & ApiConstraints.Unmanaged) != 0)
					words.Add("unmanaged");
				if ((c & ApiConstraints.NotNull) != 0)
					words.Add("notnull");
				if ((c & ApiConstraints.New) != 0)
					words.Add("new()");
				if ((c & ApiConstraints.AllowsRefStruct) != 0)
					words.Add("allows-ref-struct");
				words.AddRange(p.ConstraintTypes.Select(Render).Order(StringComparer.Ordinal));
				return string.Join(" ", words);
			})) + ">";

	private static string Key(IEnumerable<ApiTypeReference> types, IEnumerable<ApiRefKind>? refKinds = null) =>
		"(" + string.Join(",", types.Zip(refKinds ?? types.Select(_ => ApiRefKind.None), (t, r) => (r == ApiRefKind.None ? "" : "&") + Erase(t))) + ")";

	private static string RefKind(ApiRefKind refKind) => refKind switch
	{
		ApiRefKind.Ref => "ref ",
		ApiRefKind.Out => "out ",
		ApiRefKind.In => "in ",
		ApiRefKind.RefReadOnly => "ref readonly ",
		_ => "",
	};

	/// <summary>A type without nullability, tuple names or dynamic, which tells overloads apart.</summary>
	private static string Erase(ApiTypeReference type) => type switch
	{
		ApiNamedType named =>
			(named.DeclaringType is null ? named.FullName : Erase(named.DeclaringType) + "/" + named.Name) +
				(named.TypeArguments.Count == 0 ? "" : "<" + string.Join(",", named.TypeArguments.Select(Erase)) + ">"),
		ApiTupleType tuple => ErasedTuple(tuple.Elements.Select(e => Erase(e.Type)).ToList()),
		ApiArrayType array => Erase(array.ElementType) + "[" + new string(',', array.Rank - 1) + "]",
		ApiPointerType pointer => Erase(pointer.ElementType) + "*",
		ApiFunctionPointerType => "fnptr",
		ApiTypeParameterReference parameter => parameter.Name,
		ApiDynamicType => "System.Object",
		ApiNativeIntegerType native => native.IsUnsigned ? "System.UIntPtr" : "System.IntPtr",
		ApiModifiedType modified => Erase(modified.Type),
		_ => type.ToString(),
	};

	/// <summary>A tuple as its ValueTuple: the eighth argument of a long one holds the rest.</summary>
	private static string ErasedTuple(List<string> elements) =>
		elements.Count <= 7
			? "System.ValueTuple`" + elements.Count + "<" + string.Join(",", elements) + ">"
			: "System.ValueTuple`8<" + string.Join(",", elements.Take(7)) + "," + ErasedTuple(elements.Skip(7).ToList()) + ">";

	private static string Render(ApiTypeReference type) => type switch
	{
		ApiNamedType { IsNullableValueType: true } nullable => Render(nullable.TypeArguments[0]) + "?",
		ApiNamedType named =>
			(named.DeclaringType is null ? named.FullName : Render(named.DeclaringType) + "/" + named.Name) +
				(named.TypeArguments.Count == 0 ? "" : "<" + string.Join(",", named.TypeArguments.Select(Render)) + ">") + (named.IsNullable ? "?" : ""),
		ApiTupleType tuple =>
			"(" + string.Join(",", tuple.Elements.Select(e => Render(e.Type) + (e.Name is null ? "" : " " + e.Name))) + ")" + (tuple.IsNullable ? "?" : ""),
		ApiArrayType array => Render(array.ElementType) + "[" + new string(',', array.Rank - 1) + "]" + (array.IsNullable ? "?" : ""),
		ApiPointerType pointer => Render(pointer.ElementType) + "*",
		ApiFunctionPointerType => "fnptr",
		ApiTypeParameterReference parameter => parameter.Name + (parameter.IsNullable ? "?" : ""),
		ApiDynamicType dynamic => "dynamic" + (dynamic.IsNullable ? "?" : ""),
		ApiNativeIntegerType native => native.IsUnsigned ? "System.UIntPtr" : "System.IntPtr",
		ApiModifiedType modified => Render(modified.Type),
		_ => type.ToString(),
	};

	// Roslyn's side

	private static Dictionary<string, string> Theirs(byte[] image, string name)
	{
		var reference = MetadataReference.CreateFromImage(image);
		var compilation = CSharpCompilation.Create(
			"Oracle",
			[],
			[reference, .. Compiler.References.Where(r => Path.GetFileNameWithoutExtension(r.Display) != name)],
			new CSharpCompilationOptions(OutputKind.DynamicallyLinkedLibrary, nullableContextOptions: NullableContextOptions.Enable)
		);
		var assembly = (IAssemblySymbol)compilation.GetAssemblyOrModuleSymbol(reference)!;
		var result = new Dictionary<string, string>(StringComparer.Ordinal);
		foreach (var type in Types(assembly.GlobalNamespace))
			Theirs(type, null, result);
		return result;
	}

	private static IEnumerable<INamedTypeSymbol> Types(INamespaceSymbol ns)
	{
		foreach (var type in ns.GetTypeMembers())
			yield return type;
		foreach (var child in ns.GetNamespaceMembers())
			foreach (var type in Types(child))
				yield return type;
	}

	private static bool Visible(ISymbol symbol, bool extendable) =>
		symbol.DeclaredAccessibility == Accessibility.Public ||
			(extendable && symbol.DeclaredAccessibility is Accessibility.Protected or Accessibility.ProtectedOrInternal);

	private static bool Extendable(INamedTypeSymbol type) =>
		type.TypeKind == TypeKind.Interface ||
			(type.TypeKind == TypeKind.Class && !type.IsSealed && !type.IsStatic && type.InstanceConstructors.Any(
				c => c.DeclaredAccessibility is Accessibility.Public or Accessibility.Protected or Accessibility.ProtectedOrInternal
			));

	private static void Theirs(INamedTypeSymbol type, INamedTypeSymbol? declaring, Dictionary<string, string> result)
	{
		if (type.Name.StartsWith('<') || type.MetadataName.StartsWith('<'))
			return;
		if (declaring is null ? type.DeclaredAccessibility != Accessibility.Public : !Visible(type, Extendable(declaring)))
			return;
		var name = MetadataName(type);
		var bases = new List<string>();
		if (type.TypeKind == TypeKind.Class && type.BaseType is { SpecialType: not SpecialType.System_Object } baseType)
			bases.Add(Render(baseType));
		bases.AddRange(type.Interfaces.Where(PublicAllTheWay).Select(Render).Order(StringComparer.Ordinal));
		var typeFacts = new List<string> { Access(type.DeclaredAccessibility), Kind(type) };
		if (type.TypeKind == TypeKind.Class)
		{
			if (type.IsStatic)
				typeFacts.Add("static");
			else if (type.IsAbstract)
				typeFacts.Add("abstract");
			else if (type.IsSealed)
				typeFacts.Add("sealed");
			if (type.IsRecord)
				typeFacts.Add("record");
		}
		if (type.TypeKind == TypeKind.Struct && type.IsReadOnly)
			typeFacts.Add("readonly");
		if (type.IsRefLikeType)
			typeFacts.Add("ref");
		result[name] = string.Join(" ", typeFacts) + TypeParameters(type.TypeParameters) + " : " + string.Join(", ", bases);
		if (type.TypeKind is TypeKind.Enum or TypeKind.Delegate)
			return;
		var extendable = Extendable(type);
		foreach (var member in type.GetMembers())
		{
			if (!Visible(member, extendable))
				continue;
			switch (member)
			{
				case IFieldSymbol field:
					{
						var facts = new List<string> { Access(field.DeclaredAccessibility) };
						if (field.IsConst)
							facts.Add("const");
						else
						{
							if (field.IsStatic)
								facts.Add("static");
							if (field.IsReadOnly)
								facts.Add("readonly");
							if (field.IsVolatile)
								facts.Add("volatile");
						}
						if (field.IsRequired)
							facts.Add("required");
						result[name + "::" + field.MetadataName] = string.Join(" ", facts) + " " + Render(field.Type, field.NullableAnnotation);
						break;
					}
				case IPropertySymbol property when (property.GetMethod is { } g && Visible(g, extendable)) ||
					(property.SetMethod is { } s && Visible(s, extendable)):
					{
						var get = property.GetMethod is { } getter && Visible(getter, extendable) ? getter : null;
						var set = property.SetMethod is { } setter && Visible(setter, extendable) ? setter : null;
						var most = new[] { get, set }.Where(m => m is not null).Select(m => m!.DeclaredAccessibility).Max();
						var facts = new List<string> { Access(most), Modifiers(property) };
						if (property.IsRequired)
							facts.Add("required");
						if (get is not null)
							facts.Add("get:" + Access(get.DeclaredAccessibility));
						if (set is not null)
							facts.Add((set.IsInitOnly ? "init:" : "set:") + Access(set.DeclaredAccessibility));
						result[name + "::" + property.MetadataName + Key(property.Parameters)] = string.Join(" ", facts) + " " +
							(property.RefKind == Microsoft.CodeAnalysis.RefKind.RefReadOnly ? "ref readonly " : RefKind(property.RefKind)) + Render(
							property.Type,
							property.NullableAnnotation
						) + Parameters(property.Parameters);
						break;
					}
				case IEventSymbol @event:
					result[name + "::" + @event.MetadataName] = Access(@event.DeclaredAccessibility) + " " + Modifiers(@event) + " " + Render(
						@event.Type,
						@event.NullableAnnotation
					);
					break;
				// a struct's parameterless constructor is not in metadata unless the struct declares one
				case IMethodSymbol { IsImplicitlyDeclared: true, MethodKind: MethodKind.Constructor, ContainingType.IsValueType: true }:
					break;
				case IMethodSymbol method when method.MethodKind is not (MethodKind.PropertyGet or MethodKind.PropertySet or MethodKind.EventAdd or MethodKind.EventRemove or MethodKind.StaticConstructor):
					result[name + "::" + method.MetadataName + "`" + method.TypeParameters.Length + Key(method.Parameters)] =
					// Roslyn shows a finalizer as a destructor, which is never virtual; metadata says it is
					Access(method.DeclaredAccessibility) + " " + (method.MethodKind == MethodKind.Destructor ? "[finalizer]" : Modifiers(method)) +
						(method.IsExtensionMethod ? " extension" : "") + TypeParameters(method.TypeParameters) + " " +
						(method.RefKind == Microsoft.CodeAnalysis.RefKind.RefReadOnly ? "ref readonly " : RefKind(method.RefKind)) + Render(
						method.ReturnType,
						method.ReturnNullableAnnotation
					) + Parameters(method.Parameters);
					break;
			}
		}
		foreach (var nested in type.GetTypeMembers())
			Theirs(nested, type, result);
	}

	private static bool PublicAllTheWay(INamedTypeSymbol type) =>
		type.DeclaredAccessibility == Accessibility.Public && (type.ContainingType is not { } containing || PublicAllTheWay(containing));

	private static string MetadataName(INamedTypeSymbol type) =>
		type.ContainingType is { } containing
			? MetadataName(containing) + "/" + type.MetadataName
			: type.ContainingNamespace.IsGlobalNamespace ? type.MetadataName : type.ContainingNamespace.ToDisplayString() + "." + type.MetadataName;

	private static string Parameters(ImmutableArray<IParameterSymbol> parameters) =>
		"(" + string.Join(
			", ",
			parameters.Select(
				p => (p.IsParams ? "params " : "") +
					(p.ScopedKind != ScopedKind.None && p.RefKind != Microsoft.CodeAnalysis.RefKind.Out && !p.IsThis ? "scoped " : "") + RefKind(
					p.RefKind
				) + Render(p.Type, p.NullableAnnotation) + " " + p.MetadataName + (p.IsOptional ? " = ?" : "")
			)
		) + ")";

	private static string Kind(INamedTypeSymbol type) => type.TypeKind.ToString().ToLowerInvariant();

	private static string Access(Accessibility accessibility) => accessibility switch
	{
		Accessibility.Public => "public",
		Accessibility.Protected => "protected",
		_ => "protected-internal"
	};

	private static string Modifiers(ISymbol symbol)
	{
		var words = new List<string>();
		if (symbol.IsStatic)
			words.Add("static");
		if (symbol.IsAbstract)
			words.Add("abstract");
		if (symbol.IsVirtual)
			words.Add("virtual");
		if (symbol.IsOverride)
			words.Add("override");
		if (symbol.IsSealed && symbol.IsOverride)
			words.Add("sealed");
		return "[" + string.Join(" ", words) + "]";
	}

	private static string TypeParameters(ImmutableArray<ITypeParameterSymbol> parameters) =>
		parameters.Length == 0
			? ""
			: "<" + string.Join(", ", parameters.Select(p =>
			{
				var words = new List<string>();
				if (p.Variance != VarianceKind.None)
					words.Add(p.Variance.ToString().ToLowerInvariant());
				words.Add(p.Name);
				if (p.HasReferenceTypeConstraint)
					words.Add(p.ReferenceTypeConstraintNullableAnnotation == NullableAnnotation.Annotated ? "class?" : "class");
				if (p.HasUnmanagedTypeConstraint)
					words.Add("unmanaged");
				else if (p.HasValueTypeConstraint)
					words.Add("struct");
				if (p.HasNotNullConstraint)
					words.Add("notnull");
				if (p.HasConstructorConstraint && !p.HasValueTypeConstraint)
					words.Add("new()");
				if (p.AllowsRefLikeType)
					words.Add("allows-ref-struct");
				words.AddRange(p.ConstraintTypes.Zip(p.ConstraintNullableAnnotations, (t, a) => Render(t, a)).Order(StringComparer.Ordinal));
				return string.Join(" ", words);
			})) + ">";

	private static string Key(ImmutableArray<IParameterSymbol> parameters) =>
		"(" + string.Join(",", parameters.Select(p => (p.RefKind == Microsoft.CodeAnalysis.RefKind.None ? "" : "&") + Erase(p.Type))) + ")";

	private static string RefKind(RefKind refKind) => refKind switch
	{
		Microsoft.CodeAnalysis.RefKind.Ref => "ref ",
		Microsoft.CodeAnalysis.RefKind.Out => "out ",
		Microsoft.CodeAnalysis.RefKind.In => "in ",
		Microsoft.CodeAnalysis.RefKind.RefReadOnlyParameter => "ref readonly ",
		_ => "",
	};

	private static string Erase(ITypeSymbol type) => type switch
	{
		INamedTypeSymbol named =>
			(named.ContainingType is { } containing ? Erase(containing) + "/" + named.MetadataName : Qualified(named)) +
				(named.TypeArguments.Length == 0 ? "" : "<" + string.Join(",", named.TypeArguments.Select(Erase)) + ">"),
		IArrayTypeSymbol array => Erase(array.ElementType) + "[" + new string(',', array.Rank - 1) + "]",
		IPointerTypeSymbol pointer => Erase(pointer.PointedAtType) + "*",
		IFunctionPointerTypeSymbol => "fnptr",
		IDynamicTypeSymbol => "System.Object",
		ITypeParameterSymbol parameter => parameter.Name,
		_ => type.ToDisplayString(),
	};

	private static string Qualified(INamedTypeSymbol type)
	{
		var original = type.IsTupleType && type.TupleUnderlyingType is { } underlying ? underlying : type;
		if (original.IsNativeIntegerType)
			return original.SpecialType == SpecialType.System_UIntPtr ? "System.UIntPtr" : "System.IntPtr";
		return original.ContainingNamespace.IsGlobalNamespace
			? original.MetadataName
			: original.ContainingNamespace.ToDisplayString() + "." + original.MetadataName;
	}

	private static string Render(ITypeSymbol type) => Render(type, type.NullableAnnotation);

	private static string Render(ITypeSymbol type, NullableAnnotation annotation)
	{
		var mark = annotation == NullableAnnotation.Annotated && !type.IsValueType ? "?" : "";
		switch (type)
		{
			case INamedTypeSymbol { OriginalDefinition.SpecialType: SpecialType.System_Nullable_T } nullable:
				return Render(nullable.TypeArguments[0], nullable.TypeArgumentNullableAnnotations[0]) + "?";
			// Roslyn calls the non-generic ValueTuple a tuple of none
			case INamedTypeSymbol { IsTupleType: true, TupleElements.Length: 0 }:
				return "System.ValueTuple";
			case INamedTypeSymbol { IsTupleType: true } tuple:
				return "(" + string.Join(
					",",
					tuple.TupleElements.Select(e => Render(e.Type, e.NullableAnnotation) + (e.IsExplicitlyNamedTupleElement ? " " + e.Name : ""))
				) + ")";
			// the reader keeps IntPtr where no attribute says nint; since C# 11 they are the same type
			case INamedTypeSymbol { IsNativeIntegerType: true } native:
				return native.SpecialType == SpecialType.System_UIntPtr ? "System.UIntPtr" : "System.IntPtr";
			case INamedTypeSymbol named:
				{
					var builder = new StringBuilder(
						named.ContainingType is { } containing ? Render(containing, NullableAnnotation.None) + "/" + named.MetadataName : Qualified(named)
					);
					if (named.TypeArguments.Length > 0)
						builder
							.Append('<')
							.Append(string.Join(",", named.TypeArguments.Zip(named.TypeArgumentNullableAnnotations, (t, a) => Render(t, a))))
							.Append('>');
					return builder.Append(mark).ToString();
				}
			case IArrayTypeSymbol array:
				return Render(array.ElementType, array.ElementNullableAnnotation) + "[" + new string(',', array.Rank - 1) + "]" + mark;
			case IPointerTypeSymbol pointer:
				return Render(pointer.PointedAtType, NullableAnnotation.None) + "*";
			case IFunctionPointerTypeSymbol:
				return "fnptr";
			case IDynamicTypeSymbol:
				return "dynamic" + mark;
			case ITypeParameterSymbol parameter:
				return parameter.Name + (annotation == NullableAnnotation.Annotated ? "?" : "");
			default:
				return type.ToDisplayString();
		}
	}
}
