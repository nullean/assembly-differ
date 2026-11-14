#nullable enable
using System;
using System.Collections.Generic;
using System.Linq;
using System.Runtime.CompilerServices;

namespace Nullean.PublicApi;

/// <summary>A <c>.api</c> file that could not be read, with where in it.</summary>
public sealed class ApiParseException : FormatException
{
	/// <summary>A failure at <paramref name="line"/> and <paramref name="column"/>, both counted from one.</summary>
	public ApiParseException(string message, int line, int column) : base($"{message} ({line}:{column})")
	{
		Line = line;
		Column = column;
	}

	/// <summary>The line, counted from one.</summary>
	public int Line { get; }

	/// <summary>The column, counted from one.</summary>
	public int Column { get; }
}

/// <summary>Where a type or member is declared in a <c>.api</c> file.</summary>
/// <param name="File">The file, as it was given to the parser; null for text parsed without a name.</param>
/// <param name="Line">The line, counted from one.</param>
public sealed record ApiLocation(string? File, int Line);

/// <summary>Parsed <c>.api</c> files: the API they describe, and where each type and member is declared.</summary>
public sealed class ApiDocument
{
	private readonly ConditionalWeakTable<object, ApiLocation> _locations;

	internal ApiDocument(ApiAssembly assembly, ConditionalWeakTable<object, ApiLocation> locations)
	{
		Assembly = assembly;
		_locations = locations;
	}

	/// <summary>The API the files describe. Its version, public key token and target framework are not in them and are left empty.</summary>
	public ApiAssembly Assembly { get; }

	/// <summary>Where a type or member was declared; null for anything else.</summary>
	public ApiLocation? LocationOf(object node) => _locations.TryGetValue(node, out var location) ? location : null;

	/// <summary>The line a type or member was declared on, counted from one; null for anything else.</summary>
	public int? LineOf(object node) => LocationOf(node)?.Line;
}

/// <summary>Reads a <c>.api</c> file, as <see cref="ApiWriter"/> writes one, back into an <see cref="ApiAssembly"/>.</summary>
public static class ApiParser
{
	/// <summary>Parses <paramref name="text"/>.</summary>
	/// <exception cref="ApiParseException">The text is not a <c>.api</c> file.</exception>
	public static ApiDocument Parse(string text) => Parse([(null, text)]);

	/// <summary>
	/// Parses the files one assembly's API was split into, as <see cref="ApiWriter.Write(ApiAssembly, ApiSplit)"/> writes
	/// them, into one API.
	/// </summary>
	/// <exception cref="ApiParseException">A file is not a <c>.api</c> file, or the files are of different assemblies.</exception>
	public static ApiDocument Parse(IEnumerable<(string? File, string Text)> files)
	{
		var locations = new ConditionalWeakTable<object, ApiLocation>();
		string? name = null;
		var types = new List<ApiType>();
		foreach (var (file, text) in files)
		{
			var assembly = new Parser(text, file, locations).Parse();
			if (name is not null && assembly.Name != name)
				throw new ApiParseException($"{file} is of {assembly.Name}, the others of {name}", 1, 1);
			name = assembly.Name;
			types.AddRange(assembly.Types);
		}
		if (name is null)
			throw new ApiParseException("no .api file to read", 1, 1);
		types = types.OrderBy(t => t.Namespace, StringComparer.Ordinal).ThenBy(t => t.Name, StringComparer.Ordinal).ToList();
		return new ApiDocument(new ApiAssembly(name, new Version(0, 0, 0, 0), null, null, types), locations);
	}

	private sealed record Line(int Number, int Depth, string Text);

	/// <summary>A type declared in the file, before its members are read.</summary>
	private sealed class Declared(string ns, string name, Declared? parent, ApiTypeKind kind, IReadOnlyList<string> typeParameters)
	{
		public string Namespace { get; } = ns;
		public string Name { get; } = name;
		public Declared? Parent { get; } = parent;
		public ApiTypeKind Kind { get; } = kind;
		public IReadOnlyList<string> TypeParameters { get; } = typeParameters;
		public HashSet<string> StructParameters { get; } = [];
		public Dictionary<string, Declared> Nested { get; } = new(StringComparer.Ordinal);
		public bool IsValueType => Kind is ApiTypeKind.Struct or ApiTypeKind.Enum;
	}

	private sealed class Parser
	{
		private readonly List<Line> _lines = [];
		private readonly string? _file;
		private readonly ConditionalWeakTable<object, ApiLocation> _locations;

		// (namespace, metadata name) of every top-level type the file declares or its header names
		private readonly Dictionary<(string, string), Declared> _own = [];
		private readonly HashSet<(string Namespace, string Name)> _external = [];
		private readonly HashSet<(string Namespace, string Path)> _externalValueTypes = [];
		private readonly HashSet<(string Namespace, string Path)> _externalInterfaces = [];
		private readonly Dictionary<string, List<string>> _byShortName = new(StringComparer.Ordinal);

		public Parser(string text, string? file, ConditionalWeakTable<object, ApiLocation> locations)
		{
			_file = file;
			_locations = locations;
			var number = 0;
			foreach (var raw in text.Split('\n'))
			{
				number++;
				var line = raw.TrimEnd('\r');
				if (line.Trim().Length == 0)
					continue;
				var spaces = 0;
				while (spaces < line.Length && line[spaces] == ' ')
					spaces++;
				if (spaces % 2 != 0)
					throw new ApiParseException("indentation is two spaces a level", number, spaces + 1);
				_lines.Add(new Line(number, spaces / 2, line.Substring(spaces)));
			}
		}

		public ApiAssembly Parse()
		{
			var index = 0;
			if (_lines.Count == 0 || !_lines[0].Text.StartsWith("assembly ", StringComparison.Ordinal))
				throw new ApiParseException("a .api file starts with 'assembly <name>'", _lines.Count == 0 ? 1 : _lines[0].Number, 1);
			var name = _lines[0].Text.Substring("assembly ".Length).Trim();
			index++;
			for (; index < _lines.Count && _lines[index].Text.StartsWith("using ", StringComparison.Ordinal); index++)
				Header(_lines[index]);

			Declare(index);

			var types = new List<ApiType>();
			string? ns = null;
			for (var i = index; i < _lines.Count;)
			{
				var line = _lines[i];
				if (line.Depth == 0)
				{
					if (!line.Text.StartsWith("namespace ", StringComparison.Ordinal))
						throw new ApiParseException("expected 'namespace <name>'", line.Number, 1);
					ns = Namespace(line.Text.Substring("namespace ".Length).Trim());
					i++;
					continue;
				}
				if (ns is null)
					throw new ApiParseException("a type outside a namespace", line.Number, 1);
				types.Add(Type(ref i, 1, ns, null));
			}
			return new ApiAssembly(name, new Version(0, 0, 0, 0), null, null, types);
		}

		private static string Namespace(string label) => label == "global" ? string.Empty : label;

