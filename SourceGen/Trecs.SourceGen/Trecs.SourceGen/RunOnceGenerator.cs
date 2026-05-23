#nullable enable

using System;
using System.Collections.Generic;
using System.Linq;
using Microsoft.CodeAnalysis;
using Microsoft.CodeAnalysis.CSharp;
using Microsoft.CodeAnalysis.CSharp.Syntax;
using Trecs.SourceGen.Internal;
using Trecs.SourceGen.Performance;
using Trecs.SourceGen.Shared;

namespace Trecs.SourceGen
{
    /// <summary>
    /// Source generator for run-once methods on partial system classes — methods that
    /// have one or more <c>[SingleEntity]</c> parameters and no other iteration attribute.
    /// <para>
    /// Each <c>[SingleEntity]</c> parameter is resolved via
    /// <c>World.Query().WithTags&lt;...&gt;().SingleIndex()</c> (which asserts
    /// exactly one match) before the user method body. Aspect-typed parameters get a
    /// materialized aspect view; <c>in</c>/<c>ref</c> component parameters get a buffer
    /// element reference. The user method is then called once with all parameters bound.
    /// </para>
    /// <para>Pipeline shape: the transform produces a value-equatable
    /// <see cref="RunOnceModel"/> (strings + EquatableArray, no symbols) and the
    /// terminal stage materializes diagnostics + emits source. The compilation's
    /// global-namespace name is folded in via a lightweight value-equatable combine.</para>
    /// </summary>
    [Generator]
    public class RunOnceGenerator : IIncrementalGenerator
    {
        public void Initialize(IncrementalGeneratorInitializationContext context)
        {
            var hasTrecsReference = AssemblyFilterHelper.CreateTrecsReferenceCheck(context);

            var modelsRaw = context
                .SyntaxProvider.CreateSyntaxProvider(
                    predicate: static (s, _) => IsCandidateMethod(s),
                    transform: static (ctx, _) => BuildModel(ctx)
                )
                .Where(static m => m is not null)
                .Select(static (m, _) => m!.Value);
            var models = AssemblyFilterHelper.FilterByTrecsReference(modelsRaw, hasTrecsReference);

            // Only the global-namespace name leaves the CompilationProvider — a single
            // string, value-equatable. Unrelated edits to other types don't invalidate
            // the downstream combine.
            var globalNsProvider = context.CompilationProvider.Select(
                static (c, _) =>
                    PerformanceCache.GetDisplayString(c.GlobalNamespace) ?? string.Empty
            );

            var withGlobalNs = models.Combine(globalNsProvider);

            context.RegisterSourceOutput(
                withGlobalNs,
                static (spc, source) => GenerateSource(spc, source.Left, source.Right)
            );
        }

        private static bool IsCandidateMethod(SyntaxNode node)
        {
            // Cheap syntactic predicate: any parameter carries an attribute whose simple
            // name (after stripping `Attribute`) matches "SingleEntity".
            if (node is not MethodDeclarationSyntax methodDecl)
                return false;
            foreach (var param in methodDecl.ParameterList.Parameters)
            {
                foreach (var alist in param.AttributeLists)
                {
                    foreach (var attr in alist.Attributes)
                    {
                        if (
                            IterationCriteriaParser.ExtractAttributeName(attr.Name.ToString())
                            == TrecsAttributeNames.SingleEntity
                        )
                            return true;
                    }
                }
            }
            return false;
        }

        private static RunOnceModel? BuildModel(GeneratorSyntaxContext context)
        {
            var methodDecl = (MethodDeclarationSyntax)context.Node;
            var classDecl = methodDecl.FirstAncestorOrSelf<ClassDeclarationSyntax>();
            if (classDecl == null)
                return null;

            var methodSymbol = context.SemanticModel.GetDeclaredSymbol(methodDecl) as IMethodSymbol;
            if (methodSymbol == null)
                return null;

            // Only claim methods that are RunOnce (have [SingleEntity] params, no
            // [ForEachEntity], no [WrapAsJob]). Mixed methods (e.g. [ForEachEntity] +
            // [SingleEntity] params) belong to the ForEach generators, which honor the
            // hoisted-singleton slots emitted by ParameterClassifier.
            if (!IterationAttributeRouting.IsRunOnceMethod(methodSymbol))
                return null;

            var classSymbol = methodSymbol.ContainingType;
            var className = classDecl.Identifier.Text;
            var methodName = methodDecl.Identifier.Text;
            var diagnostics = new List<DiagnosticInfo>();
            bool isValid;
            EquatableArray<RunOnceParamSlotModel> paramSlots =
                EquatableArray<RunOnceParamSlotModel>.Empty;
            EquatableArray<HoistedSingletonModel> hoistedSingletons =
                EquatableArray<HoistedSingletonModel>.Empty;

            try
            {
                isValid = Validate(
                    classDecl,
                    methodDecl,
                    context.SemanticModel,
                    diagnostics,
                    out paramSlots,
                    out hoistedSingletons
                );
            }
            catch (Exception ex)
            {
                diagnostics.Add(
                    DiagnosticInfo.Create(
                        DiagnosticDescriptors.SourceGenerationError,
                        methodDecl.GetLocation(),
                        "RunOnce method validation",
                        ex.Message
                    )
                );
                isValid = false;
            }

            var containingTypes = SymbolAnalyzer
                .GetContainingTypeChainInfo(classSymbol)
                .ToEquatableArray();

            return new RunOnceModel(
                ClassName: className,
                MethodName: methodName,
                Namespace: SymbolAnalyzer.GetNamespace(classDecl),
                Visibility: SymbolAnalyzer.GetMethodVisibility(methodDecl),
                HintFileName: SymbolAnalyzer.GetSafeFileName(classSymbol, $"{methodName}_RunOnce"),
                ContainingTypes: containingTypes,
                ParamSlots: paramSlots,
                HoistedSingletons: hoistedSingletons,
                IsValid: isValid,
                Diagnostics: diagnostics.ToEquatableArray()
            );
        }

