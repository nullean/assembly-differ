#nullable enable
using System;
using System.Collections.Generic;
using System.Linq;
using System.Text.RegularExpressions;

using Nullean.PublicApi;

namespace Nullean.PublicApi.Diff;

/// <summary>
/// Compares two versions of an API, read from assemblies or <c>.api</c> files, and classifies each difference by who it
/// breaks, whether it breaks compiled code, source or both, and the version change it calls for.
/// </summary>
public static class ApiComparison
{
	private const string Obsolete = "System.ObsoleteAttribute";

	/// <summary>The differences from <paramref name="oldApi"/> to <paramref name="newApi"/>.</summary>
	public static ApiComparisonResult Compare(ApiAssembly oldApi, ApiAssembly newApi, ApiComparisonOptions? options = null)
	{
		var context = new Context(options ?? new ApiComparisonOptions(), newApi);
		Pair(
			oldApi.Types,
			newApi.Types,
			t => t.Namespace + "." + t.Name,
			(o, n) => context.Type(o, n, (o?.Namespace ?? n!.Namespace) is { Length: > 0 } ns ? [ns] : [])
		);
		return new ApiComparisonResult(context.Changes);
	}

	/// <summary>Pairs two lists by key, in the new list's order with the removed ones after.</summary>
	private static void Pair<T>(IReadOnlyList<T> oldItems, IReadOnlyList<T> newItems, Func<T, string> key, Action<T?, T?> visit) where T : class
	{
		var old = new Dictionary<string, T>(StringComparer.Ordinal);
		foreach (var item in oldItems)
			old[key(item)] = item;
		var seen = new HashSet<string>(StringComparer.Ordinal);
		foreach (var item in newItems)
		{
			var k = key(item);
			seen.Add(k);
			visit(old.TryGetValue(k, out var match) ? match : null, item);
		}
		foreach (var item in oldItems)
			if (!seen.Contains(key(item)))
				visit(item, null);
	}

	private sealed class Context(ApiComparisonOptions options, ApiAssembly newApi)
	{
		public List<ApiChange> Changes { get; } = [];

		private readonly Dictionary<string, ApiType> _newTypes = Index(newApi);
		private readonly Dictionary<ApiType, ApiType> _pairs = new(TypeIdentity.Instance);
		private ApiType? _oldScope;

		private static Dictionary<string, ApiType> Index(ApiAssembly api)
		{
			var index = new Dictionary<string, ApiType>(StringComparer.Ordinal);

			void Add(ApiType type, string? declaring)
			{
				var name = declaring is null ? type.Namespace + "." + type.Name : declaring + "/" + type.Name;
				index[name] = type;
				foreach (var nested in type.NestedTypes)
					Add(nested, name);
			}

			foreach (var type in api.Types)
				Add(type, null);
			return index;
		}

		// types

		public void Type(ApiType? oldType, ApiType? newType, List<string> path)
		{
			var type = newType ?? oldType!;
			var here = path.Append(TypeSegment(type)).ToList();
			if (oldType is null)
			{
				Add(here, ApiChangeKind.Added, "", null, ApiWriter.Declaration(newType!), ApiAudience.None, ApiCompatibility.None, null, newType, newType);
				return;
			}
			if (newType is null)
			{
				Add(
					here,
					ApiChangeKind.Removed,
					"",
					ApiWriter.Declaration(oldType),
					null,
					ApiAudience.Callers,
					Both,
					oldType,
					oldType,
					null,
					Deprecated(oldType.Attributes)
				);
				return;
			}

			_pairs[oldType] = newType;
			var outer = _oldScope;
			_oldScope = oldType;
			try
			{
				Compare(oldType, newType, here);
			}
			finally
			{
				_oldScope = outer;
			}
		}