		private void Header(Line line)
		{
			var text = line.Text.Substring("using ".Length);
			var isStruct = text.StartsWith("struct ", StringComparison.Ordinal);
			var isInterface = text.StartsWith("interface ", StringComparison.Ordinal);
			if (isStruct)
				text = text.Substring("struct ".Length);
			if (isInterface)
				text = text.Substring("interface ".Length);
			var colon = text.IndexOf(':');
			if (colon < 0)
				throw new ApiParseException("expected 'using <namespace>: <types>'", line.Number, 1);
			var ns = Namespace(text.Substring(0, colon).Trim());
			foreach (var entry in SplitTopLevel(text.Substring(colon + 1)))
			{
				var path = string.Join("/", SplitDots(entry.Trim()).Select(Metadata));
				var top = path.Split('/')[0];
				Known(ns, top);
				_external.Add((ns, top));
				if (isStruct)
					_externalValueTypes.Add((ns, path));
				if (isInterface)
					_externalInterfaces.Add((ns, path));
			}
		}

		/// <summary><c>Dictionary&lt;,&gt;</c> as <c>Dictionary`2</c>.</summary>
		private static string Metadata(string unbound)
		{
			var open = unbound.IndexOf('<');
			return open < 0 ? unbound : unbound.Substring(0, open) + "`" + (unbound.Count(c => c == ',') + 1);
		}

		private void Known(string ns, string metadataName)
		{
			if (!_byShortName.TryGetValue(metadataName, out var namespaces))
				_byShortName[metadataName] = namespaces = [];
			if (!namespaces.Contains(ns))
				namespaces.Add(ns);
		}

		/// <summary>The first pass: every type the file declares, so a member can name one declared further down.</summary>
		private void Declare(int start)
		{
			string? ns = null;
			var stack = new List<Declared>();
			foreach (var line in _lines.Skip(start))
			{
				if (line.Depth == 0)
				{
					ns = line.Text.StartsWith("namespace ", StringComparison.Ordinal) ? Namespace(line.Text.Substring("namespace ".Length).Trim()) : ns;
					stack.Clear();
					continue;
				}
				if (ns is null || !IsTypeLine(line.Text, out var kind, out var typeName, out var typeParameters))
					continue;
				while (stack.Count >= line.Depth)
					stack.RemoveAt(stack.Count - 1);
				var parent = stack.Count > 0 ? stack[stack.Count - 1] : null;
				var metadataName = typeParameters.Count == 0 ? typeName : typeName + "`" + typeParameters.Count;
				var declared = new Declared(ns, metadataName, parent, kind, typeParameters);
				declared.StructParameters.UnionWith(StructParameters(line.Text));
				if (parent is null)
				{
					_own[(ns, metadataName)] = declared;
					Known(ns, metadataName);
				}
				else
					parent.Nested[metadataName] = declared;
				stack.Add(declared);
			}
		}

		private static readonly HashSet<string> TypeModifiers = ["public", "protected", "internal", "static", "abstract", "sealed", "readonly", "ref"];

		private static bool IsTypeLine(string text, out ApiTypeKind kind, out string name, out List<string> typeParameters)
		{
			kind = ApiTypeKind.Class;
			name = string.Empty;
			typeParameters = [];
			if (text.StartsWithChar('['))
				return false;
			var cursor = new Cursor(text, 0);
			while (true)
			{
				var word = cursor.PeekWord();
				if (!TypeModifiers.Contains(word))
					break;
				cursor.Word();
			}
			var keyword = cursor.PeekWord();
			switch (keyword)
			{
				case "class" or "record":
					kind = ApiTypeKind.Class;
					break;
				case "struct":
					kind = ApiTypeKind.Struct;
					break;
				case "interface":
					kind = ApiTypeKind.Interface;
					break;
				case "enum":
					kind = ApiTypeKind.Enum;
					break;
				// delegate* is a function pointer type, which a field or a parameter has
				case "delegate" when cursor.PeekAfterWord() != '*':
					kind = ApiTypeKind.Delegate;
					break;
				default:
					return false;
			}
			cursor.Word();
			if (kind == ApiTypeKind.Delegate)
			{
				// delegate [ref [readonly]] ReturnType Name<T>(...)
				var paren = TopLevelIndex(text, cursor.Position, '(');
				var head = text.Substring(cursor.Position, paren - cursor.Position).TrimEnd();
				var lt = head.EndsWithChar('>') ? MatchingOpen(head, head.Length - 1) : -1;
				var nameEnd = lt >= 0 ? lt : head.Length;
				var nameStart = head.LastIndexOf(' ', nameEnd - 1) + 1;
				name = head.Substring(nameStart, nameEnd - nameStart);
				if (lt >= 0)
					typeParameters = SplitTopLevel(head.Substring(lt + 1, head.Length - lt - 2)).Select(p => LastWord(p.Trim())).ToList();
				return true;
			}
			name = cursor.Identifier();
			if (cursor.Peek() == '<')
			{
				var close = Matching(text, cursor.Position);
				typeParameters = SplitTopLevel(text.Substring(cursor.Position + 1, close - cursor.Position - 1)).Select(p => LastWord(p.Trim())).ToList();
			}
			return true;
		}

		private static string LastWord(string text) => text.Substring(text.LastIndexOf(' ') + 1);

		/// <summary>
		/// The type parameters a line constrains to struct or unmanaged, read ahead of the declaration: <c>T?</c> is
		/// Nullable&lt;T&gt; for those, and the constraint comes after the types that use it.
		/// </summary>
		private static HashSet<string> StructParameters(string text)
		{
			var names = new HashSet<string>(StringComparer.Ordinal);
			var at = 0;
			while ((at = text.IndexOf(" where ", at, StringComparison.Ordinal)) >= 0)
			{
				at += " where ".Length;
				var colon = text.IndexOf(" : ", at, StringComparison.Ordinal);
				if (colon < 0)
					break;
				var name = text.Substring(at, colon - at);
				var first = text.Substring(colon + 3);
				if (first.StartsWith("struct", StringComparison.Ordinal) || first.StartsWith("unmanaged", StringComparison.Ordinal))
					names.Add(name);
			}
			return names;
		}

		// types

