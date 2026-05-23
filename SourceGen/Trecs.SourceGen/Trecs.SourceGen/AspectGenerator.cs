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
    ///
    /// <para>Pipeline shape: the transform produces a fully-precomputed <see cref="AspectModel"/>
    /// (value-equatable, holds no symbols or syntax) and the terminal stage materializes
    /// diagnostics + emits source. This keeps the incremental cache effective — typing in
    /// an unrelated file does not invalidate aspect codegen.</para>
    /// </summary>
    [Generator]
    public class AspectGenerator : IIncrementalGenerator
    {
        public void Initialize(IncrementalGeneratorInitializationContext context)
        {
            var hasTrecsReference = AssemblyFilterHelper.CreateTrecsReferenceCheck(context);

            // Aspect types (struct or interface) — detected by IAspect appearing in the base list.
            // The transform builds an equatable AspectModel; symbols and syntax do not leave it.
            var aspectModelsRaw = context
                .SyntaxProvider.CreateSyntaxProvider(
                    predicate: static (node, _) => HasIAspectInBaseList(node),
                    transform: static (ctx, _) => BuildAspectModel(ctx)
                )
                .Where(static x => x is not null);
            var aspectModels = AssemblyFilterHelper.FilterByTrecsReference(
                aspectModelsRaw,
                hasTrecsReference
            );

            // Unwrap component structs — used for validation independent of any aspect.
            var unwrapComponentProviderRaw = context
                .SyntaxProvider.ForAttributeWithMetadataName(
                    "Trecs.UnwrapAttribute",
                    predicate: static (node, _) => node is StructDeclarationSyntax,
                    transform: static (ctx, _) => GetUnwrapComponentData(ctx)
                )
                .Where(static x => x != null);
            var unwrapComponentProvider = AssemblyFilterHelper.FilterByTrecsReference(
                unwrapComponentProviderRaw,
                hasTrecsReference
            );

            // Unwrap components are validated in their own pipeline so validation runs once per
            // component rather than once per (aspect × unwrap) pair during ExecuteGeneration.
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

            // Aspect codegen consumes only the equatable model — no Compilation, no symbols.
            context.RegisterSourceOutput(
                aspectModels,
                static (spc, model) => ExecuteGeneration(spc, model!)
            );
        }

        /// <summary>
        /// Cheap syntactic filter: does this struct or interface declaration *potentially*
        /// implement <c>Trecs.IAspect</c>? Runs before any semantic work, so it must stay
        /// string-based.
        ///
        /// <para>Under the attribute-free aspect design an aspect can implement IAspect
        /// transitively via an aspect-interface whose name we can't resolve syntactically
        /// (e.g. <c>struct Foo : IMovable</c>). So this predicate lets through anything whose
        /// base list contains at least one non-IRead/IWrite entry, and the transform step does
        /// the real semantic check via <see cref="SymbolAnalyzer.ImplementsInterface"/>. The
        /// cost of the extra semantic resolutions is bounded (at most one per type with a
        /// user-defined base interface) and paid only when the syntax node changes — Roslyn
        /// caches the transform output.</para>
        ///
        /// <para>Intentionally does NOT gate on the <c>partial</c> modifier so the validator can
        /// emit <c>TRECS020 AspectInterfaceMustBePartial</c> on non-partial aspect interfaces.</para>
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

        private static AspectModel? BuildAspectModel(GeneratorSyntaxContext context)
        {
            var typeDecl = (TypeDeclarationSyntax)context.Node;
            var symbol = context.SemanticModel.GetDeclaredSymbol(typeDecl) as INamedTypeSymbol;

            if (symbol == null)
                return null;

            // Final gate: the syntactic predicate is conservative; verify the type really
            // implements Trecs.IAspect before producing a model.
            if (!SymbolAnalyzer.ImplementsInterface(symbol, "IAspect", TrecsNamespaces.Trecs))
                return null;

            return AspectModelBuilder.Build(typeDecl, symbol);
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

        private static void ExecuteGeneration(SourceProductionContext context, AspectModel model)
        {
            // Replay diagnostics collected in the transform phase. Materializing a Diagnostic
            // happens here (terminal stage) — the pipeline only carried DiagnosticInfo.
            foreach (var diag in model.Diagnostics)
                context.ReportDiagnostic(diag.ToDiagnostic());

            if (!model.IsValid)
                return;

            if (model.IsInterface)
                ExecuteAspectInterfaceGeneration(context, model);
            else
                ExecuteAspectStructGeneration(context, model);
        }

        private static void ExecuteAspectStructGeneration(
            SourceProductionContext context,
            AspectModel model
        )
        {
            var location = Location.None;
            try
            {
                using var _ = SourceGenTimer.Time("AspectGenerator.Total");
                SourceGenLogger.Log($"[AspectGenerator] Processing {model.TypeName}");

                string? source;
                using (SourceGenTimer.Time("AspectGenerator.CodeGen"))
                {
                    source = ErrorRecovery.TryExecute(
                        () => AspectCodeGenerator.GenerateAspectSource(model),
                        context,
                        location,
                        "Aspect code generation"
                    );
                }

                if (source != null)
                {
                    context.AddSource(model.HintFileName, source);
                    SourceGenLogger.WriteGeneratedFile(model.HintFileName, source);
                }
            }
            catch (System.Exception ex)
            {
                ErrorRecovery.ReportError(context, location, "Aspect generation", ex);
            }
        }

        private static void ExecuteAspectInterfaceGeneration(
            SourceProductionContext context,
            AspectModel model
        )
        {
            var location = Location.None;
            try
            {
                SourceGenLogger.Log(
                    $"[AspectGenerator] Processing AspectInterface {model.TypeName}"
                );

                var source = ErrorRecovery.TryExecute(
                    () => AspectCodeGenerator.GenerateAspectInterfaceSource(model),
                    context,
                    location,
                    "AspectInterface code generation"
                );

                if (source != null)
                {
                    context.AddSource(model.HintFileName, source);
                    SourceGenLogger.WriteGeneratedFile(model.HintFileName, source);
                }
            }
            catch (System.Exception ex)
            {
                ErrorRecovery.ReportError(context, location, "AspectInterface generation", ex);
            }
        }
    }

    /// <summary>
    /// The Unwrap-component validation pipeline still carries Roslyn types because it's a
    /// fire-and-forget validator (no source emission, just diagnostics). Caching its output
    /// gives nothing back, so the symbol/syntax retention cost is acceptable here.
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
}
