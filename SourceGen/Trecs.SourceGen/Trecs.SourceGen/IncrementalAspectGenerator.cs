using System;
using System.Collections.Immutable;
using System.Linq;
using Microsoft.CodeAnalysis;
using Microsoft.CodeAnalysis.CSharp;
using Microsoft.CodeAnalysis.CSharp.Syntax;
using Trecs.SourceGen.Shared;

namespace Trecs.SourceGen
{
    /// <summary>
    /// Optimized incremental source generator for Aspect implementations
    /// Uses advanced caching and dependency tracking for better performance
    /// </summary>
    [Generator]
    public class IncrementalAspectGenerator : IIncrementalGenerator
    {
        public void Initialize(IncrementalGeneratorInitializationContext context)
        {
            // Check if compilation references Trecs assembly for better performance
            var hasTrecsReference = AssemblyFilterHelper.CreateTrecsReferenceCheck(context);

            // Create optimized incremental provider for Aspect structs (detected by IAspect interface)
            var aspectProviderRaw = context
                .SyntaxProvider.CreateSyntaxProvider(
                    predicate: static (node, _) =>
                        node is StructDeclarationSyntax sds
                        && sds.Modifiers.Any(SyntaxKind.PartialKeyword)
                        && sds.BaseList != null
                        && sds.BaseList.Types.Any(static t =>
                        {
                            var typeStr = t.Type.ToString();
                            return typeStr == "IAspect" || typeStr == "Trecs.IAspect";
                        }),
                    transform: static (context, _) => GetAspectDataFromSyntax(context)
                )
                .Where(static x => x != null);
            var aspectProvider = AssemblyFilterHelper.FilterByTrecsReference(
                aspectProviderRaw,
                hasTrecsReference
            );

            // Create optimized provider for Unwrap component structs
            var unwrapComponentProviderRaw = context
                .SyntaxProvider.ForAttributeWithMetadataName(
                    "Trecs.UnwrapAttribute",
                    predicate: static (node, _) => node is StructDeclarationSyntax,
                    transform: static (context, _) => GetUnwrapComponentData(context)
                )
                .Where(static x => x != null);
            var unwrapComponentProvider = AssemblyFilterHelper.FilterByTrecsReference(
                unwrapComponentProviderRaw,
                hasTrecsReference
            );

            // Create optimized provider for AspectInterface interfaces
            var aspectInterfaceProviderRaw = context
                .SyntaxProvider.ForAttributeWithMetadataName(
                    "Trecs.AspectInterfaceAttribute",
                    predicate: static (node, _) => node is InterfaceDeclarationSyntax,
                    transform: static (context, _) => GetAspectInterfaceData(context)
                )
                .Where(static x => x != null);
            var aspectInterfaceProvider = AssemblyFilterHelper.FilterByTrecsReference(
                aspectInterfaceProviderRaw,
                hasTrecsReference
            );

            // Collect all UnwrapComponents for cross-validation
            var allUnwrapComponents = unwrapComponentProvider.Collect();

            // Combine Aspects with compilation and UnwrapComponents
            var aspectsWithDependencies = aspectProvider
                .Combine(allUnwrapComponents)
                .Combine(context.CompilationProvider);

            // Register optimized source output with error handling for Aspects
            context.RegisterSourceOutput(
                aspectsWithDependencies,
                static (spc, source) =>
                    ExecuteGeneration(spc, source.Left.Left, source.Left.Right, source.Right)
            );

            // Register source output for AspectInterfaces
            var aspectInterfacesWithCompilation = aspectInterfaceProvider.Combine(
                context.CompilationProvider
            );

            context.RegisterSourceOutput(
                aspectInterfacesWithCompilation,
                static (spc, source) =>
                    ExecuteAspectInterfaceGeneration(spc, source.Left, source.Right)
            );
        }

        private static AspectData? GetAspectDataFromSyntax(GeneratorSyntaxContext context)
        {
            var structDecl = (StructDeclarationSyntax)context.Node;
            var symbol = context.SemanticModel.GetDeclaredSymbol(structDecl) as INamedTypeSymbol;

            if (symbol == null)
                return null;

            return new AspectData(structDecl, symbol);
        }

        private static UnwrapComponentData? GetUnwrapComponentData(
            GeneratorAttributeSyntaxContext context
        )
        {
            var structDecl = (StructDeclarationSyntax)context.TargetNode;
            var symbol = context.TargetSymbol as INamedTypeSymbol;

            if (symbol == null)
                return null;

            return new UnwrapComponentData(structDecl, symbol);
        }