		private ApiType Type(ref int i, int depth, string ns, (ApiType Type, Declared Declared)? parent)
		{
			var attributes = new List<ApiAttribute>();
			var returnAttributes = new List<ApiAttribute>();
			var scope = new Scope(this, parent?.Declared, []);
			while (_lines[i].Text.StartsWithChar('['))
			{
				Attribute(_lines[i], scope, attributes, returnAttributes, out _);
				i++;
			}
			var line = _lines[i];
			if (line.Depth != depth || !IsTypeLine(line.Text, out var kind, out var name, out var typeParameterNames))
				throw new ApiParseException("expected a type declaration", line.Number, line.Depth * 2 + 1);
			var metadataName = typeParameterNames.Count == 0 ? name : name + "`" + typeParameterNames.Count;
			var declared = parent is null ? _own[(ns, metadataName)] : parent.Value.Declared.Nested[metadataName];
			scope = new Scope(this, declared, []);
			var cursor = new Cursor(line.Text, 0, line.Number, depth * 2);

			// public goes without saying; it is still read when written
			var accessibility = Accessibility(cursor) ?? ApiAccessibility.Public;
			var modifiers = ApiTypeModifiers.None;
			while (true)
			{
				var word = cursor.PeekWord();
				var modifier = word switch
				{
					"static" => ApiTypeModifiers.Static,
					"abstract" => ApiTypeModifiers.Abstract,
					"sealed" => ApiTypeModifiers.Sealed,
					"readonly" => ApiTypeModifiers.ReadOnly,
					"ref" => ApiTypeModifiers.Ref,
					_ => ApiTypeModifiers.None,
				};
				if (modifier == ApiTypeModifiers.None)
					break;
				cursor.Word();
				modifiers |= modifier;
			}
			if (cursor.Word() == "record")
				modifiers |= ApiTypeModifiers.Record;

			var members = new List<ApiMember>();
			List<ApiTypeParameter> typeParameters;
			ApiTypeReference? baseType = null;
			var interfaces = new List<ApiTypeReference>();
			if (kind == ApiTypeKind.Delegate)
			{
				var returnRefKind = RefKind(cursor);
				var returnType = scope.Type(cursor);
				cursor.Identifier();
				typeParameters = TypeParameters(cursor, true);
				var parameters = Parameters(cursor, scope, '(', ')', out _);
				Constraints(cursor, scope, typeParameters);
				cursor.End();
				members.Add(
					new ApiMethod(
						"Invoke",
						ApiAccessibility.Public,
						[],
						ApiMethodKind.Method,
						ApiMemberModifiers.Virtual,
						returnType,
						returnRefKind,
						returnAttributes,
						[],
						parameters,
						false
					)
				);
				i++;
			}
			else
			{
				cursor.Identifier();
				typeParameters = TypeParameters(cursor, true);
				if (cursor.TryChar(':'))
				{
					do
					{
						var reference = scope.Type(cursor);
						if (kind is ApiTypeKind.Class && baseType is null && interfaces.Count == 0 && !IsInterface(reference))
							baseType = reference;
						else if (kind is ApiTypeKind.Enum)
							baseType = reference;
						else
							interfaces.Add(reference);
					}
					while (cursor.TryChar(','));
				}
				Constraints(cursor, scope, typeParameters);
				cursor.End();
				i++;
			}

			var shell = new ApiType(ns, metadataName, kind, accessibility, modifiers, typeParameters, baseType, interfaces, attributes, [], [], false);
			var nested = new List<ApiType>();
			var underlying = kind == ApiTypeKind.Enum ? baseType ?? TypeReferenceProvider.Primitive(System.Reflection.Metadata.PrimitiveTypeCode.Int32) : null;
			while (i < _lines.Count && _lines[i].Depth > depth)
			{
				var member = _lines[i];
				if (member.Depth != depth + 1)
					throw new ApiParseException("indented too far", member.Number, member.Depth * 2 + 1);
				if (StartsType(i))
				{
					nested.Add(Type(ref i, depth + 1, ns, (shell, declared)));
					continue;
				}
				members.Add(Member(ref i, depth + 1, shell, declared, underlying));
			}

			var canBeExtended = kind == ApiTypeKind.Interface ||
				(kind == ApiTypeKind.Class && (modifiers & (ApiTypeModifiers.Sealed | ApiTypeModifiers.Static)) == 0 && members.Any(
					m => m is ApiMethod { Kind: ApiMethodKind.Constructor }
				));
			var type = shell with { Members = members, NestedTypes = nested, CanBeExtendedOutside = canBeExtended };
			_locations.Add(type, new ApiLocation(_file, line.Number));
			return type;
		}

		/// <summary>Whether a base list entry is an interface: the file declares it as one, or the header lists it as one.</summary>
		private bool IsInterface(ApiTypeReference reference) =>
			reference is ApiNamedType named &&
				(Find(named) is { } declared ? declared.Kind == ApiTypeKind.Interface : _externalInterfaces.Contains((named.Namespace, Path(named))));

		private static string Path(ApiNamedType named) => named.DeclaringType is null ? named.Name : Path(named.DeclaringType) + "/" + named.Name;

		private Declared? Find(ApiNamedType named)
		{
			if (named.DeclaringType is null)
				return _own.TryGetValue((named.Namespace, named.Name), out var top) ? top : null;
			return Find(named.DeclaringType) is { } parent && parent.Nested.TryGetValue(named.Name, out var nested) ? nested : null;
		}

		/// <summary>Whether the lines from <paramref name="i"/>, past any attributes, declare a type.</summary>
		private bool StartsType(int i)
		{
			var depth = _lines[i].Depth;
			while (i < _lines.Count && _lines[i].Depth == depth && _lines[i].Text.StartsWithChar('['))
				i++;
			return i < _lines.Count && _lines[i].Depth == depth && IsTypeLine(_lines[i].Text, out _, out _, out _);
		}

		private static ApiAccessibility? Accessibility(Cursor cursor)
		{
			switch (cursor.PeekWord())
			{
				case "public":
					cursor.Word();
					return ApiAccessibility.Public;
				case "protected":
					cursor.Word();
					if (cursor.PeekWord() == "internal")
					{
						cursor.Word();
						return ApiAccessibility.ProtectedInternal;
					}
					return ApiAccessibility.Protected;
				default:
					return null;
			}
		}

		private static List<ApiTypeParameter> TypeParameters(Cursor cursor, bool hasVariance)
		{
			var parameters = new List<ApiTypeParameter>();
			if (!cursor.TryChar('<'))
				return parameters;
			do
			{
				var variance = ApiVariance.None;
				if (hasVariance && cursor.PeekWord() is "out" or "in")
					variance = cursor.Word() == "out" ? ApiVariance.Out : ApiVariance.In;
				parameters.Add(new ApiTypeParameter(cursor.Identifier(), variance, ApiConstraints.None, []));
			}
			while (cursor.TryChar(','));
			cursor.Expect('>');
			return parameters;
		}

		private static void Constraints(Cursor cursor, Scope scope, List<ApiTypeParameter> parameters, bool isMethod = false)
		{
			while (cursor.PeekWord() == "where")
			{
				cursor.Word();
				var name = cursor.Identifier();
				var index = parameters.FindIndex(p => p.Name == name);
				if (index < 0)
					throw cursor.Error($"no type parameter {name}");
				cursor.Expect(':');
				var constraints = ApiConstraints.None;
				var types = new List<ApiTypeReference>();
				// a type's own type parameters are in its scope already; a method's are added to it
				var withConstraints = isMethod ? scope.With(parameters) : scope;
				do
				{
					switch (cursor.PeekWord())
					{
						case "class":
							cursor.Word();
							constraints |= cursor.TryChar('?') ? ApiConstraints.NullableClass : ApiConstraints.Class;
							break;
						case "struct":
							cursor.Word();
							constraints |= ApiConstraints.Struct;
							break;
						case "unmanaged":
							cursor.Word();
							constraints |= ApiConstraints.Unmanaged;
							break;
						case "notnull":
							cursor.Word();
							constraints |= ApiConstraints.NotNull;
							break;
						case "new":
							cursor.Word();
							cursor.Expect('(');
							cursor.Expect(')');
							constraints |= ApiConstraints.New;
							break;
						case "allows":
							cursor.Word();
							cursor.ExpectWord("ref");
							cursor.ExpectWord("struct");
							constraints |= ApiConstraints.AllowsRefStruct;
							break;
						default:
							types.Add(withConstraints.Type(cursor));
							break;
					}
				}
				while (cursor.TryChar(','));
				parameters[index] = parameters[index] with { Constraints = constraints, ConstraintTypes = types };
			}
		}

