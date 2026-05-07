using System;
using System.Collections.Generic;
using System.Collections.Immutable;
using Microsoft.CodeAnalysis;
using Microsoft.CodeAnalysis.CSharp.Syntax;
using Trecs.SourceGen.Aspect;
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
    public class AspectGenerator : IIncrementalGenerator
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

            // Unwrap components are validated in their own pipeline so validation runs once
            // per component rather than once per (aspect × unwrap) pair inside ExecuteGeneration.
            context.RegisterSourceOutput(
                unwrapComponentProvider,
                static (spc, data) =>
                {
                    if (data == null)
                        return;
                    ComponentTypeHelper.ValidateUnwrapComponent(
                        data.Symbol,
                        data.Syntax.GetLocation(),
                        spc.ReportDiagnostic
                    );
                }
            );

            // Aspect codegen no longer needs Compilation — the parser walks the symbol and the
            // code generator is symbol-only. Dropping .Combine(CompilationProvider) lets upstream
            // caching survive compilation edits that don't touch any aspect syntax.
            context.RegisterSourceOutput(
                aspectProvider,
                static (spc, source) => ExecuteGeneration(spc, source)
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

            // Run parsing + validation here (the transform phase) so RegisterSourceOutput
            // only needs the cached value-equality-ish data to emit source. Diagnostics
            // accumulate into a list and are replayed downstream. Unexpected exceptions
            // surface as a SourceGenerationError rather than a generator crash.
            var diagnostics = new List<Diagnostic>();
            bool isValid;
            AspectAttributeData? attributeData = null;
            AspectInterfaceData? interfaceData = null;

            try
            {
                if (symbol.TypeKind == TypeKind.Interface)
                {
                    var ifaceDecl = (InterfaceDeclarationSyntax)typeDecl;
                    var isPartial = AspectValidator.ValidateAspectInterfaceDeclaration(
                        ifaceDecl,
                        symbol,
                        diagnostics.Add
                    );
                    if (isPartial)
                    {
                        interfaceData = AspectInterfaceParser.ParseAspectInterface(symbol);
                        isValid = interfaceData != null;
                    }
                    else
                    {
                        isValid = false;
                    }
                }
                else
                {
                    attributeData = AspectAttributeParser.ParseAspectData(symbol);
                    isValid = AspectValidator.ValidateAspect(
                        typeDecl,
                        symbol,
                        attributeData,
                        diagnostics.Add
                    );
                }
            }
            catch (Exception ex)
            {
                diagnostics.Add(
                    Diagnostic.Create(
                        DiagnosticDescriptors.SourceGenerationError,
                        typeDecl.GetLocation(),
                        "Aspect parse/validate",
                        ex.Message
                    )
                );
                isValid = false;
                attributeData = null;
                interfaceData = null;
            }

            return new AspectTargetData(
                typeDecl,
                symbol,
                isValid,
                attributeData,
                interfaceData,
                diagnostics.ToImmutableArray()
            );
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
            AspectTargetData? aspectData
        )
        {
            if (aspectData == null)
                return;

            // Replay diagnostics collected in the transform phase.
            foreach (var diag in aspectData.Diagnostics)
            {
                context.ReportDiagnostic(diag);
            }

            if (!aspectData.IsValid)
                return;

            if (aspectData.Symbol.TypeKind == TypeKind.Interface)
            {
                ExecuteAspectInterfaceGeneration(context, aspectData);
            }
            else
            {
                ExecuteAspectStructGeneration(context, aspectData);
            }
        }

        private static void ExecuteAspectStructGeneration(
            SourceProductionContext context,
            AspectTargetData aspectData
        )
        {
            // Defensive: an IsValid struct-path target is guaranteed to have AttributeData,
            // but this guards against future refactors.
            if (aspectData.AttributeData == null)
                return;

            var location = aspectData.Syntax.GetLocation();
            var typeName = aspectData.Symbol.Name;
            var fileName = GeneratorBase.CreateSafeFileName(aspectData.Symbol, "Aspect");

            try
            {
                using var _ = SourceGenTimer.Time("AspectGenerator.Total");
                SourceGenLogger.Log($"[AspectGenerator] Processing {typeName}");

                string? source;
                using (SourceGenTimer.Time("AspectGenerator.CodeGen"))
                {
                    source = ErrorRecovery.TryExecute(
                        () =>
                            AspectCodeGenerator.GenerateAspectSource(
                                aspectData.Symbol,
                                aspectData.AttributeData
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
            AspectTargetData interfaceData
        )
        {
            // Defensive: an IsValid interface-path target is guaranteed to have
            // InterfaceData, but this guards against future refactors.
            if (interfaceData.InterfaceData == null)
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
                    $"[AspectGenerator] Processing AspectInterface {typeName}"
                );

                var source = ErrorRecovery.TryExecute(
                    () =>
                        AspectCodeGenerator.GenerateAspectInterfaceSource(
                            interfaceData.Symbol,
                            interfaceData.InterfaceData
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
    ///
    /// Carries the parsed attribute/interface data and validation diagnostics forward from
    /// the transform phase so the terminal stage can emit source without re-running parse
    /// + validate on every compilation pump.
    /// </summary>
    internal class AspectTargetData
    {
        public TypeDeclarationSyntax Syntax { get; }
        public INamedTypeSymbol Symbol { get; }
        public bool IsValid { get; }

        /// <summary>Populated for the struct-aspect path; null on the interface path.</summary>
        public AspectAttributeData? AttributeData { get; }

        /// <summary>Populated for the interface-aspect path; null on the struct path.</summary>
        public AspectInterfaceData? InterfaceData { get; }

        public ImmutableArray<Diagnostic> Diagnostics { get; }

        public AspectTargetData(
            TypeDeclarationSyntax syntax,
            INamedTypeSymbol symbol,
            bool isValid,
            AspectAttributeData? attributeData,
            AspectInterfaceData? interfaceData,
            ImmutableArray<Diagnostic> diagnostics
        )
        {
            Syntax = syntax;
            Symbol = symbol;
            IsValid = isValid;
            AttributeData = attributeData;
            InterfaceData = interfaceData;
            Diagnostics = diagnostics;
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
