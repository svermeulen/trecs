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
    /// Incremental source generator for ForEach methods that iterate over ECS components.
    /// Provides much better compilation performance than the legacy ForEachGenerator.
    /// </summary>
    [Generator]
    public class IncrementalForEachGenerator : IIncrementalGenerator
    {
        public void Initialize(IncrementalGeneratorInitializationContext context)
        {
            // Check if compilation references Trecs assembly for better performance
            var hasTrecsReference = AssemblyFilterHelper.CreateTrecsReferenceCheck(context);

            // Create provider for methods with GenerateForEachComponentsMethods attribute
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

            // Combine with compilation provider
            var forEachMethodWithCompilation = forEachMethodProvider.Combine(
                context.CompilationProvider
            );

            // Register source output
            context.RegisterSourceOutput(
                forEachMethodWithCompilation,
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
                        IterationCriteriaParser.ExtractAttributeName(attr.Name.ToString()) == TrecsAttributeNames.EntityFilter
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
            // (ForEachAspectGenerator) takes it.
            if (!IterationAttributeRouting.HasEntityFilter(methodSymbol))
                return null;
            if (IterationAttributeRouting.HasWrapAsJobAttribute(methodSymbol))
                return null;
            if (!IterationAttributeRouting.RoutesToComponentsGenerator(methodSymbol))
                return null;

            return new ForEachMethodData(classDecl, methodDecl, methodSymbol);
        }

        private static void GenerateForEachSource(
            SourceProductionContext context,
            ForEachMethodData data,
            Compilation compilation
        )
        {
            var location = data.MethodDecl.GetLocation();
            var className = data.ClassDecl.Identifier.Text;
            var methodName = data.MethodDecl.Identifier.Text;
            var fileName = SymbolAnalyzer.GetSafeFileName(
                data.MethodSymbol.ContainingType,
                $"{methodName}_ForEach"
            );

            try
            {
                using var _timer_ = SourceGenTimer.Time("ForEachGenerator.Total");
                SourceGenLogger.Log(
                    $"[IncrementalForEachGenerator] Processing {className}.{methodName}"
                );

                // Validate the method and extract info in a single pass
                ValidatedMethodInfo? validatedParamsInfo = null;
                var isValid =
                    ErrorRecovery.TryExecuteBool(
                        () =>
                            ValidateMethod(
                                data.ClassDecl,
                                data.MethodDecl,
                                data.MethodSymbol,
                                context,
                                compilation,
                                out validatedParamsInfo
                            ),
                        context,
                        location,
                        "ForEach method validation"
                    ) ?? false;

                if (!isValid || validatedParamsInfo == null)
                {
                    return;
                }

                // Generate the source code
                var source = ErrorRecovery.TryExecute(
                    () =>
                        GenerateSource(
                            context,
                            data.ClassDecl,
                            data.MethodDecl,
                            validatedParamsInfo,
                            compilation
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
                    // Generation failed - report error
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
                // Report error for any unhandled exceptions
                ErrorRecovery.ReportError(
                    context,
                    location,
                    $"ForEach {className}.{methodName}",
                    ex
                );
            }
        }

        private static string GenerateSource(
            SourceProductionContext context,
            ClassDeclarationSyntax classDec,
            MethodDeclarationSyntax methodDec,
            ValidatedMethodInfo validatedParamsInfo,
            Compilation compilation
        )
        {
            var namespaceName = SymbolAnalyzer.GetNamespace(classDec);
            var className = classDec.Identifier.Text;
            var methodName = methodDec.Identifier.Text;

            var componentCount = validatedParamsInfo.ComponentParameters.Count;
            var sb = OptimizedStringBuilder.ForAspect(componentCount);

            // Add required namespaces
            var requiredNamespaces = NamespaceCollector.Collect(
                compilation,
                validatedParamsInfo
            );

            sb.AppendUsings(requiredNamespaces.ToArray());

            return sb.WrapInNamespace(
                    namespaceName,
                    (builder) =>
                    {
                        // Generate class declaration manually like legacy generator
                        builder.AppendLine(1, $"partial class {className}");
                        builder.AppendLine(1, "{");
                        GenerateForEachOverloads(
                            builder,
                            methodName,
                            validatedParamsInfo,
                            methodDec
                        );
                        builder.AppendLine(1, "}");
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
            // their callback args + custom event args). Mirrors [ForEachAspect].
            sb.AppendLine(
                2,
                $"void {methodName}(Group __group, EntityRange __indices, WorldAccessor __world{customArgsDecStr})"
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
                var attributeChain = QueryBuilderHelper.BuildAttributeCriteriaChain(info.AttributeTagTypes, info.MatchByComponents, info.ComponentParameters.Select(p => p.TypeSymbol));
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

            var chain = QueryBuilderHelper.BuildAttributeCriteriaChain(info.AttributeTagTypes, info.MatchByComponents, info.ComponentParameters.Select(p => p.TypeSymbol));
            if (chain.Length > 0)
                sb.AppendLine(3, $"__builder = __builder{chain};");

            sb.AppendLine(
                3,
                $"Trecs.Internal.Assert.That(__builder.HasAnyCriteria, \"{methodName} requires query criteria — pass a builder with at least one tag, component, or set constraint, or specify Tag/Set/MatchByComponents on the [ForEachEntity] attribute.\");"
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

            var chain = QueryBuilderHelper.BuildAttributeCriteriaChain(info.AttributeTagTypes, info.MatchByComponents, info.ComponentParameters.Select(p => p.TypeSymbol));
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
                    $"var values{i + 1} = {worldName}.ComponentBuffer<{typeStr}>(__slice.Group).{bufferSuffix};"
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

            if (info.HasEntityIndexParameter)
                sb.AppendLine(
                    indentLevel + 2,
                    "var __entityIndex = new EntityIndex(__i, __slice.Group);"
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
                    $"var values{i + 1} = {worldName}.ComponentBuffer<{typeStr}>(__slice.Group).{bufferSuffix};"
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

            if (info.HasEntityIndexParameter)
                sb.AppendLine(
                    indentLevel + 2,
                    "var __entityIndex = new EntityIndex(__i, __slice.Group);"
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
            NamespaceCollector.EmitSetAccessorDeclarations(sb, validatedParamsInfo, indentLevel, "__world");

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

            if (validatedParamsInfo.HasEntityIndexParameter)
            {
                sb.AppendLine(
                    indentLevel + 1,
                    "var __entityIndex = new EntityIndex(__i, __group);"
                );
            }

            EmitUserBodyOrCall(sb, indentLevel + 1, methodName, validatedParamsInfo, "__world");
            sb.AppendLine(indentLevel, "}");
        }

        // Validation and parameter info classes (reused from original ForEachGenerator)
        private static bool ValidateMethod(
            ClassDeclarationSyntax classDec,
            MethodDeclarationSyntax methodDec,
            IMethodSymbol methodSymbol,
            SourceProductionContext context,
            Compilation compilation,
            out ValidatedMethodInfo? validatedParamsInfo
        )
        {
            validatedParamsInfo = null;
            bool isValid = true;

            // Check if the class is partial
            if (!classDec.Modifiers.Any(m => m.IsKind(SyntaxKind.PartialKeyword)))
            {
                context.ReportDiagnostic(
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
                context.ReportDiagnostic(
                    Diagnostic.Create(
                        DiagnosticDescriptors.InvalidReturnType,
                        methodDec.ReturnType.GetLocation()
                    )
                );
                isValid = false;
            }

            // Validate parameters
            var semanticModel = compilation.GetSemanticModel(methodDec.SyntaxTree);
            var parameters = methodDec.ParameterList.Parameters;

            if (parameters.Count == 0)
            {
                context.ReportDiagnostic(
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
                context,
                methodName: null,
                supportsEntityIndex: true,
                aspectParam: null,
                isValid: ref isValid);
            bool hasAnyIterationParameter =
                classified.ComponentParameters.Count > 0
                || classified.HasEntityIndex;

            if (!hasAnyIterationParameter)
            {
                context.ReportDiagnostic(
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
                context, methodDec, methodSymbol, className, TrecsAttributeNames.EntityFilter);
            if (criteria == null)
            {
                isValid = false;
            }
            var attributeTagTypes = criteria?.TagTypes ?? new List<ITypeSymbol>();
            var setTypes = criteria?.SetTypes ?? new List<ITypeSymbol>();
            bool attributeMatchByComponents = criteria?.MatchByComponents ?? false;

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

    /// <summary>
    /// Data structure for ForEach method information used in incremental generation
    /// </summary>
    internal class ForEachMethodData
    {
        public ClassDeclarationSyntax ClassDecl { get; }
        public MethodDeclarationSyntax MethodDecl { get; }
        public IMethodSymbol MethodSymbol { get; }

        public ForEachMethodData(
            ClassDeclarationSyntax classDecl,
            MethodDeclarationSyntax methodDecl,
            IMethodSymbol methodSymbol
        )
        {
            ClassDecl = classDecl;
            MethodDecl = methodDecl;
            MethodSymbol = methodSymbol;
        }
    }

}
