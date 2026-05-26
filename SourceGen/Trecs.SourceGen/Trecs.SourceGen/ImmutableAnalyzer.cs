#nullable enable

using System.Collections.Concurrent;
using System.Collections.Generic;
using System.Collections.Immutable;
using System.Linq;
using Microsoft.CodeAnalysis;
using Microsoft.CodeAnalysis.CSharp;
using Microsoft.CodeAnalysis.CSharp.Syntax;
using Microsoft.CodeAnalysis.Diagnostics;

namespace Trecs.SourceGen
{
    /// <summary>
    /// Enforces immutability for managed shared blobs.
    ///
    /// <para><b>TRECS125</b> — at every <c>SharedPtr&lt;T&gt;</c> instantiation
    /// site (field, local, parameter, generic method type arg, factory call
    /// like <c>SharedPtr.Alloc&lt;T&gt;(...)</c>), require T to be either:
    /// <list type="bullet">
    ///   <item>a <b>class</b> marked <c>[Trecs.Immutable]</c> — the class
    ///   itself is structurally immutable, audited by TRECS126;</item>
    ///   <item>an <b>interface</b> marked <c>[Trecs.Immutable]</c> — the
    ///   interface represents the read-only face of an underlying mutable
    ///   concrete; callers holding the SharedPtr see only the immutable
    ///   surface even though the implementation may be pool-built. Audited
    ///   by TRECS126 with a smaller rule set;</item>
    ///   <item>or one of the implicitly-allowed types (<c>string</c>; the
    ///   marker doesn't make sense on primitives since <c>SharedPtr&lt;T&gt;</c>
    ///   already constrains T to a reference type).</item>
    /// </list>
    /// Unresolved type parameters defer to the instantiation site.</para>
    ///
    /// <para><b>TRECS126</b> — validate the contents of any type declared
    /// with <c>[Trecs.Immutable]</c>.
    /// <br/>
    /// <b>For classes:</b>
    /// <list type="bullet">
    ///   <item>Every instance field must be declared <c>readonly</c>.</item>
    ///   <item>No publicly-settable property setters (public / internal /
    ///   protected <c>set</c>). <c>init</c> accessors are allowed.</item>
    ///   <item>Field-like events are rejected (their compiler-generated
    ///   backing field is mutable through the <c>add</c>/<c>remove</c>
    ///   accessors).</item>
    ///   <item>Public / internal / protected fields and properties must be
    ///   declared with a "safe" type — recursively: primitives, <c>string</c>,
    ///   enums, <c>readonly struct</c>, pure-value structs (every field is
    ///   recursively safe, none are reference-typed, the struct exposes no
    ///   writable indexer setter, holds no unsafe pointer field, and is not
    ///   decorated with Unity's <c>[NativeContainer]</c> — admits
    ///   <c>Unity.Mathematics.float3</c> / <c>quaternion</c> / etc. while
    ///   still rejecting writable native containers like <c>NativeArray&lt;T&gt;</c>
    ///   / <c>NativeSlice&lt;T&gt;</c> / <c>NativeHashMap&lt;K, V&gt;</c>),
    ///   <c>[Immutable]</c> classes or interfaces, and an external-library allowlist
    ///   of immutable / read-only views (<c>ImmutableArray&lt;T&gt;</c>,
    ///   <c>ReadOnlyMemory&lt;T&gt;</c>, <c>ReadOnlyCollection&lt;T&gt;</c>,
    ///   <c>IReadOnlyList&lt;T&gt;</c>, <c>IReadOnlyDictionary&lt;K, V&gt;</c>,
    ///   the Unity native read-only views — <c>NativeArray&lt;T&gt;.ReadOnly</c>,
    ///   <c>NativeHashMap&lt;K, V&gt;.ReadOnly</c>, <c>NativeHashSet&lt;T&gt;.ReadOnly</c>,
    ///   <c>NativeParallelHashMap&lt;K, V&gt;.ReadOnly</c>,
    ///   <c>NativeParallelMultiHashMap&lt;K, V&gt;.ReadOnly</c> — etc.).</item>
    ///   <item>Private instance fields are exempt from the type check — the
    ///   canonical "wrap a mutable <c>float[]</c> as a private readonly
    ///   field and expose <c>ReadOnlySpan&lt;float&gt;</c>" pattern is
    ///   permitted.</item>
    ///   <item>Base class (other than <c>object</c>) must directly carry
    ///   <c>[Immutable]</c> as well. The marker does not inherit; every
    ///   class in a chain opts in explicitly so the analyzer validates
    ///   each one.</item>
    /// </list>
    /// <b>For interfaces:</b>
    /// <list type="bullet">
    ///   <item>No publicly-settable property accessors (<c>init</c> is
    ///   allowed).</item>
    ///   <item>No events.</item>
    ///   <item>Public property types must be in the same safe-type set as
    ///   for classes (recursively).</item>
    /// </list>
    /// Interfaces have no instance fields and no base class, so those rules
    /// don't apply.</para>
    ///
    /// <para><b>TRECS127</b> — warn (severity Warning, default-on) when a
    /// method declared on an <c>[Immutable]</c> interface returns a type
    /// that is not safe per the same walker TRECS126 uses for fields and
    /// properties. Methods are the v1 escape hatch for the interface route
    /// — they let a fat retrofit-heavy concrete expose query helpers without
    /// forcing the entire reachable object graph to be <c>[Immutable]</c>.
    /// The warning surfaces the looseness in review without breaking
    /// existing adopters mid-migration. Authors opt out at the declaration
    /// site with <c>[Trecs.AllowMutableReturn]</c>; reviewer-facing rationale
    /// goes in a comment above the attribute when useful. <c>void</c>
    /// returns and non-ordinary methods (property/event accessors,
    /// operators, etc.) are excluded —
    /// property safety is already covered by TRECS126. Class-route
    /// <c>[Immutable]</c> is intentionally not checked: structurally tight
    /// classes have a different audit and the per-class method-leak risk
    /// profile is different.</para>
    /// </summary>
    [DiagnosticAnalyzer(LanguageNames.CSharp)]
    public class ImmutableAnalyzer : DiagnosticAnalyzer
    {
        const string TrecsNamespace = "Trecs";
        const string ImmutableAttributeName = "ImmutableAttribute";
        const string AllowMutableReturnAttributeName = "AllowMutableReturnAttribute";
        const string SharedPtrName = "SharedPtr";