        private static AspectInterfaceData? GetAspectInterfaceData(
            GeneratorAttributeSyntaxContext context
        )
        {
            var interfaceDecl = (InterfaceDeclarationSyntax)context.TargetNode;
            var symbol = context.TargetSymbol as INamedTypeSymbol;

            if (symbol == null)
                return null;

            return new AspectInterfaceData(interfaceDecl, symbol);
        }

        private static void ExecuteGeneration(
            SourceProductionContext context,
            AspectData? aspectData,
            ImmutableArray<UnwrapComponentData?> unwrapComponents,
            Compilation compilation
        )
        {
            if (aspectData == null)
                return;

            var location = aspectData.Syntax.GetLocation();
            var typeName = aspectData.Symbol.Name;
            var fileName = GeneratorBase.CreateSafeFileName(aspectData.Symbol, "Aspect");

            try
            {
                using var _ = SourceGenTimer.Time("AspectGenerator.Total");

                SourceGenLogger.Log($"[IncrementalAspectGenerator] Processing {typeName}");

                // Validate all Unwrap component types first
                ErrorRecovery.TryExecute(
                    () =>
                    {
                        using var _t = SourceGenTimer.Time("AspectGenerator.ValidateUnwrap");
                        Aspect.AspectValidator.ValidateAllUnwrapComponents(
                            unwrapComponents
                                .Where(svc => svc != null)
                                .Select(svc => svc!.Syntax)
                                .ToArray(),
                            compilation,
                            context.ReportDiagnostic
                        );
                    },
                    context,
                    location,
                    "UnwrapComponent validation"
                );

                // Parse the Aspect data from interfaces and attributes
                Aspect.AspectAttributeData? attributeData;
                using (SourceGenTimer.Time("AspectGenerator.ParseAttributes"))
                {
                    attributeData = ErrorRecovery.TryExecute(
                        () =>
                            Aspect.AspectAttributeParser.ParseAspectData(
                                aspectData.Symbol,
                                context.ReportDiagnostic,
                                location
                            )!,
                        context,
                        location,
                        "Aspect data parsing"
                    );
                }

                if (attributeData == null)
                {
                    // Generate fallback with error comment
                    var fallbackSource = ErrorRecovery.GenerateErrorFallback(
                        typeName,
                        SymbolAnalyzer.GetNamespaceChain(aspectData.Symbol),
                        "Failed to parse Aspect attribute"
                    );
                    context.AddSource(fileName, fallbackSource);
                    return;
                }

                // Validate the Aspect
                var isValid =
                    ErrorRecovery.TryExecuteBool(
                        () =>
                        {
                            using var _t = SourceGenTimer.Time("AspectGenerator.Validate");
                            return Aspect.AspectValidator.ValidateAspect(
                                aspectData.Syntax,
                                aspectData.Symbol,
                                attributeData,
                                context.ReportDiagnostic
                            );
                        },
                        context,
                        location,
                        "Aspect validation"
                    ) ?? false;

                if (!isValid)
                {
                    // Generate fallback with validation error
                    var fallbackSource = ErrorRecovery.GenerateErrorFallback(
                        typeName,
                        SymbolAnalyzer.GetNamespaceChain(aspectData.Symbol),
                        "Aspect validation failed. Check diagnostics for details."
                    );
                    context.AddSource(fileName, fallbackSource);
                    return;
                }

                // Validate usage patterns (naming conventions, etc.).
                ErrorRecovery.TryExecute(
                    () =>
                        Aspect.AspectValidator.ValidateUsagePatterns(
                            attributeData,
                            aspectData.Symbol,
                            location,
                            context.ReportDiagnostic
                        ),
                    context,
                    location,
                    "Usage pattern validation"
                );

                // Generate the source code
                string? source;
                using (SourceGenTimer.Time("AspectGenerator.CodeGen"))
                {
                    source = ErrorRecovery.TryExecute(
                        () =>
                            Aspect.AspectCodeGenerator.GenerateAspectSource(
                                aspectData.Symbol,
                                attributeData,
                                compilation
                            ),
                        context,
                        location,
                        "Aspect code generation"
                    );
                }

                if (source != null)
                {
                    context.AddSource(fileName, source);
                    SourceGenLogger.WriteGeneratedFile(fileName, source);
                }
                else
                {
                    // Generation failed, provide fallback
                    var fallbackSource = ErrorRecovery.GenerateErrorFallback(
                        typeName,
                        SymbolAnalyzer.GetNamespaceChain(aspectData.Symbol),
                        "Code generation failed. Check diagnostics for details."
                    );
                    context.AddSource(fileName, fallbackSource);
                }
            }
            catch (Exception ex)
            {
                // Final fallback for any unhandled errors
                ErrorRecovery.ReportError(context, location, "Aspect generation", ex);

                // Still generate a partial class so compilation can continue
                var fallbackSource = ErrorRecovery.GenerateErrorFallback(
                    typeName,
                    SymbolAnalyzer.GetNamespaceChain(aspectData.Symbol),
                    $"Unexpected error: {ex.Message}"
                );
                context.AddSource(fileName, fallbackSource);
            }
        }

