#nullable enable

using System.Collections.Concurrent;
using System.Collections.Generic;
using System.Collections.Immutable;
using Microsoft.CodeAnalysis;
using Microsoft.CodeAnalysis.Diagnostics;
using Microsoft.CodeAnalysis.Operations;

namespace Trecs.SourceGen
{
    /// <summary>
    /// Prevents by-value copies of structs that are non-copyable.
    ///
    /// <para>A struct is non-copyable iff it carries <c>[Trecs.NonCopyable]</c>,
    /// OR it implements <c>Trecs.IEntityComponent</c> and does not carry
    /// <c>[Trecs.Copyable]</c>, OR it transitively contains a non-static instance
    /// field whose type is non-copyable (a wrapper of a non-copyable is itself
    /// non-copyable — copying the wrapper copies the inner data the same way).</para>
    ///
    /// <para>Components live in component buffers and are accessed by reference (via
    /// aspect properties, <c>NativeComponentLookup</c> indexers, etc.); copying a
    /// component to a by-value local typically defeats that. Small flat components
    /// where copy semantics are legitimate (typed handles, configs) opt out with
    /// <c>[Copyable]</c>. Non-<c>IEntityComponent</c> structs use <c>[NonCopyable]</c>
    /// for inline-storage types like <c>FixedList256&lt;T&gt;</c>.</para>
    ///
    /// <para>Note that <c>[Copyable]</c> does <em>not</em> suppress the transitive
    /// rule: a struct that wraps a non-copyable cannot be made copyable just by
    /// adding the attribute, because the copy would still duplicate the inner
    /// non-copyable storage. The intended fix is to mark the wrapper
    /// <c>[NonCopyable]</c> too (acknowledging the constraint), or restructure
    /// so the non-copyable lives behind an indirection.</para>
    ///
    /// <para>Locals are only flagged when the initializer is a reference to
    /// existing storage (field, local, parameter, or property). Receiving a return
    /// value from a method, constructing a new instance, or <c>default</c>
    /// initialization are allowed.</para>
    ///
    /// <para>TRECS133 covers the <em>implicit</em> copy the compiler synthesizes when a
    /// non-<c>readonly</c> instance member is invoked on a non-copyable value through a
    /// read-only reference (an <c>in</c> parameter or a <c>ref readonly</c> local). Unlike
    /// TRECS118/131, this copy is nowhere in the source — the receiver is silently duplicated
    /// before the call, so the mutation lands on a throwaway copy and a pointer-backed wrapper
    /// can be left aliasing freed storage. Static members (including <c>ref this</c> extension
    /// methods, which the compiler already rejects on a readonly receiver via CS8329) and
    /// members the author marked <c>readonly</c> never copy, so neither fires. The fix is to
    /// pass the receiver by <c>ref</c>, or to mark a genuinely-non-mutating member
    /// <c>readonly</c>. Scoped to <c>in</c>-parameter / <c>ref readonly</c>-local receivers —
    /// the surface that matters for <c>ref struct</c> wrappers, which can never be fields.</para>
    ///
    /// <list type="bullet">
    ///   <item><b>TRECS118</b> — by-value local copied from an existing variable</item>
    ///   <item><b>TRECS131</b> — by-value method parameter of a non-copyable type</item>
    ///   <item><b>TRECS120</b> — a struct carries both <c>[NonCopyable]</c> and <c>[Copyable]</c></item>
    ///   <item><b>TRECS133</b> — non-<c>readonly</c> member invoked on a non-copyable value through a read-only reference</item>
    /// </list>
    /// </summary>
    [DiagnosticAnalyzer(LanguageNames.CSharp)]
    public class NonCopyableAnalyzer : DiagnosticAnalyzer
    {
        const string NonCopyableAttributeName = "NonCopyableAttribute";
        const string CopyableAttributeName = "CopyableAttribute";
        const string EntityComponentInterfaceName = "IEntityComponent";
        const string TrecsNamespace = "Trecs";