        public override ImmutableArray<DiagnosticDescriptor> SupportedDiagnostics { get; } =
            ImmutableArray.Create(
                DiagnosticDescriptors.SharedPtrTypeMustBeImmutable,
                DiagnosticDescriptors.ImmutableTypeViolatesContract,
                DiagnosticDescriptors.ImmutableInterfaceMethodMutableReturn
            );

        public override void Initialize(AnalysisContext context)
        {
            context.ConfigureGeneratedCodeAnalysis(GeneratedCodeAnalysisFlags.None);
            context.EnableConcurrentExecution();

            context.RegisterCompilationStartAction(start =>
            {
                var safeTypeCache = new ConcurrentDictionary<ITypeSymbol, bool>(
                    SymbolEqualityComparer.Default
                );

                start.RegisterSyntaxNodeAction(
                    ctx => AnalyzeGenericName(ctx, safeTypeCache),
                    SyntaxKind.GenericName
                );

                start.RegisterSymbolAction(
                    ctx => AnalyzeImmutableType(ctx, safeTypeCache),
                    SymbolKind.NamedType
                );
            });
        }

        // ─────────────────────────── TRECS125 ────────────────────────────

        static void AnalyzeGenericName(
            SyntaxNodeAnalysisContext context,
            ConcurrentDictionary<ITypeSymbol, bool> cache
        )
        {
            var generic = (GenericNameSyntax)context.Node;
            var symbolInfo = context.SemanticModel.GetSymbolInfo(
                generic,
                context.CancellationToken
            );

            var symbol =
                symbolInfo.Symbol
                ?? (
                    symbolInfo.CandidateSymbols.IsDefaultOrEmpty
                        ? null
                        : symbolInfo.CandidateSymbols[0]
                );

            ITypeSymbol? typeArg = null;

            if (symbol is INamedTypeSymbol named)
            {
                if (!IsSharedPtrGenericType(named))
                    return;
                if (named.TypeArguments.Length != 1)
                    return;
                typeArg = named.TypeArguments[0];
            }
            else if (symbol is IMethodSymbol method)
            {
                if (!IsSharedPtrStaticFactoryMethod(method))
                    return;
                if (method.TypeArguments.Length != 1)
                    return;
                typeArg = method.TypeArguments[0];
            }
            else
            {
                return;
            }

            if (typeArg is null)
                return;
            if (IsAcceptableSharedPtrTypeArg(typeArg))
                return;

            context.ReportDiagnostic(
                Diagnostic.Create(
                    DiagnosticDescriptors.SharedPtrTypeMustBeImmutable,
                    generic.GetLocation(),
                    typeArg.ToDisplayString(SymbolDisplayFormat.MinimallyQualifiedFormat)
                )
            );
        }

        static bool IsSharedPtrGenericType(INamedTypeSymbol type)
        {
            if (type.ContainingNamespace?.ToDisplayString() != TrecsNamespace)
                return false;
            return type.OriginalDefinition.Name == SharedPtrName;
        }

        static bool IsSharedPtrStaticFactoryMethod(IMethodSymbol method)
        {
            // Non-generic static helper Trecs.SharedPtr hosts Alloc / Acquire / TryGet /
            // GetOrAlloc factories. Arity == 0 distinguishes it from the generic struct.
            if (!method.IsStatic)
                return false;
            if (method.ContainingType is not INamedTypeSymbol containing)
                return false;
            if (containing.ContainingNamespace?.ToDisplayString() != TrecsNamespace)
                return false;
            if (containing.Name != SharedPtrName)
                return false;
            return containing.Arity == 0;
        }