        private static void GenerateSource(
            SourceProductionContext context,
            RunOnceModel model,
            string globalNamespaceName
        )
        {
            foreach (var diag in model.Diagnostics)
                context.ReportDiagnostic(diag.ToDiagnostic());

            if (!model.IsValid)
                return;

            try
            {
                using var _ = SourceGenTimer.Time("RunOnceGenerator.Total");
                SourceGenLogger.Log(
                    $"[RunOnceGenerator] Processing {model.ClassName}.{model.MethodName}"
                );

                var source = ErrorRecovery.TryExecute(
                    () => Emit(model, globalNamespaceName),
                    context,
                    Location.None,
                    "RunOnce code generation"
                );

                if (source != null)
                {
                    context.AddSource(model.HintFileName, source);
                    SourceGenLogger.WriteGeneratedFile(model.HintFileName, source);
                }
                else
                {
                    context.ReportDiagnostic(
                        Diagnostic.Create(
                            DiagnosticDescriptors.CouldNotResolveSymbol,
                            Location.None,
                            $"{model.ClassName}.{model.MethodName}"
                        )
                    );
                }
            }
            catch (Exception ex)
            {
                ErrorRecovery.ReportError(
                    context,
                    Location.None,
                    $"RunOnce {model.ClassName}.{model.MethodName}",
                    ex
                );
            }
        }

        // ─────────────────────────────────────────────────────────────────────
        // Validation
        // ─────────────────────────────────────────────────────────────────────