        public override ImmutableArray<DiagnosticDescriptor> SupportedDiagnostics { get; } =
            ImmutableArray.Create(
                DiagnosticDescriptors.NonCopyableByValueLocal,
                DiagnosticDescriptors.NonCopyableByValueParameter,
                DiagnosticDescriptors.NonCopyableCopyableConflict,
                DiagnosticDescriptors.NonCopyableReadonlyRefMemberAccess
            );

        public override void Initialize(AnalysisContext context)
        {
            context.ConfigureGeneratedCodeAnalysis(GeneratedCodeAnalysisFlags.None);
            context.EnableConcurrentExecution();

            context.RegisterCompilationStartAction(start =>
            {
                var cache = new ConcurrentDictionary<INamedTypeSymbol, bool>(
                    SymbolEqualityComparer.Default
                );

                start.RegisterOperationAction(
                    ctx => AnalyzeVariableDeclarator(ctx, cache),
                    OperationKind.VariableDeclarator
                );
                start.RegisterSymbolAction(
                    ctx => AnalyzeMethodParameters(ctx, cache),
                    SymbolKind.Method
                );
                start.RegisterSymbolAction(AnalyzeTypeForConflict, SymbolKind.NamedType);
                start.RegisterOperationAction(
                    ctx => AnalyzeReadonlyRefMemberAccess(ctx, cache),
                    OperationKind.Invocation,
                    OperationKind.PropertyReference
                );
            });
        }

        static void AnalyzeVariableDeclarator(
            OperationAnalysisContext context,
            ConcurrentDictionary<INamedTypeSymbol, bool> cache
        )
        {
            var declarator = (IVariableDeclaratorOperation)context.Operation;
            var local = declarator.Symbol;

            if (local.RefKind != RefKind.None)
                return;

            if (!IsNonCopyable(local.Type, cache))
                return;

            var initializer = declarator.Initializer?.Value;
            if (initializer == null)
                return;

            if (!IsCopyFromVariable(initializer))
                return;

            context.ReportDiagnostic(
                Diagnostic.Create(
                    DiagnosticDescriptors.NonCopyableByValueLocal,
                    declarator.Syntax.GetLocation(),
                    local.Type.ToDisplayString(SymbolDisplayFormat.MinimallyQualifiedFormat)
                )
            );
        }

        static bool IsCopyFromVariable(IOperation operation)
        {
            while (operation is IConversionOperation { IsImplicit: true } conv)
                operation = conv.Operand;

            return operation
                is IFieldReferenceOperation
                    or ILocalReferenceOperation
                    or IParameterReferenceOperation
                    or IPropertyReferenceOperation;
        }

        static void AnalyzeMethodParameters(
            SymbolAnalysisContext context,
            ConcurrentDictionary<INamedTypeSymbol, bool> cache
        )
        {
            var method = (IMethodSymbol)context.Symbol;

            // Members on a non-copyable type itself control their own internals
            // (Equals(in T), ==, conversions, etc.); don't fight them.
            if (
                method.ContainingType is INamedTypeSymbol containing
                && IsNonCopyable(containing, cache)
            )
            {
                return;
            }

            foreach (var param in method.Parameters)
            {
                if (param.RefKind != RefKind.None)
                    continue;

                if (!IsNonCopyable(param.Type, cache))
                    continue;

                context.ReportDiagnostic(
                    Diagnostic.Create(
                        DiagnosticDescriptors.NonCopyableByValueParameter,
                        param.DeclaringSyntaxReferences.Length > 0
                            ? param
                                .DeclaringSyntaxReferences[0]
                                .GetSyntax(context.CancellationToken)
                                .GetLocation()
                            : method.Locations[0],
                        param.Name,
                        param.Type.ToDisplayString(SymbolDisplayFormat.MinimallyQualifiedFormat)
                    )
                );
            }
        }