        static bool IsAcceptableSharedPtrTypeArg(ITypeSymbol type)
        {
            // Defer unresolved type parameters — the instantiation site catches concrete T.
            if (type is ITypeParameterSymbol)
                return true;

            // SharedPtr's T : class constraint means only reference types reach here.
            // string is the canonical "always immutable" reference type.
            if (type.SpecialType == SpecialType.System_String)
                return true;

            // Class with [Immutable] (structural-immutability route) OR interface with
            // [Immutable] (read-only-face route over a possibly-mutable concrete). Both
            // are validated by AnalyzeImmutableType (TRECS126) with appropriate rule sets.
            if (type is INamedTypeSymbol named)
            {
                if (
                    (named.TypeKind == TypeKind.Class || named.TypeKind == TypeKind.Interface)
                    && HasImmutableAttribute(named)
                )
                    return true;
            }

            return false;
        }

        // ─────────────────────────── TRECS126 ────────────────────────────

        static void AnalyzeImmutableType(
            SymbolAnalysisContext context,
            ConcurrentDictionary<ITypeSymbol, bool> safeTypeCache
        )
        {
            var type = (INamedTypeSymbol)context.Symbol;
            if (type.TypeKind != TypeKind.Class && type.TypeKind != TypeKind.Interface)
                return;
            if (!HasImmutableAttribute(type))
                return;

            var violations = new List<string>();
            // Base-class audit only applies to classes — interfaces have no base. Field
            // and event audits apply to both via CheckMembers (interfaces can't declare
            // instance fields, so CheckField never fires there; events are still illegal).
            if (type.TypeKind == TypeKind.Class)
                CheckBaseClass(type, violations);
            CheckMembers(type, violations, safeTypeCache);

            // TRECS127: warn on interface methods that return a non-safe type without
            // an [AllowMutableReturn] opt-out. Class methods are intentionally not
            // checked — class-route [Immutable] has a different (structural) audit
            // and the per-class methods are not the v1 escape vector this rule
            // targets. Reported per-method (separate diagnostic from TRECS126) so the
            // squiggle lands at the method's own declaration site.
            if (type.TypeKind == TypeKind.Interface)
                CheckInterfaceMethods(context, type, safeTypeCache);

            if (violations.Count == 0)
                return;

            // Stable order so tests don't depend on member enumeration order.
            violations.Sort(System.StringComparer.Ordinal);

            context.ReportDiagnostic(
                Diagnostic.Create(
                    DiagnosticDescriptors.ImmutableTypeViolatesContract,
                    type.Locations.Length > 0 ? type.Locations[0] : Location.None,
                    type.Name,
                    string.Join("; ", violations)
                )
            );
        }

        // ─────────────────────────── TRECS127 ────────────────────────────

        static void CheckInterfaceMethods(
            SymbolAnalysisContext context,
            INamedTypeSymbol iface,
            ConcurrentDictionary<ITypeSymbol, bool> safeTypeCache
        )
        {
            foreach (var member in iface.GetMembers())
            {
                if (member is not IMethodSymbol method)
                    continue;
                // Ordinary methods only — skip property/event accessors, operators,
                // conversions, constructors, finalizers, etc. Property safety is
                // already covered by TRECS126's property-type check.
                if (method.MethodKind != MethodKind.Ordinary)
                    continue;
                if (method.IsImplicitlyDeclared)
                    continue;
                // A void return can't leak a reference. Mutating-through-callback
                // patterns aren't what this rule targets and aren't caught here.
                if (method.ReturnsVoid)
                    continue;
                if (HasAllowMutableReturnAttribute(method))
                    continue;
                if (IsSafeType(method.ReturnType, safeTypeCache))
                    continue;

                var location =
                    method.Locations.Length > 0
                        ? method.Locations[0]
                        : (iface.Locations.Length > 0 ? iface.Locations[0] : Location.None);

                context.ReportDiagnostic(
                    Diagnostic.Create(
                        DiagnosticDescriptors.ImmutableInterfaceMethodMutableReturn,
                        location,
                        method.Name,
                        iface.Name,
                        method.ReturnType.ToDisplayString(
                            SymbolDisplayFormat.MinimallyQualifiedFormat
                        )
                    )
                );
            }
        }

        static bool HasAllowMutableReturnAttribute(IMethodSymbol method)
        {
            foreach (var attr in method.GetAttributes())
            {
                var ac = attr.AttributeClass;
                if (ac is null)
                    continue;
                if (ac.Name != AllowMutableReturnAttributeName)
                    continue;
                if (ac.ContainingNamespace?.ToDisplayString() != TrecsNamespace)
                    continue;
                return true;
            }
            return false;
        }

        static void CheckBaseClass(INamedTypeSymbol type, List<string> violations)
        {
            var baseType = type.BaseType;
            if (baseType is null)
                return;
            if (baseType.SpecialType == SpecialType.System_Object)
                return;
            if (HasImmutableAttribute(baseType))
                return;
            violations.Add(
                $"base class '{baseType.ToDisplayString(SymbolDisplayFormat.MinimallyQualifiedFormat)}' is not marked [Immutable]"
            );
        }