		private void Compare(ApiType oldType, ApiType newType, List<string> here)
		{
			void Changed(string part, string? old, string? @new, ApiAudience breaks, ApiCompatibility compatibility, string? note = null) =>
				Add(here, ApiChangeKind.Changed, part, old, @new, breaks, compatibility, oldType, oldType, newType, note);

			if (oldType.Kind != newType.Kind)
				Changed("kind", oldType.Kind.ToString().ToLowerInvariant(), newType.Kind.ToString().ToLowerInvariant(), ApiAudience.Callers, Both);
			Accessibility(oldType.Accessibility, newType.Accessibility, (o, n, breaks) => Changed("accessibility", o, n, breaks, Both));

			var oldModifiers = oldType.Modifiers;
			var newModifiers = newType.Modifiers;
			Flag(oldModifiers, newModifiers, ApiTypeModifiers.Static, "static", ApiAudience.Callers, ApiAudience.None, Changed);
			Flag(oldModifiers, newModifiers, ApiTypeModifiers.Abstract, "abstract", ApiAudience.Callers, ApiAudience.None, Changed);
			Flag(oldModifiers, newModifiers, ApiTypeModifiers.Sealed, "sealed", ApiAudience.Inheritors, ApiAudience.None, Changed);
			Flag(oldModifiers, newModifiers, ApiTypeModifiers.ReadOnly, "readonly", ApiAudience.None, ApiAudience.None, Changed);
			Flag(oldModifiers, newModifiers, ApiTypeModifiers.Ref, "ref", ApiAudience.Callers, ApiAudience.Callers, Changed);
			// whether code outside can derive from a class that stays one, and is not sealed or static, either side
			const ApiTypeModifiers closed = ApiTypeModifiers.Sealed | ApiTypeModifiers.Static;
			if (
				oldType.Kind == ApiTypeKind.Class && newType.Kind == ApiTypeKind.Class && (oldModifiers & closed) == 0 && (newModifiers & closed) ==
					0 && oldType.CanBeExtendedOutside != newType.CanBeExtendedOutside
			)
			{
				if (oldType.CanBeExtendedOutside)
					Changed("constructors", "public or protected", "none", ApiAudience.Inheritors, Both);
				else
					Changed("constructors", "none", "public or protected", ApiAudience.None, ApiCompatibility.None);
			}

			TypeParameters(oldType.TypeParameters, newType.TypeParameters, oldType, newType, Changed);
			Bases(oldType, newType, Changed);
			Attributes(oldType.Attributes, newType.Attributes, (part, o, n, breaks, compatibility) => Changed(part, o, n, breaks, compatibility));

			if (oldType.Kind == ApiTypeKind.Delegate && newType.Kind == ApiTypeKind.Delegate)
			{
				var oldSignature = ApiWriter.Declaration(oldType);
				var newSignature = ApiWriter.Declaration(newType);
				if (Key(oldType.Members.OfType<ApiMethod>().First(), oldType) != Key(newType.Members.OfType<ApiMethod>().First(), newType))
					Changed("signature", oldSignature, newSignature, ApiAudience.Callers, Both);
				else
					Member(
						oldType.Members.OfType<ApiMethod>().First(),
						newType.Members.OfType<ApiMethod>().First(),
						oldType,
						newType,
						here,
						delegateInvoke: true
					);
			}
			else
				Pair(
					oldType.Members,
					newType.Members,
					m => Key(m, oldType.Members.Contains(m) ? oldType : newType),
					(o, n) => Member(o, n, oldType, newType, here)
				);

			Pair(oldType.NestedTypes, newType.NestedTypes, t => t.Name, (o, n) => Type(o, n, here));
		}

		private void Bases(ApiType oldType, ApiType newType, Action<string, string?, string?, ApiAudience, ApiCompatibility, string?> changed)
		{
			// a type that became or stopped being an enum has its kind change; its bases are not comparable
			if ((oldType.Kind == ApiTypeKind.Enum) != (newType.Kind == ApiTypeKind.Enum))
				return;
			if (oldType.Kind == ApiTypeKind.Enum)
			{
				var oldUnderlying = oldType.BaseType is null ? "int" : ApiWriter.Reference(oldType.BaseType, oldType);
				var newUnderlying = newType.BaseType is null ? "int" : ApiWriter.Reference(newType.BaseType, newType);
				if (oldUnderlying != newUnderlying)
					changed("underlying type", oldUnderlying, newUnderlying, ApiAudience.Callers, Both, null);
				return;
			}

			var oldBase = oldType.BaseType is null ? null : Erase(oldType.BaseType);
			var newBase = newType.BaseType is null ? null : Erase(newType.BaseType);
			if (oldBase != newBase)
			{
				var old = oldType.BaseType is null ? "object" : ApiWriter.Reference(oldType.BaseType, oldType);
				var @new = newType.BaseType is null ? "object" : ApiWriter.Reference(newType.BaseType, newType);
				// a class put between a type and its base keeps everything the base gave callers
				if (oldBase is null || Ancestors(newType).Contains(oldBase))
					changed("base type", old, @new, ApiAudience.None, ApiCompatibility.None, oldBase is null ? null : $"{old} is still a base");
				else
					changed("base type", old, @new, ApiAudience.Callers, Both, null);
			}

			var oldInterfaces = oldType.Interfaces.ToDictionary(Erase, i => i, StringComparer.Ordinal);
			var newInterfaces = newType.Interfaces.ToDictionary(Erase, i => i, StringComparer.Ordinal);
			foreach (var removed in oldInterfaces.Where(i => !newInterfaces.ContainsKey(i.Key)))
				changed("interfaces", ApiWriter.Reference(removed.Value, oldType), null, ApiAudience.Callers, Both, null);
			foreach (var added in newInterfaces.Where(i => !oldInterfaces.ContainsKey(i.Key)))
			{
				// an interface that extends another now asks its implementers for that one's members too
				var breaks = newType.Kind == ApiTypeKind.Interface && AsksSomething(added.Value) ? ApiAudience.Implementers : ApiAudience.None;
				changed("interfaces", null, ApiWriter.Reference(added.Value, newType), breaks, breaks == ApiAudience.None ? ApiCompatibility.None : Both, null);
			}
		}

		/// <summary>Whether implementing an interface asks for members: one from elsewhere is assumed to; one here does when it has an abstract one.</summary>
		private bool AsksSomething(ApiTypeReference reference) =>
			reference is not ApiNamedType named || !_newTypes.TryGetValue(FullName(named), out var type) || type.Members.Any(
				m => m is ApiMethod { Modifiers: var modifiers } && (modifiers & ApiMemberModifiers.Abstract) != 0 || m is ApiProperty
				{
					Modifiers: var p
				} && (p & ApiMemberModifiers.Abstract) != 0 || m is ApiEvent { Modifiers: var e } && (e & ApiMemberModifiers.Abstract) != 0
			);

