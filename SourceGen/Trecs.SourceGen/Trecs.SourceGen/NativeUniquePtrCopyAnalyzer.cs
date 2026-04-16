#nullable enable

using System.Collections.Immutable;
using Microsoft.CodeAnalysis;
using Microsoft.CodeAnalysis.Diagnostics;
using Microsoft.CodeAnalysis.Operations;

namespace Trecs.SourceGen
{
    /// <summary>
    /// Prevents by-value copies of <c>NativeUniquePtr&lt;T&gt;</c>.
    ///
    /// <c>NativeUniquePtr&lt;T&gt;</c> uses <c>ref this</c> extension methods for
    /// <c>Set</c>/<c>GetMut</c> so that callers must have write access to the owning
    /// component field. Copying the pointer to a by-value local or parameter bypasses
    /// this, so we disallow copies at compile time.
    ///
    /// <para>Locals are only flagged when the initializer is a copy from an existing
    /// variable (field, local, or parameter). Receiving a return value from a method
    /// (e.g. <c>heap.Alloc</c>) or constructing a new instance is allowed.</para>
    ///
    /// <list type="bullet">
    ///   <item><b>TRECS110</b> — by-value local copied from an existing variable</item>
    ///   <item><b>TRECS111</b> — by-value method parameter of type NativeUniquePtr</item>
    /// </list>
    /// </summary>
    [DiagnosticAnalyzer(LanguageNames.CSharp)]
    public class NativeUniquePtrCopyAnalyzer : DiagnosticAnalyzer
    {
        private const string NativeUniquePtrName = "NativeUniquePtr";
        private const string TrecsNamespace = "Trecs";

        public override ImmutableArray<DiagnosticDescriptor> SupportedDiagnostics { get; } =
            ImmutableArray.Create(
                DiagnosticDescriptors.NativeUniquePtrByValueLocal,
                DiagnosticDescriptors.NativeUniquePtrByValueParameter
            );

        public override void Initialize(AnalysisContext context)
        {
            context.ConfigureGeneratedCodeAnalysis(GeneratedCodeAnalysisFlags.None);
            context.EnableConcurrentExecution();

            context.RegisterOperationAction(AnalyzeVariableDeclarator, OperationKind.VariableDeclarator);
            context.RegisterSymbolAction(AnalyzeMethodParameters, SymbolKind.Method);
        }

        private static void AnalyzeVariableDeclarator(OperationAnalysisContext context)
        {
            var declarator = (IVariableDeclaratorOperation)context.Operation;
            var local = declarator.Symbol;

            if (local.RefKind != RefKind.None)
                return;

            if (!IsNativeUniquePtr(local.Type, out var typeArg))
                return;

            // Only flag when the initializer copies from an existing variable.
            // Method returns (Alloc, etc.), constructors, and default are allowed.
            var initializer = declarator.Initializer?.Value;
            if (initializer != null && !IsCopyFromVariable(initializer))
                return;

            // No initializer (unassigned local) is also a copy concern if assigned later,
            // but we can't easily track all assignments — flag it to be safe.
            context.ReportDiagnostic(Diagnostic.Create(
                DiagnosticDescriptors.NativeUniquePtrByValueLocal,
                declarator.Syntax.GetLocation(),
                typeArg
            ));
        }

        private static bool IsCopyFromVariable(IOperation operation)
        {
            // Unwrap implicit conversions
            while (operation is IConversionOperation { IsImplicit: true } conv)
                operation = conv.Operand;

            return operation is IFieldReferenceOperation
                or ILocalReferenceOperation
                or IParameterReferenceOperation;
        }

        private static void AnalyzeMethodParameters(SymbolAnalysisContext context)
        {
            var method = (IMethodSymbol)context.Symbol;

            // Skip methods declared on NativeUniquePtr itself (Equals, operators, etc.)
            if (method.ContainingType is INamedTypeSymbol containingType
                && containingType.IsGenericType
                && containingType.ConstructedFrom.Name == NativeUniquePtrName
                && containingType.ContainingNamespace?.ToDisplayString() == TrecsNamespace)
            {
                return;
            }

            foreach (var param in method.Parameters)
            {
                if (param.RefKind != RefKind.None)
                    continue;

                if (!IsNativeUniquePtr(param.Type, out var typeArg))
                    continue;

                context.ReportDiagnostic(Diagnostic.Create(
                    DiagnosticDescriptors.NativeUniquePtrByValueParameter,
                    param.DeclaringSyntaxReferences.Length > 0
                        ? param.DeclaringSyntaxReferences[0].GetSyntax(context.CancellationToken).GetLocation()
                        : method.Locations[0],
                    param.Name,
                    typeArg
                ));
            }
        }

        private static bool IsNativeUniquePtr(ITypeSymbol type, out string typeArgDisplay)
        {
            typeArgDisplay = "";

            if (type is not INamedTypeSymbol namedType)
                return false;

            if (!namedType.IsGenericType)
                return false;

            if (namedType.ContainingNamespace?.ToDisplayString() != TrecsNamespace)
                return false;

            if (namedType.ConstructedFrom.Name != NativeUniquePtrName)
                return false;

            typeArgDisplay = namedType.TypeArguments[0].ToDisplayString(
                SymbolDisplayFormat.MinimallyQualifiedFormat
            );
            return true;
        }
    }
}
