#nullable enable

using System.Collections.Immutable;
using Microsoft.CodeAnalysis;
using Microsoft.CodeAnalysis.CSharp;
using Microsoft.CodeAnalysis.CSharp.Syntax;
using Microsoft.CodeAnalysis.Diagnostics;

namespace Trecs.SourceGen
{
    /// <summary>
    /// Enforces that the type argument to <c>NativeSharedPtr&lt;T&gt;</c> and
    /// <c>NativeSharedRead&lt;T&gt;</c> is defensive-copy-safe so that
    /// <c>NativeSharedRead&lt;T&gt;.Value</c> can return <c>ref readonly T</c>
    /// without silently copying the value through a non-readonly method. Two
    /// shapes satisfy the analyzer:
    ///
    /// <list type="bullet">
    ///   <item><c>readonly struct</c> — every instance method is implicitly readonly.</item>
    ///   <item>Non-readonly struct where every instance method / property /
    ///     indexer accessor explicitly carries the <c>readonly</c> modifier.
    ///     Lets BlobBuilder-built blob roots have mutable fields that can be
    ///     assigned directly during construction (<c>root.Header = 42;</c>)
    ///     while still preventing defensive copies post-Build.</item>
    /// </list>
    ///
    /// <para>Built-in primitives and enums are accepted unconditionally.
    /// Unresolved type parameters (<c>NativeSharedPtr&lt;T&gt;</c> inside a
    /// generic helper) are skipped — the actual instantiation site catches
    /// any bad concrete T.</para>
    ///
    /// <para>Native shared blobs are immutable by design — the BlobCache does
    /// not snapshot blob memory alongside game-state snapshots, so any
    /// post-Alloc mutation silently desyncs determinism. Both accepted shapes
    /// above prevent that.</para>
    ///
    /// <para>Also catches the static factory methods on the non-generic
    /// <c>Trecs.NativeSharedPtr</c> helper class (<c>Alloc</c>, <c>Acquire</c>,
    /// <c>TryGet</c>, <c>AllocTakingOwnership</c>, <c>GetOrAlloc</c>,
    /// <c>GetOrAllocTakingOwnership</c>) so a call like
    /// <c>NativeSharedPtr.Alloc&lt;Bad&gt;(...)</c> is rejected at the call
    /// site too, not just wherever the returned handle lands.</para>
    ///
    /// <list type="bullet">
    ///   <item><b>TRECS124</b> — type argument is not defensive-copy-safe (not readonly struct, not all-readonly-members struct, not a primitive / enum)</item>
    /// </list>
    /// </summary>
    [DiagnosticAnalyzer(LanguageNames.CSharp)]
    public class NativeSharedPtrImmutabilityAnalyzer : DiagnosticAnalyzer
    {
        const string TrecsNamespace = "Trecs";
        const string NativeSharedPtrName = "NativeSharedPtr";
        const string NativeSharedReadName = "NativeSharedRead";

        public override ImmutableArray<DiagnosticDescriptor> SupportedDiagnostics { get; } =
            ImmutableArray.Create(DiagnosticDescriptors.NativeSharedPtrTypeMustBeReadonlyStruct);

        public override void Initialize(AnalysisContext context)
        {
            context.ConfigureGeneratedCodeAnalysis(GeneratedCodeAnalysisFlags.None);
            context.EnableConcurrentExecution();
            context.RegisterSyntaxNodeAction(AnalyzeGenericName, SyntaxKind.GenericName);
        }

        static void AnalyzeGenericName(SyntaxNodeAnalysisContext context)
        {
            var generic = (GenericNameSyntax)context.Node;
            var symbolInfo = context.SemanticModel.GetSymbolInfo(
                generic,
                context.CancellationToken
            );

            // CandidateSymbols covers cases where overload resolution didn't fully bind —
            // we still want to fire for partially-resolved invocations rather than miss them.
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
                if (!IsTrackedGenericType(named))
                    return;
                if (named.TypeArguments.Length != 1)
                    return;
                typeArg = named.TypeArguments[0];
            }
            else if (symbol is IMethodSymbol method)
            {
                if (!IsTrackedStaticFactoryMethod(method))
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
            if (IsAcceptable(typeArg))
                return;

            context.ReportDiagnostic(
                Diagnostic.Create(
                    DiagnosticDescriptors.NativeSharedPtrTypeMustBeReadonlyStruct,
                    generic.GetLocation(),
                    typeArg.ToDisplayString(SymbolDisplayFormat.MinimallyQualifiedFormat)
                )
            );
        }