		/// <summary>The erased names of a type's base classes that this assembly declares, nearest first.</summary>
		private HashSet<string> Ancestors(ApiType type)
		{
			var ancestors = new HashSet<string>(StringComparer.Ordinal);
			for (var current = type.BaseType; current is ApiNamedType named;)
			{
				ancestors.Add(Erase(named));
				current = _newTypes.TryGetValue(FullName(named), out var declared) ? declared.BaseType : null;
			}
			return ancestors;
		}

		private static string FullName(ApiNamedType named) =>
			named.DeclaringType is null ? named.Namespace + "." + named.Name : FullName(named.DeclaringType) + "/" + named.Name;

		private static void TypeParameters(
			IReadOnlyList<ApiTypeParameter> oldParameters,
			IReadOnlyList<ApiTypeParameter> newParameters,
			ApiType oldType,
			ApiType newType,
			Action<string, string?, string?, ApiAudience, ApiCompatibility, string?> changed
		)
		{
			for (var i = 0; i < Math.Min(oldParameters.Count, newParameters.Count); i++)
			{
				var o = oldParameters[i];
				var n = newParameters[i];
				if (o.Variance != n.Variance)
					changed(
						$"type parameter {n.Name} variance",
						Variance(o.Variance),
						Variance(n.Variance),
						ApiAudience.Callers,
						ApiCompatibility.Source,
						null
					);
				var oldConstraints = Constraints(o, oldType);
				var newConstraints = Constraints(n, newType);
				foreach (var added in newConstraints.Except(oldConstraints))
					changed($"type parameter {n.Name} constraint", null, added, ApiAudience.Callers, Both, null);
				foreach (var removed in oldConstraints.Except(newConstraints))
					changed($"type parameter {n.Name} constraint", removed, null, ApiAudience.None, ApiCompatibility.None, null);
			}
		}

		private static string Variance(ApiVariance variance) => variance switch
		{
			ApiVariance.Out => "out",
			ApiVariance.In => "in",
			_ => "invariant"
		};

		private static List<string> Constraints(ApiTypeParameter parameter, ApiType context)
		{
			var constraints = new List<string>();
			ApiConstraints[] flags =
			[
				ApiConstraints.Class,
				ApiConstraints.NullableClass,
				ApiConstraints.Struct,
				ApiConstraints.Unmanaged,
				ApiConstraints.NotNull,
				ApiConstraints.New
			];
			foreach (var flag in flags)
				if (flag != ApiConstraints.None && (parameter.Constraints & flag) != 0 && flag != ApiConstraints.AllowsRefStruct)
					constraints.Add(flag switch
					{
						ApiConstraints.NullableClass => "class?",
						ApiConstraints.New => "new()",
						_ => flag.ToString().ToLowerInvariant(),
					});
			constraints.AddRange(parameter.ConstraintTypes.Select(t => ApiWriter.Reference(t, context)));
			// allows ref struct loosens a constraint; taking it away is the break
			if ((parameter.Constraints & ApiConstraints.AllowsRefStruct) == 0)
				constraints.Add("not ref struct");
			return constraints;
		}

		// members

