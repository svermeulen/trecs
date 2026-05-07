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
    /// Source generator that generates efficient iteration methods for aspects
    /// marked with the ForEachAspectAttribute. Uses incremental generation for better performance.
    /// </summary>
    [Generator]
    public class ForEachAspectGenerator : IIncrementalGenerator
    {
        public void Initialize(IncrementalGeneratorInitializationContext context)
        {
            // Validation runs in the transform, so the terminal stage doesn't need a full
            // Compilation — only the global-namespace display string. See
            // ForEachGenerator for the caching rationale.
            var forEachMethodProvider = context
                .SyntaxProvider.CreateSyntaxProvider(
                    predicate: static (s, _) => IsMethodWithForEachAspectAttribute(s),
                    transform: static (ctx, _) => GetForEachAspectData(ctx)
                )
                .Where(static m => m is not null);

            var globalNsProvider = context.CompilationProvider.Select(
                static (c, _) =>
                    PerformanceCache.GetDisplayString(c.GlobalNamespace) ?? string.Empty
            );

            var forEachWithGlobalNs = forEachMethodProvider.Combine(globalNsProvider);

            context.RegisterSourceOutput(
                forEachWithGlobalNs,
                static (spc, source) => GenerateForEachAspectSource(spc, source.Left!, source.Right)
            );
        }

        private static bool IsMethodWithForEachAspectAttribute(SyntaxNode node)
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

        private static ForEachAspectData? GetForEachAspectData(GeneratorSyntaxContext context)
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

            // [ForEachEntity] only routes to this generator when the method has at
            // least one IAspect parameter — otherwise the components-side generator
            // (ForEachGenerator) takes it.
            if (!IterationAttributeRouting.HasEntityFilter(methodSymbol))
                return null;
            if (IterationAttributeRouting.HasWrapAsJobAttribute(methodSymbol))
                return null;
            if (!IterationAttributeRouting.RoutesToAspectGenerator(methodSymbol))
                return null;

            // Validation runs here so the terminal stage doesn't need a Compilation /
            // SemanticModel; captured diagnostics round-trip through value-equality
            // pipeline state and are replayed downstream. Unexpected exceptions surface
            // as a SourceGenerationError diagnostic rather than a generator crash.
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
                        "ForEachAspect method validation",
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

        private static void GenerateForEachAspectSource(
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
                $"{methodName}_ForEachAspect"
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
                using var _ = SourceGenTimer.Time("ForEachAspectGenerator.Total");
                SourceGenLogger.Log(
                    $"[ForEachAspectGenerator] Processing {className}.{methodName}"
                );

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
                    "ForEachAspect code generation"
                );

                if (source != null)
                {
                    context.AddSource(fileName, source);
                    SourceGenLogger.WriteGeneratedFile(fileName, source);
                }
                else
                {
                    // Generation failed - report error but don't generate fallback
                    // (partial methods don't need fallback)
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
                    $"ForEachAspect {className}.{methodName}",
                    ex
                );
            }
        }

        private static string GenerateSource(
            ClassDeclarationSyntax classDec,
            MethodDeclarationSyntax methodDec,
            INamedTypeSymbol classSymbol,
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

            // Extract visibility modifier from the original method
            var visibility = SymbolAnalyzer.GetMethodVisibility(methodDec);

            var componentCount = validatedMethodInfo.ComponentTypes.Count;
            var sb = OptimizedStringBuilder.ForAspect(componentCount);

            // Add required namespaces
            var requiredNamespaces = NamespaceCollector.Collect(
                globalNamespaceName,
                validatedMethodInfo
            );

            sb.AppendUsings(requiredNamespaces.ToArray());

            // Walk the system class's containing-type chain so the emitted partial merges
            // with a nested system class. Without these wrappers, `partial class InnerSystem`
            // would land at namespace scope rather than under `partial class Outer`, and the
            // emitted overloads would shadow (rather than augment) the user's method.
            var containingTypes = SymbolAnalyzer.GetContainingTypeChainInfo(classSymbol);

            return sb.WrapInNamespace(
                    namespaceName,
                    (builder) =>
                    {
                        builder.WrapInContainingTypes(
                            containingTypes,
                            0,
                            (b, indent) =>
                                b.WrapInType(
                                    "public",
                                    "class",
                                    className,
                                    (classBuilder) =>
                                    {
                                        // Generate single overload based on Aspect attribute properties
                                        GenerateSingleOverload(
                                            classBuilder,
                                            methodName,
                                            aspectTypeName,
                                            validatedMethodInfo,
                                            customArgsDecStr,
                                            customArgsCallStr,
                                            visibility
                                        );
                                    },
                                    indent
                                )
                        );
                    }
                )
                .ToString();
        }

        private static void GenerateSingleOverload(
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
            // For empty attributes we deliberately do NOT emit this; callers must explicitly pass
            // a builder so iteration cannot accidentally walk every group in the world.
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
            // (When the attribute has Sets, the iteration is forced through the sparse path.)
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

            // (4) Range overload for event handlers — always emitted (event observers may forward
            // their callback args + custom event args).
            GenerateRangeOverload(
                sb,
                methodName,
                aspectTypeName,
                info,
                customArgsDecStr,
                visibility
            );
        }

        /// <summary>
        /// Builds an attribute-criteria chain that's appended to a builder expression. The chain
        /// is exactly the criteria the attribute imposes — tags, MatchByComponents components,
        /// and Set/Sets. The caller is responsible for prefixing it with the builder expression.
        /// </summary>
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
                EmitSparseIterationBody(
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
                // Dense path. Just delegate to the (QueryBuilder) entry — no set criteria means
                // no duplication risk, so the typed entry can do the merging.
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

            // Fail loud rather than walk every group in the world. The runtime check covers both
            // the empty-attribute case (no chain merged in) and the case where the caller passed
            // an empty builder.
            sb.AppendLine(
                3,
                $"Assert.That(__builder.HasAnyCriteria, \"{methodName} requires query criteria — pass a builder with at least one tag, component, or set constraint, or specify Tag/Set/MatchByComponents on the [ForEachEntity] attribute.\");"
            );

            EmitDenseIterationBody(
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

            EmitSparseIterationBody(
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
        /// Builds the comma-separated argument list for the call to the user's [ForEachAspect]
        /// method, preserving the user's declaration order via <see cref="ValidatedMethodInfo.ParameterSlots"/>.
        /// </summary>
        private static string BuildUserMethodCallArgs(
            ValidatedMethodInfo info,
            string aspectVar,
            string worldVar,
            string entityIndexVar
        )
        {
            var args = new List<string>(info.ParameterSlots.Count);
            foreach (var slot in info.ParameterSlots)
            {
                switch (slot.Kind)
                {
                    case ParamSlotKind.LoopAspect:
                        args.Add($"in {aspectVar}");
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
                    case ParamSlotKind.HoistedSingleton:
                        var hs = info.HoistedSingletons[slot.Index];
                        args.Add(hs.IsRef ? $"ref __{hs.ParamName}" : $"in __{hs.ParamName}");
                        break;
                }
            }
            return string.Join(", ", args);
        }

        /// <summary>
        /// Emits the inline dense iteration loop: foreach DenseGroupSlice → fetch buffers → for-loop
        /// over entities. The slice already carries GroupIndex + Count from a single lookup so we don't
        /// need a separate CountEntitiesInGroup call per group.
        /// If <paramref name="worldVar"/> is null, declares a local <c>__world</c>; otherwise uses
        /// the supplied variable name (so the convenience overload's parameter doesn't shadow).
        /// </summary>
        private static void EmitDenseIterationBody(
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

            NamespaceCollector.EmitSetAccessorDeclarations(sb, info, indentLevel, worldName);
            HoistedSingleEmitter.Emit(sb, indentLevel, worldName, info.HoistedSingletons);

            sb.AppendLine(indentLevel, $"foreach (var __slice in {builderVar}.GroupSlices())");
            sb.AppendLine(indentLevel, "{");

            EmitPerGroupBufferFetch(
                sb,
                info,
                indentLevel + 1,
                groupVar: "__slice.GroupIndex",
                worldVar: worldName
            );
            sb.AppendLine();

            var constructorArgs = string.Join(", ", BufferVarNamesFor(info));
            sb.AppendLine(
                indentLevel + 1,
                $"var __view = new {aspectTypeName}(__slice.GroupIndex, {constructorArgs});"
            );
            sb.AppendLine(indentLevel + 1, "for (int __i = 0; __i < __slice.Count; __i++)");
            sb.AppendLine(indentLevel + 1, "{");
            sb.AppendLine(indentLevel + 2, "__view.SetIndex(__i);");

            if (info.HasEntityIndexParameter)
                sb.AppendLine(
                    indentLevel + 2,
                    "var __entityIndex = new EntityIndex(__i, __slice.GroupIndex);"
                );

            EmitUserBodyOrCall(sb, indentLevel + 2, methodName, info, worldName);

            sb.AppendLine(indentLevel + 1, "}");
            sb.AppendLine(indentLevel, "}");
        }

        /// <summary>
        /// Emits the inline sparse iteration loop. Same <paramref name="worldVar"/> behaviour as
        /// <see cref="EmitDenseIterationBody"/>.
        /// </summary>
        private static void EmitSparseIterationBody(
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

            NamespaceCollector.EmitSetAccessorDeclarations(sb, info, indentLevel, worldName);
            HoistedSingleEmitter.Emit(sb, indentLevel, worldName, info.HoistedSingletons);

            sb.AppendLine(indentLevel, $"foreach (var __slice in {builderVar}.GroupSlices())");
            sb.AppendLine(indentLevel, "{");

            EmitPerGroupBufferFetch(
                sb,
                info,
                indentLevel + 1,
                groupVar: "__slice.GroupIndex",
                worldVar: worldName
            );
            sb.AppendLine();

            var constructorArgs = string.Join(", ", BufferVarNamesFor(info));
            sb.AppendLine(
                indentLevel + 1,
                $"var __view = new {aspectTypeName}(__slice.GroupIndex, {constructorArgs});"
            );
            sb.AppendLine(indentLevel + 1, "foreach (var __idx in __slice.Indices)");
            sb.AppendLine(indentLevel + 1, "{");
            sb.AppendLine(indentLevel + 2, "__view.SetIndex(__idx);");

            if (info.HasEntityIndexParameter)
                sb.AppendLine(
                    indentLevel + 2,
                    "var __entityIndex = new EntityIndex(__idx, __slice.GroupIndex);"
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
            var callArgs = BuildUserMethodCallArgs(info, "__view", worldName, "__entityIndex");
            sb.AppendLine(indentLevel, $"{methodName}({callArgs});");
        }

        private static void EmitPerGroupBufferFetch(
            OptimizedStringBuilder sb,
            ValidatedMethodInfo info,
            int indentLevel,
            string groupVar,
            string worldVar
        )
        {
            foreach (var type in info.ComponentTypes)
            {
                var varName = ComponentTypeHelper.GetComponentVariableName(type);
                var typeDisplay = PerformanceCache.GetDisplayString(type);
                var bufferSuffix = info.IsReadOnly(type) ? "Read" : "Write";
                sb.AppendLine(
                    indentLevel,
                    $"var {varName} = {worldVar}.ComponentBuffer<{typeDisplay}>({groupVar}).{bufferSuffix};"
                );
            }
        }

        private static List<string> BufferVarNamesFor(ValidatedMethodInfo info)
        {
            var names = new List<string>(info.ComponentTypes.Count);
            foreach (var type in info.ComponentTypes)
                names.Add(ComponentTypeHelper.GetComponentVariableName(type));
            return names;
        }

        private static void GenerateRangeOverload(
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
                $"{visibility} void {methodName}(GroupIndex __group, EntityRange __indices, WorldAccessor __world{customArgsDecStr})"
            );
            sb.AppendLine(2, "{");

            NamespaceCollector.EmitSetAccessorDeclarations(
                sb,
                info,
                indentLevel: 3,
                worldVar: "__world"
            );
            HoistedSingleEmitter.Emit(
                sb,
                indentLevel: 3,
                worldVar: "__world",
                info.HoistedSingletons
            );

            EmitPerGroupBufferFetch(
                sb,
                info,
                indentLevel: 3,
                groupVar: "__group",
                worldVar: "__world"
            );
            sb.AppendLine();

            var constructorArgs = string.Join(", ", BufferVarNamesFor(info));
            sb.AppendLine(3, $"var __view = new {aspectTypeName}(__group, {constructorArgs});");
            sb.AppendLine(3, "for (int __i = __indices.Start; __i < __indices.End; __i++)");
            sb.AppendLine(3, "{");
            sb.AppendLine(4, "__view.SetIndex(__i);");

            if (info.HasEntityIndexParameter)
                sb.AppendLine(4, "var __entityIndex = new EntityIndex(__i, __group);");

            EmitUserBodyOrCall(sb, indentLevel: 4, methodName, info, "__world");

            sb.AppendLine(3, "}");
            sb.AppendLine(2, "}");
            sb.AppendLine();
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
            validatedMethodInfo = null;
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

                // [SingleEntity] aspects are hoisted out of the iteration loop, not
                // the iteration target — skip them here so they don't get mis-detected
                // as the loop aspect. ParameterClassifier records them as
                // ParamSlotKind.HoistedSingleton.
                bool pHasSingleEntity =
                    pSymbol != null
                    && PerformanceCache.HasAttributeByName(
                        pSymbol,
                        TrecsAttributeNames.SingleEntity,
                        TrecsNamespaces.Trecs
                    );
                if (pHasSingleEntity)
                    continue;

                if (!SymbolAnalyzer.ImplementsInterface(pType, "IAspect", TrecsNamespaces.Trecs))
                    continue;

                if (aspectParam != null)
                {
                    // Two non-pass-through aspect params is ambiguous.
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
                        "[ForEachEntity] method on a system class with an aspect parameter must take exactly one aspect parameter (implementing IAspect, declared 'in')"
                    )
                );
                return false;
            }

            // Aspect must use 'in' modifier (not 'ref', not bare).
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

            // Get component types from the aspect
            var componentTypes = new List<ITypeSymbol>();
            var firstParamNamedType = aspectParamType as INamedTypeSymbol;
            var attributeData =
                firstParamNamedType != null
                    ? AspectAttributeParser.ParseAspectData(firstParamNamedType)
                    : null;

            if (attributeData != null)
            {
                componentTypes.AddRange(attributeData.ReadTypes);
                componentTypes.AddRange(attributeData.WriteTypes);
                var distinctComponentTypes = PerformanceCache.GetDistinctTypes(componentTypes);
                componentTypes = distinctComponentTypes.ToList();
            }

            var readComponentTypes = attributeData?.ReadTypes.ToList() ?? new List<ITypeSymbol>();
            var writeComponentTypes = attributeData?.WriteTypes.ToList() ?? new List<ITypeSymbol>();

            // Extract tag types, set types, and MatchByComponents from the iteration attribute.
            var criteria = IterationCriteriaParser.ParseIterationAttribute(
                reportDiagnostic,
                methodDec,
                methodSymbol,
                classDec.Identifier.Text,
                TrecsAttributeNames.ForEachEntity
            );
            if (criteria == null)
            {
                isValid = false;
            }
            var attributeTagTypes = criteria?.TagTypes ?? new List<ITypeSymbol>();
            var setTypes = criteria?.SetTypes ?? new List<ITypeSymbol>();
            bool attributeMatchByComponents = criteria?.MatchByComponents ?? false;

            // Walk all parameters in declaration order via shared classifier.
            var classified = ParameterClassifier.Classify(
                parameters,
                semanticModel,
                IterationMode.Aspect,
                reportDiagnostic,
                methodDec.Identifier.Text,
                supportsEntityIndex: true,
                aspectParam: aspectParam,
                isValid: ref isValid
            );
            var customParameters = classified.CustomParameters;
            var setAccessorParameters = classified.SetAccessorParameters;
            var setReadParameters = classified.SetReadParameters;
            var setWriteParameters = classified.SetWriteParameters;
            var paramSlots = classified.ParameterSlots;
            bool hasEntityIndexParameter = classified.HasEntityIndex;

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
                    HasEntityIndexParameter = hasEntityIndexParameter,
                    SetTypes = effectiveSetTypes,
                    MatchByComponents = attributeMatchByComponents,
                    SetAccessorParameters = setAccessorParameters,
                    SetReadParameters = setReadParameters,
                    SetWriteParameters = setWriteParameters,
                    HoistedSingletons = classified.HoistedSingletons,
                };
            }

            return isValid;
        }
    }

    /// <summary>
    /// Data structure for ForEachAspect information used in incremental generation.
    /// Carries validation results + diagnostics forward from the transform phase so that
    /// the terminal stage doesn't need access to a <see cref="Compilation"/> or
    /// <see cref="SemanticModel"/>.
    /// </summary>
    internal class ForEachAspectData
    {
        public ClassDeclarationSyntax ClassDecl { get; }
        public MethodDeclarationSyntax MethodDecl { get; }
        public IMethodSymbol MethodSymbol { get; }
        public bool IsValid { get; }
        public ValidatedMethodInfo? ValidatedInfo { get; }
        public ImmutableArray<Diagnostic> Diagnostics { get; }

        public ForEachAspectData(
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