        static bool IsTrackedGenericType(INamedTypeSymbol type)
        {
            if (type.ContainingNamespace?.ToDisplayString() != TrecsNamespace)
                return false;
            var name = type.OriginalDefinition.Name;
            return name == NativeSharedPtrName || name == NativeSharedReadName;
        }

        static bool IsTrackedStaticFactoryMethod(IMethodSymbol method)
        {
            // The non-generic helper class Trecs.NativeSharedPtr hosts the Alloc / Acquire /
            // TryGet / AllocTakingOwnership / GetOrAlloc / GetOrAllocTakingOwnership factories.
            // Arity == 0 distinguishes it from the generic struct NativeSharedPtr<T>; instance
            // methods on the generic struct are caught at the field/local that holds the result.
            if (!method.IsStatic)
                return false;
            if (method.ContainingType is not INamedTypeSymbol containing)
                return false;
            if (containing.ContainingNamespace?.ToDisplayString() != TrecsNamespace)
                return false;
            if (containing.Name != NativeSharedPtrName)
                return false;
            return containing.Arity == 0;
        }

        static bool IsAcceptable(ITypeSymbol type)
        {
            // Unresolved type parameter — defer to whatever site supplies a concrete T.
            // The same shape `where T : unmanaged` constraints take.
            if (type is ITypeParameterSymbol)
                return true;

            // Primitives are implicitly immutable for our purposes. They're declared as plain
            // `struct` (not `readonly struct`) in System.Private.CoreLib, so the IsReadOnly
            // check below would reject them without this short-circuit.
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
                    return true;
            }

            // Enums are value types with no user-mutable state.
            if (type.TypeKind == TypeKind.Enum)
                return true;

            if (type is not INamedTypeSymbol named || named.TypeKind != TypeKind.Struct)
                return false;

            // The strict form: `readonly struct`. Every instance method is implicitly
            // readonly, so `ref readonly T` returns don't spawn defensive copies anywhere.
            if (named.IsReadOnly)
                return true;

            // The relaxed form: non-readonly struct whose every instance method (and every
            // property/indexer accessor, etc.) is `readonly`. Defensive-copy safety is the
            // load-bearing invariant we care about — readonly-struct is one way to get it,
            // but explicit-readonly-on-every-member is the same guarantee. The relaxed form
            // lets BlobBuilder users mutate fields directly during construction
            // (`root.Header = 42;`) instead of needing wholesale `root = new T(...)`
            // re-assignment or Unsafe.AsRef tricks, while still preventing defensive copies
            // post-Build when readers go through `ref readonly T` returns from the heap.
            return AllInstanceMembersAreReadonly(named);
        }

        // True if every non-static, non-constructor method (including property/indexer/event
        // accessors and user-defined operators) carries the `readonly` modifier. Field
        // accesses don't trigger defensive copies, so fields themselves are exempt.
        static bool AllInstanceMembersAreReadonly(INamedTypeSymbol type)
        {
            foreach (var member in type.GetMembers())
            {
                if (member.IsStatic)
                    continue;

                if (member is not IMethodSymbol method)
                    continue;

                // Constructors aren't callable through a `ref readonly` receiver and don't
                // produce defensive copies. Same for finalizers (irrelevant for structs).
                if (method.MethodKind == MethodKind.Constructor)
                    continue;
                if (method.MethodKind == MethodKind.StaticConstructor)
                    continue;

                if (!method.IsReadOnly)
                    return false;
            }
            return true;
        }
    }
}
