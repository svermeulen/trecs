using System.Collections.Immutable;
using Microsoft.CodeAnalysis;
using Microsoft.CodeAnalysis.CSharp.Syntax;
using Microsoft.CodeAnalysis.Diagnostics;
using Trecs.SourceGen.Shared;

namespace Trecs.SourceGen
{
    // Template fields are declared with no access modifier (TRECS034) and are
    // read at compile time via the source generator, not from C#. Roslyn's
    // analyzers can't see the SG-emitted reads in every IDE state, so they
    // flag template fields as unused. Suppress the relevant diagnostics on
    // any field whose containing class implements ITemplate.
    [DiagnosticAnalyzer(LanguageNames.CSharp)]
    public class TemplateFieldUnusedSuppressor : DiagnosticSuppressor
    {
        const string Justification =
            "Template fields are read at compile time by the Trecs source generator";

        static readonly SuppressionDescriptor SuppressIDE0051 = new(
            id: "TRECS034_IDE0051",
            suppressedDiagnosticId: "IDE0051",
            justification: Justification
        );

        static readonly SuppressionDescriptor SuppressIDE0052 = new(
            id: "TRECS034_IDE0052",
            suppressedDiagnosticId: "IDE0052",
            justification: Justification
        );

        static readonly SuppressionDescriptor SuppressCS0169 = new(
            id: "TRECS034_CS0169",
            suppressedDiagnosticId: "CS0169",
            justification: Justification
        );

        static readonly SuppressionDescriptor SuppressCS0414 = new(
            id: "TRECS034_CS0414",
            suppressedDiagnosticId: "CS0414",
            justification: Justification
        );

        static readonly SuppressionDescriptor SuppressCS0649 = new(
            id: "TRECS034_CS0649",
            suppressedDiagnosticId: "CS0649",
            justification: Justification
        );

        public override ImmutableArray<SuppressionDescriptor> SupportedSuppressions { get; } =
            ImmutableArray.Create(
                SuppressIDE0051,
                SuppressIDE0052,
                SuppressCS0169,
                SuppressCS0414,
                SuppressCS0649
            );

        public override void ReportSuppressions(SuppressionAnalysisContext context)
        {
            foreach (var diagnostic in context.ReportedDiagnostics)
            {
                var descriptor = MatchSuppressionDescriptor(diagnostic.Id);
                if (descriptor is null)
                    continue;

                var location = diagnostic.Location;
                if (!location.IsInSource)
                    continue;

                var syntaxTree = location.SourceTree;
                if (syntaxTree is null)
                    continue;

                var node = syntaxTree
                    .GetRoot(context.CancellationToken)
                    .FindNode(location.SourceSpan);

                var classDecl = FindAncestorClass(node);
                if (classDecl is null)
                    continue;

                var semanticModel = context.GetSemanticModel(syntaxTree);
                if (
                    semanticModel.GetDeclaredSymbol(
                        classDecl,
                        context.CancellationToken
                    )
                        is not INamedTypeSymbol classSymbol
                )
                    continue;

                if (!ImplementsITemplate(classSymbol))
                    continue;

                context.ReportSuppression(Suppression.Create(descriptor, diagnostic));
            }
        }

        static SuppressionDescriptor? MatchSuppressionDescriptor(string diagnosticId) =>
            diagnosticId switch
            {
                "IDE0051" => SuppressIDE0051,
                "IDE0052" => SuppressIDE0052,
                "CS0169" => SuppressCS0169,
                "CS0414" => SuppressCS0414,
                "CS0649" => SuppressCS0649,
                _ => null,
            };

        static ClassDeclarationSyntax? FindAncestorClass(SyntaxNode? node)
        {
            while (node is not null)
            {
                if (node is ClassDeclarationSyntax classDecl)
                    return classDecl;
                node = node.Parent;
            }
            return null;
        }

        static bool ImplementsITemplate(INamedTypeSymbol type)
        {
            foreach (var iface in type.AllInterfaces)
            {
                if (
                    iface.Name == "ITemplate"
                    && SymbolAnalyzer.IsInNamespace(iface.ContainingNamespace, "Trecs")
                )
                    return true;
            }
            return false;
        }
    }
}