        // TRECS133: a non-readonly instance member invoked on a non-copyable value through a
        // read-only reference. The C# compiler synthesizes a defensive copy of the receiver so
        // the readonly reference can't be mutated; the call then runs against the copy, silently
        // discarding any field mutation (and, for pointer-rebinding wrappers, leaving the original
        // aliasing freed storage). The copy is invisible in source — that's what makes it worth a
        // dedicated diagnostic on top of the explicit-copy rules above.
        static void AnalyzeReadonlyRefMemberAccess(
            OperationAnalysisContext context,
            ConcurrentDictionary<INamedTypeSymbol, bool> cache
        )
        {
            IMethodSymbol? member;
            IOperation? instance;

            switch (context.Operation)
            {
                case IInvocationOperation invocation:
                    member = invocation.TargetMethod;
                    instance = invocation.Instance;
                    break;
                case IPropertyReferenceOperation propertyRef:
                    member = SelectInvokedAccessor(propertyRef);
                    instance = propertyRef.Instance;
                    break;
                default:
                    return;
            }

            // Cheapest, most selective gate first. This action runs on every invocation and
            // property reference in the compilation; the overwhelming majority have a `this` /
            // local / field / rvalue receiver, so bail here before touching the member symbol
            // or the (cache-populating) non-copyable walk that follows.
            if (instance is null || !IsReadOnlyReferenceReceiver(instance))
                return;

            // No member resolved (e.g. a write-only property read), or one that can't copy the
            // receiver: static members (including `ref this` extension methods, which the
            // compiler already rejects on a readonly receiver via CS8329), and `readonly`
            // members, which read through the reference in place.
            if (member is null || member.IsStatic || member.IsReadOnly)
                return;

            // Constructors / finalizers aren't invoked through an existing readonly receiver.
            if (
                member.MethodKind == MethodKind.Constructor
                || member.MethodKind == MethodKind.StaticConstructor
            )
                return;

            if (member.ContainingType is not INamedTypeSymbol containing)
                return;
            if (!IsNonCopyable(containing, cache))
                return;

            context.ReportDiagnostic(
                Diagnostic.Create(
                    DiagnosticDescriptors.NonCopyableReadonlyRefMemberAccess,
                    context.Operation.Syntax.GetLocation(),
                    member.Name,
                    containing.ToDisplayString(SymbolDisplayFormat.MinimallyQualifiedFormat)
                )
            );
        }

        // The accessor actually invoked by a property/indexer reference. A write (or a
        // read-modify-write via compound assignment / increment) runs the setter — that's the
        // mutation the defensive copy swallows, so report it; a plain read runs the getter.
        // Returns null when the relevant accessor doesn't exist (the downstream IsReadOnly /
        // IsStatic checks handle the rest).
        static IMethodSymbol? SelectInvokedAccessor(IPropertyReferenceOperation propertyRef)
        {
            var property = propertyRef.Property;
            var parent = propertyRef.Parent;

            bool isWriteTarget =
                (
                    parent is ISimpleAssignmentOperation simple
                    && ReferenceEquals(simple.Target, propertyRef)
                )
                || (
                    parent is ICompoundAssignmentOperation compound
                    && ReferenceEquals(compound.Target, propertyRef)
                )
                || (
                    parent is IIncrementOrDecrementOperation incr
                    && ReferenceEquals(incr.Target, propertyRef)
                );

            if (isWriteTarget)
                return property.SetMethod ?? property.GetMethod;
            return property.GetMethod;
        }

        // True when the receiver denotes a read-only reference to value-type storage, so the
        // compiler would synthesize a defensive copy before a mutating call. `in` parameters and
        // `ref readonly` locals share RefKind.In; a `ref` local / by-value local aliases writable
        // storage (mutation lands in place — no copy), and everything else is handled elsewhere.
        // Receivers reached through a field of an `in` parameter are intentionally out of scope.
        static bool IsReadOnlyReferenceReceiver(IOperation instance)
        {
            switch (instance)
            {
                case IParameterReferenceOperation parameterRef:
                    return parameterRef.Parameter.RefKind == RefKind.In;
                case ILocalReferenceOperation localRef:
                    return localRef.Local.RefKind == RefKind.In;
                default:
                    return false;
            }
        }

        static void AnalyzeTypeForConflict(SymbolAnalysisContext context)
        {
            var type = (INamedTypeSymbol)context.Symbol;
            if (type.TypeKind != TypeKind.Struct)
                return;

            if (!HasTrecsAttribute(type, NonCopyableAttributeName))
                return;

            if (!HasTrecsAttribute(type, CopyableAttributeName))
                return;

            context.ReportDiagnostic(
                Diagnostic.Create(
                    DiagnosticDescriptors.NonCopyableCopyableConflict,
                    type.Locations.Length > 0 ? type.Locations[0] : Location.None,
                    type.Name
                )
            );
        }