        static void CheckMembers(
            INamedTypeSymbol type,
            List<string> violations,
            ConcurrentDictionary<ITypeSymbol, bool> safeTypeCache
        )
        {
            foreach (var member in type.GetMembers())
            {
                switch (member)
                {
                    case IFieldSymbol field:
                        CheckField(field, violations, safeTypeCache);
                        break;
                    case IPropertySymbol prop:
                        CheckProperty(prop, violations, safeTypeCache);
                        break;
                    case IEventSymbol evt when !evt.IsStatic:
                        violations.Add($"event '{evt.Name}' is not allowed");
                        break;
                }
            }
        }

        static void CheckField(
            IFieldSymbol field,
            List<string> violations,
            ConcurrentDictionary<ITypeSymbol, bool> safeTypeCache
        )
        {
            if (field.IsStatic || field.IsConst)
                return;
            // Compiler-generated backing fields for `init`-only / read-only auto-properties
            // are flagged as IsReadOnly == true already, and the auto-property is checked
            // via its IPropertySymbol. Skip implicitly-declared fields to avoid double-firing.
            if (field.IsImplicitlyDeclared)
                return;

            if (!field.IsReadOnly)
            {
                violations.Add($"instance field '{field.Name}' is not declared 'readonly'");
            }

            // Private fields are intentionally not type-checked — the encapsulation is
            // the developer's responsibility (e.g. `private readonly float[] _heights`
            // exposed only as a ReadOnlySpan<float>).
            if (
                field.DeclaredAccessibility != Accessibility.Private
                && !IsSafeType(field.Type, safeTypeCache)
            )
            {
                violations.Add(
                    $"non-private field '{field.Name}' has type '{field.Type.ToDisplayString(SymbolDisplayFormat.MinimallyQualifiedFormat)}' which is not provably immutable"
                );
            }
        }

        static void CheckProperty(
            IPropertySymbol prop,
            List<string> violations,
            ConcurrentDictionary<ITypeSymbol, bool> safeTypeCache
        )
        {
            if (prop.IsStatic)
                return;
            if (prop.IsIndexer)
                return; // Indexers are method-like; they can compute or read read-only state.
            // Skip compiler-generated members. Record types emit a synthetic
            // `EqualityContract { get; }` returning System.Type, which is a class
            // that isn't [Immutable] — without this guard the analyzer would fire
            // TRECS126 on every [Immutable] record class.
            if (prop.IsImplicitlyDeclared)
                return;

            var setter = prop.SetMethod;
            if (setter is not null && !setter.IsInitOnly)
            {
                // Disallow public/internal/protected non-init setters. Private setters are OK
                // (they're invisible to external code; treated like a constructor-time helper).
                if (setter.DeclaredAccessibility != Accessibility.Private)
                {
                    violations.Add(
                        $"property '{prop.Name}' has a settable accessor (use 'init' or remove it)"
                    );
                }
            }

            if (prop.DeclaredAccessibility != Accessibility.Private)
            {
                if (!IsSafeType(prop.Type, safeTypeCache))
                {
                    violations.Add(
                        $"non-private property '{prop.Name}' has type '{prop.Type.ToDisplayString(SymbolDisplayFormat.MinimallyQualifiedFormat)}' which is not provably immutable"
                    );
                }
            }
        }

        // ─────────────────────── "safe type" walker ──────────────────────

        static bool IsSafeType(ITypeSymbol type, ConcurrentDictionary<ITypeSymbol, bool> cache)
        {
            return IsSafeTypeNamed(type, cache, visiting: null);
        }

        static bool IsSafeTypeNamed(
            ITypeSymbol type,
            ConcurrentDictionary<ITypeSymbol, bool> cache,
            HashSet<ITypeSymbol>? visiting
        )
        {
            if (cache.TryGetValue(type, out var cached))
                return cached;

            // Cycle guard. Treat any in-progress lookup as safe to close the loop.
            visiting ??= new HashSet<ITypeSymbol>(SymbolEqualityComparer.Default);
            if (!visiting.Add(type))
                return true;

            try
            {
                bool result = ComputeIsSafe(type, cache, visiting);
                cache[type] = result;
                return result;
            }
            finally
            {
                visiting.Remove(type);
            }
        }