		// members

		private ApiMember Member(ref int i, int depth, ApiType type, Declared declared, ApiTypeReference? enumType)
		{
			var attributes = new List<ApiAttribute>();
			var returnAttributes = new List<ApiAttribute>();
			string? indexerName = null;
			var scope = new Scope(this, declared, []);
			while (_lines[i].Text.StartsWithChar('['))
			{
				Attribute(_lines[i], scope, attributes, returnAttributes, out var renamed);
				indexerName ??= renamed;
				i++;
			}
			var line = _lines[i];
			if (line.Depth != depth)
				throw new ApiParseException("an attribute without a declaration", line.Number, 1);
			i++;
			var cursor = new Cursor(line.Text, 0, line.Number, depth * 2);
			var member = type.Kind == ApiTypeKind.Enum
				? EnumValue(cursor, attributes, enumType!)
				: Declaration(cursor, type, scope, attributes, returnAttributes, indexerName);
			_locations.Add(member, new ApiLocation(_file, line.Number));
			return member;
		}

		private static ApiField EnumValue(Cursor cursor, List<ApiAttribute> attributes, ApiTypeReference type)
		{
			var name = cursor.Identifier();
			cursor.Expect('=');
			var value = cursor.Rest();
			return new ApiField(name, ApiAccessibility.Public, attributes, type, true, false, false, false, value);
		}

		private static ApiMember Declaration(
			Cursor cursor,
			ApiType type,
			Scope scope,
			List<ApiAttribute> attributes,
			List<ApiAttribute> returnAttributes,
			string? indexerName
		)
		{
			var inInterface = type.Kind == ApiTypeKind.Interface;
			var access = Accessibility(cursor) ?? ApiAccessibility.Public;

			var modifiers = ApiMemberModifiers.None;
			bool isConst = false, isReadOnly = false, isVolatile = false, isRequired = false, isSealedInInterface = false;
			while (true)
			{
				var word = cursor.PeekWord();
				switch (word)
				{
					case "static":
						modifiers |= ApiMemberModifiers.Static;
						break;
					case "abstract":
						modifiers |= ApiMemberModifiers.Abstract;
						break;
					case "virtual":
						modifiers |= ApiMemberModifiers.Virtual;
						break;
					case "override":
						modifiers |= ApiMemberModifiers.Override;
						break;
					case "sealed" when inInterface:
						isSealedInInterface = true;
						break;
					case "sealed":
						modifiers |= ApiMemberModifiers.Sealed;
						break;
					case "readonly":
						isReadOnly = true;
						break;
					case "const":
						isConst = true;
						break;
					case "volatile":
						isVolatile = true;
						break;
					case "required":
						isRequired = true;
						break;
					default:
						goto done;
				}
				cursor.Word();
			}
		done:
			// readonly is a field's, or a struct member's that does not change the struct
			var memberModifiers = isReadOnly ? modifiers | ApiMemberModifiers.ReadOnly : modifiers;

			if (cursor.PeekWord() == "event")
			{
				cursor.Word();
				var eventType = scope.Type(cursor);
				var eventName = cursor.MemberName();
				var hasBody = Body(cursor);
				cursor.End();
				return new ApiEvent(eventName, access, attributes, InterfaceModifiers(memberModifiers, inInterface, hasBody, isSealedInInterface), eventType);
			}

			if (cursor.PeekWord() is "implicit" or "explicit")
			{
				var conversion = cursor.Word() == "implicit" ? "op_Implicit" : "op_Explicit";
				cursor.ExpectWord("operator");
				var conversionType = scope.Type(cursor);
				var conversionParameters = Parameters(cursor, scope, '(', ')', out _);
				var conversionBody = Body(cursor);
				cursor.End();
				return new ApiMethod(
					conversion,
					access,
					attributes,
					ApiMethodKind.Conversion,
					InterfaceModifiers(memberModifiers, inInterface, conversionBody, isSealedInInterface),
					conversionType,
					ApiRefKind.None,
					returnAttributes,
					[],
					conversionParameters,
					false
				);
			}

			// a constructor is the type's name and its parameters
			var typeName = ApiWriter.NonGeneric(type.Name);
			if (cursor.PeekWord() == typeName && cursor.PeekAfterWord() == '(')
			{
				cursor.Word();
				var constructorParameters = Parameters(cursor, scope, '(', ')', out _);
				cursor.End();
				return new ApiMethod(
					".ctor",
					access,
					attributes,
					ApiMethodKind.Constructor,
					modifiers,
					TypeReferenceProvider.Primitive(System.Reflection.Metadata.PrimitiveTypeCode.Void),
					ApiRefKind.None,
					returnAttributes,
					[],
					constructorParameters,
					false
				);
			}

			var refKind = RefKind(cursor);

			// a method's type parameters come after its return type, which may use them: read ahead for them
			var methodTypeParameters = MethodTypeParameters(cursor);
			var memberScope = scope.With(methodTypeParameters);
			var memberType = memberScope.Type(cursor);

			if (cursor.PeekWord() == "operator")
			{
				cursor.Word();
				var symbol = cursor.OperatorSymbol();
				var operatorParameters = Parameters(cursor, scope, '(', ')', out _);
				var operatorBody = Body(cursor);
				cursor.End();
				return new ApiMethod(
					OperatorName(symbol, operatorParameters.Count, cursor),
					access,
					attributes,
					ApiMethodKind.Operator,
					InterfaceModifiers(memberModifiers, inInterface, operatorBody, isSealedInInterface),
					memberType,
					refKind,
					returnAttributes,
					[],
					operatorParameters,
					false
				);
			}

			if (cursor.PeekWord() == "this" && cursor.PeekAfterWord() == '[')
			{
				cursor.Word();
				var indexerParameters = Parameters(cursor, scope, '[', ']', out _);
				return Property(
					cursor,
					indexerName ?? "Item",
					access,
					attributes,
					memberModifiers,
					memberType,
					refKind,
					indexerParameters,
					isRequired,
					inInterface,
					isSealedInInterface
				);
			}

			var name = cursor.MemberName();
			if (cursor.Peek() == '{')
				return Property(cursor, name, access, attributes, memberModifiers, memberType, refKind, [], isRequired, inInterface, isSealedInInterface);

			if (cursor.Peek() is '(' or '<')
			{
				var typeParameters = TypeParameters(cursor, false);
				var parameters = Parameters(cursor, memberScope, '(', ')', out var isExtension);
				Constraints(cursor, memberScope, typeParameters, isMethod: true);
				var hasBody = Body(cursor);
				cursor.End();
				return new ApiMethod(
					name,
					access,
					attributes,
					ApiMethodKind.Method,
					InterfaceModifiers(memberModifiers, inInterface, hasBody, isSealedInInterface),
					memberType,
					refKind,
					returnAttributes,
					typeParameters,
					parameters,
					isExtension
				);
			}

			string? constant = null;
			if (cursor.TryChar('='))
				constant = cursor.Rest();
			cursor.End();
			if (isConst && constant is null)
				throw cursor.Error("a const without a value");
			return new ApiField(
				name,
				access,
				attributes,
				memberType,
				isConst || (modifiers & ApiMemberModifiers.Static) != 0,
				isReadOnly,
				isVolatile,
				isRequired,
				constant
			);
		}

