using System.Collections.Immutable;
using System.Linq;
using Microsoft.CodeAnalysis;
using Microsoft.CodeAnalysis.Diagnostics;

namespace Trecs.SourceGen
{
    // The JobGenerator emits an additional partial struct definition that adds fields
    // (component buffers, group, etc.) to partial structs declared with
    // [ForEachEntity] on Execute, or with any [FromWorld] field.
    // C# emits CS0282 ("there is no defined ordering between fields in multiple
    // declarations of partial struct") for these — suppress it for our generated cases.
    [DiagnosticAnalyzer(LanguageNames.CSharp)]
    public class PartialStructWarningSupressor : DiagnosticSuppressor
    {
        public override ImmutableArray<SuppressionDescriptor> SupportedSuppressions { get; } =
            ImmutableArray.Create(
                new SuppressionDescriptor(
                    id: "TRECS001_CS0282",
                    suppressedDiagnosticId: "CS0282",
                    justification: "Suppressing CS0282 for partial struct job declarations augmented by JobGenerator"
                )
            );

        public override void ReportSuppressions(SuppressionAnalysisContext context)
        {
            foreach (var diagnostic in context.ReportedDiagnostics)
            {
                if (diagnostic.Id != "CS0282")
                    continue;

                var location = diagnostic.Location;
                if (!location.IsInSource)
                    continue;

                var syntaxTree = location.SourceTree;
                if (syntaxTree == null)
                    continue;

                var syntaxNode = syntaxTree
                    .GetRoot(context.CancellationToken)
                    .FindNode(location.SourceSpan);

                var semanticModel = context.GetSemanticModel(syntaxTree);

                if (semanticModel.GetDeclaredSymbol(syntaxNode) is not INamedTypeSymbol typeSymbol)
                    continue;

                if (IsTrecsJobStruct(typeSymbol))
                {
                    context.ReportSuppression(
                        Suppression.Create(SupportedSuppressions[0], diagnostic)
                    );
                }
            }
        }

        static bool IsTrecsJobStruct(INamedTypeSymbol typeSymbol)
        {
            if (typeSymbol.TypeKind != TypeKind.Struct)
                return false;

            foreach (var member in typeSymbol.GetMembers())
            {
                if (member is IMethodSymbol method && method.Name == "Execute")
                {
                    foreach (var attr in method.GetAttributes())
                    {
                        if (attr.AttributeClass?.Name == TrecsAttributeNames.ForEachEntity)
                            return true;
                    }
                }
                else if (member is IFieldSymbol field)
                {
                    foreach (var attr in field.GetAttributes())
                    {
                        if (attr.AttributeClass?.Name == TrecsAttributeNames.FromWorld)
                            return true;
                    }
                }
            }
            return false;
        }
    }
}