        static bool ComputeIsSafe(
            ITypeSymbol type,
            ConcurrentDictionary<ITypeSymbol, bool> cache,
            HashSet<ITypeSymbol> visiting
        )
        {
            // Unresolved type parameter: can't verify, assume safe (rare in [Immutable] types;
            // user shouldn't introduce them, but defer rather than block).
            if (type is ITypeParameterSymbol)
                return true;

            // Built-in primitives.
            switch (type.SpecialType)
            {
                case SpecialType.System_Boolean:
                case SpecialType.System_Char:
                case SpecialType.System_SByte:
                case SpecialType.System_Byte:
                case SpecialType.System_Int16:
                case SpecialType.System_UInt16:
                case SpecialType.System_Int32:
                case SpecialType.System_UInt32:
                case SpecialType.System_Int64:
                case SpecialType.System_UInt64:
                case SpecialType.System_Single:
                case SpecialType.System_Double:
                case SpecialType.System_Decimal:
                case SpecialType.System_IntPtr:
                case SpecialType.System_UIntPtr:
                case SpecialType.System_String:
                    return true;
            }

            // Enums.
            if (type.TypeKind == TypeKind.Enum)
                return true;

            // Arrays are never safe — elements can be reassigned.
            if (type is IArrayTypeSymbol)
                return false;

            if (type is INamedTypeSymbol named)
            {
                // External-library allowlist — generic immutable / read-only views and
                // interfaces from the BCL and from Unity.Collections (NativeArray<T>.ReadOnly
                // and friends — read-only "views" over native memory whose indexers don't
                // expose a setter). For each type argument we only recurse on reference
                // types: if the element type is a value type, the container's indexer
                // returns a by-value copy and any element mutation lands on the copy, not
                // on the blob's storage. Reference-type elements share identity with the
                // storage, so they must themselves be immutable.
                //
                // This check must run before the struct branches below: several of the
                // Unity entries (NativeArray<T>.ReadOnly, NativeHashMap<K, V>.ReadOnly,
                // etc.) are non-readonly structs whose internal storage is an unsafe
                // pointer plus a safety handle. The non-readonly-struct field walk
                // below would reject them on the pointer field; the allowlist asserts
                // "trust the public API surface" and short-circuits before we ever
                // inspect internals.
                if (IsAllowlistedExternalGeneric(named))
                {
                    foreach (var arg in named.TypeArguments)
                    {
                        if (arg.IsValueType)
                            continue;
                        if (!IsSafeTypeNamed(arg, cache, visiting))
                            return false;
                    }
                    return true;
                }

                // Explicit blocklist: System.Span<T> is a `readonly ref struct` but its
                // indexer returns `ref T`, so it would pass the generic readonly-struct
                // gate below while still letting callers mutate elements. ReadOnlySpan<T>
                // is also a readonly ref struct but only exposes `ref readonly T`, so it
                // remains safe and reaches the readonly-struct branch below. (The
                // writable-indexer-setter check we apply to all structs below does NOT
                // catch Span<T> — its indexer has no `set` accessor; mutation flows
                // through the `ref T` return of the `get` instead. Hence the explicit
                // blocklist still earns its keep.)
                if (
                    named.TypeKind == TypeKind.Struct
                    && named.OriginalDefinition.Name == "Span"
                    && named.ContainingNamespace?.ToDisplayString() == "System"
                )
                    return false;

                // ── Structural rejections that apply to both readonly and non-readonly
                // structs (the allowlist branch above short-circuits before we get here,
                // so legitimate read-only views — e.g. NativeArray<T>.ReadOnly — that
                // happen to contain unsafe pointers are still accepted).
                //
                // These three checks close the gap that the pure-value-non-readonly-struct
                // rule below leaves open: a struct can be mutable through an indexer setter
                // or through pointer-addressed memory without holding any reference-typed
                // instance field, so the "no reference field" rule alone admits writable
                // Unity native containers (NativeArray<T>, NativeSlice<T>, NativeHashMap<K,V>,
                // …). CS1648 protects `instance.Pos.X = 5;` on a readonly value-typed field
                // but does NOT protect `instance.Values[0] = 5;` because the indexer setter
                // writes through the pointer the container holds, not to the container
                // value itself.
                if (named.TypeKind == TypeKind.Struct)
                {
                    // (a) Reject any struct decorated with Unity's [NativeContainer]. The
                    // attribute marks the type as a wrapper over native memory with
                    // mutation verbs through pointer-addressed storage; combined with the
                    // explicit .ReadOnly allowlist above this closes the loop on the
                    // Unity-native side (writable native containers rejected by attribute,
                    // read-only views accepted by allowlist).
                    if (HasNativeContainerAttribute(named))
                        return false;

                    // (b) Reject any struct that exposes a writable indexer setter (other
                    // than init-only). The indexer setter is the canonical mutation
                    // surface that bypasses the field-readonly guarantee — e.g.
                    // `NativeArray<T>.this[int] { get; set; }`. We don't distinguish on
                    // accessibility: even an internal setter could be reachable from
                    // friend assemblies and a private setter is still a mutation verb the
                    // type's own methods can exploit. Worth the false-positive risk on
                    // "smart" value-type wrappers — currently there are none in trecs.
                    if (HasWritableIndexerSetter(named))
                        return false;

                    // (c) Reject any struct with a pointer instance field. We can't
                    // analyze where the pointer points, so we can't prove non-mutation.
                    // Mirrors the principle that pointer fields are an analyzer dead-end.
                    // Real Unity types like NativeArray<T> hold an unsafe pointer plus an
                    // AtomicSafetyHandle; the .ReadOnly views have the same shape but
                    // pass via the allowlist branch above. Anything that reaches here
                    // with a pointer field is unaccounted for and should be rejected.
                    if (HasPointerInstanceField(named))
                        return false;
                }

                // readonly struct. We still need to recurse on reference-type type
                // arguments — `readonly struct Box<T> { public readonly T Value; }` with
                // T = MutableClass exposes the inner reference, and mutating through it
                // corrupts the blob's logical state. Value-type args are returned by
                // copy at every access, so they don't propagate the concern.
                if (named.TypeKind == TypeKind.Struct && named.IsReadOnly)
                {
                    foreach (var arg in named.TypeArguments)
                    {
                        if (arg.IsValueType)
                            continue;
                        if (!IsSafeTypeNamed(arg, cache, visiting))
                            return false;
                    }
                    return true;
                }

                // Non-readonly struct: safe IFF every instance field is itself safe and
                // none are reference-typed. This admits pure-POD value types like
                // Unity.Mathematics.float3 / quaternion / int3 — they're declared as
                // plain `struct` (mutable fields), but a `public readonly float3 Pos;`
                // field on an [Immutable] class still can't be mutated by callers
                // (CS1648 forbids writing to members of a readonly value-typed field),
                // and a `float3 Pos { get; }` interface property returns a fresh copy
                // every access. The risk only arises when the struct contains a
                // reference-typed field — then a caller can navigate to the heap
                // object and mutate it. Rejecting bare reference-type fields keeps that
                // hazard contained without needing a hand-maintained namespace allowlist.
                // Mutation-via-indexer / pointer-field hazards are caught by the
                // structural rejections above before we reach this point.
                if (named.TypeKind == TypeKind.Struct)
                {
                    foreach (var member in named.GetMembers())
                    {
                        if (member is not IFieldSymbol field)
                            continue;
                        if (field.IsStatic || field.IsConst)
                            continue;
                        if (field.Type.IsReferenceType)
                            return false;
                        if (!IsSafeTypeNamed(field.Type, cache, visiting))
                            return false;
                    }
                    return true;
                }

                // [Immutable] class — fully audited by TRECS126.
                if (named.TypeKind == TypeKind.Class && HasImmutableAttribute(named))
                    return true;

                // [Immutable] interface — the read-only-face route. The interface's own
                // member surface is audited by TRECS126 (no settable accessors, no events,
                // property types must themselves be safe), so by the time we see a field
                // typed as the interface we can trust callers won't reach mutable state
                // through the interface API. Same caveat about downcast applies as
                // elsewhere — that's a convention, not a type-system guarantee.
                if (named.TypeKind == TypeKind.Interface && HasImmutableAttribute(named))
                    return true;
            }

            return false;
        }

