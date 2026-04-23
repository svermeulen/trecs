#nullable enable

using System;
using System.Collections.Generic;
using System.Collections.Immutable;
using System.Linq;
using Microsoft.CodeAnalysis;
using Microsoft.CodeAnalysis.CSharp;
using Microsoft.CodeAnalysis.CSharp.Syntax;
using Trecs.SourceGen.Aspect;
using Trecs.SourceGen.Internal;
using Trecs.SourceGen.Performance;
using Trecs.SourceGen.Shared;

namespace Trecs.SourceGen
{
    /// <summary>
    /// Source generator for ForSingleAspect methods that query a single matching entity using an Aspect.
    /// Uses the same semantics as Aspect.Single() — asserts exactly one entity via Assert.That.
    /// </summary>
    [Generator]
    public class ForSingleAspectGenerator : IIncrementalGenerator
    {
        public void Initialize(IncrementalGeneratorInitializationContext context)
        {
            var hasTrecsReference = AssemblyFilterHelper.CreateTrecsReferenceCheck(context);

            var methodProviderRaw = context
                .SyntaxProvider.CreateSyntaxProvider(
                    predicate: static (s, _) => IsMethodWithForSingleAspectAttribute(s),
                    transform: static (ctx, _) => GetMethodData(ctx)
                )
                .Where(static m => m is not null);
            var methodProvider = AssemblyFilterHelper.FilterByTrecsReference(
                methodProviderRaw,
                hasTrecsReference
            );

            // See IncrementalForEachGenerator for the caching rationale: validation runs
            // in the transform, and the terminal stage only needs the global-namespace
            // display string (not the full Compilation).
            var globalNsProvider = context.CompilationProvider.Select(
                static (c, _) =>
                    PerformanceCache.GetDisplayString(c.GlobalNamespace) ?? string.Empty
            );

            var methodWithGlobalNs = methodProvider.Combine(globalNsProvider);

            context.RegisterSourceOutput(
                methodWithGlobalNs,
                static (spc, source) =>
                    GenerateForSingleAspectSource(spc, source.Left!, source.Right)
            );
        }

        private static bool IsMethodWithForSingleAspectAttribute(SyntaxNode node)
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

