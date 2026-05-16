#nullable enable

using System;
using System.Collections.Generic;
using System.Collections.Immutable;
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
    /// </summary>
    [Generator]
    public class RunOnceGenerator : IIncrementalGenerator
    {
        public void Initialize(IncrementalGeneratorInitializationContext context)
        {
            var hasTrecsReference = AssemblyFilterHelper.CreateTrecsReferenceCheck(context);

            var methodProviderRaw = context
                .SyntaxProvider.CreateSyntaxProvider(
                    predicate: static (s, _) => IsCandidateMethod(s),
                    transform: static (ctx, _) => GetMethodData(ctx)
                )
                .Where(static m => m is not null);
            var methodProvider = AssemblyFilterHelper.FilterByTrecsReference(
                methodProviderRaw,
                hasTrecsReference
            );

            var globalNsProvider = context.CompilationProvider.Select(
                static (c, _) =>
                    PerformanceCache.GetDisplayString(c.GlobalNamespace) ?? string.Empty
            );

            var methodWithGlobalNs = methodProvider.Combine(globalNsProvider);

            context.RegisterSourceOutput(
                methodWithGlobalNs,
                static (spc, source) => GenerateSource(spc, source.Left!, source.Right)
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

        private static MethodData? GetMethodData(GeneratorSyntaxContext context)
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

            var diagnostics = new List<Diagnostic>();
            ValidatedRunOnce? validated = null;
            bool isValid;
            try
            {
                isValid = Validate(
                    classDecl,
                    methodDecl,
                    methodSymbol,
                    context.SemanticModel,
                    diagnostics.Add,
                    out validated
                );
            }
            catch (Exception ex)
            {
                diagnostics.Add(
                    Diagnostic.Create(
                        DiagnosticDescriptors.SourceGenerationError,
                        methodDecl.GetLocation(),
                        "RunOnce method validation",
                        ex.Message
                    )
                );
                isValid = false;
                validated = null;
            }

            return new MethodData(
                classDecl,
                methodDecl,
                methodSymbol,
                isValid,
                validated,
                diagnostics.ToImmutableArray()
            );
        }

        private static void GenerateSource(
            SourceProductionContext context,
            MethodData data,
            string globalNamespaceName
        )
        {
            foreach (var diag in data.Diagnostics)
                context.ReportDiagnostic(diag);

            if (!data.IsValid || data.Validated == null)
                return;

            var className = data.ClassDecl.Identifier.Text;
            var methodName = data.MethodDecl.Identifier.Text;
            var fileName = SymbolAnalyzer.GetSafeFileName(
                data.MethodSymbol.ContainingType,
                $"{methodName}_RunOnce"
            );
            var location = data.MethodDecl.GetLocation();

            try
            {
                using var _ = SourceGenTimer.Time("RunOnceGenerator.Total");
                SourceGenLogger.Log($"[RunOnceGenerator] Processing {className}.{methodName}");

                var source = ErrorRecovery.TryExecute(
                    () =>
                        Emit(
                            data.ClassDecl,
                            data.MethodDecl,
                            data.MethodSymbol.ContainingType,
                            data.Validated,
                            globalNamespaceName
                        ),
                    context,
                    location,
                    "RunOnce code generation"
                );

                if (source != null)
                {
                    context.AddSource(fileName, source);
                    SourceGenLogger.WriteGeneratedFile(fileName, source);
                }
                else
                {
                    context.ReportDiagnostic(
                        Diagnostic.Create(
                            DiagnosticDescriptors.CouldNotResolveSymbol,
                            location,
                            $"{className}.{methodName}"
                        )
                    );
                }
            }
            catch (Exception ex)
            {
                ErrorRecovery.ReportError(
                    context,
                    location,
                    $"RunOnce {className}.{methodName}",
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
            IMethodSymbol methodSymbol,
            SemanticModel semanticModel,
            Action<Diagnostic> reportDiagnostic,
            out ValidatedRunOnce? validated
        )
        {
            validated = null;
            bool isValid = true;

            if (!classDecl.Modifiers.Any(m => m.IsKind(SyntaxKind.PartialKeyword)))
            {
                reportDiagnostic(
                    Diagnostic.Create(
                        DiagnosticDescriptors.NotPartialClass,
                        classDecl.Identifier.GetLocation(),
                        classDecl.Identifier.Text
                    )
                );
                isValid = false;
            }

            if (methodDecl.ReturnType.ToString() != "void")
            {
                reportDiagnostic(
                    Diagnostic.Create(
                        DiagnosticDescriptors.InvalidReturnType,
                        methodDecl.ReturnType.GetLocation()
                    )
                );
                isValid = false;
            }

            var paramSlots = new List<RunOnceParamSlot>();
            var hoistedSingletons = new List<HoistedSingletonInfo>();
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
                        reportDiagnostic(
                            Diagnostic.Create(
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
                        reportDiagnostic
                    );
                    if (info == null)
                    {
                        isValid = false;
                        continue;
                    }
                    var idx = hoistedSingletons.Count;
                    hoistedSingletons.Add(info);
                    paramSlots.Add(new RunOnceParamSlot(RunOnceParamKind.Singleton, idx));
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
                        reportDiagnostic(
                            Diagnostic.Create(
                                DiagnosticDescriptors.ParameterMustBeByValue,
                                param.GetLocation(),
                                param.Identifier.Text
                            )
                        );
                        isValid = false;
                        continue;
                    }
                    paramSlots.Add(new RunOnceParamSlot(RunOnceParamKind.WorldAccessor, 0));
                    continue;
                }

                if (hasPassThrough)
                {
                    var customIndex = paramSlots.Count(s => s.Kind == RunOnceParamKind.Custom);
                    paramSlots.Add(
                        new RunOnceParamSlot(
                            RunOnceParamKind.Custom,
                            customIndex,
                            param.Identifier.ToString(),
                            PerformanceCache.GetDisplayString(paramType),
                            isRef,
                            isIn
                        )
                    );
                    continue;
                }

                // Anything else is an error — RunOnce methods are nothing but
                // [SingleEntity] params + WorldAccessor + [PassThroughArgument].
                reportDiagnostic(
                    Diagnostic.Create(
                        DiagnosticDescriptors.UnrecognizedParameterType,
                        param.GetLocation(),
                        param.Identifier.Text,
                        PerformanceCache.GetDisplayString(paramType)
                    )
                );
                isValid = false;
            }

            if (isValid && hoistedSingletons.Count == 0)
            {
                reportDiagnostic(
                    Diagnostic.Create(
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
                && paramSlots.Any(s => s.Kind == RunOnceParamKind.Custom)
            )
            {
                reportDiagnostic(
                    Diagnostic.Create(
                        DiagnosticDescriptors.AutoSystemMethodHasCustomParams,
                        methodDecl.Identifier.GetLocation(),
                        methodDecl.Identifier.Text
                    )
                );
                isValid = false;
            }

            if (isValid)
            {
                validated = new ValidatedRunOnce
                {
                    Visibility = SymbolAnalyzer.GetMethodVisibility(methodDecl),
                    ParamSlots = paramSlots,
                    HoistedSingletons = hoistedSingletons,
                };
            }
            return isValid;
        }

        // (singleton classification lives in ParameterClassifier.ClassifyHoistedSingleton —
        // RunOnceGenerator's Validate above calls into it directly.)

        // ─────────────────────────────────────────────────────────────────────
        // Code emission
        // ─────────────────────────────────────────────────────────────────────

        private static string Emit(
            ClassDeclarationSyntax classDecl,
            MethodDeclarationSyntax methodDecl,
            INamedTypeSymbol classSymbol,
            ValidatedRunOnce info,
            string globalNamespaceName
        )
        {
            var namespaceName = SymbolAnalyzer.GetNamespace(classDecl);
            var className = classDecl.Identifier.Text;
            var methodName = methodDecl.Identifier.Text;
            var visibility = info.Visibility;

            var sb = OptimizedStringBuilder.ForAspect(8);

            var namespaces = new HashSet<string>(CommonUsings.Namespaces)
            {
                "Unity.Jobs",
                "System",
            };
            HoistedSingleEmitter.CollectNamespaces(
                namespaces,
                info.HoistedSingletons,
                globalNamespaceName
            );
            sb.AppendUsings(namespaces.ToArray());

            // Walk the system class's containing-type chain so the emitted partial merges
            // with a nested system class instead of landing at namespace scope.
            var containingTypes = SymbolAnalyzer.GetContainingTypeChainInfo(classSymbol);

            return sb.WrapInNamespace(
                    namespaceName,
                    (b) =>
                    {
                        b.WrapInContainingTypes(
                            containingTypes,
                            0,
                            (inner, indent) =>
                                inner.WrapInType(
                                    "public",
                                    "class",
                                    className,
                                    (cb) => EmitOverload(cb, methodName, visibility, info),
                                    indent
                                )
                        );
                    }
                )
                .ToString();
        }

        private static void EmitOverload(
            OptimizedStringBuilder sb,
            string methodName,
            string visibility,
            ValidatedRunOnce info
        )
        {
            var customDecl = string.Join(
                "",
                info.ParamSlots.Where(s => s.Kind == RunOnceParamKind.Custom)
                    .Select(s =>
                        $", {(s.IsRef ? "ref " : s.IsIn ? "in " : "")}{s.ParamTypeDisplay} {s.ParamName}"
                    )
            );
            sb.AppendLine(2, $"{visibility} void {methodName}(WorldAccessor __world{customDecl})");
            sb.AppendLine(2, "{");

            HoistedSingleEmitter.Emit(
                sb,
                indentLevel: 3,
                worldVar: "__world",
                info.HoistedSingletons
            );

            var callArgs = new List<string>();
            foreach (var slot in info.ParamSlots)
            {
                switch (slot.Kind)
                {
                    case RunOnceParamKind.Singleton:
                        var s = info.HoistedSingletons[slot.Index];
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

            sb.AppendLine(3, $"{methodName}({string.Join(", ", callArgs)});");
            sb.AppendLine(2, "}");
            sb.AppendLine();
        }

        // ─────────────────────────────────────────────────────────────────────
        // Data carriers
        // ─────────────────────────────────────────────────────────────────────

        private enum RunOnceParamKind
        {
            Singleton,
            WorldAccessor,
            Custom,
        }

        private class RunOnceParamSlot
        {
            public RunOnceParamKind Kind { get; }

            /// <summary>Index into the singleton or custom-param list (unused for WorldAccessor).</summary>
            public int Index { get; }

            // Custom-param fields. Singleton fields live in HoistedSingletonInfo.
            public string ParamName { get; }
            public string ParamTypeDisplay { get; }
            public bool IsRef { get; }
            public bool IsIn { get; }

            public RunOnceParamSlot(RunOnceParamKind kind, int index)
            {
                Kind = kind;
                Index = index;
                ParamName = string.Empty;
                ParamTypeDisplay = string.Empty;
            }

            public RunOnceParamSlot(
                RunOnceParamKind kind,
                int index,
                string paramName,
                string paramTypeDisplay,
                bool isRef,
                bool isIn
            )
            {
                Kind = kind;
                Index = index;
                ParamName = paramName;
                ParamTypeDisplay = paramTypeDisplay;
                IsRef = isRef;
                IsIn = isIn;
            }
        }

        private class ValidatedRunOnce
        {
            public string Visibility = string.Empty;
            public List<RunOnceParamSlot> ParamSlots = new();
            public List<HoistedSingletonInfo> HoistedSingletons = new();
        }

        private class MethodData
        {
            public ClassDeclarationSyntax ClassDecl { get; }
            public MethodDeclarationSyntax MethodDecl { get; }
            public IMethodSymbol MethodSymbol { get; }
            public bool IsValid { get; }
            public ValidatedRunOnce? Validated { get; }
            public ImmutableArray<Diagnostic> Diagnostics { get; }

            public MethodData(
                ClassDeclarationSyntax classDecl,
                MethodDeclarationSyntax methodDecl,
                IMethodSymbol methodSymbol,
                bool isValid,
                ValidatedRunOnce? validated,
                ImmutableArray<Diagnostic> diagnostics
            )
            {
                ClassDecl = classDecl;
                MethodDecl = methodDecl;
                MethodSymbol = methodSymbol;
                IsValid = isValid;
                Validated = validated;
                Diagnostics = diagnostics;
            }
        }
    }
}