        // Allowlist keyed on (namespace, dotted-name). The dotted-name handles nested types
        // such as `NativeArray<T>.ReadOnly` whose `OriginalDefinition.Name` is just
        // `"ReadOnly"` — without walking the ContainingType chain we'd alias across every
        // `Foo.ReadOnly` in the same namespace. Generic arity of each segment is implicit
        // via the type-arg recurse.
        //
        // The Unity entries cover read-only "views" over native memory shared by the
        // canonical `AsReadOnly()` helper on the parent container. Each is either a
        // nested `ReadOnly` struct (NativeArray, NativeHashMap, NativeHashSet,
        // NativeParallelHashMap, NativeParallelMultiHashMap) or a top-level read-only
        // alias. Notably absent:
        //
        //   * `NativeList<T>.ReadOnly` — there is no such nested type; `NativeList<T>.AsReadOnly()`
        //     returns `NativeArray<T>.ReadOnly`, which is already covered above.
        //   * `NativeSlice<T>` — the task originally proposed allowlisting it as "read-only
        //     by API surface", but Unity's NativeSlice<T> exposes `this[int index] { get; set; }`,
        //     so a `readonly NativeSlice<float> Slice;` field on an [Immutable] class would let
        //     callers do `instance.Slice[0] = 5f`. NOT safe — deliberately omitted. (The
        //     struct-mutation-surface check upstream — see ComputeIsSafe — independently rejects
        //     it via [NativeContainer] / writable indexer setter, so omitting the allowlist
        //     entry is the right answer rather than the only line of defense.)
        //
        // The Unity.Mathematics entries are pure-POD value types whose indexer setters
        // write through `fixed (float* array = &x)` — i.e. they mutate the struct's own
        // field storage, not external pointer-addressed memory. CS1648 / defensive-copy
        // rules on readonly value-typed fields still protect [Immutable] containers, so
        // the structural mutation-surface check upstream (option b) is over-broad for
        // these. The allowlist short-circuit restores the analyzer-doc's stated intent
        // ("admits Unity.Mathematics.float3 / quaternion / etc.") without weakening
        // protection against actual native-container hazards.
        //
        // Verified against `com.unity.collections@aea9d3bd5e19` (Unity 2022/6000 era).
        static readonly HashSet<(
            string Namespace,
            string DottedName
        )> _allowlistedExternalGenerics = new()
        {
            ("System.Collections.Immutable", "ImmutableArray"),
            ("System.Collections.Immutable", "ImmutableList"),
            ("System.Collections.Immutable", "ImmutableDictionary"),
            ("System.Collections.Immutable", "ImmutableHashSet"),
            ("System.Collections.Immutable", "ImmutableSortedSet"),
            ("System.Collections.Immutable", "ImmutableSortedDictionary"),
            ("System.Collections.Immutable", "ImmutableQueue"),
            ("System.Collections.Immutable", "ImmutableStack"),
            ("System.Collections.Frozen", "FrozenSet"),
            ("System.Collections.Frozen", "FrozenDictionary"),
            ("System.Collections.ObjectModel", "ReadOnlyCollection"),
            ("System.Collections.ObjectModel", "ReadOnlyDictionary"),
            ("System", "ReadOnlyMemory"),
            ("System.Collections.Generic", "IReadOnlyList"),
            ("System.Collections.Generic", "IReadOnlyCollection"),
            ("System.Collections.Generic", "IReadOnlyDictionary"),
            ("System.Collections.Generic", "IReadOnlySet"),
            ("System.Collections.Generic", "IEnumerable"),
            ("Trecs.Collections", "IReadOnlyIterableDictionary"),
            ("Unity.Collections", "NativeArray.ReadOnly"),
            ("Unity.Collections", "NativeHashMap.ReadOnly"),
            ("Unity.Collections", "NativeHashSet.ReadOnly"),
            ("Unity.Collections", "NativeParallelHashMap.ReadOnly"),
            ("Unity.Collections", "NativeParallelMultiHashMap.ReadOnly"),
            ("Unity.Mathematics", "float2"),
            ("Unity.Mathematics", "float3"),
            ("Unity.Mathematics", "float4"),
            ("Unity.Mathematics", "float2x2"),
            ("Unity.Mathematics", "float2x3"),
            ("Unity.Mathematics", "float2x4"),
            ("Unity.Mathematics", "float3x2"),
            ("Unity.Mathematics", "float3x3"),
            ("Unity.Mathematics", "float3x4"),
            ("Unity.Mathematics", "float4x2"),
            ("Unity.Mathematics", "float4x3"),
            ("Unity.Mathematics", "float4x4"),
            ("Unity.Mathematics", "int2"),
            ("Unity.Mathematics", "int3"),
            ("Unity.Mathematics", "int4"),
            ("Unity.Mathematics", "int2x2"),
            ("Unity.Mathematics", "int2x3"),
            ("Unity.Mathematics", "int2x4"),
            ("Unity.Mathematics", "int3x2"),
            ("Unity.Mathematics", "int3x3"),
            ("Unity.Mathematics", "int3x4"),
            ("Unity.Mathematics", "int4x2"),
            ("Unity.Mathematics", "int4x3"),
            ("Unity.Mathematics", "int4x4"),
            ("Unity.Mathematics", "uint2"),
            ("Unity.Mathematics", "uint3"),
            ("Unity.Mathematics", "uint4"),
            ("Unity.Mathematics", "uint2x2"),
            ("Unity.Mathematics", "uint2x3"),
            ("Unity.Mathematics", "uint2x4"),
            ("Unity.Mathematics", "uint3x2"),
            ("Unity.Mathematics", "uint3x3"),
            ("Unity.Mathematics", "uint3x4"),
            ("Unity.Mathematics", "uint4x2"),
            ("Unity.Mathematics", "uint4x3"),
            ("Unity.Mathematics", "uint4x4"),
            ("Unity.Mathematics", "bool2"),
            ("Unity.Mathematics", "bool3"),
            ("Unity.Mathematics", "bool4"),
            ("Unity.Mathematics", "bool2x2"),
            ("Unity.Mathematics", "bool2x3"),
            ("Unity.Mathematics", "bool2x4"),
            ("Unity.Mathematics", "bool3x2"),
            ("Unity.Mathematics", "bool3x3"),
            ("Unity.Mathematics", "bool3x4"),
            ("Unity.Mathematics", "bool4x2"),
            ("Unity.Mathematics", "bool4x3"),
            ("Unity.Mathematics", "bool4x4"),
            ("Unity.Mathematics", "double2"),
            ("Unity.Mathematics", "double3"),
            ("Unity.Mathematics", "double4"),
            ("Unity.Mathematics", "double2x2"),
            ("Unity.Mathematics", "double2x3"),
            ("Unity.Mathematics", "double2x4"),
            ("Unity.Mathematics", "double3x2"),
            ("Unity.Mathematics", "double3x3"),
            ("Unity.Mathematics", "double3x4"),
            ("Unity.Mathematics", "double4x2"),
            ("Unity.Mathematics", "double4x3"),
            ("Unity.Mathematics", "double4x4"),
            ("Unity.Mathematics", "half"),
            ("Unity.Mathematics", "half2"),
            ("Unity.Mathematics", "half3"),
            ("Unity.Mathematics", "half4"),
            ("Unity.Mathematics", "quaternion"),
        };