        private static void ExecuteAspectInterfaceGeneration(
            SourceProductionContext context,
            AspectInterfaceData? interfaceData,
            Compilation compilation
        )
        {
            if (interfaceData == null)
                return;

            var location = interfaceData.Syntax.GetLocation();
            var typeName = interfaceData.Symbol.Name;
            var fileName = GeneratorBase.CreateSafeFileName(
                interfaceData.Symbol,
                "AspectInterface"
            );

            try
            {
                SourceGenLogger.Log(
                    $"[IncrementalAspectGenerator] Processing AspectInterface {typeName}"
                );

                // Parse the AspectInterfaceAttribute
                var attributeData = ErrorRecovery.TryExecute(
                    () =>
                        Aspect.AspectInterfaceParser.ParseAspectInterfaceAttribute(
                            interfaceData.Symbol
                        )!,
                    context,
                    location,
                    "AspectInterface attribute parsing"
                );

                if (attributeData == null)
                {
                    // Generate fallback with error comment
                    var fallbackSource = ErrorRecovery.GenerateErrorFallback(
                        typeName,
                        SymbolAnalyzer.GetNamespaceChain(interfaceData.Symbol),
                        "Failed to parse AspectInterface attribute"
                    );
                    context.AddSource(fileName, fallbackSource);
                    return;
                }

                // Generate the interface source code
                var source = ErrorRecovery.TryExecute(
                    () =>
                        Aspect.AspectCodeGenerator.GenerateAspectInterfaceSource(
                            interfaceData.Symbol,
                            attributeData,
                            compilation
                        ),
                    context,
                    location,
                    "AspectInterface code generation"
                );

                if (source != null)
                {
                    context.AddSource(fileName, source);
                    SourceGenLogger.WriteGeneratedFile(fileName, source);
                }
                else
                {
                    // Generation failed, provide fallback
                    var fallbackSource = ErrorRecovery.GenerateErrorFallback(
                        typeName,
                        SymbolAnalyzer.GetNamespaceChain(interfaceData.Symbol),
                        "Interface code generation failed. Check diagnostics for details."
                    );
                    context.AddSource(fileName, fallbackSource);
                }
            }
            catch (Exception ex)
            {
                // Final fallback for any unhandled errors
                ErrorRecovery.ReportError(context, location, "AspectInterface generation", ex);

                // Still generate a partial interface so compilation can continue
                var fallbackSource = ErrorRecovery.GenerateErrorFallback(
                    typeName,
                    SymbolAnalyzer.GetNamespaceChain(interfaceData.Symbol),
                    $"Unexpected error: {ex.Message}"
                );
                context.AddSource(fileName, fallbackSource);
            }
        }
    }

    /// <summary>
    /// Data structure for Aspect information used in incremental generation
    /// </summary>
    internal class AspectData
    {
        public StructDeclarationSyntax Syntax { get; }
        public INamedTypeSymbol Symbol { get; }

        public AspectData(StructDeclarationSyntax syntax, INamedTypeSymbol symbol)
        {
            Syntax = syntax;
            Symbol = symbol;
        }
    }

    /// <summary>
    /// Data structure for UnwrapComponent information used in incremental generation
    /// </summary>
    internal class UnwrapComponentData
    {
        public StructDeclarationSyntax Syntax { get; }
        public INamedTypeSymbol Symbol { get; }

        public UnwrapComponentData(StructDeclarationSyntax syntax, INamedTypeSymbol symbol)
        {
            Syntax = syntax;
            Symbol = symbol;
        }
    }

    /// <summary>
    /// Data structure for AspectInterface information used in incremental generation
    /// </summary>
    internal class AspectInterfaceData
    {
        public InterfaceDeclarationSyntax Syntax { get; }
        public INamedTypeSymbol Symbol { get; }

        public AspectInterfaceData(InterfaceDeclarationSyntax syntax, INamedTypeSymbol symbol)
        {
            Syntax = syntax;
            Symbol = symbol;
        }
    }
}
