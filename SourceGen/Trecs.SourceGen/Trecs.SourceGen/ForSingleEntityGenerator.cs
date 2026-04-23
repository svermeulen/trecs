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
    /// Source generator for ForSingleComponents methods that query a single matching entity.
    /// Uses the same semantics as Aspect.Single() — asserts exactly one entity via Assert.That.
    /// </summary>
    [Generator]
    public class ForSingleComponentsGenerator : IIncrementalGenerator
    {
        public void Initialize(IncrementalGeneratorInitializationContext context)
        {
            var hasTrecsReference = AssemblyFilterHelper.CreateTrecsReferenceCheck(context);

            var methodProviderRaw = context
                .SyntaxProvider.CreateSyntaxProvider(
                    predicate: static (s, _) => IsMethodWithForSingleComponentsAttribute(s),
                    transform: static (ctx, _) => GetMethodData(ctx)
                )
                .Where(static m => m is not null);
            var methodProvider = AssemblyFilterHelper.FilterByTrecsReference(
                methodProviderRaw,
                hasTrecsReference
            );

            // See IncrementalForEachGenerator for the caching rationale: extract just the
            // compilation-derived string we need rather than combining with the full
            // CompilationProvider (which changes identity on every edit).
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

        private static bool IsMethodWithForSingleComponentsAttribute(SyntaxNode node)
        {
            return node is MethodDeclarationSyntax methodDecl
                && methodDecl.AttributeLists.Count > 0
                && methodDecl
                    .AttributeLists.SelectMany(al => al.Attributes)
                    .Any(attr =>
                        IterationCriteriaParser.ExtractAttributeName(attr.Name.ToString())
                        == TrecsAttributeNames.SingleEntity
                    );
        }

        private static ForEachMethodData? GetMethodData(GeneratorSyntaxContext context)
        {
            var methodDecl = (MethodDeclarationSyntax)context.Node;

            var classDecl = methodDecl.FirstAncestorOrSelf<ClassDeclarationSyntax>();
            if (classDecl == null)
                return null;

            var methodSymbol = context.SemanticModel.GetDeclaredSymbol(methodDecl) as IMethodSymbol;
            if (methodSymbol == null)
                return null;

            // [SingleEntity] only routes to this generator when the method has NO
            // IAspect parameter — otherwise the aspect-side generator
            // (ForSingleAspectGenerator) takes it.
            if (!IterationAttributeRouting.HasSingleEntity(methodSymbol))
                return null;
            if (!IterationAttributeRouting.RoutesToComponentsGenerator(methodSymbol))
                return null;

            // Validation runs in the transform so the terminal stage doesn't need a
            // Compilation or SemanticModel.
            var diagnostics = new List<Diagnostic>();
            ValidatedMethodInfo? validated = null;
            bool isValid = ValidateMethod(
                classDecl,
                methodDecl,
                diagnostics.Add,
                context.SemanticModel,
                out validated
            );

            return new ForEachMethodData(
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
            ForEachMethodData data,
            string globalNamespaceName
        )
        {
            var location = data.MethodDecl.GetLocation();
            var className = data.ClassDecl.Identifier.Text;
            var methodName = data.MethodDecl.Identifier.Text;
            var fileName = SymbolAnalyzer.GetSafeFileName(
                data.MethodSymbol.ContainingType,
                $"{methodName}_ForSingleComponents"
            );

            // Replay diagnostics collected in the transform phase.
            foreach (var diag in data.Diagnostics)
            {
                context.ReportDiagnostic(diag);
            }

            if (!data.IsValid || data.ValidatedInfo == null)
            {
                return;
            }

            try
            {
                using var _timer_ = SourceGenTimer.Time("ForSingleEntityGenerator.Total");
                SourceGenLogger.Log(
                    $"[ForSingleComponentsGenerator] Processing {className}.{methodName}"
                );

                var source = ErrorRecovery.TryExecute(
                    () =>
                        GenerateSourceCode(
                            data.ClassDecl,
                            data.MethodDecl,
                            data.ValidatedInfo,
                            globalNamespaceName
                        ),
                    context,
                    location,
                    "ForSingleComponents code generation"
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
                    $"ForSingleComponents {className}.{methodName}",
                    ex
                );
            }
        }

        private static string GenerateSourceCode(
            ClassDeclarationSyntax classDec,
            MethodDeclarationSyntax methodDec,
            ValidatedMethodInfo validatedParamsInfo,
            string globalNamespaceName
        )
        {
            var namespaceName = SymbolAnalyzer.GetNamespace(classDec);
            var className = classDec.Identifier.Text;
            var methodName = methodDec.Identifier.Text;

            var componentCount = validatedParamsInfo.ComponentParameters.Count;
            var sb = OptimizedStringBuilder.ForAspect(componentCount);

            var requiredNamespaces = NamespaceCollector.Collect(
                globalNamespaceName,
                validatedParamsInfo,
                includeSystemNamespace: true
            );

            sb.AppendUsings(requiredNamespaces.ToArray());

            return sb.WrapInNamespace(
                    namespaceName,
                    (builder) =>
                    {
                        builder.AppendLine(1, $"partial class {className}");
                        builder.AppendLine(1, "{");
                        GenerateSingleOverloads(builder, methodName, validatedParamsInfo);
                        builder.AppendLine(1, "}");
                    }
                )
                .ToString();
        }

        private static void GenerateSingleOverloads(
            OptimizedStringBuilder sb,
            string methodName,
            ValidatedMethodInfo info
        )
        {
            var customArgsDecStr = "";
            var customArgsCallStr = "";

            if (info.CustomParameters.Any())
            {
                customArgsDecStr =
                    ", "
                    + string.Join(
                        ", ",
                        info.CustomParameters.Select(p =>
                            $"{(p.IsRef ? "ref " : p.IsIn ? "in " : "")}{(p.TypeSymbol != null ? PerformanceCache.GetDisplayString(p.TypeSymbol) : p.Type)} {p.Name}"
                        )
                    );
                customArgsCallStr =
                    ", "
                    + string.Join(
                        ", ",
                        info.CustomParameters.Select(p =>
                            $"{(p.IsRef ? "ref " : p.IsIn ? "in " : "")}{p.Name}"
                        )
                    );
            }

            bool hasSets = info.HasSet;
            bool hasAnyAttributeCriteria =
                hasSets || info.HasAttributeTags || info.MatchByComponents;

            // (1) Convenience overload — only when the attribute supplies at least one criterion.
            if (hasAnyAttributeCriteria)
            {
                GenerateConvenienceOverload(
                    sb,
                    methodName,
                    info,
                    customArgsDecStr,
                    customArgsCallStr
                );
            }

            // (2) Public dense entry — only when the attribute imposes no Set/Sets.
            if (!hasSets)
            {
                GenerateQueryBuilderEntry(sb, methodName, info, customArgsDecStr);
            }

            // (3) Public sparse entry — only when the attribute has no set baked in.
            if (!hasSets)
            {
                GenerateSparseQueryBuilderEntry(sb, methodName, info, customArgsDecStr);
            }
        }

        private static void GenerateConvenienceOverload(
            OptimizedStringBuilder sb,
            string methodName,
            ValidatedMethodInfo info,
            string customArgsDecStr,
            string customArgsCallStr
        )
        {
            sb.AppendLine(2, $"void {methodName}(WorldAccessor __world{customArgsDecStr})");
            sb.AppendLine(2, "{");

            if (info.HasSet)
            {
                // Sparse path. Build the full SparseQueryBuilder inline.
                var firstSetName = PerformanceCache.GetDisplayString(info.SetTypes[0]);
                var attributeChain = QueryBuilderHelper.BuildAttributeCriteriaChain(
                    info.AttributeTagTypes,
                    info.MatchByComponents,
                    info.ComponentParameters.Select(p => p.TypeSymbol)
                );
                sb.AppendLine(
                    3,
                    $"var __builder = __world.Query(){attributeChain}.InSet<{firstSetName}>();"
                );
                EmitSingleEntityBody(
                    sb,
                    methodName,
                    info,
                    indentLevel: 3,
                    builderVar: "__builder",
                    worldVar: "__world"
                );
            }
            else
            {
                // Dense path: delegate to the (QueryBuilder) entry.
                sb.AppendLine(3, $"{methodName}(__world.Query(){customArgsCallStr});");
            }

            sb.AppendLine(2, "}");
            sb.AppendLine();
        }

        private static void GenerateQueryBuilderEntry(
            OptimizedStringBuilder sb,
            string methodName,
            ValidatedMethodInfo info,
            string customArgsDecStr
        )
        {
            sb.AppendLine(2, $"void {methodName}(QueryBuilder __builder{customArgsDecStr})");
            sb.AppendLine(2, "{");

            var chain = QueryBuilderHelper.BuildAttributeCriteriaChain(
                info.AttributeTagTypes,
                info.MatchByComponents,
                info.ComponentParameters.Select(p => p.TypeSymbol)
            );
            if (chain.Length > 0)
                sb.AppendLine(3, $"__builder = __builder{chain};");

            sb.AppendLine(
                3,
                $"Assert.That(__builder.HasAnyCriteria, \"{methodName} requires query criteria — pass a builder with at least one tag, component, or set constraint, or specify Tag/Set/MatchByComponents on the [SingleEntity] attribute.\");"
            );

            EmitSingleEntityBody(
                sb,
                methodName,
                info,
                indentLevel: 3,
                builderVar: "__builder",
                worldVar: null
            );

            sb.AppendLine(2, "}");
            sb.AppendLine();
        }

        private static void GenerateSparseQueryBuilderEntry(
            OptimizedStringBuilder sb,
            string methodName,
            ValidatedMethodInfo info,
            string customArgsDecStr
        )
        {
            sb.AppendLine(2, $"void {methodName}(SparseQueryBuilder __builder{customArgsDecStr})");
            sb.AppendLine(2, "{");

            var chain = QueryBuilderHelper.BuildAttributeCriteriaChain(
                info.AttributeTagTypes,
                info.MatchByComponents,
                info.ComponentParameters.Select(p => p.TypeSymbol)
            );
            if (chain.Length > 0)
                sb.AppendLine(3, $"__builder = __builder{chain};");

            EmitSingleEntityBody(
                sb,
                methodName,
                info,
                indentLevel: 3,
                builderVar: "__builder",
                worldVar: null
            );

            sb.AppendLine(2, "}");
            sb.AppendLine();
        }

        /// <summary>
        /// Emits the body that resolves a single matching entity via
        /// <c>builder.SingleEntityIndex()</c> (which asserts exactly one match), fetches the
        /// component buffers for that entity's group, gets ref/ref-readonly locals at the
        /// matched index, and calls the user method.
        /// </summary>
        private static void EmitSingleEntityBody(
            OptimizedStringBuilder sb,
            string methodName,
            ValidatedMethodInfo info,
            int indentLevel,
            string builderVar,
            string? worldVar
        )
        {
            string worldName = worldVar ?? "__world";
            if (worldVar == null)
                sb.AppendLine(indentLevel, $"var {worldName} = {builderVar}.World;");

            sb.AppendLine(indentLevel, $"var __ei = {builderVar}.SingleEntityIndex();");

            foreach (var sa in info.SetAccessorParameters)
            {
                sb.AppendLine(
                    indentLevel,
                    $"var {sa.ParamName} = {worldName}.Set<{sa.SetTypeArg}>();"
                );
            }
            foreach (var sr in info.SetReadParameters)
            {
                sb.AppendLine(
                    indentLevel,
                    $"var {sr.ParamName} = {worldName}.Set<{sr.SetTypeArg}>().Read;"
                );
            }
            foreach (var sw in info.SetWriteParameters)
            {
                sb.AppendLine(
                    indentLevel,
                    $"var {sw.ParamName} = {worldName}.Set<{sw.SetTypeArg}>().Write;"
                );
            }

            for (int i = 0; i < info.ComponentParameters.Count; i++)
            {
                var param = info.ComponentParameters[i];
                var typeStr =
                    param.TypeSymbol != null
                        ? PerformanceCache.GetDisplayString(param.TypeSymbol)
                        : param.Type;
                var bufferSuffix = param.IsRef ? "Write" : "Read";
                sb.AppendLine(
                    indentLevel,
                    $"var values{i + 1} = {worldName}.ComponentBuffer<{typeStr}>(__ei.Group).{bufferSuffix};"
                );
            }

            for (int i = 0; i < info.ComponentParameters.Count; i++)
            {
                var param = info.ComponentParameters[i];
                var refKind = param.IsRef ? "ref" : "ref readonly";
                sb.AppendLine(
                    indentLevel,
                    $"{refKind} var value{i + 1} = ref values{i + 1}[__ei.Index];"
                );
            }

            // Build the call args in the user's declared parameter order.
            var slotArgs = new List<string>(info.ParameterSlots.Count);
            foreach (var slot in info.ParameterSlots)
            {
                switch (slot.Kind)
                {
                    case ParamSlotKind.LoopComponent:
                        var c = info.ComponentParameters[slot.Index];
                        slotArgs.Add($"{(c.IsRef ? "ref" : "in")} value{slot.Index + 1}");
                        break;
                    case ParamSlotKind.LoopEntityIndex:
                        slotArgs.Add("__ei");
                        break;
                    case ParamSlotKind.LoopWorldAccessor:
                        slotArgs.Add(worldName);
                        break;
                    case ParamSlotKind.LoopSetAccessor:
                        var sa = info.SetAccessorParameters[slot.Index];
                        slotArgs.Add(sa.IsIn ? $"in {sa.ParamName}" : sa.ParamName);
                        break;
                    case ParamSlotKind.LoopSetRead:
                        var sr = info.SetReadParameters[slot.Index];
                        slotArgs.Add($"in {sr.ParamName}");
                        break;
                    case ParamSlotKind.LoopSetWrite:
                        var sw = info.SetWriteParameters[slot.Index];
                        slotArgs.Add($"in {sw.ParamName}");
                        break;
                    case ParamSlotKind.Custom:
                        var p = info.CustomParameters[slot.Index];
                        var prefix =
                            p.IsRef ? "ref "
                            : p.IsIn ? "in "
                            : "";
                        slotArgs.Add($"{prefix}{p.Name}");
                        break;
                }
            }
            sb.AppendLine(indentLevel, $"{methodName}({string.Join(", ", slotArgs)});");
        }

        private static bool ValidateMethod(
            ClassDeclarationSyntax classDec,
            MethodDeclarationSyntax methodDec,
            System.Action<Diagnostic> reportDiagnostic,
            SemanticModel semanticModel,
            out ValidatedMethodInfo? validatedParamsInfo
        )
        {
            // Reuse the same validation logic as ForEachComponents
            validatedParamsInfo = null;
            bool isValid = true;

            if (!classDec.Modifiers.Any(m => m.IsKind(SyntaxKind.PartialKeyword)))
            {
                reportDiagnostic(
                    Diagnostic.Create(
                        DiagnosticDescriptors.NotPartialClass,
                        classDec.Identifier.GetLocation(),
                        classDec.Identifier.Text
                    )
                );
                isValid = false;
            }

            if (methodDec.ReturnType.ToString() != "void")
            {
                reportDiagnostic(
                    Diagnostic.Create(
                        DiagnosticDescriptors.InvalidReturnType,
                        methodDec.ReturnType.GetLocation()
                    )
                );
                isValid = false;
            }

            var parameters = methodDec.ParameterList.Parameters;

            if (parameters.Count == 0)
            {
                reportDiagnostic(
                    Diagnostic.Create(
                        DiagnosticDescriptors.EmptyParameters,
                        methodDec.ParameterList.GetLocation()
                    )
                );
                return false;
            }

            // Classify parameters via shared classifier.
            var classified = ParameterClassifier.Classify(
                parameters,
                semanticModel,
                IterationMode.Components,
                reportDiagnostic,
                methodName: null,
                supportsEntityIndex: true,
                aspectParam: null,
                isValid: ref isValid
            );
            bool hasAnyIterationParameter =
                classified.ComponentParameters.Count > 0 || classified.HasEntityIndex;

            if (!hasAnyIterationParameter)
            {
                reportDiagnostic(
                    Diagnostic.Create(
                        DiagnosticDescriptors.EmptyParameters,
                        methodDec.ParameterList.GetLocation()
                    )
                );
                isValid = false;
            }

            // Extract tag types, set types, and MatchByComponents from the iteration attribute.
            var className = classDec.Identifier.Text;
            var methodSymbol = semanticModel.GetDeclaredSymbol(methodDec);
            var attributeTagTypes = new List<ITypeSymbol>();
            var setTypes = new List<ITypeSymbol>();
            bool attributeMatchByComponents = false;
            if (methodSymbol != null)
            {
                var criteria = IterationCriteriaParser.ParseIterationAttribute(
                    reportDiagnostic,
                    methodDec,
                    methodSymbol,
                    className,
                    TrecsAttributeNames.SingleEntity
                );
                if (criteria == null)
                {
                    isValid = false;
                }
                else
                {
                    attributeTagTypes = criteria.TagTypes;
                    setTypes = criteria.SetTypes;
                    attributeMatchByComponents = criteria.MatchByComponents;
                }
            }

            if (isValid)
            {
                validatedParamsInfo = new ValidatedMethodInfo
                {
                    ComponentParameters = classified.ComponentParameters.ToList(),
                    CustomParameters = classified.CustomParameters.ToList(),
                    HasEntityIndexParameter = classified.HasEntityIndex,
                    ParameterSlots = classified.ParameterSlots.ToList(),
                    AttributeTagTypes = attributeTagTypes,
                    SetTypes = setTypes,
                    MatchByComponents = attributeMatchByComponents,
                    SetAccessorParameters = classified.SetAccessorParameters.ToList(),
                    SetReadParameters = classified.SetReadParameters.ToList(),
                    SetWriteParameters = classified.SetWriteParameters.ToList(),
                };
            }

            return isValid;
        }
    }
}