        static bool IsAllowlistedExternalGeneric(INamedTypeSymbol type)
        {
            // Walk the containing-type chain to build the dotted name, so a nested type
            // like `Unity.Collections.NativeArray<T>.ReadOnly` is keyed as
            // ("Unity.Collections", "NativeArray.ReadOnly") rather than colliding with every
            // other `Foo.ReadOnly` in the same namespace. The original definition is used
            // for each segment so a constructed `NativeArray<int>.ReadOnly` matches the
            // unbound entry.
            var def = type.OriginalDefinition;
            var ns = def.ContainingNamespace?.ToDisplayString();
            if (ns is null)
                return false;

            // Find the outermost named type (closest to the namespace). Pre-allocate a
            // small list and reverse since most types are not deeply nested.
            var segments = new List<string>(2) { def.Name };
            for (var outer = def.ContainingType; outer is not null; outer = outer.ContainingType)
            {
                segments.Add(outer.OriginalDefinition.Name);
                ns = outer.OriginalDefinition.ContainingNamespace?.ToDisplayString() ?? ns;
            }
            segments.Reverse();
            var dotted = string.Join(".", segments);

            return _allowlistedExternalGenerics.Contains((ns, dotted));
        }

        // ───────────── struct-mutation-surface predicates ─────────────