		private void Member(ApiMember? oldMember, ApiMember? newMember, ApiType oldType, ApiType newType, List<string> path, bool delegateInvoke = false)
		{
			var member = newMember ?? oldMember!;
			var declaring = newMember is null ? oldType : newType;
			var here = delegateInvoke ? path : path.Append(MemberSegment(member, declaring)).ToList();

			if (oldMember is null)
			{
				var (breaks, compatibility, note) = Obligation(newMember!, newType);
				Add(
					here,
					ApiChangeKind.Added,
					"",
					null,
					ApiWriter.Declaration(newMember!, newType),
					breaks,
					compatibility,
					newType,
					newMember,
					newMember,
					note
				);
				return;
			}
			if (newMember is null)
			{
				var removed = ApiWriter.Declaration(oldMember, oldType);
				// an override, or a member that moved to a base class here, is still found through the base
				if (MovedToBase(oldMember, oldType, newType) is { } baseName)
					Add(
						here,
						ApiChangeKind.Removed,
						"",
						removed,
						null,
						ApiAudience.None,
						ApiCompatibility.None,
						oldType,
						oldMember,
						null,
						$"still inherited from {baseName}"
					);
				else
				{
					var breaks = oldMember.Accessibility == ApiAccessibility.Public ? ApiAudience.Callers : ApiAudience.Inheritors;
					// optional parameters added at the end: source still compiles, compiled callers do not find the old one
					var compatibility = GainedOptionalParameters(oldMember, newType) ? ApiCompatibility.Binary : Both;
					var note = compatibility == ApiCompatibility.Binary
						? "an overload with more optional parameters replaces it; callers compile, but compiled callers fail"
						: null;
					Add(
						here,
						ApiChangeKind.Removed,
						"",
						removed,
						null,
						breaks,
						compatibility,
						oldType,
						oldMember,
						null,
						Join(note, Deprecated(oldMember.Attributes))
					);
				}
				return;
			}

			void Changed(string part, string? old, string? @new, ApiAudience breaks, ApiCompatibility compatibility, string? note = null) =>
				Add(here, ApiChangeKind.Changed, part, old, @new, breaks, compatibility, oldType, oldMember, newMember, note);

			Accessibility(
				oldMember.Accessibility,
				newMember.Accessibility,
				(o, n, breaks) => Changed(
					"accessibility",
					o,
					n,
					breaks == ApiAudience.Callers && oldMember.Accessibility != ApiAccessibility.Public ? ApiAudience.Inheritors : breaks,
					Both
				)
			);
			Attributes(oldMember.Attributes, newMember.Attributes, (part, o, n, breaks, compatibility) => Changed(part, o, n, breaks, compatibility));

			switch (oldMember, newMember)
			{
				case (ApiField o, ApiField n):
					Field(o, n, oldType, newType, Changed);
					break;
				case (ApiProperty o, ApiProperty n):
					Modifiers(o.Modifiers, n.Modifiers, oldType, Changed);
					TypeOf("type", o.Type, n.Type, oldType, newType, Changed);
					if (o.RefKind != n.RefKind)
						Changed("ref", RefKind(o.RefKind), RefKind(n.RefKind), ApiAudience.Callers, Both);
					Parameters(o.Parameters, n.Parameters, oldType, newType, Changed);
					Accessor("get", o.Getter, n.Getter, Changed);
					Accessor("set", o.Setter, n.Setter, Changed);
					if (o.Setter is { } oldSetter && n.Setter is { } newSetter && oldSetter.IsInit != newSetter.IsInit)
						Changed(
							"set",
							oldSetter.IsInit ? "init" : "set",
							newSetter.IsInit ? "init" : "set",
							ApiAudience.Callers,
							newSetter.IsInit ? Both : ApiCompatibility.Binary
						);
					Required(o.IsRequired, n.IsRequired, Changed);
					break;
				case (ApiEvent o, ApiEvent n):
					Modifiers(o.Modifiers, n.Modifiers, oldType, Changed);
					TypeOf("type", o.Type, n.Type, oldType, newType, Changed);
					break;
				case (ApiMethod o, ApiMethod n):
					Modifiers(o.Modifiers, n.Modifiers, oldType, Changed);
					TypeOf("return type", o.ReturnType, n.ReturnType, oldType, newType, Changed);
					if (o.ReturnRefKind != n.ReturnRefKind)
						Changed("return ref", RefKind(o.ReturnRefKind), RefKind(n.ReturnRefKind), ApiAudience.Callers, Both);
					Attributes(
						o.ReturnAttributes,
						n.ReturnAttributes,
						(part, old, @new, breaks, compatibility) => Changed("return " + part, old, @new, breaks, compatibility)
					);
					Parameters(o.Parameters, n.Parameters, oldType, newType, Changed);
					if (o.IsExtension != n.IsExtension)
						Changed(
							"this",
							o.IsExtension ? "extension" : "static",
							n.IsExtension ? "extension" : "static",
							n.IsExtension ? ApiAudience.None : ApiAudience.Callers,
							n.IsExtension ? ApiCompatibility.None : ApiCompatibility.Source
						);
					for (var i = 0; i < Math.Min(o.TypeParameters.Count, n.TypeParameters.Count); i++)
					{
						var oldConstraints = Constraints(o.TypeParameters[i], oldType);
						var newConstraints = Constraints(n.TypeParameters[i], newType);
						foreach (var added in newConstraints.Except(oldConstraints))
							Changed($"type parameter {n.TypeParameters[i].Name} constraint", null, added, ApiAudience.Callers, Both);
						foreach (var removed in oldConstraints.Except(newConstraints))
							Changed($"type parameter {n.TypeParameters[i].Name} constraint", removed, null, ApiAudience.None, ApiCompatibility.None);
					}
					break;
			}
		}

		/// <summary>What a new member asks of code that implements or derives from its type.</summary>
		private static (ApiAudience Breaks, ApiCompatibility Compatibility, string? Note) Obligation(ApiMember member, ApiType type)
		{
			var modifiers = member switch
			{
				ApiMethod m => m.Modifiers,
				ApiProperty p => p.Modifiers,
				ApiEvent e => e.Modifiers,
				_ => ApiMemberModifiers.None,
			};
			if ((modifiers & ApiMemberModifiers.Abstract) != 0)
				return (type.Kind == ApiTypeKind.Interface ? ApiAudience.Implementers : ApiAudience.Inheritors, Both, null);
			// required is checked by the compiler, not the runtime
			if (member is ApiProperty { IsRequired: true } or ApiField { IsRequired: true })
				return (ApiAudience.Callers, ApiCompatibility.Source, "every object initializer has to set it");
			return (ApiAudience.None, ApiCompatibility.None, null);
		}

		private static bool GainedOptionalParameters(ApiMember member, ApiType newType)
		{
			if (member is not ApiMethod method)
				return false;
			var parameters = method.Parameters.Select(Parameter).ToList();
			return newType
				.Members
				.OfType<ApiMethod>()
				.Any(
					m => m.Name == method.Name && m.TypeParameters.Count == method.TypeParameters.Count && m.Parameters.Count > method.Parameters.Count && m
						.Parameters
						.Take(parameters.Count)
						.Select(Parameter)
						.SequenceEqual(parameters) && m.Parameters.Skip(parameters.Count).All(p => p.DefaultValue is not null || p.IsParams)
				);
		}

