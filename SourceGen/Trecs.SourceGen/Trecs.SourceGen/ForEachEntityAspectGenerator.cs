#nullable enable

using System;
using System.Collections.Generic;
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
    /// Source generator for the aspect-iteration mode of <c>[ForEachEntity]</c>
    /// (methods whose loop parameter is an <c>IAspect</c>). The component-iteration
    /// mode is handled by <c>ForEachGenerator</c>; routing is decided by
    /// <see cref="IterationAttributeRouting"/>.
    ///
    /// <para>Pipeline shape mirrors <see cref="ForEachGenerator"/>: the transform
    /// produces a value-equatable <see cref="ForEachAspectModel"/> (strings +
    /// EquatableArrays, zero symbols / syntax / raw Diagnostic), and the terminal
    /// stage materializes diagnostics + emits source. The compilation's
    /// global-namespace name folds in via a lightweight string Combine.</para>
    /// </summary>
    [Generator]
    public class ForEachEntityAspectGenerator : IIncrementalGenerator
    {
        public void Initialize(IncrementalGeneratorInitializationContext context)
        {
            var modelsRaw = context
                .SyntaxProvider.CreateSyntaxProvider(
                    predicate: static (s, _) => IsMethodWithForEachEntityAttribute(s),
                    transform: static (ctx, _) => BuildModel(ctx)
                )
                .Where(static m => m is not null)
                .Select(static (m, _) => m!.Value);

            var globalNsProvider = context.CompilationProvider.Select(
                static (c, _) =>
                    PerformanceCache.GetDisplayString(c.GlobalNamespace) ?? string.Empty
            );

            var withGlobalNs = modelsRaw.Combine(globalNsProvider);

            context.RegisterSourceOutput(
                withGlobalNs,
                static (spc, source) => GenerateForEachSource(spc, source.Left, source.Right)
            );
        }

        private static bool IsMethodWithForEachEntityAttribute(SyntaxNode node)
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

        private static ForEachAspectModel? BuildModel(GeneratorSyntaxContext context)
        {
            var methodDecl = (MethodDeclarationSyntax)context.Node;

            // [ForEachEntity] on a struct method is the JobGenerator's responsibility.
            if (methodDecl.Parent is StructDeclarationSyntax)
                return null;

            var classDecl = methodDecl.FirstAncestorOrSelf<ClassDeclarationSyntax>();
            if (classDecl == null)
                return null;

            var methodSymbol = context.SemanticModel.GetDeclaredSymbol(methodDecl) as IMethodSymbol;
            if (methodSymbol == null)
                return null;

            if (!IterationAttributeRouting.HasEntityFilter(methodSymbol))
                return null;
            if (IterationAttributeRouting.HasWrapAsJobAttribute(methodSymbol))
                return null;
            if (!IterationAttributeRouting.RoutesToAspectGenerator(methodSymbol))
                return null;

            var classSymbol = methodSymbol.ContainingType;
            var className = classDecl.Identifier.Text;
            var methodName = methodDecl.Identifier.Text;
            var diagnostics = new List<DiagnosticInfo>();
            ForEachAspectValidation validation = ForEachAspectValidation.Empty;
            bool isValid;

            try
            {
                isValid = ValidateMethod(
                    classDecl,
                    methodDecl,
                    methodSymbol,
                    context.SemanticModel,
                    diagnostics,
                    out validation
                );
            }
            catch (Exception ex)
            {
                diagnostics.Add(
                    DiagnosticInfo.Create(
                        DiagnosticDescriptors.SourceGenerationError,
                        methodDecl.GetLocation(),
                        "ForEachEntity (aspect mode) method validation",
                        ex.Message
                    )
                );
                isValid = false;
                validation = ForEachAspectValidation.Empty;
            }

            var containingTypes = SymbolAnalyzer
                .GetContainingTypeChainInfo(classSymbol)
                .ToEquatableArray();
            var visibility = SymbolAnalyzer.GetMethodVisibility(methodDecl);

            return new ForEachAspectModel(
                ClassName: className,
                MethodName: methodName,
                Namespace: SymbolAnalyzer.GetNamespace(classDecl),
                Visibility: visibility,
                HintFileName: SymbolAnalyzer.GetSafeFileName(
                    classSymbol,
                    $"{methodName}_ForEachEntityAspect"
                ),
                ContainingTypes: containingTypes,
                IsValid: isValid,
                Validation: validation,
                Diagnostics: diagnostics.ToEquatableArray()
            );
        }

        private static void GenerateForEachSource(
            SourceProductionContext context,
            ForEachAspectModel model,
            string globalNamespaceName
        )
        {
            foreach (var diag in model.Diagnostics)
                context.ReportDiagnostic(diag.ToDiagnostic());

            if (!model.IsValid)
                return;

            try
            {
                using var _timer_ = SourceGenTimer.Time("ForEachEntityAspectGenerator.Total");
                SourceGenLogger.Log(
                    $"[ForEachEntityAspectGenerator] Processing {model.ClassName}.{model.MethodName}"
                );

                var source = ErrorRecovery.TryExecute(
                    () => GenerateSource(model, globalNamespaceName),
                    context,
                    Location.None,
                    "ForEachEntity (aspect mode) code generation"
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
                    $"ForEachEntity (aspect mode) {model.ClassName}.{model.MethodName}",
                    ex
                );
            }
        }

        // ─────────────────────────────────────────────────────────────────────
        // Code emission (all from the equatable model — no symbol access)
        // ─────────────────────────────────────────────────────────────────────

        private static string GenerateSource(ForEachAspectModel model, string globalNamespaceName)
        {
            var v = model.Validation;

            var sb = OptimizedStringBuilder.ForAspect(v.AspectComponents.Length);

            var namespaces = new HashSet<string>(CommonUsings.Namespaces) { "Unity.Jobs" };
            foreach (var ns in v.Namespaces)
            {
                if (ns != globalNamespaceName)
                    namespaces.Add(ns);
            }
            HoistedSingleEmitter.CollectNamespaces(
                namespaces,
                v.HoistedSingletons,
                globalNamespaceName
            );
            sb.AppendUsings(namespaces.ToArray());

            return sb.WrapInNamespace(
                    model.Namespace,
                    (builder) =>
                    {
                        builder.WrapInContainingTypes(
                            model.ContainingTypes.ToArray(),
                            0,
                            (b, indent) =>
                                b.WrapInType(
                                    "public",
                                    "class",
                                    model.ClassName,
                                    (classBuilder) =>
                                    {
                                        GenerateSingleOverload(
                                            classBuilder,
                                            model.MethodName,
                                            v,
                                            model.Visibility
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
            ForEachAspectValidation v,
            string visibility
        )
        {
            string customArgsDecStr = "";
            string customArgsCallStr = "";

            if (v.CustomParameters.Length > 0)
            {
                customArgsDecStr =
                    ", "
                    + string.Join(
                        ", ",
                        v.CustomParameters.Select(p =>
                            $"{(p.IsRef ? "ref " : p.IsIn ? "in " : "")}{p.Type} {p.Name}"
                        )
                    );
                customArgsCallStr =
                    ", "
                    + string.Join(
                        ", ",
                        v.CustomParameters.Select(p =>
                            $"{(p.IsRef ? "ref " : p.IsIn ? "in " : "")}{p.Name}"
                        )
                    );
            }

            bool hasSets = v.HasSet;
            bool hasAnyAttributeCriteria = hasSets || v.HasAttributeTags || v.MatchByComponents;

            // (1) Convenience overload — only when the attribute supplies at least one criterion.
            if (hasAnyAttributeCriteria)
            {
                GenerateConvenienceOverload(
                    sb,
                    methodName,
                    v,
                    customArgsDecStr,
                    customArgsCallStr,
                    visibility
                );
            }

            // (2) Public dense entry — only when the attribute imposes no Set/Sets.
            if (!hasSets)
            {
                GenerateQueryBuilderEntry(sb, methodName, v, customArgsDecStr, visibility);
            }

            // (3) Public sparse entry — only when the attribute has no set baked in.
            if (!hasSets)
            {
                GenerateSparseQueryBuilderEntry(sb, methodName, v, customArgsDecStr, visibility);
            }

            // (4) Range overload for event handlers — always emitted.
            GenerateRangeOverload(sb, methodName, v, customArgsDecStr, visibility);
        }

        private static void GenerateConvenienceOverload(
            OptimizedStringBuilder sb,
            string methodName,
            ForEachAspectValidation v,
            string customArgsDecStr,
            string customArgsCallStr,
            string visibility
        )
        {
            sb.AppendLine(2, GeneratedCodeAttributes.Line);
            sb.AppendLine(
                2,
                $"{visibility} void {methodName}(WorldAccessor __world{customArgsDecStr})"
            );
            sb.AppendLine(2, "{");

            if (v.HasSet)
            {
                sb.AppendLine(
                    3,
                    $"var __builder = __world.Query(){v.AttributeCriteriaChainNoWithouts}.InSet<{v.FirstSetTypeDisplay}>();"
                );
                EmitSparseIterationBody(
                    sb,
                    methodName,
                    v,
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
            ForEachAspectValidation v,
            string customArgsDecStr,
            string visibility
        )
        {
            sb.AppendLine(2, GeneratedCodeAttributes.Line);
            sb.AppendLine(
                2,
                $"{visibility} void {methodName}(QueryBuilder __builder{customArgsDecStr})"
            );
            sb.AppendLine(2, "{");

            if (v.AttributeCriteriaChainFull.Length > 0)
                sb.AppendLine(3, $"__builder = __builder{v.AttributeCriteriaChainFull};");

            sb.AppendLine(
                3,
                $"TrecsDebugAssert.That(__builder.HasAnyCriteria, \"{methodName} requires query criteria — pass a builder with at least one tag, component, or set constraint, or specify Tag/Set/MatchByComponents on the [ForEachEntity] attribute.\");"
            );

            EmitDenseIterationBody(
                sb,
                methodName,
                v,
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
            ForEachAspectValidation v,
            string customArgsDecStr,
            string visibility
        )
        {
            sb.AppendLine(2, GeneratedCodeAttributes.Line);
            sb.AppendLine(
                2,
                $"{visibility} void {methodName}(SparseQueryBuilder __builder{customArgsDecStr})"
            );
            sb.AppendLine(2, "{");

            if (v.AttributeCriteriaChainFull.Length > 0)
                sb.AppendLine(3, $"__builder = __builder{v.AttributeCriteriaChainFull};");

            EmitSparseIterationBody(
                sb,
                methodName,
                v,
                indentLevel: 3,
                builderVar: "__builder",
                worldVar: null
            );

            sb.AppendLine(2, "}");
            sb.AppendLine();
        }

        private static void GenerateRangeOverload(
            OptimizedStringBuilder sb,
            string methodName,
            ForEachAspectValidation v,
            string customArgsDecStr,
            string visibility
        )
        {
            sb.AppendLine(2, GeneratedCodeAttributes.Line);
            sb.AppendLine(
                2,
                $"{visibility} void {methodName}(GroupIndex __group, EntityRange __indices, WorldAccessor __world{customArgsDecStr})"
            );
            sb.AppendLine(2, "{");

            EmitSetAccessorDeclarations(sb, v, indentLevel: 3, worldVar: "__world");
            HoistedSingleEmitter.Emit(sb, 3, "__world", v.HoistedSingletons);

            EmitPerGroupBufferFetch(
                sb,
                v,
                indentLevel: 3,
                groupVar: "__group",
                worldVar: "__world"
            );
            sb.AppendLine();

            var constructorArgs = string.Join(", ", v.AspectComponents.Select(c => c.VarName));
            sb.AppendLine(3, $"var __view = new {v.AspectTypeName}(__group, {constructorArgs});");
            sb.AppendLine(3, "for (int __i = __indices.Start; __i < __indices.End; __i++)");
            sb.AppendLine(3, "{");
            sb.AppendLine(4, "__view.SetIndex(__i);");

            EmitEntityRefDeclarations(
                sb,
                v,
                indentLevel: 4,
                indexExpr: "new EntityIndex(__i, __group)",
                worldVar: "__world"
            );

            EmitUserBodyOrCall(sb, indentLevel: 4, methodName, v, "__world");

            sb.AppendLine(3, "}");
            sb.AppendLine(2, "}");
            sb.AppendLine();
        }

        /// <summary>
        /// Emits the inline dense iteration loop: foreach DenseGroupSlice → fetch buffers →
        /// for-loop over entities. <paramref name="worldVar"/>: if null, declares a local
        /// <c>__world = {builderVar}.World;</c>; otherwise uses the supplied name (so the
        /// convenience overload's parameter doesn't shadow).
        /// </summary>
        private static void EmitDenseIterationBody(
            OptimizedStringBuilder sb,
            string methodName,
            ForEachAspectValidation v,
            int indentLevel,
            string builderVar,
            string? worldVar
        )
        {
            string worldName = worldVar ?? "__world";
            if (worldVar == null)
                sb.AppendLine(indentLevel, $"var {worldName} = {builderVar}.World;");

            EmitSetAccessorDeclarations(sb, v, indentLevel, worldName);
            HoistedSingleEmitter.Emit(sb, indentLevel, worldName, v.HoistedSingletons);

            sb.AppendLine(indentLevel, $"foreach (var __slice in {builderVar}.GroupSlices())");
            sb.AppendLine(indentLevel, "{");

            EmitPerGroupBufferFetch(
                sb,
                v,
                indentLevel + 1,
                groupVar: "__slice.GroupIndex",
                worldVar: worldName
            );
            sb.AppendLine();

            var constructorArgs = string.Join(", ", v.AspectComponents.Select(c => c.VarName));
            sb.AppendLine(
                indentLevel + 1,
                $"var __view = new {v.AspectTypeName}(__slice.GroupIndex, {constructorArgs});"
            );
            sb.AppendLine(indentLevel + 1, "for (int __i = 0; __i < __slice.Count; __i++)");
            sb.AppendLine(indentLevel + 1, "{");
            sb.AppendLine(indentLevel + 2, "__view.SetIndex(__i);");

            EmitEntityRefDeclarations(
                sb,
                v,
                indentLevel + 2,
                indexExpr: "new EntityIndex(__i, __slice.GroupIndex)",
                worldVar: worldName
            );

            EmitUserBodyOrCall(sb, indentLevel + 2, methodName, v, worldName);

            sb.AppendLine(indentLevel + 1, "}");
            sb.AppendLine(indentLevel, "}");
        }

        /// <summary>
        /// Emits the inline sparse iteration loop. Same <paramref name="worldVar"/>
        /// semantics as <see cref="EmitDenseIterationBody"/>.
        /// </summary>
        private static void EmitSparseIterationBody(
            OptimizedStringBuilder sb,
            string methodName,
            ForEachAspectValidation v,
            int indentLevel,
            string builderVar,
            string? worldVar
        )
        {
            string worldName = worldVar ?? "__world";
            if (worldVar == null)
                sb.AppendLine(indentLevel, $"var {worldName} = {builderVar}.World;");

            EmitSetAccessorDeclarations(sb, v, indentLevel, worldName);
            HoistedSingleEmitter.Emit(sb, indentLevel, worldName, v.HoistedSingletons);

            sb.AppendLine(indentLevel, $"foreach (var __slice in {builderVar}.GroupSlices())");
            sb.AppendLine(indentLevel, "{");

            EmitPerGroupBufferFetch(
                sb,
                v,
                indentLevel + 1,
                groupVar: "__slice.GroupIndex",
                worldVar: worldName
            );
            sb.AppendLine();

            var constructorArgs = string.Join(", ", v.AspectComponents.Select(c => c.VarName));
            sb.AppendLine(
                indentLevel + 1,
                $"var __view = new {v.AspectTypeName}(__slice.GroupIndex, {constructorArgs});"
            );
            sb.AppendLine(indentLevel + 1, "foreach (var __idx in __slice.Indices)");
            sb.AppendLine(indentLevel + 1, "{");
            sb.AppendLine(indentLevel + 2, "__view.SetIndex(__idx);");

            EmitEntityRefDeclarations(
                sb,
                v,
                indentLevel + 2,
                indexExpr: "new EntityIndex(__idx, __slice.GroupIndex)",
                worldVar: worldName
            );

            EmitUserBodyOrCall(sb, indentLevel + 2, methodName, v, worldName);

            sb.AppendLine(indentLevel + 1, "}");
            sb.AppendLine(indentLevel, "}");
        }

        private static void EmitUserBodyOrCall(
            OptimizedStringBuilder sb,
            int indentLevel,
            string methodName,
            ForEachAspectValidation v,
            string worldName
        )
        {
            var callArgs = BuildUserMethodCallArgs(v, "__view", worldName, "__entityIndex");
            sb.AppendLine(indentLevel, $"{methodName}({callArgs});");
        }

        private static void EmitPerGroupBufferFetch(
            OptimizedStringBuilder sb,
            ForEachAspectValidation v,
            int indentLevel,
            string groupVar,
            string worldVar
        )
        {
            foreach (var entry in v.AspectComponents)
            {
                var bufferSuffix = entry.IsWrite ? "Write" : "Read";
                sb.AppendLine(
                    indentLevel,
                    $"var {entry.VarName} = {worldVar}.ComponentBuffer<{entry.TypeDisplay}>({groupVar}).{bufferSuffix};"
                );
            }
        }

        /// <summary>
        /// Builds the comma-separated argument list for the call to the user's
        /// aspect-mode <c>[ForEachEntity]</c> method, preserving the user's
        /// declaration order via <see cref="ForEachAspectValidation.ParameterSlots"/>.
        /// </summary>
        private static string BuildUserMethodCallArgs(
            ForEachAspectValidation v,
            string aspectVar,
            string worldVar,
            string entityIndexVar
        )
        {
            var args = new List<string>(v.ParameterSlots.Length);
            foreach (var slot in v.ParameterSlots)
            {
                switch (slot.Kind)
                {
                    case ParamSlotKind.LoopAspect:
                        args.Add($"in {aspectVar}");
                        break;
                    case ParamSlotKind.LoopEntityIndex:
                        args.Add(entityIndexVar);
                        break;
                    case ParamSlotKind.LoopEntityHandle:
                        args.Add("__entityHandle");
                        break;
                    case ParamSlotKind.LoopWorldAccessor:
                        args.Add(worldVar);
                        break;
                    case ParamSlotKind.LoopSetAccessor:
                        var sa = v.SetAccessorParameters[slot.Index];
                        args.Add(sa.IsIn ? $"in {sa.ParamName}" : sa.ParamName);
                        break;
                    case ParamSlotKind.LoopSetRead:
                        var sr = v.SetReadParameters[slot.Index];
                        args.Add($"in {sr.ParamName}");
                        break;
                    case ParamSlotKind.LoopSetWrite:
                        var sw = v.SetWriteParameters[slot.Index];
                        args.Add($"in {sw.ParamName}");
                        break;
                    case ParamSlotKind.Custom:
                        var p = v.CustomParameters[slot.Index];
                        var prefix =
                            p.IsRef ? "ref "
                            : p.IsIn ? "in "
                            : "";
                        args.Add($"{prefix}{p.Name}");
                        break;
                    case ParamSlotKind.HoistedSingleton:
                        var hs = v.HoistedSingletons[slot.Index];
                        args.Add(hs.IsRef ? $"ref __{hs.ParamName}" : $"in __{hs.ParamName}");
                        break;
                }
            }
            return string.Join(", ", args);
        }

        private static void EmitSetAccessorDeclarations(
            OptimizedStringBuilder sb,
            ForEachAspectValidation v,
            int indentLevel,
            string worldVar
        )
        {
            foreach (var sa in v.SetAccessorParameters)
                sb.AppendLine(
                    indentLevel,
                    $"var {sa.ParamName} = {worldVar}.Set<{sa.SetTypeArg}>();"
                );
            foreach (var sr in v.SetReadParameters)
                sb.AppendLine(
                    indentLevel,
                    $"var {sr.ParamName} = {worldVar}.Set<{sr.SetTypeArg}>().Read;"
                );
            foreach (var sw in v.SetWriteParameters)
                sb.AppendLine(
                    indentLevel,
                    $"var {sw.ParamName} = {worldVar}.Set<{sw.SetTypeArg}>().Write;"
                );
        }

        private static void EmitEntityRefDeclarations(
            OptimizedStringBuilder sb,
            ForEachAspectValidation v,
            int indentLevel,
            string indexExpr,
            string worldVar
        )
        {
            bool needsIndex = v.HasEntityIndexParameter || v.HasEntityHandleParameter;
            if (needsIndex)
                sb.AppendLine(indentLevel, $"var __entityIndex = {indexExpr};");
            if (v.HasEntityHandleParameter)
                sb.AppendLine(
                    indentLevel,
                    $"var __entityHandle = __entityIndex.ToHandle({worldVar});"
                );
        }

        // ─────────────────────────────────────────────────────────────────────
        // Validation
        // ─────────────────────────────────────────────────────────────────────

        private static bool ValidateMethod(
            ClassDeclarationSyntax classDec,
            MethodDeclarationSyntax methodDec,
            IMethodSymbol methodSymbol,
            SemanticModel semanticModel,
            List<DiagnosticInfo> diagnostics,
            out ForEachAspectValidation validation
        )
        {
            validation = ForEachAspectValidation.Empty;
            bool isValid = true;

            if (!classDec.Modifiers.Any(m => m.IsKind(SyntaxKind.PartialKeyword)))
            {
                diagnostics.Add(
                    DiagnosticInfo.Create(
                        DiagnosticDescriptors.NotPartialClass,
                        classDec.Identifier.GetLocation(),
                        classDec.Identifier.Text
                    )
                );
                isValid = false;
            }

            if (methodDec.ReturnType.ToString() != "void")
            {
                diagnostics.Add(
                    DiagnosticInfo.Create(
                        DiagnosticDescriptors.InvalidReturnType,
                        methodDec.ReturnType.GetLocation()
                    )
                );
                isValid = false;
            }

            var parameters = methodDec.ParameterList.Parameters;
            if (parameters.Count == 0)
            {
                diagnostics.Add(
                    DiagnosticInfo.Create(
                        DiagnosticDescriptors.EmptyParameters,
                        methodDec.ParameterList.GetLocation()
                    )
                );
                return false;
            }

            // Locate the (single) aspect parameter — anywhere in the list, marked 'in',
            // implementing IAspect, no [PassThroughArgument] / [SingleEntity].
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
                    diagnostics.Add(
                        DiagnosticInfo.Create(
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
                diagnostics.Add(
                    DiagnosticInfo.Create(
                        DiagnosticDescriptors.InvalidParameterList,
                        methodDec.ParameterList.GetLocation(),
                        "[ForEachEntity] method on a system class with an aspect parameter must take exactly one aspect parameter (implementing IAspect, declared 'in')"
                    )
                );
                return false;
            }

            if (aspectParam.Modifiers.Any(m => m.IsKind(SyntaxKind.RefKeyword)))
            {
                diagnostics.Add(
                    DiagnosticInfo.Create(
                        DiagnosticDescriptors.AspectParamMustBeIn,
                        aspectParam.GetLocation(),
                        aspectParam.Identifier.Text
                    )
                );
                return false;
            }
            if (!aspectParam.Modifiers.Any(m => m.IsKind(SyntaxKind.InKeyword)))
            {
                diagnostics.Add(
                    DiagnosticInfo.Create(
                        DiagnosticDescriptors.InvalidParameterModifiers,
                        aspectParam.GetLocation(),
                        aspectParam.Identifier.Text
                    )
                );
                return false;
            }

            var aspectTypeName = aspectParam.Type?.ToString() ?? "UnknownType";

            // Parse the aspect's read/write components.
            var firstParamNamedType = aspectParamType as INamedTypeSymbol;
            AspectAttributeData? aspectData =
                firstParamNamedType != null
                    ? AspectAttributeParser.ParseAspectData(firstParamNamedType)
                    : null;

            // ParameterClassifier emits already-substituted Diagnostic objects via its
            // Action<Diagnostic> callback; stash them as preformatted DiagnosticInfo so
            // the descriptor's MessageFormat isn't applied a second time.
            System.Action<Diagnostic> reportDiag = d =>
                diagnostics.Add(DiagnosticInfo.FromDiagnostic(d));

            // Extract tag types, set types, and MatchByComponents from the iteration attribute.
            var criteria = IterationCriteriaParser.ParseIterationAttribute(
                reportDiag,
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
            var attributeWithoutTagTypes = criteria?.WithoutTagTypes ?? new List<ITypeSymbol>();
            var setTypes = criteria?.SetTypes ?? new List<ITypeSymbol>();
            bool matchByComponents = criteria?.MatchByComponents ?? false;

            var classified = ParameterClassifier.Classify(
                parameters,
                semanticModel,
                IterationMode.Aspect,
                reportDiag,
                methodDec.Identifier.Text,
                aspectParam: aspectParam,
                isValid: ref isValid
            );

            // Component count must be > 0 for the aspect to be useful.
            int componentCount =
                (aspectData?.ReadTypes.Length ?? 0) + (aspectData?.WriteTypes.Length ?? 0);
            if (componentCount == 0)
            {
                diagnostics.Add(
                    DiagnosticInfo.Create(
                        DiagnosticDescriptors.AspectNoComponents,
                        aspectParam.GetLocation(),
                        aspectParam.Type?.ToString() ?? "UnknownType"
                    )
                );
                isValid = false;
            }

            if (!isValid)
                return false;

            validation = BuildValidationResult(
                aspectTypeName,
                aspectData,
                aspectParamType,
                classified,
                attributeTagTypes,
                attributeWithoutTagTypes,
                setTypes,
                matchByComponents
            );
            return true;
        }

        /// <summary>
        /// Materializes a value-equatable validation result from the symbol-bearing
        /// classification + aspect data. Pre-computes the QueryBuilder criteria chains
        /// and the namespace set so the codegen path touches no symbols.
        /// </summary>
        private static ForEachAspectValidation BuildValidationResult(
            string aspectTypeName,
            AspectAttributeData? aspectData,
            ITypeSymbol aspectParamType,
            ClassifiedParameters classified,
            List<ITypeSymbol> attributeTagTypes,
            List<ITypeSymbol> attributeWithoutTagTypes,
            List<ITypeSymbol> setTypes,
            bool matchByComponents
        )
        {
            // Build the aspect-component buffer entries in canonical AllComponentTypes
            // order (reads first, then writes not already in reads), with precomputed
            // buffer variable names and a write flag for the buffer suffix.
            var aspectComponents = BuildAspectComponentEntries(aspectData);

            var customParams = classified
                .CustomParameters.Select(p => new ParameterInfoModel(
                    Type: p.TypeSymbol != null
                        ? PerformanceCache.GetDisplayString(p.TypeSymbol)
                        : p.Type,
                    Name: p.Name,
                    IsRef: p.IsRef,
                    IsIn: p.IsIn
                ))
                .ToEquatableArray();

            var setAccessorParams = classified
                .SetAccessorParameters.Select(sa => new SetAccessorParameterModel(
                    sa.SetTypeArg,
                    sa.ParamName,
                    sa.IsIn
                ))
                .ToEquatableArray();
            var setReadParams = classified
                .SetReadParameters.Select(sr => new SetAccessorParameterModel(
                    sr.SetTypeArg,
                    sr.ParamName,
                    sr.IsIn
                ))
                .ToEquatableArray();
            var setWriteParams = classified
                .SetWriteParameters.Select(sw => new SetAccessorParameterModel(
                    sw.SetTypeArg,
                    sw.ParamName,
                    sw.IsIn
                ))
                .ToEquatableArray();

            var hoistedSingletons = classified
                .HoistedSingletons.Select(HoistedSingletonModelBuilder.FromInfo)
                .ToEquatableArray();

            var parameterSlots = classified.ParameterSlots.ToArray().ToEquatableArray();

            // Pre-compute both query-criteria chains. The convenience overload uses the
            // no-withouts variant; the QueryBuilder / SparseQueryBuilder entries use the
            // full chain.
            var allComponentSymbols =
                aspectData != null
                    ? aspectData.AllComponentTypes.AsEnumerable()
                    : Enumerable.Empty<ITypeSymbol>();
            var chainNoWithouts = QueryBuilderHelper.BuildAttributeCriteriaChain(
                attributeTagTypes,
                matchByComponents,
                allComponentSymbols
            );
            var chainFull = QueryBuilderHelper.BuildAttributeCriteriaChain(
                attributeTagTypes,
                matchByComponents,
                allComponentSymbols,
                attributeWithoutTagTypes
            );

            var firstSetDisplay =
                setTypes.Count > 0 ? PerformanceCache.GetDisplayString(setTypes[0]) : string.Empty;

            var namespaces = CollectNamespaces(
                aspectData,
                aspectParamType,
                classified,
                attributeTagTypes,
                attributeWithoutTagTypes,
                setTypes
            );

            return new ForEachAspectValidation(
                AspectTypeName: aspectTypeName,
                AspectComponents: aspectComponents,
                CustomParameters: customParams,
                SetAccessorParameters: setAccessorParams,
                SetReadParameters: setReadParams,
                SetWriteParameters: setWriteParams,
                HoistedSingletons: hoistedSingletons,
                ParameterSlots: parameterSlots,
                HasEntityIndexParameter: classified.HasEntityIndex,
                HasEntityHandleParameter: classified.HasEntityHandle,
                HasAttributeTags: attributeTagTypes.Count > 0,
                MatchByComponents: matchByComponents,
                HasSet: setTypes.Count > 0,
                FirstSetTypeDisplay: firstSetDisplay,
                AttributeCriteriaChainNoWithouts: chainNoWithouts,
                AttributeCriteriaChainFull: chainFull,
                Namespaces: namespaces
            );
        }

        private static EquatableArray<AspectBufferEntry> BuildAspectComponentEntries(
            AspectAttributeData? aspectData
        )
        {
            if (aspectData == null)
                return EquatableArray<AspectBufferEntry>.Empty;

            var all = aspectData.AllComponentTypes;
            var entries = new AspectBufferEntry[all.Length];
            for (int i = 0; i < all.Length; i++)
            {
                var t = all[i];
                bool isWrite = aspectData.WriteTypes.Any(w =>
                    SymbolEqualityComparer.Default.Equals(w, t)
                );
                entries[i] = new AspectBufferEntry(
                    TypeDisplay: PerformanceCache.GetDisplayString(t),
                    VarName: ComponentTypeHelper.GetComponentVariableName(t),
                    IsWrite: isWrite
                );
            }
            return new EquatableArray<AspectBufferEntry>(entries);
        }

        private static EquatableArray<string> CollectNamespaces(
            AspectAttributeData? aspectData,
            ITypeSymbol aspectParamType,
            ClassifiedParameters classified,
            List<ITypeSymbol> attributeTagTypes,
            List<ITypeSymbol> attributeWithoutTagTypes,
            List<ITypeSymbol> setTypes
        )
        {
            var ns = new HashSet<string>();

            void Add(ITypeSymbol? sym)
            {
                if (sym == null)
                    return;
                var s = PerformanceCache.GetDisplayString(sym.ContainingNamespace);
                if (!string.IsNullOrEmpty(s) && s != "System" && !s.StartsWith("System."))
                    ns.Add(s);
            }

            void AddTagWithContaining(ITypeSymbol sym)
            {
                Add(sym);
                if (sym.ContainingType != null)
                    Add(sym.ContainingType);
            }

            // Aspect type itself.
            Add(aspectParamType);

            // Aspect's components.
            if (aspectData != null)
            {
                foreach (var t in aspectData.ReadTypes)
                    Add(t);
                foreach (var t in aspectData.WriteTypes)
                    Add(t);
            }

            foreach (var p in classified.CustomParameters)
                Add(p.TypeSymbol);
            foreach (var t in attributeTagTypes)
                AddTagWithContaining(t);
            foreach (var t in attributeWithoutTagTypes)
                AddTagWithContaining(t);
            foreach (var t in setTypes)
                AddTagWithContaining(t);
            foreach (var sa in classified.SetAccessorParameters)
                AddTagWithContaining(sa.SetTypeArgSymbol);
            foreach (var sr in classified.SetReadParameters)
                AddTagWithContaining(sr.SetTypeArgSymbol);
            foreach (var sw in classified.SetWriteParameters)
                AddTagWithContaining(sw.SetTypeArgSymbol);

            return ns.ToEquatableArray();
        }
    }

    /// <summary>
    /// Top-level value-equatable model carried through the
    /// ForEachEntityAspectGenerator pipeline. Holds everything the terminal stage
    /// needs to materialize diagnostics and emit source — no Roslyn symbols, syntax,
    /// or raw <see cref="Diagnostic"/> objects.
    /// </summary>
    internal readonly record struct ForEachAspectModel(
        string ClassName,
        string MethodName,
        string Namespace,
        string Visibility,
        string HintFileName,
        EquatableArray<ContainingTypeInfo> ContainingTypes,
        bool IsValid,
        ForEachAspectValidation Validation,
        EquatableArray<DiagnosticInfo> Diagnostics
    );

    /// <summary>
    /// All the codegen-time inputs the aspect-iteration emit functions need. Built
    /// once at transform time from the classified parameters + aspect data;
    /// thereafter pure data.
    /// </summary>
    internal readonly record struct ForEachAspectValidation(
        string AspectTypeName,
        EquatableArray<AspectBufferEntry> AspectComponents,
        EquatableArray<ParameterInfoModel> CustomParameters,
        EquatableArray<SetAccessorParameterModel> SetAccessorParameters,
        EquatableArray<SetAccessorParameterModel> SetReadParameters,
        EquatableArray<SetAccessorParameterModel> SetWriteParameters,
        EquatableArray<HoistedSingletonModel> HoistedSingletons,
        EquatableArray<ParamSlot> ParameterSlots,
        bool HasEntityIndexParameter,
        bool HasEntityHandleParameter,
        bool HasAttributeTags,
        bool MatchByComponents,
        bool HasSet,
        string FirstSetTypeDisplay,
        string AttributeCriteriaChainNoWithouts,
        string AttributeCriteriaChainFull,
        EquatableArray<string> Namespaces
    )
    {
        public static ForEachAspectValidation Empty { get; } =
            new(
                string.Empty,
                EquatableArray<AspectBufferEntry>.Empty,
                EquatableArray<ParameterInfoModel>.Empty,
                EquatableArray<SetAccessorParameterModel>.Empty,
                EquatableArray<SetAccessorParameterModel>.Empty,
                EquatableArray<SetAccessorParameterModel>.Empty,
                EquatableArray<HoistedSingletonModel>.Empty,
                EquatableArray<ParamSlot>.Empty,
                false,
                false,
                false,
                false,
                false,
                string.Empty,
                string.Empty,
                string.Empty,
                EquatableArray<string>.Empty
            );
    }

    /// <summary>
    /// One slot in <see cref="ForEachAspectValidation.AspectComponents"/>, in
    /// canonical AllComponentTypes order (reads first, then writes not already in
    /// reads). <see cref="VarName"/> is precomputed via
    /// <c>ComponentTypeHelper.GetComponentVariableName</c> at transform time, since
    /// that helper reads <c>SourceGenSettingsProvider</c> via the symbol — not safe
    /// to defer to the terminal stage.
    /// </summary>
    internal readonly record struct AspectBufferEntry(
        string TypeDisplay,
        string VarName,
        bool IsWrite
    );
}