        // Walks the struct's members for an indexer property with a non-init `set`
        // accessor. Indexer setters are the canonical "mutation through pointer-
        // addressed storage" surface that the readonly-field guarantee (CS1648)
        // doesn't cover — `instance.Container[0] = 5;` writes through the
        // container's pointer, not to the container field itself.
        //
        // We accept init-only setters (records / DTOs that happen to expose an
        // indexer init accessor) and we don't distinguish on accessibility:
        // a non-public setter is still a mutation verb reachable from friend
        // assemblies / the struct's own methods.
        static bool HasWritableIndexerSetter(INamedTypeSymbol type)
        {
            foreach (var member in type.GetMembers())
            {
                if (member is not IPropertySymbol prop)
                    continue;
                if (!prop.IsIndexer)
                    continue;
                var setter = prop.SetMethod;
                if (setter is null)
                    continue;
                if (setter.IsInitOnly)
                    continue;
                return true;
            }
            return false;
        }

        // Walks the struct's instance fields for an unsafe pointer field. Pointer
        // types are an analyzer dead-end — we can't prove what they point at or
        // whether the storage is mutable. Real Unity native containers hold one
        // (e.g. `NativeArray<T>` keeps a `void*` to native memory); their
        // `.ReadOnly` siblings share the shape but are intentionally accepted via
        // the external-library allowlist branch upstream, so this check only
        // affects structs that don't have an allowlist exemption.
        static bool HasPointerInstanceField(INamedTypeSymbol type)
        {
            foreach (var member in type.GetMembers())
            {
                if (member is not IFieldSymbol field)
                    continue;
                if (field.IsStatic || field.IsConst)
                    continue;
                if (field.Type.TypeKind == TypeKind.Pointer)
                    return true;
                if (field.Type.TypeKind == TypeKind.FunctionPointer)
                    return true;
            }
            return false;
        }

        // Recognises Unity's [Unity.Collections.LowLevel.Unsafe.NativeContainer]
        // attribute (and its `[NativeContainer]`-suffixed variants applied via
        // `[NativeContainerSupportsDeallocateOnJobCompletion]` etc., though those
        // are unrelated — we only match the exact `NativeContainerAttribute`).
        // The attribute marks the type as a wrapper over native memory with
        // mutation verbs through pointer storage; combined with the explicit
        // .ReadOnly allowlist this closes the loop on the Unity-native side.
        static bool HasNativeContainerAttribute(INamedTypeSymbol type)
        {
            foreach (var attr in type.OriginalDefinition.GetAttributes())
            {
                var ac = attr.AttributeClass;
                if (ac is null)
                    continue;
                if (ac.Name != "NativeContainerAttribute")
                    continue;
                if (
                    ac.ContainingNamespace?.ToDisplayString() != "Unity.Collections.LowLevel.Unsafe"
                )
                    continue;
                return true;
            }
            return false;
        }

        // ─────────────────────── attribute lookup ────────────────────────

        static bool HasImmutableAttribute(INamedTypeSymbol type)
        {
            foreach (var attr in type.OriginalDefinition.GetAttributes())
            {
                var ac = attr.AttributeClass;
                if (ac is null)
                    continue;
                if (ac.Name != ImmutableAttributeName)
                    continue;
                if (ac.ContainingNamespace?.ToDisplayString() != TrecsNamespace)
                    continue;
                return true;
            }
            return false;
        }
    }
}