		private static List<ApiTypeParameter> MethodTypeParameters(Cursor cursor)
		{
			// Type Name<T, U>(  -- find the name's type parameter list without consuming anything
			var text = cursor.Text;
			var paren = ParameterList(text, cursor.Position);
			if (paren < 0)
				return [];
			var head = text.Substring(0, paren).TrimEnd();
			if (!head.EndsWithChar('>'))
				return [];
			var open = MatchingOpen(head, head.Length - 1);
			// the list must follow a name, not be a generic return type's arguments
			if (open <= 0 || !IsNameCharacter(head[open - 1]) || head.LastIndexOf(' ', open) < cursor.Position)
				return [];
			var structs = StructParameters(text);
			return SplitTopLevel(head.Substring(open + 1, head.Length - open - 2))
				.Select(p => new ApiTypeParameter(p.Trim(), ApiVariance.None, structs.Contains(p.Trim()) ? ApiConstraints.Struct : ApiConstraints.None, []))
				.ToList();
		}

		/// <summary>
		/// Where a method's parameter list starts: the first top-level parenthesis right after a name or a type
		/// parameter list. One after a space opens a tuple return type, which is skipped.
		/// </summary>
		private static int ParameterList(string text, int start)
		{
			var depth = 0;
			for (var i = start; i < text.Length; i++)
			{
				var c = text[i];
				if (c is '"' or '\'')
				{
					i = SkipLiteral(text, i);
					continue;
				}
				if (c == '(' && depth == 0 && i > 0 && IsNameCharacter(text[i - 1]))
					return i;
				if (c is '(' or '[' or '<' or '{')
					depth++;
				else if (c is ')' or ']' or '>' or '}')
					depth--;
			}
			return -1;
		}

		private static bool IsNameCharacter(char c) => char.IsLetterOrDigit(c) || c is '_' or '$' or '>' or '@';

		private static ApiMemberModifiers InterfaceModifiers(ApiMemberModifiers modifiers, bool inInterface, bool hasBody, bool isSealed)
		{
			if (!inInterface)
				return modifiers;
			var isStatic = (modifiers & ApiMemberModifiers.Static) != 0;
			// an instance member without a body is abstract; one with a body is virtual unless sealed
			if (!isStatic)
				return hasBody ? (isSealed ? ApiMemberModifiers.None : ApiMemberModifiers.Virtual) : ApiMemberModifiers.Abstract;
			if ((modifiers & ApiMemberModifiers.Abstract) != 0)
				return modifiers;
			return hasBody ? modifiers | ApiMemberModifiers.Virtual : modifiers;
		}

		private static bool Body(Cursor cursor)
		{
			if (cursor.Peek() != '{')
				return false;
			cursor.Expect('{');
			cursor.Expect('…');
			cursor.Expect('}');
			return true;
		}

		private static ApiProperty Property(
			Cursor cursor,
			string name,
			ApiAccessibility access,
			List<ApiAttribute> attributes,
			ApiMemberModifiers modifiers,
			ApiTypeReference type,
			ApiRefKind refKind,
			List<ApiParameter> parameters,
			bool isRequired,
			bool inInterface,
			bool isSealedInInterface
		)
		{
			cursor.Expect('{');
			ApiAccessor? getter = null, setter = null;
			while (!cursor.TryChar('}'))
			{
				var accessorAccess = Accessibility(cursor) ?? access;
				var isReadOnly = false;
				if (cursor.PeekWord() == "readonly")
				{
					cursor.Word();
					isReadOnly = true;
				}
				var keyword = cursor.Word();
				cursor.Expect(';');
				switch (keyword)
				{
					case "get":
						getter = new ApiAccessor(accessorAccess, false, isReadOnly);
						break;
					case "set" or "init":
						setter = new ApiAccessor(accessorAccess, keyword == "init", isReadOnly);
						break;
					default:
						throw cursor.Error($"expected get, set or init, not {keyword}");
				}
			}
			var hasBody = Body(cursor);
			cursor.End();
			return new ApiProperty(
				name,
				access,
				attributes,
				InterfaceModifiers(modifiers, inInterface, hasBody, isSealedInInterface),
				type,
				refKind,
				parameters,
				getter,
				setter,
				isRequired
			);
		}

		private static ApiRefKind RefKind(Cursor cursor)
		{
			if (cursor.PeekWord() != "ref")
				return ApiRefKind.None;
			cursor.Word();
			if (cursor.PeekWord() == "readonly")
			{
				cursor.Word();
				return ApiRefKind.RefReadOnly;
			}
			return ApiRefKind.Ref;
		}

		private static List<ApiParameter> Parameters(Cursor cursor, Scope scope, char open, char close, out bool isExtension)
		{
			isExtension = false;
			cursor.Expect(open);
			var parameters = new List<ApiParameter>();
			if (cursor.TryChar(close))
				return parameters;
			do
			{
				var attributes = new List<ApiAttribute>();
				while (cursor.Peek() == '[')
					attributes.Add(scope.Attribute(cursor, out _));
				if (parameters.Count == 0 && cursor.PeekWord() == "this")
				{
					cursor.Word();
					isExtension = true;
				}
				var isScoped = false;
				if (cursor.PeekWord() == "scoped")
				{
					cursor.Word();
					isScoped = true;
				}
				var isParams = false;
				if (cursor.PeekWord() == "params")
				{
					cursor.Word();
					isParams = true;
				}
				var refKind = cursor.PeekWord() switch
				{
					"out" => ApiRefKind.Out,
					"in" => ApiRefKind.In,
					"ref" => ApiRefKind.Ref,
					_ => ApiRefKind.None,
				};
				if (refKind != ApiRefKind.None)
				{
					cursor.Word();
					if (refKind == ApiRefKind.Ref && cursor.PeekWord() == "readonly")
					{
						cursor.Word();
						refKind = ApiRefKind.RefReadOnly;
					}
				}
				var type = scope.Type(cursor);
				var name = cursor.MemberName();
				string? defaultValue = null;
				if (cursor.TryChar('='))
					defaultValue = cursor.RawUntil(',', close);
				parameters.Add(new ApiParameter(name, type, refKind, isParams, isScoped, defaultValue, attributes));
			}
			while (cursor.TryChar(','));
			cursor.Expect(close);
			return parameters;
		}

		private static void Attribute(Line line, Scope scope, List<ApiAttribute> attributes, List<ApiAttribute> returnAttributes, out string? indexerName)
		{
			var cursor = new Cursor(line.Text, 0, line.Number, line.Depth * 2);
			var attribute = scope.Attribute(cursor, out var target);
			cursor.End();
			indexerName = null;
			if (attribute.Type is { Namespace: "", Name: "IndexerNameAttribute" } && attribute.Arguments is [var renamed])
			{
				indexerName = Unquote(renamed);
				return;
			}
			(target == "return" ? returnAttributes : attributes).Add(attribute);
		}