		private string? MovedToBase(ApiMember member, ApiType oldType, ApiType newType)
		{
			var key = Key(member, oldType);
			for (var current = newType.BaseType; current is ApiNamedType named;)
			{
				if (!_newTypes.TryGetValue(FullName(named), out var declared))
					break;
				if (declared.Members.Any(m => Key(m, declared) == key))
					return NonGeneric(declared.Name);
				current = declared.BaseType;
			}
			// an override's base member still exists, and calls compiled against the override reach it
			return member switch
			{
				ApiMethod { Modifiers: var m } when (m & ApiMemberModifiers.Override) != 0 => "the overridden member",
				ApiProperty { Modifiers: var p } when (p & ApiMemberModifiers.Override) != 0 => "the overridden member",
				ApiEvent { Modifiers: var e } when (e & ApiMemberModifiers.Override) != 0 => "the overridden member",
				_ => null,
			};
		}

		private static void Field(
			ApiField o,
			ApiField n,
			ApiType oldType,
			ApiType newType,
			Action<string, string?, string?, ApiAudience, ApiCompatibility, string?> changed
		)
		{
			TypeOf("type", o.Type, n.Type, oldType, newType, changed);
			if (o.IsStatic != n.IsStatic)
				changed("static", o.IsStatic ? "static" : "instance", n.IsStatic ? "static" : "instance", ApiAudience.Callers, Both, null);
			if (o.Constant != n.Constant)
			{
				// compiled code holds the old value
				if (o.Constant is not null && n.Constant is not null)
					changed(
						oldType.Kind == ApiTypeKind.Enum ? "value" : "constant",
						o.Constant,
						n.Constant,
						ApiAudience.Callers,
						ApiCompatibility.Binary,
						"compiled code keeps the old value"
					);
				else
					changed("const", o.Constant is null ? "field" : "const", n.Constant is null ? "field" : "const", ApiAudience.Callers, Both, null);
			}
			if (o.IsReadOnly != n.IsReadOnly)
				changed(
					"readonly",
					o.IsReadOnly ? "readonly" : "writable",
					n.IsReadOnly ? "readonly" : "writable",
					n.IsReadOnly ? ApiAudience.Callers : ApiAudience.None,
					n.IsReadOnly ? Both : ApiCompatibility.None,
					null
				);
			if (o.IsVolatile != n.IsVolatile)
				changed(
					"volatile",
					o.IsVolatile ? "volatile" : "not volatile",
					n.IsVolatile ? "volatile" : "not volatile",
					ApiAudience.Callers,
					ApiCompatibility.Binary,
					null
				);
			Required(o.IsRequired, n.IsRequired, changed);
		}

		private static void Required(bool old, bool @new, Action<string, string?, string?, ApiAudience, ApiCompatibility, string?> changed)
		{
			if (old != @new)
				changed(
					"required",
					old ? "required" : "optional",
					@new ? "required" : "optional",
					@new ? ApiAudience.Callers : ApiAudience.None,
					@new ? ApiCompatibility.Source : ApiCompatibility.None,
					@new ? "every object initializer has to set it" : null
				);
		}

		private static void Modifiers(
			ApiMemberModifiers old,
			ApiMemberModifiers @new,
			ApiType type,
			Action<string, string?, string?, ApiAudience, ApiCompatibility, string?> changed
		)
		{
			if ((old & ApiMemberModifiers.Static) != (@new & ApiMemberModifiers.Static))
				changed(
					"static",
					Has(old, ApiMemberModifiers.Static) ? "static" : "instance",
					Has(@new, ApiMemberModifiers.Static) ? "static" : "instance",
					ApiAudience.Callers,
					Both,
					null
				);
			// whether it can be overridden, and whether it has to be, each the same part whichever way it changes
			var overridable = ApiMemberModifiers.Virtual | ApiMemberModifiers.Abstract | ApiMemberModifiers.Override;
			var wasOverridable = (old & overridable) != 0 && !Has(old, ApiMemberModifiers.Sealed);
			var isOverridable = (@new & overridable) != 0 && !Has(@new, ApiMemberModifiers.Sealed);
			if (wasOverridable != isOverridable)
				changed(
					"virtual",
					Describe(old),
					Describe(@new),
					wasOverridable ? ApiAudience.Inheritors : ApiAudience.None,
					wasOverridable ? Both : ApiCompatibility.None,
					null
				);
			if (Has(old, ApiMemberModifiers.Abstract) != Has(@new, ApiMemberModifiers.Abstract))
			{
				var added = Has(@new, ApiMemberModifiers.Abstract);
				var breaks = !added ? ApiAudience.None : type.Kind == ApiTypeKind.Interface ? ApiAudience.Implementers : ApiAudience.Inheritors;
				changed("abstract", Describe(old), Describe(@new), breaks, added ? Both : ApiCompatibility.None, null);
			}
		}

		private static bool Has(ApiMemberModifiers modifiers, ApiMemberModifiers flag) => (modifiers & flag) != 0;

		private static string Describe(ApiMemberModifiers modifiers)
		{
			var words = new List<string>();
			if (Has(modifiers, ApiMemberModifiers.Sealed))
				words.Add("sealed");
			if (Has(modifiers, ApiMemberModifiers.Abstract))
				words.Add("abstract");
			if (Has(modifiers, ApiMemberModifiers.Virtual))
				words.Add("virtual");
			if (Has(modifiers, ApiMemberModifiers.Override))
				words.Add("override");
			return words.Count == 0 ? "not virtual" : string.Join(" ", words);
		}

