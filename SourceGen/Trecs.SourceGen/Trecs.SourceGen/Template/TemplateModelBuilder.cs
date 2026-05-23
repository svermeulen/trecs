using System;
using System.Collections.Generic;
using Microsoft.CodeAnalysis;
using Microsoft.CodeAnalysis.CSharp.Syntax;
using Trecs.SourceGen.Shared;

namespace Trecs.SourceGen.Template
{
    /// <summary>
    /// Builds an equatable <see cref="TemplateModel"/> from a Roslyn syntax+symbol pair. This
    /// is the only place in the template pipeline that touches <see cref="INamedTypeSymbol"/>;
    /// the resulting model carries only strings, bools, and value-equatable arrays, so it can
    /// flow through the incremental cache without pinning compilations or producing spurious
    /// invalidations.
    /// </summary>
    internal static class TemplateModelBuilder
    {
        public static TemplateModel Build(TypeDeclarationSyntax typeDecl, INamedTypeSymbol symbol)
        {
            var diagnostics = new List<DiagnosticInfo>();
            var hintFileName = SymbolAnalyzer.GetSafeFileName(symbol, "TemplateDefinition");
            var location = LocationInfo.From(typeDecl.GetLocation());

            TemplateDefinitionData data;
            try
            {
                data = TemplateAttributeParser.Parse(symbol, typeDecl);
            }
            catch (Exception ex)
            {
                diagnostics.Add(
                    DiagnosticInfo.Create(
                        DiagnosticDescriptors.SourceGenerationError,
                        location,
                        "Template attribute parsing",
                        ex.Message
                    )
                );
                return new TemplateModel(
                    Data: EmptyData(symbol),
                    HintFileName: hintFileName,
                    IsValid: false,
                    Diagnostics: diagnostics.ToEquatableArray()
                );
            }

            bool isValid;
            try
            {
                isValid = TemplateValidator.Validate(typeDecl, symbol, data, diagnostics.Add);
            }
            catch (Exception ex)
            {
                diagnostics.Add(
                    DiagnosticInfo.Create(
                        DiagnosticDescriptors.SourceGenerationError,
                        location,
                        "Template validation",
                        ex.Message
                    )
                );
                isValid = false;
            }

            return new TemplateModel(
                Data: data,
                HintFileName: hintFileName,
                IsValid: isValid,
                Diagnostics: diagnostics.ToEquatableArray()
            );
        }

        // Placeholder used only when parsing throws before any data is produced. Carries the
        // type name and namespace so the model is still distinguishable in the cache; rest is
        // zeroed because IsValid will be false and codegen won't run.
        private static TemplateDefinitionData EmptyData(INamedTypeSymbol symbol) =>
            new TemplateDefinitionData(
                TypeName: symbol.Name,
                NamespaceName: SymbolAnalyzer.GetNamespaceChain(symbol),
                Accessibility: "internal",
                IsClass: symbol.TypeKind == TypeKind.Class,
                IsAbstract: false,
                IsGlobals: false,
                IsVariableUpdateOnly: false,
                ContainingTypes: EquatableArray<string>.Empty,
                TagTypeNames: EquatableArray<string>.Empty,
                BaseTemplateTypeNames: EquatableArray<string>.Empty,
                Components: EquatableArray<TemplateComponentData>.Empty,
                Dimensions: EquatableArray<TemplateDimensionData>.Empty
            );
    }
}