		private static string Unquote(string literal) =>
			literal.Length >= 2 && literal[0] == '"' ? literal.Substring(1, literal.Length - 2).Replace("\\\"", "\"").Replace("\\\\", "\\") : literal;

		private static string OperatorName(string symbol, int parameters, Cursor cursor)
		{
			var unary = parameters == 1;
			var name = symbol switch
			{
				"+" => unary ? "op_UnaryPlus" : "op_Addition",
				"-" => unary ? "op_UnaryNegation" : "op_Subtraction",
				"*" => "op_Multiply",
				"/" => "op_Division",
				"%" => "op_Modulus",
				"&" => "op_BitwiseAnd",
				"|" => "op_BitwiseOr",
				"^" => "op_ExclusiveOr",
				"<<" => "op_LeftShift",
				">>" => "op_RightShift",
				">>>" => "op_UnsignedRightShift",
				"==" => "op_Equality",
				"!=" => "op_Inequality",
				"<" => "op_LessThan",
				">" => "op_GreaterThan",
				"<=" => "op_LessThanOrEqual",
				">=" => "op_GreaterThanOrEqual",
				"!" => "op_LogicalNot",
				"~" => "op_OnesComplement",
				"++" => "op_Increment",
				"--" => "op_Decrement",
				"true" => "op_True",
				"false" => "op_False",
				"checked +" => "op_CheckedAddition",
				"checked -" => unary ? "op_CheckedUnaryNegation" : "op_CheckedSubtraction",
				"checked *" => "op_CheckedMultiply",
				"checked /" => "op_CheckedDivision",
				"checked ++" => "op_CheckedIncrement",
				"checked --" => "op_CheckedDecrement",
				"+=" => "op_AdditionAssignment",
				"-=" => "op_SubtractionAssignment",
				"*=" => "op_MultiplicationAssignment",
				"/=" => "op_DivisionAssignment",
				_ when symbol.StartsWith("op_", StringComparison.Ordinal) => symbol,
				_ => null,
			};
			return name ?? throw cursor.Error($"unknown operator {symbol}");
		}

		// name resolution

		/// <summary>The names a declaration can use: the types in the file and its header, and the type parameters in scope.</summary>
		private sealed class Scope(Parser parser, Declared? declaring, IReadOnlyList<ApiTypeParameter> methodTypeParameters)
		{
			public Scope With(IReadOnlyList<ApiTypeParameter> parameters) => new(parser, declaring, [.. methodTypeParameters, .. parameters]);

			private ApiTypeParameter? MethodParameter(string name) => methodTypeParameters.LastOrDefault(p => p.Name == name);

			private bool IsTypeParameter(string name)
			{
				for (var current = declaring; current is not null; current = current.Parent)
					if (current.TypeParameters.Contains(name))
						return true;
				return false;
			}

			public ApiAttribute Attribute(Cursor cursor, out string? target)
			{
				cursor.Expect('[');
				target = null;
				if (cursor.PeekWord() == "return" && cursor.PeekAfterWord() == ':')
				{
					target = cursor.Word();
					cursor.Expect(':');
				}
				var segments = DottedName(cursor);
				ApiNamedType type;
				if (segments is [("IndexerName", null)])
					type = new ApiNamedType(string.Empty, "IndexerNameAttribute", null, [], false);
				else
				{
					// C# lets an attribute drop its Attribute suffix
					var last = segments[segments.Count - 1];
					var withSuffix = segments.Take(segments.Count - 1).Append((last.Name + "Attribute", last.Arguments)).ToList();
					type = (Named(withSuffix, cursor, allowMissing: true) ?? Named(segments, cursor, allowMissing: false))!;
				}
				var arguments = new List<string>();
				if (cursor.TryChar('('))
				{
					if (!cursor.TryChar(')'))
					{
						do
							arguments.Add(cursor.RawUntil(',', ')'));
						while (cursor.TryChar(','));
						cursor.Expect(')');
					}
				}
				cursor.Expect(']');
				return new ApiAttribute(type, arguments);
			}

			public ApiTypeReference Type(Cursor cursor)
			{
				ApiTypeReference type;
				if (cursor.Peek() == '(')
					type = Tuple(cursor);
				else if (cursor.PeekWord() is "modreq" or "modopt")
				{
					var isRequired = cursor.Word() == "modreq";
					cursor.Expect('(');
					var modifier = (ApiNamedType)Type(cursor);
					cursor.Expect(')');
					return new ApiModifiedType(Type(cursor), modifier, isRequired);
				}
				else if (cursor.PeekWord() == "delegate" && cursor.PeekAfterWord() == '*')
					type = FunctionPointer(cursor);
				else
					type = NamedOrKeyword(cursor);

				while (true)
				{
					if (cursor.PeekImmediate() == '*')
					{
						cursor.Expect('*');
						type = new ApiPointerType(type);
						continue;
					}
					if (cursor.PeekImmediate() == '?')
					{
						cursor.Expect('?');
						type = Nullable(type);
						continue;
					}
					break;
				}

				if (cursor.PeekImmediate() == '[' && cursor.PeekNext() is ']' or ',')
				{
					// outermost rank first: int[][,] holds int[,]
					var ranks = new List<(int Rank, bool Nullable)>();
					while (cursor.PeekImmediate() == '[' && cursor.PeekNext() is ']' or ',')
					{
						cursor.Expect('[');
						var rank = 1;
						while (cursor.TryChar(','))
							rank++;
						cursor.Expect(']');
						ranks.Add((rank, cursor.PeekImmediate() == '?' && cursor.TryChar('?')));
					}
					for (var r = ranks.Count - 1; r >= 0; r--)
						type = new ApiArrayType(type, ranks[r].Rank) { IsNullable = ranks[r].Nullable };
				}
				return type;
			}

			private ApiTypeReference Nullable(ApiTypeReference type) => type switch
			{
				ApiNamedType { IsValueType: true } or ApiTupleType => Wrap(type),
				// T? on a type parameter constrained to struct is Nullable<T>
				ApiTypeParameterReference parameter when IsStruct(parameter) => Wrap(type),
				_ => type with { IsNullable = true },
			};

			private static ApiNamedType Wrap(ApiTypeReference type) => new("System", "Nullable`1", null, [type], true);

			private bool IsStruct(ApiTypeParameterReference parameter)
			{
				if (parameter.IsMethodParameter)
					return ((MethodParameter(parameter.Name)?.Constraints ?? ApiConstraints.None) & (ApiConstraints.Struct | ApiConstraints.Unmanaged)) != 0;
				for (var current = declaring; current is not null; current = current.Parent)
					if (current.TypeParameters.Contains(parameter.Name))
						return current.StructParameters.Contains(parameter.Name);
				return false;
			}