        private static bool Validate(
            ClassDeclarationSyntax classDecl,
            MethodDeclarationSyntax methodDecl,
            SemanticModel semanticModel,
            List<DiagnosticInfo> diagnostics,
            out EquatableArray<RunOnceParamSlotModel> paramSlots,
            out EquatableArray<HoistedSingletonModel> hoistedSingletons
        )
        {
            paramSlots = EquatableArray<RunOnceParamSlotModel>.Empty;
            hoistedSingletons = EquatableArray<HoistedSingletonModel>.Empty;

            bool isValid = true;

            if (!classDecl.Modifiers.Any(m => m.IsKind(SyntaxKind.PartialKeyword)))
            {
                diagnostics.Add(
                    DiagnosticInfo.Create(
                        DiagnosticDescriptors.NotPartialClass,
                        classDecl.Identifier.GetLocation(),
                        classDecl.Identifier.Text
                    )
                );
                isValid = false;
            }

            if (methodDecl.ReturnType.ToString() != "void")
            {
                diagnostics.Add(
                    DiagnosticInfo.Create(
                        DiagnosticDescriptors.InvalidReturnType,
                        methodDecl.ReturnType.GetLocation()
                    )
                );
                isValid = false;
            }

            var slots = new List<RunOnceParamSlotModel>();
            var hoisted = new List<HoistedSingletonModel>();
            // ParameterClassifier.ClassifyHoistedSingleton emits already-substituted
            // Diagnostic objects via its Action<Diagnostic> callback. Stash them as
            // preformatted DiagnosticInfo so the descriptor's MessageFormat isn't applied
            // a second time when we materialize at the terminal stage.
            System.Action<Diagnostic> reportDiag = d =>
                diagnostics.Add(DiagnosticInfo.FromDiagnostic(d));

            foreach (var param in methodDecl.ParameterList.Parameters)
            {
                var paramType =
                    param.Type != null ? semanticModel.GetTypeInfo(param.Type).Type : null;
                if (paramType == null)
                {
                    isValid = false;
                    continue;
                }

                var paramSymbol = semanticModel.GetDeclaredSymbol(param);
                bool hasSingleEntity =
                    paramSymbol != null
                    && PerformanceCache.HasAttributeByName(
                        paramSymbol,
                        TrecsAttributeNames.SingleEntity,
                        TrecsNamespaces.Trecs
                    );
                bool hasFromWorld =
                    paramSymbol != null
                    && PerformanceCache.HasAttributeByName(
                        paramSymbol,
                        TrecsAttributeNames.FromWorld,
                        TrecsNamespaces.Trecs
                    );
                bool hasPassThrough =
                    paramSymbol != null
                    && PerformanceCache.HasAttributeByName(
                        paramSymbol,
                        TrecsAttributeNames.PassThroughArgument,
                        TrecsNamespaces.Trecs
                    );

                bool isRef = param.Modifiers.Any(m => m.IsKind(SyntaxKind.RefKeyword));
                bool isIn = param.Modifiers.Any(m => m.IsKind(SyntaxKind.InKeyword));

                if (hasSingleEntity)
                {
                    if (hasFromWorld || hasPassThrough)
                    {
                        diagnostics.Add(
                            DiagnosticInfo.Create(
                                DiagnosticDescriptors.SingleEntityConflictingAttributes,
                                param.GetLocation(),
                                param.Identifier.Text,
                                hasFromWorld ? "FromWorld" : "PassThroughArgument"
                            )
                        );
                        isValid = false;
                        continue;
                    }

                    var info = ParameterClassifier.ClassifyHoistedSingleton(
                        param,
                        paramType,
                        paramSymbol!,
                        isRef,
                        isIn,
                        reportDiag
                    );
                    if (info == null)
                    {
                        isValid = false;
                        continue;
                    }
                    var idx = hoisted.Count;
                    hoisted.Add(HoistedSingletonModelBuilder.FromInfo(info));
                    slots.Add(RunOnceParamSlotModel.Singleton(idx));
                    continue;
                }

                bool isWorldAccessor = SymbolAnalyzer.IsExactType(
                    paramType,
                    "WorldAccessor",
                    TrecsNamespaces.Trecs
                );
                if (isWorldAccessor)
                {
                    if (isRef || isIn)
                    {
                        diagnostics.Add(
                            DiagnosticInfo.Create(
                                DiagnosticDescriptors.ParameterMustBeByValue,
                                param.GetLocation(),
                                param.Identifier.Text
                            )
                        );
                        isValid = false;
                        continue;
                    }
                    slots.Add(RunOnceParamSlotModel.WorldAccessor());
                    continue;
                }

                if (hasPassThrough)
                {
                    slots.Add(
                        RunOnceParamSlotModel.Custom(
                            paramName: param.Identifier.ToString(),
                            paramTypeDisplay: PerformanceCache.GetDisplayString(paramType),
                            isRef: isRef,
                            isIn: isIn
                        )
                    );
                    continue;
                }

                // Anything else is an error — RunOnce methods are nothing but
                // [SingleEntity] params + WorldAccessor + [PassThroughArgument].
                diagnostics.Add(
                    DiagnosticInfo.Create(
                        DiagnosticDescriptors.UnrecognizedParameterType,
                        param.GetLocation(),
                        param.Identifier.Text,
                        PerformanceCache.GetDisplayString(paramType)
                    )
                );
                isValid = false;
            }

            if (isValid && hoisted.Count == 0)
            {
                diagnostics.Add(
                    DiagnosticInfo.Create(
                        DiagnosticDescriptors.EmptyParameters,
                        methodDecl.ParameterList.GetLocation()
                    )
                );
                isValid = false;
            }

            // Custom params on a method named "Execute" are forbidden by the auto-system
            // wrapper rules (TRECS043).
            if (
                methodDecl.Identifier.Text == "Execute"
                && slots.Any(s => s.Kind == RunOnceParamKind.Custom)
            )
            {
                diagnostics.Add(
                    DiagnosticInfo.Create(
                        DiagnosticDescriptors.AutoSystemMethodHasCustomParams,
                        methodDecl.Identifier.GetLocation(),
                        methodDecl.Identifier.Text
                    )
                );
                isValid = false;
            }

            paramSlots = slots.ToEquatableArray();
            hoistedSingletons = hoisted.ToEquatableArray();
            return isValid;
        }

        // ─────────────────────────────────────────────────────────────────────
        // Code emission
        // ─────────────────────────────────────────────────────────────────────