        static bool IsNonCopyable(
            ITypeSymbol type,
            ConcurrentDictionary<INamedTypeSymbol, bool> cache
        )
        {
            if (type is not INamedTypeSymbol named)
                return false;
            return IsNonCopyableNamed(named, cache, visiting: null);
        }

        static bool IsNonCopyableNamed(
            INamedTypeSymbol type,
            ConcurrentDictionary<INamedTypeSymbol, bool> cache,
            HashSet<INamedTypeSymbol>? visiting
        )
        {
            // Built-in primitive value types can't be non-copyable. Short-circuit
            // before walking attributes / interfaces / members for every int/float
            // field the transitive rule visits.
            if (
                type.SpecialType
                is SpecialType.System_Boolean
                    or SpecialType.System_Char
                    or SpecialType.System_SByte
                    or SpecialType.System_Byte
                    or SpecialType.System_Int16
                    or SpecialType.System_UInt16
                    or SpecialType.System_Int32
                    or SpecialType.System_UInt32
                    or SpecialType.System_Int64
                    or SpecialType.System_UInt64
                    or SpecialType.System_Single
                    or SpecialType.System_Double
                    or SpecialType.System_Decimal
                    or SpecialType.System_IntPtr
                    or SpecialType.System_UIntPtr
            )
                return false;

            if (cache.TryGetValue(type, out var cached))
                return cached;

            // Cycle guard. C# rejects directly recursive struct fields, but
            // pathological generic graphs could in principle reach the same type
            // via mutual recursion — treat any in-progress lookup as copyable to
            // close the loop, and don't cache the conservative answer.
            visiting ??= new HashSet<INamedTypeSymbol>(SymbolEqualityComparer.Default);
            if (!visiting.Add(type))
                return false;

            try
            {
                bool result = ComputeNonCopyable(type, cache, visiting);
                cache[type] = result;
                return result;
            }
            finally
            {
                visiting.Remove(type);
            }
        }

        static bool ComputeNonCopyable(
            INamedTypeSymbol type,
            ConcurrentDictionary<INamedTypeSymbol, bool> cache,
            HashSet<INamedTypeSymbol> visiting
        )
        {
            if (HasTrecsAttribute(type, NonCopyableAttributeName))
                return true;

            // IEntityComponent is non-copyable by default; [Copyable] opts back out.
            if (ImplementsEntityComponent(type) && !HasTrecsAttribute(type, CopyableAttributeName))
                return true;

            // Only structs propagate non-copyability transitively. Reference types
            // (classes, interfaces) hold their fields by reference; copying the
            // reference doesn't duplicate the underlying storage.
            if (type.TypeKind != TypeKind.Struct)
                return false;

            foreach (var member in type.GetMembers())
            {
                if (member is not IFieldSymbol field)
                    continue;
                if (field.IsStatic || field.IsConst)
                    continue;
                if (field.Type is not INamedTypeSymbol fieldType)
                    continue;

                if (IsNonCopyableNamed(fieldType, cache, visiting))
                    return true;
            }

            return false;
        }

        static bool HasTrecsAttribute(INamedTypeSymbol type, string attributeName)
        {
            foreach (var attr in type.OriginalDefinition.GetAttributes())
            {
                var ac = attr.AttributeClass;
                if (ac == null)
                    continue;
                if (ac.Name != attributeName)
                    continue;
                if (ac.ContainingNamespace?.ToDisplayString() != TrecsNamespace)
                    continue;
                return true;
            }
            return false;
        }

        static bool ImplementsEntityComponent(INamedTypeSymbol type)
        {
            foreach (var iface in type.AllInterfaces)
            {
                if (
                    iface.Name == EntityComponentInterfaceName
                    && iface.ContainingNamespace?.ToDisplayString() == TrecsNamespace
                )
                {
                    return true;
                }
            }
            return false;
        }
    }
}