			private ApiTupleType Tuple(Cursor cursor)
			{
				cursor.Expect('(');
				var elements = new List<ApiTupleElement>();
				do
				{
					var type = Type(cursor);
					string? name = null;
					if (cursor.Peek() is not (',' or ')'))
						name = cursor.MemberName();
					elements.Add(new ApiTupleElement(type, name));
				}
				while (cursor.TryChar(','));
				cursor.Expect(')');
				return new ApiTupleType(elements);
			}

			private ApiFunctionPointerType FunctionPointer(Cursor cursor)
			{
				cursor.ExpectWord("delegate");
				cursor.Expect('*');
				var isUnmanaged = false;
				if (cursor.PeekWord() == "unmanaged")
				{
					cursor.Word();
					isUnmanaged = true;
				}
				cursor.Expect('<');
				var types = new List<ApiTypeReference>();
				do
					types.Add(Type(cursor));
				while (cursor.TryChar(','));
				cursor.Expect('>');
				return new ApiFunctionPointerType(types[types.Count - 1], types.Take(types.Count - 1).ToList(), isUnmanaged);
			}

			private ApiTypeReference NamedOrKeyword(Cursor cursor)
			{
				var word = cursor.PeekWord();
				switch (word)
				{
					case "dynamic":
						cursor.Word();
						return new ApiDynamicType();
					case "nint" or "nuint":
						cursor.Word();
						return new ApiNativeIntegerType(word == "nuint");
				}
				if (Keyword(word) is { } primitive && cursor.PeekAfterWord() != '.')
				{
					cursor.Word();
					return primitive;
				}

				var segments = DottedName(cursor);
				if (segments is [(var single, null)])
				{
					if (MethodParameter(single) is not null)
						return new ApiTypeParameterReference(single, true);
					if (IsTypeParameter(single))
						return new ApiTypeParameterReference(single, false);
				}
				// C# has no syntax for a tuple of one
				if (segments is [("System", null), ("ValueTuple", [var element])])
					return new ApiTupleType([new ApiTupleElement(element, null)]);
				return Named(segments, cursor, allowMissing: false)!;
			}

			/// <summary>Dotted name segments, each with its type arguments: <c>Outer&lt;int&gt;.Inner</c>, or <c>global::Name</c>.</summary>
			private List<(string Name, List<ApiTypeReference>? Arguments)> DottedName(Cursor cursor)
			{
				var segments = new List<(string Name, List<ApiTypeReference>? Arguments)>();
				if (cursor.TryText("global::"))
					segments.Add(("global::", null));
				while (true)
				{
					var name = cursor.Word();
					List<ApiTypeReference>? arguments = null;
					if (cursor.PeekImmediate() == '<')
					{
						cursor.Expect('<');
						arguments = [];
						do
							arguments.Add(Type(cursor));
						while (cursor.TryChar(','));
						cursor.Expect('>');
					}
					segments.Add((name, arguments));
					if (cursor.PeekImmediate() != '.')
						return segments;
					cursor.Expect('.');
				}
			}

			private static ApiNamedType? Keyword(string word) => word switch
			{
				"bool" => TypeReferenceProvider.Primitive(System.Reflection.Metadata.PrimitiveTypeCode.Boolean),
				"byte" => TypeReferenceProvider.Primitive(System.Reflection.Metadata.PrimitiveTypeCode.Byte),
				"sbyte" => TypeReferenceProvider.Primitive(System.Reflection.Metadata.PrimitiveTypeCode.SByte),
				"char" => TypeReferenceProvider.Primitive(System.Reflection.Metadata.PrimitiveTypeCode.Char),
				"short" => TypeReferenceProvider.Primitive(System.Reflection.Metadata.PrimitiveTypeCode.Int16),
				"ushort" => TypeReferenceProvider.Primitive(System.Reflection.Metadata.PrimitiveTypeCode.UInt16),
				"int" => TypeReferenceProvider.Primitive(System.Reflection.Metadata.PrimitiveTypeCode.Int32),
				"uint" => TypeReferenceProvider.Primitive(System.Reflection.Metadata.PrimitiveTypeCode.UInt32),
				"long" => TypeReferenceProvider.Primitive(System.Reflection.Metadata.PrimitiveTypeCode.Int64),
				"ulong" => TypeReferenceProvider.Primitive(System.Reflection.Metadata.PrimitiveTypeCode.UInt64),
				"float" => TypeReferenceProvider.Primitive(System.Reflection.Metadata.PrimitiveTypeCode.Single),
				"double" => TypeReferenceProvider.Primitive(System.Reflection.Metadata.PrimitiveTypeCode.Double),
				"decimal" => new ApiNamedType("System", "Decimal", null, [], true),
				"string" => TypeReferenceProvider.Primitive(System.Reflection.Metadata.PrimitiveTypeCode.String),
				"object" => TypeReferenceProvider.Primitive(System.Reflection.Metadata.PrimitiveTypeCode.Object),
				"void" => TypeReferenceProvider.Primitive(System.Reflection.Metadata.PrimitiveTypeCode.Void),
				_ => null,
			};

			/// <summary>
			/// A dotted name: a namespace, if any, then a top-level type the file or its header knows, then the types
			/// nested in it. Without a namespace the short name must be one only one namespace has.
			/// </summary>
			private ApiNamedType? Named(List<(string Name, List<ApiTypeReference>? Arguments)> segments, Cursor cursor, bool allowMissing)
			{
				static string Metadata((string Name, List<ApiTypeReference>? Arguments) segment) =>
					segment.Arguments is { Count: > 0 } arguments ? segment.Name + "`" + arguments.Count : segment.Name;

				for (var top = segments.Count - 1; top >= 0; top--)
				{
					var ns = string.Join(".", segments.Take(top).Select(s => s.Name));
					if (top == 1 && segments[0].Name == "global::")
						ns = string.Empty;
					var name = Metadata(segments[top]);
					string? resolved = null;
					if (top == 0 || (top == 1 && segments[0].Name == "global::"))
					{
						if (top == 0 && parser._byShortName.TryGetValue(name, out var namespaces))
						{
							if (namespaces.Count > 1)
								throw cursor.Error($"{name} is in {string.Join(" and ", namespaces)}; write it in full");
							resolved = namespaces[0];
						}
						else if (top == 1)
							resolved = string.Empty;
					}
					else if (parser._own.ContainsKey((ns, name)) || parser._external.Contains((ns, name)))
						resolved = ns;
					if (resolved is null)
						continue;

					var own = parser._own.TryGetValue((resolved, name), out var declared) ? declared : null;
					var path = name;
					var named = new ApiNamedType(resolved, name, null, segments[top].Arguments ?? [], false);
					for (var n = top + 1; n < segments.Count; n++)
					{
						var nestedName = Metadata(segments[n]);
						path += "/" + nestedName;
						own = own is not null && own.Nested.TryGetValue(nestedName, out var nested) ? nested : null;
						named = new ApiNamedType(resolved, nestedName, named, segments[n].Arguments ?? [], false);
					}
					var isValueType = own?.IsValueType ?? parser._externalValueTypes.Contains((resolved, path));
					return named with { IsValueType = isValueType };
				}
				if (allowMissing)
					return null;
				throw cursor.Error($"unknown type {string.Join(".", segments.Select(s => s.Name))}; the header names every type from another assembly");
			}
		}

