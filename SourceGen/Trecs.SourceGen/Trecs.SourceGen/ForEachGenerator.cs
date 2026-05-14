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
    /// Source generator for [ForEachEntity] methods that iterate over ECS components
    /// (component-parameter shape). The aspect-parameter shape is handled by
    /// ForEachEntityAspectGenerator; routing is decided by IterationAttributeRouting.
    /// </summary>
    [Generator]
    public class ForEachGenerator : IIncrementalGenerator
    {
        public void Initialize(IncrementalGeneratorInitializationContext context)
        {
            // Check if compilation references Trecs assembly for better performance
            var hasTrecsReference = AssemblyFilterHelper.CreateTrecsReferenceCheck(context);

            // Create provider for methods with GenerateForEachComponentsMethods attribute.
            // Validation runs inside the transform — accumulated diagnostics are replayed
            // in the terminal stage. This avoids having to .Combine(CompilationProvider)
            // to get a SemanticModel (the transform already has one via
            // GeneratorSyntaxContext.SemanticModel), which is what used to invalidate the
            // pipeline cache on every compilation change.
            var forEachMethodProviderRaw = context
                .SyntaxProvider.CreateSyntaxProvider(
                    predicate: static (s, _) => IsMethodWithForEachComponentsAttribute(s),
                    transform: static (ctx, _) => GetForEachMethodData(ctx)
                )
                .Where(static m => m is not null);
            var forEachMethodProvider = AssemblyFilterHelper.FilterByTrecsReference(
                forEachMethodProviderRaw,
                hasTrecsReference
            );

            // Value-equality provider for the one compilation-derived datum we still need
            // at codegen time: the global-namespace display string (used by
            // NamespaceCollector to suppress a spurious `using ;`). Extracting just this
            // string from the Compilation means the downstream .Combine only re-fires when
            // the string actually changes, which is ~never in a given project.
            var globalNsProvider = context.CompilationProvider.Select(
                static (c, _) =>
                    PerformanceCache.GetDisplayString(c.GlobalNamespace) ?? string.Empty
            );

            var forEachWithGlobalNs = forEachMethodProvider.Combine(globalNsProvider);

            context.RegisterSourceOutput(
                forEachWithGlobalNs,
                static (spc, source) => GenerateForEachSource(spc, source.Left!, source.Right)
            );
        }

        private static bool IsMethodWithForEachComponentsAttribute(SyntaxNode node)
        {
            return node is MethodDeclarationSyntax methodDecl
                && methodDecl.AttributeLists.Count > 0
                && methodDecl
                    .AttributeLists.SelectMany(al => al.Attributes)
                    .Any(attr =>
                        IterationCriteriaParser.ExtractAttributeName(attr.Name.ToString())
                        == TrecsAttributeNames.ForEachEntity
                    );
        }

        private static ForEachMethodData? GetForEachMethodData(GeneratorSyntaxContext context)
        {
            var methodDecl = (MethodDeclarationSyntax)context.Node;

            // [ForEachEntity] on a method of a struct is the JobGenerator's
            // responsibility (it generates IJobFor scheduling). Skip those here.
            if (methodDecl.Parent is StructDeclarationSyntax)
                return null;

            // Find the containing class
            var classDecl = methodDecl.FirstAncestorOrSelf<ClassDeclarationSyntax>();
            if (classDecl == null)
                return null;

            var methodSymbol = context.SemanticModel.GetDeclaredSymbol(methodDecl) as IMethodSymbol;
            if (methodSymbol == null)
                return null;

            // [ForEachEntity] only routes to this generator when the method has NO
            // IAspect parameter — otherwise the aspect-side generator
            // (ForEachEntityAspectGenerator) takes it.
            if (!IterationAttributeRouting.HasEntityFilter(methodSymbol))
                return null;
            if (IterationAttributeRouting.HasWrapAsJobAttribute(methodSymbol))
                return null;
            if (!IterationAttributeRouting.RoutesToComponentsGenerator(methodSymbol))
                return null;

            // Run validation here (the transform phase) so we don't have to combine with
            // CompilationProvider just to fetch a SemanticModel in the terminal stage.
            // Diagnostics are accumulated and replayed in RegisterSourceOutput. Unexpected
            // exceptions are caught and surfaced as a SourceGenerationError diagnostic
            // (matching the old ErrorRecovery.TryExecuteBool behaviour from before the
            // validator moved into the transform).
            var diagnostics = new List<Diagnostic>();
            ValidatedMethodInfo? validated = null;
            bool isValid;
            try
            {
                isValid = ValidateMethod(
                    classDecl,
                    methodDecl,
                    methodSymbol,
                    diagnostics.Add,
                    context.SemanticModel,
                    out validated
                );
            }
            catch (Exception ex)
            {
                diagnostics.Add(
                    Diagnostic.Create(
                        DiagnosticDescriptors.SourceGenerationError,
                        methodDecl.GetLocation(),
                        "ForEach method validation",
                        ex.Message
                    )
                );
                isValid = false;
                validated = null;
            }

            return new ForEachMethodData(
                classDecl,
                methodDecl,
                methodSymbol,
                isValid,
                validated,
                diagnostics.ToImmutableArray()
            );
        }

        private static void GenerateForEachSource(
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
                $"{methodName}_ForEach"
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
                using var _timer_ = SourceGenTimer.Time("ForEachGenerator.Total");
                SourceGenLogger.Log($"[ForEachGenerator] Processing {className}.{methodName}");

                var source = ErrorRecovery.TryExecute(
                    () =>
                        GenerateSource(
                            data.ClassDecl,
                            data.MethodDecl,
                            data.MethodSymbol.ContainingType,
                            data.ValidatedInfo,
                            globalNamespaceName
                        ),
                    context,
                    location,
                    "ForEach code generation"
                );

                if (source != null)
                {
                    context.AddSource(fileName, source);
                    SourceGenLogger.WriteGeneratedFile(fileName, source);
                }
                else
                {
                    var diagnostic = Diagnostic.Create(
                        DiagnosticDescriptors.CouldNotResolveSymbol,
                        location,
                        $"{className}.{methodName}"
                    );
                    context.ReportDiagnostic(diagnostic);
                }
            }
            catch (Exception ex)
            {
                ErrorRecovery.ReportError(
                    context,
                    location,
                    $"ForEach {className}.{methodName}",
                    ex
                );
            }
        }

        private static string GenerateSource(
            ClassDeclarationSyntax classDec,
            MethodDeclarationSyntax methodDec,
            INamedTypeSymbol classSymbol,
            ValidatedMethodInfo validatedParamsInfo,
            string globalNamespaceName
        )
        {
            var namespaceName = SymbolAnalyzer.GetNamespace(classDec);
            var className = classDec.Identifier.Text;
            var methodName = methodDec.Identifier.Text;

            var componentCount = validatedParamsInfo.ComponentParameters.Count;
            var sb = OptimizedStringBuilder.ForAspect(componentCount);

            // Add required namespaces
            var requiredNamespaces = NamespaceCollector.Collect(
                globalNamespaceName,
                validatedParamsInfo
            );

            sb.AppendUsings(requiredNamespaces.ToArray());

            // Walk the system class's containing-type chain so the emitted partial merges
            // with a nested system class instead of landing at namespace scope.
            var containingTypes = SymbolAnalyzer.GetContainingTypeChainInfo(classSymbol);

            return sb.WrapInNamespace(
                    namespaceName,
                    (builder) =>
                    {
                        builder.WrapInContainingTypes(
                            containingTypes,
                            0,
                            (b, indent) =>
                            {
                                b.AppendLine(indent, $"partial class {className}");
                                b.AppendLine(indent, "{");
                                GenerateForEachOverloads(
                                    b,
                                    methodName,
                                    validatedParamsInfo,
                                    methodDec
                                );
                                b.AppendLine(indent, "}");
                            }
                        );
                    }
                )
                .ToString();
        }

        private static void GenerateForEachOverloads(
            OptimizedStringBuilder sb,
            string methodName,
            ValidatedMethodInfo info,
            MethodDeclarationSyntax methodDec
        )
        {
            // Build parameter declarations for custom arguments
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
            // For empty attributes we deliberately do NOT emit this; callers must explicitly pass
            // a builder so iteration cannot accidentally walk every group in the world.
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

            // (4) Range overload for event handlers — always emitted (event observers may forward
            // their callback args + custom event args). Mirrors the aspect-mode generator.
            sb.AppendLine(
                2,
                $"void {methodName}(GroupIndex __group, EntityRange __indices, WorldAccessor __world{customArgsDecStr})"
            );
            sb.AppendLine(2, "{");
            GenerateRangeIteration(sb, methodName, info, 3);
            sb.AppendLine(2, "}");
            sb.AppendLine();
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
                EmitSparseIterationBody(
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
                info.ComponentParameters.Select(p => p.TypeSymbol),
                info.AttributeWithoutTagTypes
            );
            if (chain.Length > 0)
                sb.AppendLine(3, $"__builder = __builder{chain};");

            sb.AppendLine(
                3,
                $"TrecsAssert.That(__builder.HasAnyCriteria, \"{methodName} requires query criteria — pass a builder with at least one tag, component, or set constraint, or specify Tag/Set/MatchByComponents on the [ForEachEntity] attribute.\");"
            );

            EmitDenseIterationBody(
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
                info.ComponentParameters.Select(p => p.TypeSymbol),
                info.AttributeWithoutTagTypes
            );
            if (chain.Length > 0)
                sb.AppendLine(3, $"__builder = __builder{chain};");

            EmitSparseIterationBody(
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
        /// Builds the comma-separated argument list for the call to the user's method,
        /// preserving the user's declaration order via <see cref="ValidatedMethodInfo.ParameterSlots"/>.
        /// </summary>
        private static string BuildUserMethodCallArgs(
            ValidatedMethodInfo info,
            string worldVar,
            string entityIndexVar,
            string componentValueVarPrefix
        )
        {
            var args = new List<string>(info.ParameterSlots.Count);
            foreach (var slot in info.ParameterSlots)
            {
                switch (slot.Kind)
                {
                    case ParamSlotKind.LoopComponent:
                        var c = info.ComponentParameters[slot.Index];
                        var refOrIn = c.IsRef ? "ref" : "in";
                        args.Add($"{refOrIn} {componentValueVarPrefix}{slot.Index + 1}");
                        break;
                    case ParamSlotKind.LoopEntityIndex:
                        args.Add(entityIndexVar);
                        break;
                    case ParamSlotKind.LoopEntityHandle:
                        args.Add("__entityHandle");
                        break;
                    case ParamSlotKind.LoopEntityAccessor:
                        args.Add("__entityAccessor");
                        break;
                    case ParamSlotKind.LoopWorldAccessor:
                        args.Add(worldVar);
                        break;
                    case ParamSlotKind.LoopSetAccessor:
                        var sa = info.SetAccessorParameters[slot.Index];
                        args.Add(sa.IsIn ? $"in {sa.ParamName}" : sa.ParamName);
                        break;
                    case ParamSlotKind.LoopSetRead:
                        var sr = info.SetReadParameters[slot.Index];
                        args.Add($"in {sr.ParamName}");
                        break;
                    case ParamSlotKind.LoopSetWrite:
                        var sw = info.SetWriteParameters[slot.Index];
                        args.Add($"in {sw.ParamName}");
                        break;
                    case ParamSlotKind.Custom:
                        var p = info.CustomParameters[slot.Index];
                        var prefix =
                            p.IsRef ? "ref "
                            : p.IsIn ? "in "
                            : "";
                        args.Add($"{prefix}{p.Name}");
                        break;
                    case ParamSlotKind.HoistedSingleton:
                        var hs = info.HoistedSingletons[slot.Index];
                        args.Add(hs.IsRef ? $"ref __{hs.ParamName}" : $"in __{hs.ParamName}");
                        break;
                }
            }
            return string.Join(", ", args);
        }

        private static void EmitDenseIterationBody(
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

            NamespaceCollector.EmitSetAccessorDeclarations(sb, info, indentLevel, worldName);
            HoistedSingleEmitter.Emit(sb, indentLevel, worldName, info.HoistedSingletons);

            sb.AppendLine(indentLevel, $"foreach (var __slice in {builderVar}.GroupSlices())");
            sb.AppendLine(indentLevel, "{");

            for (int i = 0; i < info.ComponentParameters.Count; i++)
            {
                var param = info.ComponentParameters[i];
                var typeStr =
                    param.TypeSymbol != null
                        ? PerformanceCache.GetDisplayString(param.TypeSymbol)
                        : param.Type;
                var bufferSuffix = param.IsRef ? "Write" : "Read";
                sb.AppendLine(
                    indentLevel + 1,
                    $"var values{i + 1} = {worldName}.ComponentBuffer<{typeStr}>(__slice.GroupIndex).{bufferSuffix};"
                );
            }
            sb.AppendLine();

            sb.AppendLine(indentLevel + 1, "for (int __i = 0; __i < __slice.Count; __i++)");
            sb.AppendLine(indentLevel + 1, "{");

            for (int i = 0; i < info.ComponentParameters.Count; i++)
            {
                var param = info.ComponentParameters[i];
                var refKind = param.IsRef ? "ref" : "ref readonly";
                sb.AppendLine(
                    indentLevel + 2,
                    $"{refKind} var value{i + 1} = ref values{i + 1}[__i];"
                );
            }

            EntityRefEmitter.EmitDeclarations(
                sb,
                info,
                indentLevel + 2,
                indexExpr: "new EntityIndex(__i, __slice.GroupIndex)",
                worldVar: worldName
            );

            EmitUserBodyOrCall(sb, indentLevel + 2, methodName, info, worldName);

            sb.AppendLine(indentLevel + 1, "}");
            sb.AppendLine(indentLevel, "}");
        }

        private static void EmitSparseIterationBody(
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

            NamespaceCollector.EmitSetAccessorDeclarations(sb, info, indentLevel, worldName);
            HoistedSingleEmitter.Emit(sb, indentLevel, worldName, info.HoistedSingletons);

            sb.AppendLine(indentLevel, $"foreach (var __slice in {builderVar}.GroupSlices())");
            sb.AppendLine(indentLevel, "{");

            for (int i = 0; i < info.ComponentParameters.Count; i++)
            {
                var param = info.ComponentParameters[i];
                var typeStr =
                    param.TypeSymbol != null
                        ? PerformanceCache.GetDisplayString(param.TypeSymbol)
                        : param.Type;
                var bufferSuffix = param.IsRef ? "Write" : "Read";
                sb.AppendLine(
                    indentLevel + 1,
                    $"var values{i + 1} = {worldName}.ComponentBuffer<{typeStr}>(__slice.GroupIndex).{bufferSuffix};"
                );
            }
            sb.AppendLine();

            sb.AppendLine(indentLevel + 1, "foreach (var __i in __slice.Indices)");
            sb.AppendLine(indentLevel + 1, "{");

            for (int i = 0; i < info.ComponentParameters.Count; i++)
            {
                var param = info.ComponentParameters[i];
                var refKind = param.IsRef ? "ref" : "ref readonly";
                sb.AppendLine(
                    indentLevel + 2,
                    $"{refKind} var value{i + 1} = ref values{i + 1}[__i];"
                );
            }

            EntityRefEmitter.EmitDeclarations(
                sb,
                info,
                indentLevel + 2,
                indexExpr: "new EntityIndex(__i, __slice.GroupIndex)",
                worldVar: worldName
            );

            EmitUserBodyOrCall(sb, indentLevel + 2, methodName, info, worldName);

            sb.AppendLine(indentLevel + 1, "}");
            sb.AppendLine(indentLevel, "}");
        }

        private static void EmitUserBodyOrCall(
            OptimizedStringBuilder sb,
            int indentLevel,
            string methodName,
            ValidatedMethodInfo info,
            string worldName
        )
        {
            var callArgs = BuildUserMethodCallArgs(info, worldName, "__entityIndex", "value");
            sb.AppendLine(indentLevel, $"{methodName}({callArgs});");
        }

        private static void GenerateRangeIteration(
            OptimizedStringBuilder sb,
            string methodName,
            ValidatedMethodInfo validatedParamsInfo,
            int indentLevel
        )
        {
            NamespaceCollector.EmitSetAccessorDeclarations(
                sb,
                validatedParamsInfo,
                indentLevel,
                "__world"
            );
            HoistedSingleEmitter.Emit(
                sb,
                indentLevel,
                worldVar: "__world",
                validatedParamsInfo.HoistedSingletons
            );

            var componentParams = validatedParamsInfo.ComponentParameters;

            for (int idx = 0; idx < componentParams.Count; idx++)
            {
                var param = componentParams[idx];
                var typeStr =
                    param.TypeSymbol != null
                        ? PerformanceCache.GetDisplayString(param.TypeSymbol)
                        : param.Type;
                var bufferSuffix = param.IsRef ? "Write" : "Read";
                sb.AppendLine(
                    indentLevel,
                    $"var values{idx + 1} = __world.ComponentBuffer<{typeStr}>(__group).{bufferSuffix};"
                );
            }

            sb.AppendLine();

            sb.AppendLine(
                indentLevel,
                "for (int __i = __indices.Start; __i < __indices.End; __i++)"
            );
            sb.AppendLine(indentLevel, "{");

            for (int i = 0; i < validatedParamsInfo.ComponentParameters.Count; i++)
            {
                var param = validatedParamsInfo.ComponentParameters[i];
                var refKind = param.IsRef ? "ref" : "ref readonly";
                sb.AppendLine(
                    indentLevel + 1,
                    $"{refKind} var value{i + 1} = ref values{i + 1}[__i];"
                );
            }

            EntityRefEmitter.EmitDeclarations(
                sb,
                validatedParamsInfo,
                indentLevel + 1,
                indexExpr: "new EntityIndex(__i, __group)",
                worldVar: "__world"
            );

            EmitUserBodyOrCall(sb, indentLevel + 1, methodName, validatedParamsInfo, "__world");
            sb.AppendLine(indentLevel, "}");
        }

        // Validation and parameter info classes (reused from original ForEachGenerator)
        private static bool ValidateMethod(
            ClassDeclarationSyntax classDec,
            MethodDeclarationSyntax methodDec,
            IMethodSymbol methodSymbol,
            System.Action<Diagnostic> reportDiagnostic,
            SemanticModel semanticModel,
            out ValidatedMethodInfo? validatedParamsInfo
        )
        {
            validatedParamsInfo = null;
            bool isValid = true;

            // Check if the class is partial
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

            // Check return type
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

            // Validate parameters
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
                aspectParam: null,
                isValid: ref isValid
            );
            bool hasAnyIterationParameter =
                classified.ComponentParameters.Count > 0
                || classified.HasEntityIndex
                || classified.HasEntityHandle
                || classified.HasEntityAccessor;

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
            var criteria = IterationCriteriaParser.ParseIterationAttribute(
                reportDiagnostic,
                methodDec,
                methodSymbol,
                className,
                TrecsAttributeNames.ForEachEntity
            );
            if (criteria == null)
            {
                isValid = false;
            }
            var attributeTagTypes = criteria?.TagTypes ?? new List<ITypeSymbol>();
            var attributeWithoutTagTypes = criteria?.WithoutTagTypes ?? new List<ITypeSymbol>();
            var setTypes = criteria?.SetTypes ?? new List<ITypeSymbol>();
            bool attributeMatchByComponents = criteria?.MatchByComponents ?? false;

            if (isValid)
            {
                validatedParamsInfo = new ValidatedMethodInfo
                {
                    ComponentParameters = classified.ComponentParameters.ToList(),
                    CustomParameters = classified.CustomParameters.ToList(),
                    HasEntityIndexParameter = classified.HasEntityIndex,
                    HasEntityHandleParameter = classified.HasEntityHandle,
                    HasEntityAccessorParameter = classified.HasEntityAccessor,
                    ParameterSlots = classified.ParameterSlots.ToList(),
                    AttributeTagTypes = attributeTagTypes,
                    AttributeWithoutTagTypes = attributeWithoutTagTypes,
                    SetTypes = setTypes,
                    MatchByComponents = attributeMatchByComponents,
                    SetAccessorParameters = classified.SetAccessorParameters.ToList(),
                    SetReadParameters = classified.SetReadParameters.ToList(),
                    SetWriteParameters = classified.SetWriteParameters.ToList(),
                    HoistedSingletons = classified.HoistedSingletons.ToList(),
                };
            }

            return isValid;
        }
    }

    /// <summary>
    /// Data structure for ForEach method information used in incremental generation.
    /// Carries the validation result + captured diagnostics forward from the transform
    /// phase so that the terminal stage doesn't need access to a <see cref="Compilation"/>
    /// or <see cref="SemanticModel"/>.
    /// </summary>
    internal class ForEachMethodData
    {
        public ClassDeclarationSyntax ClassDecl { get; }
        public MethodDeclarationSyntax MethodDecl { get; }
        public IMethodSymbol MethodSymbol { get; }
        public bool IsValid { get; }
        public ValidatedMethodInfo? ValidatedInfo { get; }
        public ImmutableArray<Diagnostic> Diagnostics { get; }

        public ForEachMethodData(
            ClassDeclarationSyntax classDecl,
            MethodDeclarationSyntax methodDecl,
            IMethodSymbol methodSymbol,
            bool isValid,
            ValidatedMethodInfo? validatedInfo,
            ImmutableArray<Diagnostic> diagnostics
        )
        {
            ClassDecl = classDecl;
            MethodDecl = methodDecl;
            MethodSymbol = methodSymbol;
            IsValid = isValid;
            ValidatedInfo = validatedInfo;
            Diagnostics = diagnostics;
        }
    }
}
