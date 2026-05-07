using System;
using Microsoft.CodeAnalysis;
using Microsoft.CodeAnalysis.CSharp.Syntax;
using Trecs.SourceGen.Shared;
using Trecs.SourceGen.Template;

namespace Trecs.SourceGen
{
    /// <summary>
    /// Incremental source generator that processes ITemplate struct/class declarations
    /// and emits Template.Builder()...Build() code.
    /// </summary>
    [Generator]
    public class TemplateDefinitionGenerator : IIncrementalGenerator
    {
        public void Initialize(IncrementalGeneratorInitializationContext context)
        {
            var hasTrecsReference = AssemblyFilterHelper.CreateTrecsReferenceCheck(context);

            // Find structs/classes implementing ITemplate via CreateSyntaxProvider
            // We use CreateSyntaxProvider because ITemplate is an interface, not an attribute
            var templateProviderRaw = context
                .SyntaxProvider.CreateSyntaxProvider(
                    predicate: static (node, _) =>
                        node is TypeDeclarationSyntax tds
                        && (tds is StructDeclarationSyntax || tds is ClassDeclarationSyntax)
                        && HasBaseList(tds),
                    transform: static (context, _) => GetTemplateData(context)
                )
                .Where(static x => x != null);

            var templateProvider = AssemblyFilterHelper.FilterByTrecsReference(
                templateProviderRaw,
                hasTrecsReference
            );

            var templatesWithCompilation = templateProvider.Combine(context.CompilationProvider);

            context.RegisterSourceOutput(
                templatesWithCompilation,
                static (spc, source) => ExecuteGeneration(spc, source.Left, source.Right)
            );
        }

        private static bool HasBaseList(TypeDeclarationSyntax tds)
        {
            return tds.BaseList != null && tds.BaseList.Types.Count > 0;
        }

        private static TemplateTypeData? GetTemplateData(GeneratorSyntaxContext context)
        {
            var typeDecl = (TypeDeclarationSyntax)context.Node;
            var symbol = context.SemanticModel.GetDeclaredSymbol(typeDecl) as INamedTypeSymbol;

            if (symbol == null)
            {
                return null;
            }

            // Check if this type implements ITemplate (generic or non-generic)
            if (!ImplementsITemplate(symbol))
            {
                return null;
            }

            return new TemplateTypeData(typeDecl, symbol);
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

        private static void ExecuteGeneration(
            SourceProductionContext context,
            TemplateTypeData? templateData,
            Compilation compilation
        )
        {
            if (templateData == null)
            {
                return;
            }

            var location = templateData.Syntax.GetLocation();
            var typeName = templateData.Symbol.Name;
            var fileName = GeneratorBase.CreateSafeFileName(
                templateData.Symbol,
                "TemplateDefinition"
            );

            try
            {
                using var _timer_ = SourceGenTimer.Time("TemplateDefinitionGenerator.Total");
                SourceGenLogger.Log($"[TemplateDefinitionGenerator] Processing {typeName}");

                // Parse the template declaration
                var definitionData = ErrorRecovery.TryExecute(
                    () =>
                        TemplateAttributeParser.Parse(
                            templateData.Symbol,
                            templateData.Syntax,
                            context.ReportDiagnostic,
                            location
                        ),
                    context,
                    location,
                    "Template attribute parsing"
                );

                if (definitionData == null)
                {
                    return;
                }

                // Validate the template
                var isValid =
                    ErrorRecovery.TryExecuteBool(
                        () =>
                            TemplateValidator.Validate(
                                templateData.Syntax,
                                templateData.Symbol,
                                definitionData,
                                context.ReportDiagnostic
                            ),
                        context,
                        location,
                        "Template validation"
                    ) ?? false;

                if (!isValid)
                {
                    return;
                }

                // Generate the source code
                var source = ErrorRecovery.TryExecute(
                    () => TemplateCodeGenerator.Generate(definitionData),
                    context,
                    location,
                    "Template code generation"
                );

                if (source != null)
                {
                    context.AddSource(fileName, source);
                    SourceGenLogger.WriteGeneratedFile(fileName, source);
                }
            }
            catch (Exception ex)
            {
                ErrorRecovery.ReportError(context, location, "Template definition generation", ex);
            }
        }
    }

    /// <summary>
    /// Data structure for template type information (struct or class)
    /// </summary>
    internal class TemplateTypeData
    {
        public TypeDeclarationSyntax Syntax { get; }
        public INamedTypeSymbol Symbol { get; }

        public TemplateTypeData(TypeDeclarationSyntax syntax, INamedTypeSymbol symbol)
        {
            Syntax = syntax;
            Symbol = symbol;
        }
    }
}