		// text

		/// <summary>The index of the first <paramref name="target"/> at bracket depth zero, outside literals.</summary>
		private static int TopLevelIndex(string text, int start, char target)
		{
			var depth = 0;
			for (var i = start; i < text.Length; i++)
			{
				var c = text[i];
				if (c is '"' or '\'')
				{
					i = SkipLiteral(text, i);
					continue;
				}
				if (c == target && depth == 0)
					return i;
				if (c is '(' or '[' or '<' or '{')
					depth++;
				else if (c is ')' or ']' or '>' or '}')
					depth--;
			}
			return -1;
		}

		private static int SkipLiteral(string text, int start)
		{
			var quote = text[start];
			for (var i = start + 1; i < text.Length; i++)
			{
				if (text[i] == '\\')
					i++;
				else if (text[i] == quote)
					return i;
			}
			return text.Length - 1;
		}

		private static int Matching(string text, int open)
		{
			var depth = 0;
			for (var i = open; i < text.Length; i++)
			{
				if (text[i] == '<')
					depth++;
				else if (text[i] == '>' && --depth == 0)
					return i;
			}
			return text.Length - 1;
		}

		private static int MatchingOpen(string text, int close)
		{
			var depth = 0;
			for (var i = close; i >= 0; i--)
			{
				if (text[i] == '>')
					depth++;
				else if (text[i] == '<' && --depth == 0)
					return i;
			}
			return -1;
		}

		private static List<string> SplitTopLevel(string text)
		{
			var parts = new List<string>();
			var start = 0;
			var depth = 0;
			for (var i = 0; i < text.Length; i++)
			{
				var c = text[i];
				if (c is '"' or '\'')
					i = SkipLiteral(text, i);
				else if (c is '(' or '[' or '<' or '{')
					depth++;
				else if (c is ')' or ']' or '>' or '}')
					depth--;
				else if (c == ',' && depth == 0)
				{
					parts.Add(text.Substring(start, i - start));
					start = i + 1;
				}
			}
			parts.Add(text.Substring(start));
			return parts.Where(p => p.Trim().Length > 0).ToList();
		}

		/// <summary>Splits <c>Outer&lt;,&gt;.Inner</c> at the dots outside angle brackets.</summary>
		private static List<string> SplitDots(string text)
		{
			var parts = new List<string>();
			var start = 0;
			var depth = 0;
			for (var i = 0; i < text.Length; i++)
			{
				if (text[i] == '<')
					depth++;
				else if (text[i] == '>')
					depth--;
				else if (text[i] == '.' && depth == 0)
				{
					parts.Add(text.Substring(start, i - start));
					start = i + 1;
				}
			}
			parts.Add(text.Substring(start));
			return parts;
		}

		/// <summary>Reads one line.</summary>
		private sealed class Cursor(string text, int position, int line = 0, int indent = 0)
		{
			public string Text { get; } = text;
			public int Position { get; private set; } = position;

			public ApiParseException Error(string message) => new(message, line, indent + Position + 1);

			private void Skip()
			{
				while (Position < Text.Length && Text[Position] == ' ')
					Position++;
			}

			public char Peek()
			{
				Skip();
				return Position < Text.Length ? Text[Position] : '\0';
			}

			public char PeekNext()
			{
				Skip();
				return Position + 1 < Text.Length ? Text[Position + 1] : '\0';
			}

			public bool TryChar(char c)
			{
				if (Peek() != c)
					return false;
				Position++;
				return true;
			}

			public void Expect(char c)
			{
				if (!TryChar(c))
					throw Error($"expected '{c}'");
			}

			public void ExpectWord(string word)
			{
				if (Word() != word)
					throw Error($"expected '{word}'");
			}

			public void End()
			{
				if (Peek() != '\0')
					throw Error($"unexpected '{Text.Substring(Position)}'");
			}

			private static bool IsWordCharacter(char c) => char.IsLetterOrDigit(c) || c is '_' or '@' or '$';

			public string PeekWord()
			{
				Skip();
				var end = Position;
				while (end < Text.Length && IsWordCharacter(Text[end]))
					end++;
				return Text.Substring(Position, end - Position);
			}

			/// <summary>The character after the word at the cursor, past spaces.</summary>
			public char PeekAfterWord()
			{
				var word = PeekWord();
				var after = Position + word.Length;
				while (after < Text.Length && Text[after] == ' ')
					after++;
				return after < Text.Length ? Text[after] : '\0';
			}

			public string Word()
			{
				var word = PeekWord();
				if (word.Length == 0)
					throw Error("expected a word");
				Position += word.Length;
				return word;
			}

			public string Identifier() => Word();

			/// <summary>A member's name, which metadata lets hold characters C# does not: <c>&lt;Clone&gt;$</c>.</summary>
			public string MemberName()
			{
				Skip();
				var start = Position;
				if (Position < Text.Length && Text[Position] == '<')
				{
					var close = Text.IndexOf('>', Position);
					if (close < 0)
						throw Error("expected '>'");
					Position = close + 1;
				}
				while (Position < Text.Length && (IsWordCharacter(Text[Position]) || Text[Position] == '.'))
					Position++;
				if (Position == start)
					throw Error("expected a name");
				return Text.Substring(start, Position - start);
			}

			public string OperatorSymbol()
			{
				Skip();
				if (PeekWord() is "checked")
				{
					Word();
					return "checked " + OperatorSymbol();
				}
				if (PeekWord() is "true" or "false" || PeekWord().StartsWith("op_", StringComparison.Ordinal))
					return Word();
				var start = Position;
				while (Position < Text.Length && Text[Position] != '(' && Text[Position] != ' ')
					Position++;
				return Text.Substring(start, Position - start);
			}

			/// <summary>A literal, up to the first of <paramref name="stops"/> outside brackets and quotes.</summary>
			public string RawUntil(params char[] stops)
			{
				Skip();
				var start = Position;
				var depth = 0;
				for (; Position < Text.Length; Position++)
				{
					var c = Text[Position];
					if (c is '"' or '\'')
					{
						Position = SkipLiteral(Text, Position);
						continue;
					}
					if (depth == 0 && Array.IndexOf(stops, c) >= 0)
						break;
					if (c is '(' or '[' or '{')
						depth++;
					else if (c is ')' or ']' or '}')
						depth--;
				}
				return Text.Substring(start, Position - start).TrimEnd();
			}

			public string Rest()
			{
				Skip();
				var rest = Text.Substring(Position).TrimEnd();
				Position = Text.Length;
				return rest;
			}

			/// <summary>The character at the cursor, without skipping spaces.</summary>
			public char PeekImmediate() => Position < Text.Length ? Text[Position] : '\0';

			public bool TryText(string text)
			{
				Skip();
				if (string.CompareOrdinal(Text, Position, text, 0, text.Length) != 0)
					return false;
				Position += text.Length;
				return true;
			}
		}
	}
}

internal static class Characters
{
	public static bool StartsWithChar(this string text, char c) => text.Length > 0 && text[0] == c;

	public static bool EndsWithChar(this string text, char c) => text.Length > 0 && text[text.Length - 1] == c;
}