        private static string Emit(RunOnceModel model, string globalNamespaceName)
        {
            var sb = OptimizedStringBuilder.ForAspect(8);

            var namespaces = new HashSet<string>(CommonUsings.Namespaces)
            {
                "Unity.Jobs",
                "System",
            };
            HoistedSingleEmitter.CollectNamespaces(
                namespaces,
                model.HoistedSingletons,
                globalNamespaceName
            );
            sb.AppendUsings(namespaces.ToArray());

            return sb.WrapInNamespace(
                    model.Namespace,
                    (b) =>
                    {
                        b.WrapInContainingTypes(
                            model.ContainingTypes.ToArray(),
                            0,
                            (inner, indent) =>
                                inner.WrapInType(
                                    "public",
                                    "class",
                                    model.ClassName,
                                    (cb) => EmitOverload(cb, model),
                                    indent
                                )
                        );
                    }
                )
                .ToString();
        }

        private static void EmitOverload(OptimizedStringBuilder sb, RunOnceModel model)
        {
            var customDecl = string.Join(
                "",
                model
                    .ParamSlots.Where(s => s.Kind == RunOnceParamKind.Custom)
                    .Select(s =>
                        $", {(s.IsRef ? "ref " : s.IsIn ? "in " : "")}{s.ParamTypeDisplay} {s.ParamName}"
                    )
            );
            sb.AppendLine(2, GeneratedCodeAttributes.Line);
            sb.AppendLine(
                2,
                $"{model.Visibility} void {model.MethodName}(WorldAccessor __world{customDecl})"
            );
            sb.AppendLine(2, "{");

            HoistedSingleEmitter.Emit(
                sb,
                indentLevel: 3,
                worldVar: "__world",
                model.HoistedSingletons
            );

            var callArgs = new List<string>();
            foreach (var slot in model.ParamSlots)
            {
                switch (slot.Kind)
                {
                    case RunOnceParamKind.Singleton:
                        var s = model.HoistedSingletons[slot.Index];
                        callArgs.Add(s.IsRef ? $"ref __{s.ParamName}" : $"in __{s.ParamName}");
                        break;
                    case RunOnceParamKind.WorldAccessor:
                        callArgs.Add("__world");
                        break;
                    case RunOnceParamKind.Custom:
                        var prefix =
                            slot.IsRef ? "ref "
                            : slot.IsIn ? "in "
                            : "";
                        callArgs.Add($"{prefix}{slot.ParamName}");
                        break;
                }
            }

            sb.AppendLine(3, $"{model.MethodName}({string.Join(", ", callArgs)});");
            sb.AppendLine(2, "}");
            sb.AppendLine();
        }
    }

    /// <summary>
    /// What a parameter slot in a RunOnce method represents. The slot list is walked at
    /// emit time to build the call to the user's method preserving declaration order.
    /// </summary>
    internal enum RunOnceParamKind
    {
        Singleton,
        WorldAccessor,
        Custom,
    }

    /// <summary>
    /// Value-equality slot record for a single RunOnce parameter. <see cref="Index"/>
    /// references <see cref="RunOnceModel.HoistedSingletons"/> for <c>Singleton</c>
    /// kind; the custom-param fields are populated only for <c>Custom</c>; everything
    /// else is unused.
    /// </summary>
    internal readonly record struct RunOnceParamSlotModel(
        RunOnceParamKind Kind,
        int Index,
        string ParamName,
        string ParamTypeDisplay,
        bool IsRef,
        bool IsIn
    )
    {
        public static RunOnceParamSlotModel Singleton(int index) =>
            new(RunOnceParamKind.Singleton, index, string.Empty, string.Empty, false, false);

        public static RunOnceParamSlotModel WorldAccessor() =>
            new(RunOnceParamKind.WorldAccessor, 0, string.Empty, string.Empty, false, false);

        public static RunOnceParamSlotModel Custom(
            string paramName,
            string paramTypeDisplay,
            bool isRef,
            bool isIn
        ) => new(RunOnceParamKind.Custom, 0, paramName, paramTypeDisplay, isRef, isIn);
    }

    /// <summary>
    /// Value-equality model carried through the RunOnceGenerator pipeline. Has no
    /// references to Roslyn symbols, syntax, or live <see cref="Diagnostic"/> objects
    /// (diagnostics flow as <see cref="DiagnosticInfo"/> and are materialized only at
    /// the terminal stage). That's what lets the SourceOutput step cache when an
    /// unrelated tree edit re-runs the generator.
    /// </summary>
    internal readonly record struct RunOnceModel(
        string ClassName,
        string MethodName,
        string Namespace,
        string Visibility,
        string HintFileName,
        EquatableArray<ContainingTypeInfo> ContainingTypes,
        EquatableArray<RunOnceParamSlotModel> ParamSlots,
        EquatableArray<HoistedSingletonModel> HoistedSingletons,
        bool IsValid,
        EquatableArray<DiagnosticInfo> Diagnostics
    );
}