		private static void Accessor(
			string name,
			ApiAccessor? old,
			ApiAccessor? @new,
			Action<string, string?, string?, ApiAudience, ApiCompatibility, string?> changed
		)
		{
			if (old is null && @new is null)
				return;
			if (old is null)
				changed(name, null, Access(@new!.Accessibility) + " " + name, ApiAudience.None, ApiCompatibility.None, null);
			else if (@new is null)
				changed(
					name,
					Access(old.Accessibility) + " " + name,
					null,
					old.Accessibility == ApiAccessibility.Public ? ApiAudience.Callers : ApiAudience.Inheritors,
					Both,
					null
				);
			else
				Accessibility(old.Accessibility, @new.Accessibility, (o, n, breaks) => changed(name, o + " " + name, n + " " + name, breaks, Both, null));
		}

		private static void Parameters(
			IReadOnlyList<ApiParameter> oldParameters,
			IReadOnlyList<ApiParameter> newParameters,
			ApiType oldType,
			ApiType newType,
			Action<string, string?, string?, ApiAudience, ApiCompatibility, string?> changed
		)
		{
			for (var i = 0; i < Math.Min(oldParameters.Count, newParameters.Count); i++)
			{
				var o = oldParameters[i];
				var n = newParameters[i];
				// by position, which both versions share; a renamed parameter has a name of its own on each side
				var label = $"parameter {i + 1}";
				if (o.Name != n.Name)
					changed($"parameter {i + 1} name", o.Name, n.Name, ApiAudience.Callers, ApiCompatibility.Source, "callers that pass it by name");
				TypeOf(label, o.Type, n.Type, oldType, newType, changed);
				if (o.RefKind != n.RefKind)
				{
					// in and ref readonly are meant to be interchangeable
					var compatible = o.RefKind is ApiRefKind.In or ApiRefKind.RefReadOnly && n.RefKind is ApiRefKind.In or ApiRefKind.RefReadOnly;
					changed(
						label + " ref",
						RefKind(o.RefKind),
						RefKind(n.RefKind),
						compatible ? ApiAudience.None : ApiAudience.Callers,
						compatible ? ApiCompatibility.None : ApiCompatibility.Source,
						null
					);
				}
				if (o.IsParams != n.IsParams)
					changed(
						label,
						o.IsParams ? "params" : "not params",
						n.IsParams ? "params" : "not params",
						n.IsParams ? ApiAudience.None : ApiAudience.Callers,
						n.IsParams ? ApiCompatibility.None : ApiCompatibility.Source,
						null
					);
				if (o.IsScoped != n.IsScoped)
					changed(
						label,
						o.IsScoped ? "scoped" : "not scoped",
						n.IsScoped ? "scoped" : "not scoped",
						n.IsScoped ? ApiAudience.Inheritors : ApiAudience.Callers,
						ApiCompatibility.Source,
						null
					);
				if (o.DefaultValue != n.DefaultValue)
				{
					if (o.DefaultValue is null)
						changed(label + " default", null, n.DefaultValue, ApiAudience.None, ApiCompatibility.None, null);
					else if (n.DefaultValue is null)
						changed(label + " default", o.DefaultValue, null, ApiAudience.Callers, ApiCompatibility.Source, "callers that left it out");
					else
						changed(
							label + " default",
							o.DefaultValue,
							n.DefaultValue,
							ApiAudience.Callers,
							ApiCompatibility.Binary,
							"compiled callers pass the old default"
						);
				}
				Attributes(
					o.Attributes,
					n.Attributes,
					(part, old, @new, breaks, compatibility) => changed(label + " " + part, old, @new, breaks, compatibility, null)
				);
			}
		}

		/// <summary>A member's or parameter's type: a different type breaks callers; nullability, tuple names and dynamic only change what the compiler says.</summary>
		private static void TypeOf(
			string part,
			ApiTypeReference old,
			ApiTypeReference @new,
			ApiType oldType,
			ApiType newType,
			Action<string, string?, string?, ApiAudience, ApiCompatibility, string?> changed
		)
		{
			var oldText = ApiWriter.Reference(old, oldType);
			var newText = ApiWriter.Reference(@new, newType);
			if (Erase(old) != Erase(@new))
				changed(part, oldText, newText, ApiAudience.Callers, Both, null);
			else if (TupleNames(old) != TupleNames(@new))
				changed(part, oldText, newText, ApiAudience.Callers, ApiCompatibility.Source, "callers that use the element names");
			else if (oldText != newText)
				changed(part, oldText, newText, ApiAudience.None, ApiCompatibility.None, "nullability");
		}

		private static string TupleNames(ApiTypeReference type) => type switch
		{
			ApiTupleType tuple => "(" + string.Join(",", tuple.Elements.Select(e => e.Name + ":" + TupleNames(e.Type))) + ")",
			ApiNamedType named =>
				string.Join(",", named.TypeArguments.Select(TupleNames)) + (named.DeclaringType is null ? "" : "/" + TupleNames(named.DeclaringType)),
			ApiArrayType array => TupleNames(array.ElementType),
			_ => "",
		};

		private static void Accessibility(ApiAccessibility old, ApiAccessibility @new, Action<string, string, ApiAudience> changed)
		{
			if (old == @new)
				return;
			// public, then protected internal, then protected
			static int Rank(ApiAccessibility a) => a switch
			{
				ApiAccessibility.Public => 2,
				ApiAccessibility.ProtectedInternal => 1,
				_ => 0
			};

			changed(Access(old), Access(@new), Rank(@new) < Rank(old) ? ApiAudience.Callers : ApiAudience.None);
		}

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
			ApiRefKind.RefReadOnly => "ref readonly",
			_ => "by value"
		};

