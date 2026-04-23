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
    /// Incremental source generator for Trecs aspects. Handles both flavors of aspect — struct
    /// (concrete, with component buffers and NativeFactory) and interface (shared contract for
    /// polymorphic helpers). A type participates by implementing Trecs.IAspect in its base list;
    /// dispatch into the two codegen paths is by symbol kind.
    /// </summary>
    [Generator]
    public class IncrementalAspectGenerator : IIncrementalGenerator
    {
        public void Initialize(IncrementalGeneratorInitializationContext context)
        {
            // Check if compilation references Trecs assembly for better performance
            var hasTrecsReference = AssemblyFilterHelper.CreateTrecsReferenceCheck(context);

            // Aspect types (struct or interface) — detected by IAspect appearing in the base list.
            var aspectProviderRaw = context
                .SyntaxProvider.CreateSyntaxProvider(
                    predicate: static (node, _) => HasIAspectInBaseList(node),
                    transform: static (context, _) => GetAspectTargetFromSyntax(context)
                )
                .Where(static x => x != null);
            var aspectProvider = AssemblyFilterHelper.FilterByTrecsReference(
                aspectProviderRaw,
                hasTrecsReference
            );

            // Unwrap component structs — used for validation downstream.
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

            var allUnwrapComponents = unwrapComponentProvider.Collect();

            var aspectsWithDependencies = aspectProvider
                .Combine(allUnwrapComponents)
                .Combine(context.CompilationProvider);

            context.RegisterSourceOutput(
                aspectsWithDependencies,
                static (spc, source) =>
                    ExecuteGeneration(spc, source.Left.Left, source.Left.Right, source.Right)
            );
        }

        /// <summary>
        /// Cheap syntactic filter: does this struct or interface declaration *potentially*
        /// implement <c>Trecs.IAspect</c>? Runs before any semantic work, so it must stay
        /// string-based.
        ///
        /// Under the new (attribute-free) design an aspect can implement IAspect transitively
        /// via an aspect-interface whose name we can't resolve syntactically (e.g. <c>struct
        /// Foo : IMovable</c>). So this predicate must let through anything whose base list
        /// contains at least one non-IRead/IWrite entry, and the transform step does the real
        /// semantic check via <see cref="SymbolAnalyzer.ImplementsInterface"/>. The cost of
        /// the extra semantic resolutions is bounded (at most one per type with a user-defined
        /// base interface) and paid only when the syntax node changes — Roslyn caches the
        /// transform output.
        ///
        /// Intentionally does NOT gate on the <c>partial</c> modifier so the validator can
        /// emit <c>TRECS020 AspectInterfaceMustBePartial</c> on non-partial aspect interfaces.
        /// </summary>
        private static bool HasIAspectInBaseList(SyntaxNode node)
        {
            BaseListSyntax? baseList = node switch
            {
                StructDeclarationSyntax sds => sds.BaseList,
                InterfaceDeclarationSyntax ids => ids.BaseList,
                _ => null,
            };

            if (baseList == null)
                return false;

            foreach (var baseType in baseList.Types)
            {
                if (IsCandidateBaseIdentifier(baseType.Type.ToString()))
                    return true;
            }
            return false;
        }

        /// <summary>
        /// Rules out base types that can't transitively carry IAspect: generic IRead/IWrite
        /// (user-declared component access, never an aspect interface). Everything else is a
        /// candidate for the semantic pass.
        /// </summary>
        private static bool IsCandidateBaseIdentifier(string typeStr)
        {
            if (typeStr.StartsWith("IRead<") || typeStr.StartsWith("Trecs.IRead<"))
                return false;
            if (typeStr.StartsWith("IWrite<") || typeStr.StartsWith("Trecs.IWrite<"))
                return false;
            return true;
        }

        private static AspectTargetData? GetAspectTargetFromSyntax(GeneratorSyntaxContext context)
        {
            var typeDecl = (TypeDeclarationSyntax)context.Node;
            var symbol = context.SemanticModel.GetDeclaredSymbol(typeDecl) as INamedTypeSymbol;

            if (symbol == null)
                return null;

            // Only emit for types that actually implement Trecs.IAspect. The syntactic predicate
            // is conservative and lets through any partial with a non-IRead/IWrite base; this
            // semantic check is the final gate.
            if (!SymbolAnalyzer.ImplementsInterface(symbol, "IAspect", TrecsNamespaces.Trecs))
                return null;

            return new AspectTargetData(typeDecl, symbol);
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

        private static void ExecuteGeneration(
            SourceProductionContext context,
            AspectTargetData? aspectData,
            ImmutableArray<UnwrapComponentData?> unwrapComponents,
            Compilation compilation
        )
        {
            if (aspectData == null)
                return;

            if (aspectData.Symbol.TypeKind == TypeKind.Interface)
            {
                ExecuteAspectInterfaceGeneration(context, aspectData, compilation);
            }
            else
            {
                ExecuteAspectStructGeneration(context, aspectData, unwrapComponents, compilation);
            }
        }

        private static void ExecuteAspectStructGeneration(
            SourceProductionContext context,
            AspectTargetData aspectData,
            ImmutableArray<UnwrapComponentData?> unwrapComponents,
            Compilation compilation
        )
        {
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
                        () => Aspect.AspectAttributeParser.ParseAspectData(aspectData.Symbol),
                        context,
                        location,
                        "Aspect data parsing"
                    );
                }

                if (attributeData == null)
                {
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
                    return;
                }

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
            }
            catch (Exception ex)
            {
                ErrorRecovery.ReportError(context, location, "Aspect generation", ex);
            }
        }

        private static void ExecuteAspectInterfaceGeneration(
            SourceProductionContext context,
            AspectTargetData interfaceData,
            Compilation compilation
        )
        {
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

                // The interface must be partial so the generator can merge its emitted contract.
                var ifaceDecl = (InterfaceDeclarationSyntax)interfaceData.Syntax;
                var isPartial =
                    ErrorRecovery.TryExecuteBool(
                        () =>
                            Aspect.AspectValidator.ValidateAspectInterfaceDeclaration(
                                ifaceDecl,
                                interfaceData.Symbol,
                                context.ReportDiagnostic
                            ),
                        context,
                        location,
                        "AspectInterface partial validation"
                    ) ?? false;

                if (!isPartial)
                    return;

                var parsedData = ErrorRecovery.TryExecute(
                    () => Aspect.AspectInterfaceParser.ParseAspectInterface(interfaceData.Symbol)!,
                    context,
                    location,
                    "AspectInterface parsing"
                );

                if (parsedData == null)
                {
                    return;
                }

                var source = ErrorRecovery.TryExecute(
                    () =>
                        Aspect.AspectCodeGenerator.GenerateAspectInterfaceSource(
                            interfaceData.Symbol,
                            parsedData,
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
            }
            catch (Exception ex)
            {
                ErrorRecovery.ReportError(context, location, "AspectInterface generation", ex);
            }
        }
    }

    /// <summary>
    /// Data structure for an aspect-generation target — a struct or an interface implementing
    /// Trecs.IAspect. TypeKind on the symbol distinguishes the two flavors.
    /// </summary>
    internal class AspectTargetData
    {
        public TypeDeclarationSyntax Syntax { get; }
        public INamedTypeSymbol Symbol { get; }

        public AspectTargetData(TypeDeclarationSyntax syntax, INamedTypeSymbol symbol)
        {
            Syntax = syntax;
            Symbol = symbol;
        }
    }

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
}