        private static ForEachAspectData? GetMethodData(GeneratorSyntaxContext context)
        {
            var methodDecl = (MethodDeclarationSyntax)context.Node;

            var classDecl = methodDecl.FirstAncestorOrSelf<ClassDeclarationSyntax>();
            if (classDecl == null)
                return null;

            var methodSymbol = context.SemanticModel.GetDeclaredSymbol(methodDecl) as IMethodSymbol;
            if (methodSymbol == null)
                return null;

            // [SingleEntity] only routes to this generator when the method has at
            // least one IAspect parameter — otherwise the components-side generator
            // (ForSingleEntityGenerator) takes it.
            if (!IterationAttributeRouting.HasSingleEntity(methodSymbol))
                return null;
            if (!IterationAttributeRouting.RoutesToAspectGenerator(methodSymbol))
                return null;

            // Validate in the transform so RegisterSourceOutput doesn't need a
            // Compilation. Diagnostics accumulated here are replayed downstream.
            // Unexpected exceptions surface as a SourceGenerationError diagnostic rather
            // than a generator crash.
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
                        "ForSingleAspect method validation",
                        ex.Message
                    )
                );
                isValid = false;
                validated = null;
            }

            return new ForEachAspectData(
                classDecl,
                methodDecl,
                methodSymbol,
                isValid,
                validated,
                diagnostics.ToImmutableArray()
            );
        }

        private static void GenerateForSingleAspectSource(
            SourceProductionContext context,
            ForEachAspectData data,
            string globalNamespaceName
        )
        {
            var location = data.MethodDecl.GetLocation();
            var className = data.ClassDecl.Identifier.Text;
            var methodName = data.MethodDecl.Identifier.Text;
            var fileName = SymbolAnalyzer.GetSafeFileName(
                data.MethodSymbol.ContainingType,
                $"{methodName}_ForSingleAspect"
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
                using var _ = SourceGenTimer.Time("ForSingleAspectGenerator.Total");
                SourceGenLogger.Log(
                    $"[ForSingleAspectGenerator] Processing {className}.{methodName}"
                );

                var source = ErrorRecovery.TryExecute(
                    () =>
                        GenerateSource(
                            data.ClassDecl,
                            data.MethodDecl,
                            data.ValidatedInfo,
                            globalNamespaceName
                        ),
                    context,
                    location,
                    "ForSingleAspect code generation"
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
                    $"ForSingleAspect {className}.{methodName}",
                    ex
                );
            }
        }

        private static string GenerateSource(
            ClassDeclarationSyntax classDec,
            MethodDeclarationSyntax methodDec,
            ValidatedMethodInfo validatedMethodInfo,
            string globalNamespaceName
        )
        {
            var customArgsDecStr = "";
            var customArgsCallStr = "";

            if (validatedMethodInfo.CustomParameters.Any())
            {
                customArgsDecStr +=
                    ", "
                    + string.Join(
                        ", ",
                        validatedMethodInfo.CustomParameters.Select(p =>
                            $"{(p.IsRef ? "ref " : p.IsIn ? "in " : "")}{(p.TypeSymbol != null ? PerformanceCache.GetDisplayString(p.TypeSymbol) : p.Type)} {p.Name}"
                        )
                    );
                customArgsCallStr +=
                    ", "
                    + string.Join(
                        ", ",
                        validatedMethodInfo.CustomParameters.Select(p =>
                            $"{(p.IsRef ? "ref " : p.IsIn ? "in " : "")}{p.Name}"
                        )
                    );
            }

            var namespaceName = SymbolAnalyzer.GetNamespace(classDec);
            var className = classDec.Identifier.Text;
            var methodName = methodDec.Identifier.Text;
            var aspectTypeName = validatedMethodInfo.AspectTypeName;

            var visibility = SymbolAnalyzer.GetMethodVisibility(methodDec);

            var componentCount = validatedMethodInfo.ComponentTypes.Count;
            var sb = OptimizedStringBuilder.ForAspect(componentCount);

            var requiredNamespaces = NamespaceCollector.Collect(
                globalNamespaceName,
                validatedMethodInfo,
                includeSystemNamespace: true
            );

            sb.AppendUsings(requiredNamespaces.ToArray());

            return sb.WrapInNamespace(
                    namespaceName,
                    (builder) =>
                    {
                        builder.WrapInType(
                            "public",
                            "class",
                            className,
                            (classBuilder) =>
                            {
                                GenerateOverloads(
                                    classBuilder,
                                    methodName,
                                    aspectTypeName,
                                    validatedMethodInfo,
                                    customArgsDecStr,
                                    customArgsCallStr,
                                    visibility
                                );
                            },
                            1
                        );
                    }
                )
                .ToString();
        }

        private static void GenerateOverloads(
            OptimizedStringBuilder sb,
            string methodName,
            string aspectTypeName,
            ValidatedMethodInfo info,
            string customArgsDecStr,
            string customArgsCallStr,
            string visibility
        )
        {
            bool hasSets = info.HasSet;
            bool hasAnyAttributeCriteria =
                hasSets || info.HasAttributeTags || info.MatchByComponents;

            // (1) Convenience overload — only when the attribute supplies at least one criterion.
            if (hasAnyAttributeCriteria)
            {
                GenerateConvenienceOverload(
                    sb,
                    methodName,
                    aspectTypeName,
                    info,
                    customArgsDecStr,
                    customArgsCallStr,
                    visibility
                );
            }

            // (2) Public dense entry — only when the attribute imposes no Set/Sets.
            if (!hasSets)
            {
                GenerateQueryBuilderEntry(
                    sb,
                    methodName,
                    aspectTypeName,
                    info,
                    customArgsDecStr,
                    visibility
                );
            }

            // (3) Public sparse entry — only when the attribute has no set baked in.
            if (!hasSets)
            {
                GenerateSparseQueryBuilderEntry(
                    sb,
                    methodName,
                    aspectTypeName,
                    info,
                    customArgsDecStr,
                    visibility
                );
            }
        }

        private static void GenerateConvenienceOverload(
            OptimizedStringBuilder sb,
            string methodName,
            string aspectTypeName,
            ValidatedMethodInfo info,
            string customArgsDecStr,
            string customArgsCallStr,
            string visibility
        )
        {
            sb.AppendLine(
                2,
                $"{visibility} void {methodName}(WorldAccessor __world{customArgsDecStr})"
            );
            sb.AppendLine(2, "{");

            if (info.HasSet)
            {
                // Sparse path. Build the full SparseQueryBuilder inline.
                var firstSetName = PerformanceCache.GetDisplayString(info.SetTypes[0]);
                var attributeChain = QueryBuilderHelper.BuildAttributeCriteriaChain(
                    info.AttributeTagTypes,
                    info.MatchByComponents,
                    info.ComponentTypes
                );
                sb.AppendLine(
                    3,
                    $"var __builder = __world.Query(){attributeChain}.InSet<{firstSetName}>();"
                );
                EmitSingleEntityBody(
                    sb,
                    methodName,
                    aspectTypeName,
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
            string aspectTypeName,
            ValidatedMethodInfo info,
            string customArgsDecStr,
            string visibility
        )
        {
            sb.AppendLine(
                2,
                $"{visibility} void {methodName}(QueryBuilder __builder{customArgsDecStr})"
            );
            sb.AppendLine(2, "{");

            var chain = QueryBuilderHelper.BuildAttributeCriteriaChain(
                info.AttributeTagTypes,
                info.MatchByComponents,
                info.ComponentTypes
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
                aspectTypeName,
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
            string aspectTypeName,
            ValidatedMethodInfo info,
            string customArgsDecStr,
            string visibility
        )
        {
            sb.AppendLine(
                2,
                $"{visibility} void {methodName}(SparseQueryBuilder __builder{customArgsDecStr})"
            );
            sb.AppendLine(2, "{");

            var chain = QueryBuilderHelper.BuildAttributeCriteriaChain(
                info.AttributeTagTypes,
                info.MatchByComponents,
                info.ComponentTypes
            );
            if (chain.Length > 0)
                sb.AppendLine(3, $"__builder = __builder{chain};");

            EmitSingleEntityBody(
                sb,
                methodName,
                aspectTypeName,
                info,
                indentLevel: 3,
                builderVar: "__builder",
                worldVar: null
            );

            sb.AppendLine(2, "}");
            sb.AppendLine();
        }

        /// <summary>
        /// Emits the inline body that resolves a single matching entity via
        /// <c>builder.SingleEntityIndex()</c> (which asserts exactly one match), fetches the
        /// component buffers for that entity's group, constructs the aspect view, and calls
        /// the user method.
        /// </summary>
        private static void EmitSingleEntityBody(
            OptimizedStringBuilder sb,
            string methodName,
            string aspectTypeName,
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

            var componentVarNames = new List<string>();
            foreach (var type in info.ComponentTypes)
            {
                var varName = ComponentTypeHelper.GetComponentVariableName(type);
                componentVarNames.Add(varName);
                var typeDisplay = PerformanceCache.GetDisplayString(type);
                var bufferSuffix = info.IsReadOnly(type) ? "Read" : "Write";
                sb.AppendLine(
                    indentLevel,
                    $"var {varName} = {worldName}.ComponentBuffer<{typeDisplay}>(__ei.Group).{bufferSuffix};"
                );
            }

            var constructorArgs = string.Join(", ", componentVarNames);
            sb.AppendLine(
                indentLevel,
                $"var __view = new {aspectTypeName}(__ei, {constructorArgs});"
            );

            // Build the call args in user declared parameter order. ForSingleAspect
            // does not support a loop EntityIndex (the index is on the view), so the
            // entityIndexVar is unused; pass an empty string to keep the helper signature.
            var slotArgs = new List<string>(info.ParameterSlots.Count);
            foreach (var slot in info.ParameterSlots)
            {
                switch (slot.Kind)
                {
                    case ParamSlotKind.LoopAspect:
                        slotArgs.Add("in __view");
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

        // GetComponentVariableName consolidated into ComponentTypeHelper.GetComponentVariableName

        private static bool ValidateMethod(
            ClassDeclarationSyntax classDec,
            MethodDeclarationSyntax methodDec,
            IMethodSymbol methodSymbol,
            System.Action<Diagnostic> reportDiagnostic,
            SemanticModel semanticModel,
            out ValidatedMethodInfo? validatedMethodInfo
        )
        {
            // Reuse same validation as ForEachAspect
            validatedMethodInfo = null;
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

            // Locate the (single) aspect parameter — anywhere in the parameter list,
            // marked 'in', and implementing IAspect (without [PassThroughArgument]).
            ParameterSyntax? aspectParam = null;
            ITypeSymbol? aspectParamType = null;
            for (int pi = 0; pi < parameters.Count; pi++)
            {
                var p = parameters[pi];
                var pType = p.Type != null ? semanticModel.GetTypeInfo(p.Type).Type : null;
                if (pType == null)
                    continue;

                var pSymbol = semanticModel.GetDeclaredSymbol(p);
                bool pIsPassThrough =
                    pSymbol != null
                    && PerformanceCache.HasAttributeByName(
                        pSymbol,
                        TrecsAttributeNames.PassThroughArgument,
                        TrecsNamespaces.Trecs
                    );
                if (pIsPassThrough)
                    continue;

                if (!SymbolAnalyzer.ImplementsInterface(pType, "IAspect", TrecsNamespaces.Trecs))
                    continue;

                if (aspectParam != null)
                {
                    reportDiagnostic(
                        Diagnostic.Create(
                            DiagnosticDescriptors.DuplicateLoopParameter,
                            p.GetLocation(),
                            p.Identifier.Text,
                            "aspect"
                        )
                    );
                    return false;
                }

                aspectParam = p;
                aspectParamType = pType;
            }

            if (aspectParam == null || aspectParamType == null)
            {
                reportDiagnostic(
                    Diagnostic.Create(
                        DiagnosticDescriptors.InvalidParameterList,
                        methodDec.ParameterList.GetLocation(),
                        "[SingleEntity] method on a system class with an aspect parameter must take exactly one aspect parameter (implementing IAspect, declared 'in')"
                    )
                );
                return false;
            }

            if (aspectParam.Modifiers.Any(m => m.IsKind(SyntaxKind.RefKeyword)))
            {
                reportDiagnostic(
                    Diagnostic.Create(
                        DiagnosticDescriptors.AspectParamMustBeIn,
                        aspectParam.GetLocation(),
                        aspectParam.Identifier.Text
                    )
                );
                return false;
            }

            if (!aspectParam.Modifiers.Any(m => m.IsKind(SyntaxKind.InKeyword)))
            {
                reportDiagnostic(
                    Diagnostic.Create(
                        DiagnosticDescriptors.InvalidParameterModifiers,
                        aspectParam.GetLocation(),
                        aspectParam.Identifier.Text
                    )
                );
                return false;
            }

            var attributeData = AspectAttributeParser.ParseAspectData(
                (INamedTypeSymbol)aspectParamType
            );

            var componentTypes = new List<ITypeSymbol>();
            componentTypes.AddRange(attributeData.ReadTypes);
            componentTypes.AddRange(attributeData.WriteTypes);
            var distinctComponentTypes = PerformanceCache.GetDistinctTypes(componentTypes);
            componentTypes = distinctComponentTypes.ToList();

            var readComponentTypes = attributeData.ReadTypes.ToList();
            var writeComponentTypes = attributeData.WriteTypes.ToList();

            // Extract tag types, set types, and MatchByComponents from the iteration attribute.
            var criteria = IterationCriteriaParser.ParseIterationAttribute(
                reportDiagnostic,
                methodDec,
                methodSymbol,
                classDec.Identifier.Text,
                TrecsAttributeNames.SingleEntity
            );
            if (criteria == null)
            {
                isValid = false;
            }
            var attributeTagTypes = criteria?.TagTypes ?? new List<ITypeSymbol>();
            var setTypes = criteria?.SetTypes ?? new List<ITypeSymbol>();
            bool attributeMatchByComponents = criteria?.MatchByComponents ?? false;

            // Walk all parameters in declaration order via shared classifier.
            // ForSingleAspect does NOT support a loop EntityIndex (the matched entity
            // index is exposed through the aspect view itself).
            var classified = ParameterClassifier.Classify(
                parameters,
                semanticModel,
                IterationMode.Aspect,
                reportDiagnostic,
                methodDec.Identifier.Text,
                supportsEntityIndex: false,
                aspectParam: aspectParam,
                isValid: ref isValid
            );
            var customParameters = classified.CustomParameters;
            var setAccessorParameters = classified.SetAccessorParameters;
            var setReadParameters = classified.SetReadParameters;
            var setWriteParameters = classified.SetWriteParameters;
            var paramSlots = classified.ParameterSlots;

            if (componentTypes.Count == 0)
            {
                reportDiagnostic(
                    Diagnostic.Create(
                        DiagnosticDescriptors.AspectNoComponents,
                        aspectParam.GetLocation(),
                        aspectParam.Type?.ToString() ?? "UnknownType"
                    )
                );
                isValid = false;
            }

            var effectiveSetTypes = new List<ITypeSymbol>(setTypes);

            if (isValid)
            {
                validatedMethodInfo = new ValidatedMethodInfo
                {
                    AspectTypeName = aspectParam.Type?.ToString() ?? "UnknownType",
                    AspectTypeSymbol = aspectParamType as INamedTypeSymbol,
                    ComponentTypes = componentTypes,
                    ReadComponentTypes = readComponentTypes,
                    WriteComponentTypes = writeComponentTypes,
                    CustomParameters = customParameters,
                    ParameterSlots = paramSlots,
                    AttributeTagTypes = attributeTagTypes,
                    SetTypes = effectiveSetTypes,
                    MatchByComponents = attributeMatchByComponents,
                    SetAccessorParameters = setAccessorParameters,
                    SetReadParameters = setReadParameters,
                    SetWriteParameters = setWriteParameters,
                };
            }

            return isValid;
        }
    }
}
