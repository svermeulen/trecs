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
    /// <list type="bullet">
    ///   <item><b>TRECS118</b> — by-value local copied from an existing variable</item>
    ///   <item><b>TRECS119</b> — by-value method parameter of a non-copyable type</item>
    ///   <item><b>TRECS120</b> — a struct carries both <c>[NonCopyable]</c> and <c>[Copyable]</c></item>
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
                DiagnosticDescriptors.NonCopyableCopyableConflict
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