		private static void Flag(
			ApiTypeModifiers old,
			ApiTypeModifiers @new,
			ApiTypeModifiers flag,
			string word,
			ApiAudience added,
			ApiAudience removed,
			Action<string, string?, string?, ApiAudience, ApiCompatibility, string?> changed
		)
		{
			var had = (old & flag) != 0;
			var has = (@new & flag) != 0;
			if (had == has)
				return;
			var breaks = has ? added : removed;
			changed(word, had ? word : "not " + word, has ? word : "not " + word, breaks, breaks == ApiAudience.None ? ApiCompatibility.None : Both, null);
		}

		/// <summary>
		/// Attributes, by type: [Obsolete] that becomes an error breaks callers' builds; other attributes say something
		/// about the API without breaking it.
		/// </summary>
		private static void Attributes(
			IReadOnlyList<ApiAttribute> old,
			IReadOnlyList<ApiAttribute> @new,
			Action<string, string?, string?, ApiAudience, ApiCompatibility> changed
		)
		{
			var oldByName = old.GroupBy(a => a.Type.FullName).ToDictionary(g => g.Key, g => g.Select(Text).OrderBy(t => t, StringComparer.Ordinal).ToList());
			var newByName = @new.GroupBy(a => a.Type.FullName).ToDictionary(g => g.Key, g => g.Select(Text).OrderBy(t => t, StringComparer.Ordinal).ToList());
			foreach (var name in oldByName.Keys.Union(newByName.Keys).OrderBy(n => n, StringComparer.Ordinal))
			{
				var o = oldByName.TryGetValue(name, out var oldTexts) ? string.Join(", ", oldTexts) : null;
				var n = newByName.TryGetValue(name, out var newTexts) ? string.Join(", ", newTexts) : null;
				if (o == n)
					continue;
				var isError = name == Obsolete && IsError(@new) && !IsError(old);
				changed("attribute", o, n, isError ? ApiAudience.Callers : ApiAudience.None, isError ? ApiCompatibility.Source : ApiCompatibility.None);
			}
		}

		private static bool IsError(IReadOnlyList<ApiAttribute> attributes) =>
			attributes.Any(a => a.Type.FullName == Obsolete && a.Arguments.Count >= 2 && a.Arguments[1] == "true");

		private static string Text(ApiAttribute attribute) =>
			"[" + NonGeneric(attribute.Type.Name).Replace("Attribute", "") +
				(attribute.Arguments.Count == 0 ? "" : "(" + string.Join(", ", attribute.Arguments) + ")") + "]";

		private static string? Deprecated(IReadOnlyList<ApiAttribute> attributes) =>
			attributes.Any(a => a.Type.FullName == Obsolete) ? "deprecated, now removed" : null;

		private static readonly ApiCompatibility Both = ApiCompatibility.Binary | ApiCompatibility.Source;

		// classification

		private void Add(
			List<string> path,
			ApiChangeKind kind,
			string part,
			string? old,
			string? @new,
			ApiAudience breaks,
			ApiCompatibility compatibility,
			ApiType? declaring,
			object? oldNode,
			object? newNode,
			string? note = null
		)
		{
			var counts = true;
			if (breaks != ApiAudience.None && (breaks & ApiAudience.Callers) == 0 && declaring is not null)
			{
				// a type that cannot be implemented or derived from outside has nobody to break
				if (!declaring.CanBeExtendedOutside)
				{
					breaks = ApiAudience.None;
					compatibility = ApiCompatibility.None;
				}
				else if (NotAnExtensionPoint(declaring, path) is { } reason)
				{
					counts = false;
					note = Join(note, $"{TypeName(path)} is not an extension point ({reason})");
				}
			}
			// a break that counts asks for a major version; anything else callers can see, for a minor one; a member
			// that is still found through a base class, for a patch
			var level = breaks != ApiAudience.None && counts
				? SuggestedVersionChange.Major
				: kind == ApiChangeKind.Removed && breaks == ApiAudience.None ? SuggestedVersionChange.Patch : SuggestedVersionChange.Minor;
			Changes.Add(new ApiChange(path, kind, part, old, @new, breaks, compatibility, counts, note)
			{
				OldNode = oldNode,
				NewNode = newNode,
				OldScope = _oldScope,
				Level = level
			});
		}

		private static string TypeName(List<string> path) => path.Count > 1 ? path[1] : path[0];

		private static string? Join(string? first, string? second) => first is null ? second : second is null ? first : first + "; " + second;

		/// <summary>
		/// Why a type is not an extension point: an attribute named InternalImplementationOnlyAttribute, in either
		/// version, declares it; the options can configure it. Null when it is one.
		/// </summary>
		private string? NotAnExtensionPoint(ApiType type, List<string> path)
		{
			static bool Declares(ApiType t) => t.Attributes.Any(a => a.Type.Name == "InternalImplementationOnlyAttribute");

			if (Declares(type) || (_pairs.TryGetValue(type, out var counterpart) && Declares(counterpart)))
				return "declared";
			var fullName = type.Namespace + "." + type.Name;
			foreach (var pattern in options.NotExtensionPoints)
				if (Regex.IsMatch(fullName, "^" + Regex.Escape(pattern).Replace("\\*", ".*") + "$"))
					return "configured";
			return null;
		}

