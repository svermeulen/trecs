using System;
using Microsoft.CodeAnalysis;
using Microsoft.CodeAnalysis.CSharp.Syntax;
using Trecs.SourceGen.Shared;
using Trecs.SourceGen.Template;

namespace Trecs.SourceGen
{
    /// <summary>
    /// Incremental source generator that processes <c>ITemplate</c> struct/class declarations
    /// and emits <c>Template.Builder()...Build()</c> code.
    ///
    /// <para>Pipeline shape: the transform produces a fully-precomputed
    /// <see cref="TemplateModel"/> (value-equatable, holds no symbols or syntax) and the
    /// terminal stage materializes diagnostics + emits source. The previous version combined
    /// the provider with the <c>CompilationProvider</c> even though the compilation was
    /// never used in code generation; that combine has been dropped, which lets the cache
    /// survive unrelated compilation edits.</para>
    /// </summary>
    [Generator]
    public class TemplateDefinitionGenerator : IIncrementalGenerator
    {
        public void Initialize(IncrementalGeneratorInitializationContext context)
        {
            var hasTrecsReference = AssemblyFilterHelper.CreateTrecsReferenceCheck(context);

            // ITemplate is an interface, not an attribute, so the fast
            // ForAttributeWithMetadataName path doesn't apply. The syntactic predicate stays
            // string-cheap; semantic resolution + parsing + validation happen in the transform.
            var templateModelsRaw = context
                .SyntaxProvider.CreateSyntaxProvider(
                    predicate: static (node, _) =>
                        node is TypeDeclarationSyntax tds
                        && (tds is StructDeclarationSyntax || tds is ClassDeclarationSyntax)
                        && HasBaseList(tds),
                    transform: static (ctx, _) => BuildTemplateModel(ctx)
                )
                .Where(static x => x is not null);

            var templateModels = AssemblyFilterHelper.FilterByTrecsReference(
                templateModelsRaw,
                hasTrecsReference
            );

            context.RegisterSourceOutput(
                templateModels,
                static (spc, model) => ExecuteGeneration(spc, model!)
            );
        }

        private static bool HasBaseList(TypeDeclarationSyntax tds)
        {
            return tds.BaseList != null && tds.BaseList.Types.Count > 0;
        }

        private static TemplateModel? BuildTemplateModel(GeneratorSyntaxContext context)
        {
            var typeDecl = (TypeDeclarationSyntax)context.Node;
            var symbol = context.SemanticModel.GetDeclaredSymbol(typeDecl) as INamedTypeSymbol;

            if (symbol == null)
                return null;

            // Final gate: predicate is syntactic and lets through any type with a base list.
            // Verify the symbol actually implements Trecs.ITemplate before building a model.
            if (!ImplementsITemplate(symbol))
                return null;

            return TemplateModelBuilder.Build(typeDecl, symbol);
        }

        private static bool ImplementsITemplate(INamedTypeSymbol symbol)
        {
            foreach (var iface in symbol.Interfaces)
            {
                if (
                    iface.Name == "ITemplate"
                    && SymbolAnalyzer.IsInNamespace(iface.ContainingNamespace, "Trecs")
                )
                {
                    return true;
                }
            }
            return false;
        }

        private static void ExecuteGeneration(SourceProductionContext context, TemplateModel model)
        {
            // Replay diagnostics collected in the transform stage. DiagnosticInfo→Diagnostic
            // materialization happens here in the terminal stage.
            foreach (var diag in model.Diagnostics)
                context.ReportDiagnostic(diag.ToDiagnostic());

            if (!model.IsValid)
                return;

            var location = Location.None;
            try
            {
                using var _timer_ = SourceGenTimer.Time("TemplateDefinitionGenerator.Total");
                SourceGenLogger.Log(
                    $"[TemplateDefinitionGenerator] Processing {model.Data.TypeName}"
                );

                var source = ErrorRecovery.TryExecute(
                    () => TemplateCodeGenerator.Generate(model.Data),
                    context,
                    location,
                    "Template code generation"
                );

                if (source != null)
                {
                    context.AddSource(model.HintFileName, source);
                    SourceGenLogger.WriteGeneratedFile(model.HintFileName, source);
                }
            }
            catch (Exception ex)
            {
                ErrorRecovery.ReportError(context, location, "Template definition generation", ex);
            }
        }
    }
}
