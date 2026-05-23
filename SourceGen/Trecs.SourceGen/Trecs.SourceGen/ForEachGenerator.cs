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
    /// Source generator for <c>[ForEachEntity]</c> methods that iterate over ECS
    /// components (component-parameter shape). The aspect-parameter shape is handled
    /// by <c>ForEachEntityAspectGenerator</c>; routing is decided by
    /// <see cref="IterationAttributeRouting"/>.
    ///
    /// <para>Pipeline shape: the transform produces a value-equatable
    /// <see cref="ForEachComponentModel"/> (strings + EquatableArrays, zero symbols
    /// or syntax) and the terminal stage materializes diagnostics + emits source.
    /// The compilation's global-namespace name folds in via a lightweight
    /// value-equatable combine.</para>
    /// </summary>
    [Generator]
    public class ForEachGenerator : IIncrementalGenerator
    {
        public void Initialize(IncrementalGeneratorInitializationContext context)
        {
            var hasTrecsReference = AssemblyFilterHelper.CreateTrecsReferenceCheck(context);

            var modelsRaw = context
                .SyntaxProvider.CreateSyntaxProvider(
                    predicate: static (s, _) => IsMethodWithForEachAttribute(s),
                    transform: static (ctx, _) => BuildModel(ctx)
                )
                .Where(static m => m is not null)
                .Select(static (m, _) => m!.Value);
            var models = AssemblyFilterHelper.FilterByTrecsReference(modelsRaw, hasTrecsReference);

            // Only the global-namespace name leaves CompilationProvider — a single string,
            // value-equatable. Unrelated edits don't invalidate this combine.
            var globalNsProvider = context.CompilationProvider.Select(
                static (c, _) =>
                    PerformanceCache.GetDisplayString(c.GlobalNamespace) ?? string.Empty
            );

            var withGlobalNs = models.Combine(globalNsProvider);

            context.RegisterSourceOutput(
                withGlobalNs,
                static (spc, source) => GenerateForEachSource(spc, source.Left, source.Right)
            );
        }

        private static bool IsMethodWithForEachAttribute(SyntaxNode node)
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

        private static ForEachComponentModel? BuildModel(GeneratorSyntaxContext context)
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

            // [ForEachEntity] only routes here when the method has NO IAspect parameter.
            if (!IterationAttributeRouting.HasEntityFilter(methodSymbol))
                return null;
            if (IterationAttributeRouting.HasWrapAsJobAttribute(methodSymbol))
                return null;
            if (!IterationAttributeRouting.RoutesToComponentsGenerator(methodSymbol))
                return null;

            var classSymbol = methodSymbol.ContainingType;
            var className = classDecl.Identifier.Text;
            var methodName = methodDecl.Identifier.Text;
            var diagnostics = new List<DiagnosticInfo>();
            ForEachComponentValidation validation = ForEachComponentValidation.Empty;
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
                        "ForEach method validation",
                        ex.Message
                    )
                );
                isValid = false;
                validation = ForEachComponentValidation.Empty;
            }

            var containingTypes = SymbolAnalyzer
                .GetContainingTypeChainInfo(classSymbol)
                .ToEquatableArray();

            return new ForEachComponentModel(
                ClassName: className,
                MethodName: methodName,
                Namespace: SymbolAnalyzer.GetNamespace(classDecl),
                HintFileName: SymbolAnalyzer.GetSafeFileName(classSymbol, $"{methodName}_ForEach"),
                ContainingTypes: containingTypes,
                IsValid: isValid,
                Validation: validation,
                Diagnostics: diagnostics.ToEquatableArray()
            );
        }

        private static void GenerateForEachSource(
            SourceProductionContext context,
            ForEachComponentModel model,
            string globalNamespaceName
        )
        {
            foreach (var diag in model.Diagnostics)
                context.ReportDiagnostic(diag.ToDiagnostic());

            if (!model.IsValid)
                return;

            try
            {
                using var _timer_ = SourceGenTimer.Time("ForEachGenerator.Total");
                SourceGenLogger.Log(
                    $"[ForEachGenerator] Processing {model.ClassName}.{model.MethodName}"
                );

                var source = ErrorRecovery.TryExecute(
                    () => GenerateSource(model, globalNamespaceName),
                    context,
                    Location.None,
                    "ForEach code generation"
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
                    $"ForEach {model.ClassName}.{model.MethodName}",
                    ex
                );
            }
        }

        // ─────────────────────────────────────────────────────────────────────
        // Code emission (all from the equatable model — no symbol access)
        // ─────────────────────────────────────────────────────────────────────

        private static string GenerateSource(
            ForEachComponentModel model,
            string globalNamespaceName
        )
        {
            var v = model.Validation;

            var sb = OptimizedStringBuilder.ForAspect(v.ComponentParameters.Length);

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
                            {
                                b.AppendLine(indent, $"partial class {model.ClassName}");
                                b.AppendLine(indent, "{");
                                GenerateForEachOverloads(b, model.MethodName, v);
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
            ForEachComponentValidation v
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
                GenerateConvenienceOverload(sb, methodName, v, customArgsDecStr, customArgsCallStr);
            }

            // (2) Public dense entry — only when the attribute imposes no Set/Sets.
            if (!hasSets)
            {
                GenerateQueryBuilderEntry(sb, methodName, v, customArgsDecStr);
            }

            // (3) Public sparse entry — only when the attribute has no set baked in.
            if (!hasSets)
            {
                GenerateSparseQueryBuilderEntry(sb, methodName, v, customArgsDecStr);
            }

            // (4) Range overload for event handlers — always emitted.
            sb.AppendLine(2, GeneratedCodeAttributes.Line);
            sb.AppendLine(
                2,
                $"void {methodName}(GroupIndex __group, EntityRange __indices, WorldAccessor __world{customArgsDecStr})"
            );
            sb.AppendLine(2, "{");
            GenerateRangeIteration(sb, methodName, v, 3);
            sb.AppendLine(2, "}");
            sb.AppendLine();
        }

        private static void GenerateConvenienceOverload(
            OptimizedStringBuilder sb,
            string methodName,
            ForEachComponentValidation v,
            string customArgsDecStr,
            string customArgsCallStr
        )
        {
            sb.AppendLine(2, GeneratedCodeAttributes.Line);
            sb.AppendLine(2, $"void {methodName}(WorldAccessor __world{customArgsDecStr})");
            sb.AppendLine(2, "{");

            if (v.HasSet)
            {
                // Sparse path. Build the full SparseQueryBuilder inline.
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
            ForEachComponentValidation v,
            string customArgsDecStr
        )
        {
            sb.AppendLine(2, GeneratedCodeAttributes.Line);
            sb.AppendLine(2, $"void {methodName}(QueryBuilder __builder{customArgsDecStr})");
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
            ForEachComponentValidation v,
            string customArgsDecStr
        )
        {
            sb.AppendLine(2, GeneratedCodeAttributes.Line);
            sb.AppendLine(2, $"void {methodName}(SparseQueryBuilder __builder{customArgsDecStr})");
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

        private static string BuildUserMethodCallArgs(
            ForEachComponentValidation v,
            string worldVar,
            string entityIndexVar,
            string componentValueVarPrefix
        )
        {
            var args = new List<string>(v.ParameterSlots.Length);
            foreach (var slot in v.ParameterSlots)
            {
                switch (slot.Kind)
                {
                    case ParamSlotKind.LoopComponent:
                        var c = v.ComponentParameters[slot.Index];
                        var refOrIn = c.IsRef ? "ref" : "in";
                        args.Add($"{refOrIn} {componentValueVarPrefix}{slot.Index + 1}");
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

        private static void EmitDenseIterationBody(
            OptimizedStringBuilder sb,
            string methodName,
            ForEachComponentValidation v,
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

            for (int i = 0; i < v.ComponentParameters.Length; i++)
            {
                var param = v.ComponentParameters[i];
                var bufferSuffix = param.IsRef ? "Write" : "Read";
                sb.AppendLine(
                    indentLevel + 1,
                    $"var values{i + 1} = {worldName}.ComponentBuffer<{param.Type}>(__slice.GroupIndex).{bufferSuffix};"
                );
            }
            sb.AppendLine();

            sb.AppendLine(indentLevel + 1, "for (int __i = 0; __i < __slice.Count; __i++)");
            sb.AppendLine(indentLevel + 1, "{");

            for (int i = 0; i < v.ComponentParameters.Length; i++)
            {
                var param = v.ComponentParameters[i];
                var refKind = param.IsRef ? "ref" : "ref readonly";
                sb.AppendLine(
                    indentLevel + 2,
                    $"{refKind} var value{i + 1} = ref values{i + 1}[__i];"
                );
            }

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

        private static void EmitSparseIterationBody(
            OptimizedStringBuilder sb,
            string methodName,
            ForEachComponentValidation v,
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

            for (int i = 0; i < v.ComponentParameters.Length; i++)
            {
                var param = v.ComponentParameters[i];
                var bufferSuffix = param.IsRef ? "Write" : "Read";
                sb.AppendLine(
                    indentLevel + 1,
                    $"var values{i + 1} = {worldName}.ComponentBuffer<{param.Type}>(__slice.GroupIndex).{bufferSuffix};"
                );
            }
            sb.AppendLine();

            sb.AppendLine(indentLevel + 1, "foreach (var __i in __slice.Indices)");
            sb.AppendLine(indentLevel + 1, "{");

            for (int i = 0; i < v.ComponentParameters.Length; i++)
            {
                var param = v.ComponentParameters[i];
                var refKind = param.IsRef ? "ref" : "ref readonly";
                sb.AppendLine(
                    indentLevel + 2,
                    $"{refKind} var value{i + 1} = ref values{i + 1}[__i];"
                );
            }

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

        private static void EmitUserBodyOrCall(
            OptimizedStringBuilder sb,
            int indentLevel,
            string methodName,
            ForEachComponentValidation v,
            string worldName
        )
        {
            var callArgs = BuildUserMethodCallArgs(v, worldName, "__entityIndex", "value");
            sb.AppendLine(indentLevel, $"{methodName}({callArgs});");
        }

        private static void GenerateRangeIteration(
            OptimizedStringBuilder sb,
            string methodName,
            ForEachComponentValidation v,
            int indentLevel
        )
        {
            EmitSetAccessorDeclarations(sb, v, indentLevel, "__world");
            HoistedSingleEmitter.Emit(sb, indentLevel, worldVar: "__world", v.HoistedSingletons);

            for (int idx = 0; idx < v.ComponentParameters.Length; idx++)
            {
                var param = v.ComponentParameters[idx];
                var bufferSuffix = param.IsRef ? "Write" : "Read";
                sb.AppendLine(
                    indentLevel,
                    $"var values{idx + 1} = __world.ComponentBuffer<{param.Type}>(__group).{bufferSuffix};"
                );
            }

            sb.AppendLine();

            sb.AppendLine(
                indentLevel,
                "for (int __i = __indices.Start; __i < __indices.End; __i++)"
            );
            sb.AppendLine(indentLevel, "{");

            for (int i = 0; i < v.ComponentParameters.Length; i++)
            {
                var param = v.ComponentParameters[i];
                var refKind = param.IsRef ? "ref" : "ref readonly";
                sb.AppendLine(
                    indentLevel + 1,
                    $"{refKind} var value{i + 1} = ref values{i + 1}[__i];"
                );
            }

            EmitEntityRefDeclarations(
                sb,
                v,
                indentLevel + 1,
                indexExpr: "new EntityIndex(__i, __group)",
                worldVar: "__world"
            );

            EmitUserBodyOrCall(sb, indentLevel + 1, methodName, v, "__world");
            sb.AppendLine(indentLevel, "}");
        }

        private static void EmitSetAccessorDeclarations(
            OptimizedStringBuilder sb,
            ForEachComponentValidation v,
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
            ForEachComponentValidation v,
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
            out ForEachComponentValidation validation
        )
        {
            validation = ForEachComponentValidation.Empty;
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

            // ParameterClassifier emits already-substituted Diagnostic objects via its
            // Action<Diagnostic> callback; stash them as preformatted DiagnosticInfo so
            // the descriptor's MessageFormat isn't applied a second time.
            System.Action<Diagnostic> reportDiag = d =>
                diagnostics.Add(DiagnosticInfo.FromDiagnostic(d));

            var classified = ParameterClassifier.Classify(
                parameters,
                semanticModel,
                IterationMode.Components,
                reportDiag,
                methodName: null,
                aspectParam: null,
                isValid: ref isValid
            );
            bool hasAnyIterationParameter =
                classified.ComponentParameters.Count > 0
                || classified.HasEntityIndex
                || classified.HasEntityHandle;

            if (!hasAnyIterationParameter)
            {
                diagnostics.Add(
                    DiagnosticInfo.Create(
                        DiagnosticDescriptors.EmptyParameters,
                        methodDec.ParameterList.GetLocation()
                    )
                );
                isValid = false;
            }

            var className = classDec.Identifier.Text;
            var criteria = IterationCriteriaParser.ParseIterationAttribute(
                reportDiag,
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
            bool matchByComponents = criteria?.MatchByComponents ?? false;

            if (!isValid)
                return false;

            validation = BuildValidationResult(
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
        /// <see cref="ClassifiedParameters"/> and criteria lists. Pre-computes the
        /// QueryBuilder criteria chains and the namespace set so the codegen path
        /// touches no symbols.
        /// </summary>
        private static ForEachComponentValidation BuildValidationResult(
            ClassifiedParameters classified,
            List<ITypeSymbol> attributeTagTypes,
            List<ITypeSymbol> attributeWithoutTagTypes,
            List<ITypeSymbol> setTypes,
            bool matchByComponents
        )
        {
            var componentParams = classified
                .ComponentParameters.Select(p => new ParameterInfoModel(
                    Type: p.TypeSymbol != null
                        ? PerformanceCache.GetDisplayString(p.TypeSymbol)
                        : p.Type,
                    Name: p.Name,
                    IsRef: p.IsRef,
                    IsIn: p.IsIn
                ))
                .ToEquatableArray();

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
            // full chain. Doing this at transform time keeps the symbol references off
            // the pipeline boundary entirely.
            var componentTypes = classified.ComponentParameters.Select(p => p.TypeSymbol);
            var chainNoWithouts = QueryBuilderHelper.BuildAttributeCriteriaChain(
                attributeTagTypes,
                matchByComponents,
                componentTypes
            );
            var chainFull = QueryBuilderHelper.BuildAttributeCriteriaChain(
                attributeTagTypes,
                matchByComponents,
                classified.ComponentParameters.Select(p => p.TypeSymbol),
                attributeWithoutTagTypes
            );

            var firstSetDisplay =
                setTypes.Count > 0 ? PerformanceCache.GetDisplayString(setTypes[0]) : string.Empty;

            // Pre-collect required namespaces. Everything except the global-namespace
            // filter (we don't know that here — the consumer drops it at emit time).
            var namespaces = CollectNamespaces(
                classified,
                attributeTagTypes,
                attributeWithoutTagTypes,
                setTypes
            );

            return new ForEachComponentValidation(
                ComponentParameters: componentParams,
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

        private static EquatableArray<string> CollectNamespaces(
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

            foreach (var p in classified.ComponentParameters)
                Add(p.TypeSymbol);
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
    /// Top-level value-equatable model carried through the ForEachGenerator pipeline.
    /// Holds everything the terminal stage needs to materialize diagnostics and emit
    /// source — no Roslyn symbols, syntax, or raw Diagnostic objects.
    /// </summary>
    internal readonly record struct ForEachComponentModel(
        string ClassName,
        string MethodName,
        string Namespace,
        string HintFileName,
        EquatableArray<ContainingTypeInfo> ContainingTypes,
        bool IsValid,
        ForEachComponentValidation Validation,
        EquatableArray<DiagnosticInfo> Diagnostics
    );

    /// <summary>
    /// All the codegen-time inputs the iteration emit functions need. Built once at
    /// transform time from the classified parameters + iteration-attribute criteria;
    /// thereafter pure data.
    /// </summary>
    internal readonly record struct ForEachComponentValidation(
        EquatableArray<ParameterInfoModel> ComponentParameters,
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
        public static ForEachComponentValidation Empty { get; } =
            new(
                EquatableArray<ParameterInfoModel>.Empty,
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
    /// Value-equatable projection of <see cref="Shared.ParameterInfo"/> for use in
    /// the ForEach pipeline model.
    /// </summary>
    internal readonly record struct ParameterInfoModel(
        string Type,
        string Name,
        bool IsRef,
        bool IsIn
    );

    /// <summary>
    /// Value-equatable projection of <see cref="Shared.SetAccessorParameterInfo"/> for
    /// the ForEach pipeline model. The symbol-bearing original isn't equatable.
    /// </summary>
    internal readonly record struct SetAccessorParameterModel(
        string SetTypeArg,
        string ParamName,
        bool IsIn
    );
}