		// keys and segments

		/// <summary>A member's identity: its kind, name, number of type parameters and parameter types, without what only the compiler reads.</summary>
		private static string Key(ApiMember member, ApiType declaring) => member switch
		{
			ApiField f => "field " + f.Name,
			ApiProperty p => "property " + p.Name + "[" + string.Join(",", p.Parameters.Select(Parameter)) + "]",
			ApiEvent e => "event " + e.Name,
			ApiMethod m =>
				"method " + m.Name + "`" + m.TypeParameters.Count + "(" + string.Join(",", m.Parameters.Select(Parameter)) + ")" +
					(m.Kind == ApiMethodKind.Conversion ? Erase(m.ReturnType) : ""),
			_ => member.Name,
		};

		private static string Parameter(ApiParameter parameter) => (parameter.RefKind == ApiRefKind.None ? "" : "&") + Erase(parameter.Type);

		/// <summary>A type as the runtime sees it: no nullability, tuple names, dynamic or native integers.</summary>
		internal static string Erase(ApiTypeReference type) => type switch
		{
			ApiNamedType named =>
				(named.DeclaringType is null ? named.Namespace + "." + named.Name : Erase(named.DeclaringType) + "/" + named.Name) +
					(named.TypeArguments.Count == 0 ? "" : "<" + string.Join(",", named.TypeArguments.Select(Erase)) + ">"),
			ApiTupleType tuple => "System.ValueTuple<" + string.Join(",", tuple.Elements.Select(e => Erase(e.Type))) + ">",
			ApiArrayType array => Erase(array.ElementType) + "[" + new string(',', array.Rank - 1) + "]",
			ApiPointerType pointer => Erase(pointer.ElementType) + "*",
			ApiFunctionPointerType function => "delegate*<" + string.Join(",", function.ParameterTypes.Append(function.ReturnType).Select(Erase)) + ">",
			ApiTypeParameterReference parameter => (parameter.IsMethodParameter ? "!!" : "!") + parameter.Name,
			ApiDynamicType => "System.Object",
			ApiNativeIntegerType native => native.IsUnsigned ? "System.UIntPtr" : "System.IntPtr",
			ApiModifiedType modified => Erase(modified.Type),
			_ => type.ToString() ?? "",
		};

		private static string NonGeneric(string metadataName)
		{
			var tick = metadataName.LastIndexOf('`');
			return tick < 0 ? metadataName : metadataName.Substring(0, tick);
		}

		private static string TypeSegment(ApiType type) =>
			NonGeneric(type.Name) + (type.TypeParameters.Count == 0 ? "" : "<" + string.Join(", ", type.TypeParameters.Select(p => p.Name)) + ">");

		/// <summary>A type without nullable annotations on reference types; a nullable value type is a type of its own and stays.</summary>
		private static ApiTypeReference WithoutAnnotations(ApiTypeReference type) => type switch
		{
			ApiNamedType named =>
				named with
				{
					IsNullable = false,
					DeclaringType = named.DeclaringType is null ? null : (ApiNamedType)WithoutAnnotations(named.DeclaringType),
					TypeArguments = named.TypeArguments.Select(WithoutAnnotations).ToList(),
				},
			ApiTupleType tuple =>
				tuple with { IsNullable = false, Elements = tuple.Elements.Select(e => e with { Type = WithoutAnnotations(e.Type) }).ToList() },
			ApiArrayType array => array with { IsNullable = false, ElementType = WithoutAnnotations(array.ElementType) },
			ApiPointerType pointer => pointer with { ElementType = WithoutAnnotations(pointer.ElementType) },
			ApiModifiedType modified => modified with { Type = WithoutAnnotations(modified.Type) },
			_ => type with { IsNullable = false },
		};

		private static string MemberSegment(ApiMember member, ApiType declaring)
		{
			// a path names a member by its identity, which nullability is not part of: either version gives the same one
			string Types(IEnumerable<ApiParameter> parameters) =>
				string.Join(", ", parameters.Select(p => ApiWriter.Reference(WithoutAnnotations(p.Type), declaring)));

			return member switch
			{
				ApiMethod { Kind: ApiMethodKind.Constructor } m => NonGeneric(declaring.Name) + "(" + Types(m.Parameters) + ")",
				ApiMethod { Kind: ApiMethodKind.Conversion } m =>
					(m.Name == "op_Implicit" ? "implicit" : "explicit") + " operator " + ApiWriter.Reference(m.ReturnType, declaring) + "(" + Types(
						m.Parameters
					) + ")",
				ApiMethod m =>
					m.Name + (m.TypeParameters.Count == 0 ? "" : "<" + string.Join(", ", m.TypeParameters.Select(p => p.Name)) + ">") + "(" + Types(
						m.Parameters
					) + ")",
				ApiProperty { Parameters.Count: > 0 } p => "this[" + Types(p.Parameters) + "]",
				_ => member.Name,
			};
		}
	}
}

/// <summary>Compares by reference: two records with the same contents are still two types.</summary>
internal sealed class TypeIdentity : IEqualityComparer<ApiType>
{
	public static readonly TypeIdentity Instance = new();

	public bool Equals(ApiType? x, ApiType? y) => ReferenceEquals(x, y);

	public int GetHashCode(ApiType obj) => System.Runtime.CompilerServices.RuntimeHelpers.GetHashCode(obj);
}
