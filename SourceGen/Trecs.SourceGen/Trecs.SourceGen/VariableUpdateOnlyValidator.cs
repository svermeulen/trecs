using Microsoft.CodeAnalysis;
using Microsoft.CodeAnalysis.CSharp.Syntax;
using Trecs.SourceGen.Shared;

namespace Trecs.SourceGen
{
    /// <summary>
    /// Diagnostic-only generator that flags <c>[VariableUpdateOnly]</c> applied
    /// to a class that is not a template (does not implement <c>ITemplate</c>).
    /// On those targets the attribute is silently ignored at runtime, which is a
    /// footgun. Struct targets are blocked by <c>AttributeTargets</c> on the
    /// attribute itself, so only the class case needs runtime enforcement.
    /// <para>
    /// Field targets are intentionally not checked here — the existing template
    /// pipeline already drives field-level VUO interpretation, and a VUO field
    /// outside of a template is harmless (just unused).
    /// </para>
    /// </summary>
    [Generator]
    public class VariableUpdateOnlyValidator : IIncrementalGenerator
    {
        public void Initialize(IncrementalGeneratorInitializationContext context)
        {
            var hasTrecsReference = AssemblyFilterHelper.CreateTrecsReferenceCheck(context);

            var candidatesRaw = context
                .SyntaxProvider.CreateSyntaxProvider(
                    predicate: static (node, _) =>
                        node is ClassDeclarationSyntax tds && tds.AttributeLists.Count > 0,
                    transform: static (ctx, _) => GetCandidate(ctx)
                )
                .Where(static x => x != null);

            var candidates = AssemblyFilterHelper.FilterByTrecsReference(
                candidatesRaw,
                hasTrecsReference
            );

            context.RegisterSourceOutput(
                candidates,
                static (spc, candidate) => Report(spc, candidate)
            );
        }

        private static Candidate? GetCandidate(GeneratorSyntaxContext ctx)
        {
            var typeDecl = (ClassDeclarationSyntax)ctx.Node;
            var symbol = ctx.SemanticModel.GetDeclaredSymbol(typeDecl) as INamedTypeSymbol;
            if (symbol == null)
            {
                return null;
            }

            if (!HasVariableUpdateOnlyAttribute(symbol))
            {
                return null;
            }

            if (ImplementsTrecsInterface(symbol, "ITemplate"))
            {
                return null;
            }

            return new Candidate(typeDecl, symbol.Name);
        }

        private static bool HasVariableUpdateOnlyAttribute(INamedTypeSymbol symbol)
        {
            foreach (var attr in symbol.GetAttributes())
            {
                if (
                    attr.AttributeClass != null
                    && attr.AttributeClass.Name == "VariableUpdateOnlyAttribute"
                    && SymbolAnalyzer.IsInNamespace(
                        attr.AttributeClass.ContainingNamespace,
                        "Trecs"
                    )
                )
                {
                    return true;
                }
            }

            return false;
        }

        private static bool ImplementsTrecsInterface(INamedTypeSymbol symbol, string interfaceName)
        {
            foreach (var iface in symbol.AllInterfaces)
            {
                if (
                    iface.Name == interfaceName
                    && SymbolAnalyzer.IsInNamespace(iface.ContainingNamespace, "Trecs")
                )
                {
                    return true;
                }
            }

            return false;
        }

        private static void Report(SourceProductionContext spc, Candidate? candidate)
        {
            if (candidate == null)
            {
                return;
            }

            spc.ReportDiagnostic(
                Diagnostic.Create(
                    DiagnosticDescriptors.VariableUpdateOnlyOnInvalidTarget,
                    candidate.Syntax.GetLocation(),
                    candidate.TypeName
                )
            );
        }

        private sealed class Candidate
        {
            public Candidate(ClassDeclarationSyntax syntax, string typeName)
            {
                Syntax = syntax;
                TypeName = typeName;
            }

            public ClassDeclarationSyntax Syntax { get; }
            public string TypeName { get; }
        }
    }
}
