#nullable enable

using System;
using System.Collections.Generic;
using System.Linq;
using System.Text;
using Microsoft.CodeAnalysis;
using Microsoft.CodeAnalysis.CSharp;
using Microsoft.CodeAnalysis.CSharp.Syntax;
using Trecs.SourceGen.Aspect;
using Trecs.SourceGen.Performance;
using Trecs.SourceGen.Shared;

namespace Trecs.SourceGen
{
    /// <summary>
    /// Source generator for <c>[WrapAsJob]</c> + <c>[ForEachEntity]</c> static methods on
    /// system classes. For each decorated method it emits:
    /// <list type="bullet">
    /// <item><description>A <c>[BurstCompile] partial struct _MethodName_AutoJob : IJobFor</c>
    /// with buffer fields, an <c>Execute(int i)</c> shim that constructs the aspect/components
    /// and calls the user's static method, and</description></item>
    /// <item><description><c>ScheduleParallel</c> overloads (WorldAccessor convenience +
    /// QueryBuilder dense path) that iterate groups, manage dependencies, and dispatch the
    /// job.</description></item>
    /// <item><description>A wrapper method with the same name as the user's static method
    /// (different signature) that invokes the default ScheduleParallel entry.</description></item>
    /// </list>
    /// </summary>
    [Generator]
    public class AutoJobGenerator : IIncrementalGenerator
    {
        // Forwards to the single source of truth so a future prefix change happens once.
        const string GenPrefix = FromWorldEmitter.GenPrefix;

        public void Initialize(IncrementalGeneratorInitializationContext context)
        {
            // Equatable-pipeline shape: the transform stage produces a value-equatable
            // AutoJobModel (strings + EquatableArrays — no symbols, no syntax nodes, no
            // raw Diagnostics). The terminal RegisterSourceOutput stage materializes
            // diagnostics and emits source. The compilation's global-namespace name
            // folds in via a lightweight string Combine — required by FromWorldFieldEmit
            // projection's namespace filtering, even though AutoJobGenerator's emitted
            // source uses a fixed `using`s set.
            var modelsRaw = context
                .SyntaxProvider.CreateSyntaxProvider(
                    predicate: static (s, _) => IsCandidateMethod(s),
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
                static (spc, source) => GenerateAutoJobSource(spc, source.Left, source.Right)
            );
        }

        /// <summary>
        /// Bridge from the framework's <see cref="Action{Diagnostic}"/> callback shape
        /// (used by shared helpers like <see cref="IterationCriteriaParser"/> and
        /// <see cref="InlineTagsParser"/>) into the pipeline's <see cref="DiagnosticInfo"/>
        /// accumulator. <see cref="DiagnosticInfo.FromDiagnostic"/> stashes the
        /// pre-built message so terminal-stage materialization doesn't double-format.
        /// </summary>
        static Action<Diagnostic> ToBridge(Action<DiagnosticInfo> diagnostics) =>
            d => diagnostics(DiagnosticInfo.FromDiagnostic(d));

        // ─── Predicate (syntax-level) ──────────────────────────────────────────

        /// <summary>
        /// Syntax-level predicate: the method must have both <c>[WrapAsJob]</c> and
        /// <c>[ForEachEntity]</c> attributes and be inside a class (not struct).
        /// </summary>
        static bool IsCandidateMethod(SyntaxNode node)
        {
            if (node is not MethodDeclarationSyntax method)
                return false;
            if (method.AttributeLists.Count == 0)
                return false;

            // Must be inside a class, not a struct.
            if (method.Parent is not ClassDeclarationSyntax)
                return false;

            bool hasWrapAsJob = false;
            bool hasEntityFilter = false;

            foreach (var attrList in method.AttributeLists)
            {
                foreach (var attr in attrList.Attributes)
                {
                    var name = IterationCriteriaParser.ExtractAttributeName(attr.Name.ToString());
                    if (name == TrecsAttributeNames.WrapAsJob)
                        hasWrapAsJob = true;
                    else if (name == TrecsAttributeNames.ForEachEntity)
                        hasEntityFilter = true;
                }
            }

            return hasWrapAsJob && hasEntityFilter;
        }

        // ─── Transform (syntax → data → model) ────────────────────────────────

        /// <summary>
        /// Transform stage: validates a candidate <c>[WrapAsJob]</c> method and
        /// projects the result (success or failure) into a value-equatable
        /// <see cref="AutoJobModel"/>. The terminal stage materializes diagnostics and
        /// emits source from the model.
        /// </summary>
        static AutoJobModel? BuildModel(GeneratorSyntaxContext context)
        {
            var methodDecl = (MethodDeclarationSyntax)context.Node;
            var classDecl = methodDecl.Parent as ClassDeclarationSyntax;
            if (classDecl == null)
                return null;

            var methodSymbol = context.SemanticModel.GetDeclaredSymbol(methodDecl) as IMethodSymbol;
            if (methodSymbol == null)
                return null;

            // Double-check at the semantic level that both attributes are present.
            if (!IterationAttributeRouting.HasWrapAsJobAttribute(methodSymbol))
                return null;
            if (!IterationAttributeRouting.HasEntityFilter(methodSymbol))
                return null;

            var diagnostics = new List<DiagnosticInfo>();
            Action<DiagnosticInfo> reporter = diagnostics.Add;

            AutoJobInfo? info;
            try
            {
                info = Validate(
                    reporter,
                    new AutoJobData(classDecl, methodDecl, methodSymbol),
                    context.SemanticModel
                );
            }
            catch (Exception ex)
            {
                diagnostics.Add(
                    DiagnosticInfo.Create(
                        DiagnosticDescriptors.SourceGenerationError,
                        methodDecl.GetLocation(),
                        $"AutoJob {methodSymbol.ContainingType.Name}.{methodDecl.Identifier.Text}",
                        ex.Message
                    )
                );
                info = null;
            }

            return BuildAutoJobModelFromInfo(info, methodSymbol, methodDecl, diagnostics);
        }

        // ─── Source output ─────────────────────────────────────────────────────

        /// <summary>
        /// Terminal stage: surface accumulated diagnostics, then emit source from the
        /// AutoJobModel. Skips emission when validation failed so the user sees
        /// diagnostics without a cascading CS error from a half-formed partial.
        /// </summary>
        static void GenerateAutoJobSource(
            SourceProductionContext context,
            AutoJobModel model,
            string globalNamespaceName
        )
        {
            foreach (var d in model.Diagnostics)
                context.ReportDiagnostic(d.ToDiagnostic());

            if (!model.IsValid)
                return;

            try
            {
                using var _t = SourceGenTimer.Time("AutoJobGenerator.Total");

                var source = GenerateSource(model);
                if (source == null)
                    return;

                context.AddSource(model.HintFileName, source);
                SourceGenLogger.WriteGeneratedFile(model.HintFileName, source);
            }
            catch (Exception ex)
            {
                ErrorRecovery.ReportError(
                    context,
                    Location.None,
                    $"AutoJob {model.ClassName}.{model.MethodName}",
                    ex
                );
            }
        }

        // ─── Validation ────────────────────────────────────────────────────────

        static AutoJobInfo? Validate(
            Action<DiagnosticInfo> diagnostics,
            AutoJobData data,
            SemanticModel semanticModel
        )
        {
            var methodDecl = data.MethodDecl;
            var classDecl = data.ClassDecl;
            var methodSymbol = data.MethodSymbol;
            var classSymbol = methodSymbol.ContainingType;
            var methodName = methodDecl.Identifier.Text;

            // V2: Must be static.
            if (!methodSymbol.IsStatic)
            {
                diagnostics(
                    DiagnosticInfo.Create(
                        DiagnosticDescriptors.WrapAsJobNonStatic,
                        methodDecl.Identifier.GetLocation(),
                        methodName
                    )
                );
                return null;
            }

            // V3: Class must be partial.
            if (!SymbolAnalyzer.IsPartialType(classDecl))
            {
                diagnostics(
                    DiagnosticInfo.Create(
                        DiagnosticDescriptors.NotPartialClass,
                        classDecl.Identifier.GetLocation(),
                        classSymbol.Name
                    )
                );
                return null;
            }

            // V4: Must return void.
            if (!methodSymbol.ReturnsVoid)
            {
                diagnostics(
                    DiagnosticInfo.Create(
                        DiagnosticDescriptors.InvalidReturnType,
                        methodDecl.ReturnType.GetLocation()
                    )
                );
                return null;
            }

            // V5: Must have at least one parameter.
            if (methodSymbol.Parameters.Length == 0)
            {
                diagnostics(
                    DiagnosticInfo.Create(
                        DiagnosticDescriptors.EmptyParameters,
                        methodDecl.GetLocation()
                    )
                );
                return null;
            }

            // V6: Reject jobs nested inside a generic enclosing type.
            for (var t = classSymbol.ContainingType; t != null; t = t.ContainingType)
            {
                if (t.IsGenericType)
                {
                    diagnostics(
                        DiagnosticInfo.Create(
                            DiagnosticDescriptors.JobInsideGenericOuterTypeNotSupported,
                            methodDecl.Identifier.GetLocation(),
                            methodName,
                            t.Name
                        )
                    );
                    return null;
                }
            }

            // ── Classify parameters ──

            var paramSlots = new List<AutoJobParam>();
            int bufferIndex = 0;
            bool hasAspect = false;
            bool hasEntityIndex = false;
            bool hasEntityHandle = false;
            bool hasGlobalIndex = false;
            bool hasNwa = false;
            AspectIterationData? aspectData = null;

            foreach (var param in methodSymbol.Parameters)
            {
                var paramType = param.Type;
                var paramName = param.Name;

                // [GlobalIndex] takes precedence over the type check (an int with the
                // attribute is the global index, regardless of position). Mirrors
                // JobGenerator's parameter classifier so the user-facing API reads
                // identically across the manual-job-struct and [WrapAsJob] paths.
                bool isGlobalIndex = PerformanceCache.HasAttributeByName(
                    param,
                    TrecsAttributeNames.GlobalIndex,
                    TrecsNamespaces.Trecs
                );
                if (isGlobalIndex)
                {
                    if (hasGlobalIndex)
                    {
                        diagnostics(
                            DiagnosticInfo.Create(
                                DiagnosticDescriptors.InvalidJobParameterList,
                                param.Locations.FirstOrDefault() ?? methodDecl.GetLocation(),
                                $"Method '{methodName}' has more than one [GlobalIndex] parameter — only one is allowed."
                            )
                        );
                        return null;
                    }
                    if (paramType.SpecialType != SpecialType.System_Int32)
                    {
                        diagnostics(
                            DiagnosticInfo.Create(
                                DiagnosticDescriptors.GlobalIndexParamMustBeInt,
                                param.Locations.FirstOrDefault() ?? methodDecl.GetLocation(),
                                paramName,
                                methodName,
                                paramType.ToDisplayString()
                            )
                        );
                        return null;
                    }
                    hasGlobalIndex = true;
                    paramSlots.Add(AutoJobParam.GlobalIndex(paramName));
                    continue;
                }

                // Check for [PassThroughArgument] first — it overrides all auto-detection.
                bool isPassThrough = PerformanceCache.HasAttributeByName(
                    param,
                    TrecsAttributeNames.PassThroughArgument,
                    TrecsNamespaces.Trecs
                );

                if (isPassThrough)
                {
                    // Validate: no ref/out on pass-through.
                    if (param.RefKind == RefKind.Ref || param.RefKind == RefKind.Out)
                    {
                        diagnostics(
                            DiagnosticInfo.Create(
                                DiagnosticDescriptors.WrapAsJobRefPassThrough,
                                param.Locations.FirstOrDefault() ?? methodDecl.GetLocation(),
                                methodName,
                                paramName
                            )
                        );
                        return null;
                    }

                    // Validate: must be unmanaged.
                    if (paramType.IsReferenceType)
                    {
                        diagnostics(
                            DiagnosticInfo.Create(
                                DiagnosticDescriptors.WrapAsJobManagedPassThrough,
                                param.Locations.FirstOrDefault() ?? methodDecl.GetLocation(),
                                methodName,
                                paramName,
                                PerformanceCache.GetDisplayString(paramType)
                            )
                        );
                        return null;
                    }

                    paramSlots.Add(
                        AutoJobParam.PassThrough(
                            paramType,
                            paramName,
                            PerformanceCache.GetDisplayString(paramType)
                        )
                    );
                    continue;
                }

                // [SingleEntity]-marked params must not be claimed by ANY of the
                // accepting classifications below (NativeWorldAccessor, NativeSetRead/Write,
                // EntityIndex, IAspect, IEntityComponent). Without this guard a user who
                // accidentally writes e.g. `[SingleEntity] in NativeWorldAccessor` would
                // get the param silently classified as a NativeWorldAccessor with the
                // [SingleEntity] attribute dropped on the floor. Forbidden classifications
                // (WorldAccessor, SetAccessor) still error out as before.
                bool paramHasSingleEntity = PerformanceCache.HasAttributeByName(
                    param,
                    TrecsAttributeNames.SingleEntity,
                    TrecsNamespaces.Trecs
                );

                // Check for WorldAccessor (forbidden on [WrapAsJob]).
                if (SymbolAnalyzer.IsExactType(paramType, "WorldAccessor", TrecsNamespaces.Trecs))
                {
                    diagnostics(
                        DiagnosticInfo.Create(
                            DiagnosticDescriptors.WrapAsJobWorldAccessorParam,
                            param.Locations.FirstOrDefault() ?? methodDecl.GetLocation(),
                            methodName
                        )
                    );
                    return null;
                }

                // Check for NativeWorldAccessor.
                if (
                    !paramHasSingleEntity
                    && SymbolAnalyzer.IsExactType(
                        paramType,
                        "NativeWorldAccessor",
                        TrecsNamespaces.Trecs
                    )
                )
                {
                    if (param.RefKind != RefKind.In)
                    {
                        diagnostics(
                            DiagnosticInfo.Create(
                                DiagnosticDescriptors.InvalidParameterModifiers,
                                param.Locations.FirstOrDefault() ?? methodDecl.GetLocation(),
                                paramName
                            )
                        );
                        return null;
                    }
                    if (hasNwa)
                    {
                        diagnostics(
                            DiagnosticInfo.Create(
                                DiagnosticDescriptors.DuplicateLoopParameter,
                                param.Locations.FirstOrDefault() ?? methodDecl.GetLocation(),
                                paramName,
                                "NativeWorldAccessor"
                            )
                        );
                        return null;
                    }
                    hasNwa = true;
                    paramSlots.Add(AutoJobParam.NativeWorldAccessor(paramName));
                    continue;
                }

                // Check for NativeSetRead<T>.
                if (
                    !paramHasSingleEntity
                    && paramType is INamedTypeSymbol namedNsr
                    && namedNsr.Name == "NativeSetRead"
                    && namedNsr.TypeArguments.Length == 1
                    && PerformanceCache.GetDisplayString(namedNsr.ContainingNamespace) == "Trecs"
                )
                {
                    if (param.RefKind != RefKind.In)
                    {
                        diagnostics(
                            DiagnosticInfo.Create(
                                DiagnosticDescriptors.InvalidParameterModifiers,
                                param.Locations.FirstOrDefault() ?? methodDecl.GetLocation(),
                                paramName
                            )
                        );
                        return null;
                    }
                    var setTypeArgSymbol = namedNsr.TypeArguments[0];
                    var setTypeArg = PerformanceCache.GetDisplayString(setTypeArgSymbol);
                    paramSlots.Add(
                        AutoJobParam.NativeSetRead(paramName, setTypeArg, setTypeArgSymbol)
                    );
                    continue;
                }

                // Check for NativeSetCommandBuffer<T>.
                if (
                    !paramHasSingleEntity
                    && paramType is INamedTypeSymbol namedNsw
                    && namedNsw.Name == "NativeSetCommandBuffer"
                    && namedNsw.TypeArguments.Length == 1
                    && PerformanceCache.GetDisplayString(namedNsw.ContainingNamespace) == "Trecs"
                )
                {
                    if (param.RefKind != RefKind.In)
                    {
                        diagnostics(
                            DiagnosticInfo.Create(
                                DiagnosticDescriptors.InvalidParameterModifiers,
                                param.Locations.FirstOrDefault() ?? methodDecl.GetLocation(),
                                paramName
                            )
                        );
                        return null;
                    }
                    var setTypeArgSymbol = namedNsw.TypeArguments[0];
                    var setTypeArg = PerformanceCache.GetDisplayString(setTypeArgSymbol);
                    paramSlots.Add(
                        AutoJobParam.NativeSetCommandBuffer(paramName, setTypeArg, setTypeArgSymbol)
                    );
                    continue;
                }

                // Check for SetAccessor<T> / SetRead<T> / SetWrite<T> (main-thread only — forbidden in [WrapAsJob]).
                if (
                    paramType is INamedTypeSymbol namedSa
                    && (
                        namedSa.Name == "SetAccessor"
                        || namedSa.Name == "SetRead"
                        || namedSa.Name == "SetWrite"
                    )
                    && namedSa.TypeArguments.Length == 1
                    && PerformanceCache.GetDisplayString(namedSa.ContainingNamespace) == "Trecs"
                )
                {
                    var saTypeArg = PerformanceCache.GetDisplayString(namedSa.TypeArguments[0]);
                    diagnostics(
                        DiagnosticInfo.Create(
                            DiagnosticDescriptors.SetAccessorNotAllowedInJob,
                            param.Locations.FirstOrDefault() ?? methodDecl.GetLocation(),
                            paramName,
                            saTypeArg
                        )
                    );
                    return null;
                }

                // Check for EntityIndex.
                if (
                    !paramHasSingleEntity
                    && SymbolAnalyzer.IsExactType(paramType, "EntityIndex", TrecsNamespaces.Trecs)
                )
                {
                    if (param.RefKind != RefKind.None)
                    {
                        diagnostics(
                            DiagnosticInfo.Create(
                                DiagnosticDescriptors.ParameterMustBeByValue,
                                param.Locations.FirstOrDefault() ?? methodDecl.GetLocation(),
                                paramName
                            )
                        );
                        return null;
                    }
                    if (hasEntityIndex)
                    {
                        diagnostics(
                            DiagnosticInfo.Create(
                                DiagnosticDescriptors.DuplicateLoopParameter,
                                param.Locations.FirstOrDefault() ?? methodDecl.GetLocation(),
                                paramName,
                                "EntityIndex"
                            )
                        );
                        return null;
                    }
                    hasEntityIndex = true;
                    paramSlots.Add(AutoJobParam.EntityIndex(paramName));
                    continue;
                }

                // EntityHandle: per-iteration handle materialized from a hidden
                // NativeEntityHandleBuffer field at no extra cost (single buffered read,
                // no dictionary lookup).
                if (
                    !paramHasSingleEntity
                    && SymbolAnalyzer.IsExactType(paramType, "EntityHandle", TrecsNamespaces.Trecs)
                )
                {
                    if (param.RefKind != RefKind.None)
                    {
                        diagnostics(
                            DiagnosticInfo.Create(
                                DiagnosticDescriptors.ParameterMustBeByValue,
                                param.Locations.FirstOrDefault() ?? methodDecl.GetLocation(),
                                paramName
                            )
                        );
                        return null;
                    }
                    if (hasEntityHandle)
                    {
                        diagnostics(
                            DiagnosticInfo.Create(
                                DiagnosticDescriptors.DuplicateLoopParameter,
                                param.Locations.FirstOrDefault() ?? methodDecl.GetLocation(),
                                paramName,
                                "EntityHandle"
                            )
                        );
                        return null;
                    }
                    hasEntityHandle = true;
                    paramSlots.Add(AutoJobParam.EntityHandle(paramName));
                    continue;
                }

                // Check for IAspect. [SingleEntity]-marked aspect params skip the
                // iteration-target classifier — they're hoisted out of the loop.
                // (paramHasSingleEntity is hoisted further up the method, before the
                // accepting-classifications, so all of them honor it uniformly.)
                if (
                    !paramHasSingleEntity
                    && SymbolAnalyzer.ImplementsInterface(
                        paramType,
                        "IAspect",
                        TrecsNamespaces.Trecs
                    )
                )
                {
                    if (param.RefKind == RefKind.Ref)
                    {
                        diagnostics(
                            DiagnosticInfo.Create(
                                DiagnosticDescriptors.AspectParamMustBeIn,
                                param.Locations.FirstOrDefault() ?? methodDecl.GetLocation(),
                                paramName
                            )
                        );
                        return null;
                    }
                    if (param.RefKind != RefKind.In)
                    {
                        diagnostics(
                            DiagnosticInfo.Create(
                                DiagnosticDescriptors.InvalidParameterModifiers,
                                param.Locations.FirstOrDefault() ?? methodDecl.GetLocation(),
                                paramName
                            )
                        );
                        return null;
                    }
                    if (hasAspect)
                    {
                        diagnostics(
                            DiagnosticInfo.Create(
                                DiagnosticDescriptors.DuplicateLoopParameter,
                                param.Locations.FirstOrDefault() ?? methodDecl.GetLocation(),
                                paramName,
                                "aspect"
                            )
                        );
                        return null;
                    }
                    hasAspect = true;

                    var aspectNamedType = paramType as INamedTypeSymbol;
                    if (aspectNamedType == null)
                    {
                        diagnostics(
                            DiagnosticInfo.Create(
                                DiagnosticDescriptors.CouldNotResolveSymbol,
                                param.Locations.FirstOrDefault() ?? methodDecl.GetLocation(),
                                paramName
                            )
                        );
                        return null;
                    }

                    var parsed = AspectAttributeParser.ParseAspectData(aspectNamedType);
                    if (parsed == null)
                        return null;

                    var allComponentTypes = new List<ITypeSymbol>();
                    allComponentTypes.AddRange(parsed.ReadTypes);
                    allComponentTypes.AddRange(parsed.WriteTypes);
                    allComponentTypes = PerformanceCache
                        .GetDistinctTypes(allComponentTypes)
                        .ToList();

                    aspectData = new AspectIterationData(
                        PerformanceCache.GetDisplayString(paramType),
                        aspectNamedType,
                        allComponentTypes,
                        parsed.ReadTypes.ToList(),
                        parsed.WriteTypes.ToList()
                    );

                    paramSlots.Add(
                        AutoJobParam.Aspect(
                            paramType,
                            paramName,
                            PerformanceCache.GetDisplayString(paramType)
                        )
                    );
                    continue;
                }

                // Check for IEntityComponent. [SingleEntity] params with component types
                // are hoisted out of the loop and handled at the [SingleEntity] block —
                // skip them here.
                if (
                    !paramHasSingleEntity
                    && SymbolAnalyzer.ImplementsInterface(
                        paramType,
                        "IEntityComponent",
                        TrecsNamespaces.Trecs
                    )
                )
                {
                    if (hasAspect)
                    {
                        // Mixed aspect and component params are not supported.
                        diagnostics(
                            DiagnosticInfo.Create(
                                DiagnosticDescriptors.MixedAspectAndComponentParams,
                                param.Locations.FirstOrDefault() ?? methodDecl.GetLocation(),
                                methodName,
                                paramName,
                                PerformanceCache.GetDisplayString(paramType)
                            )
                        );
                        return null;
                    }

                    bool isRef = param.RefKind == RefKind.Ref;
                    bool isIn = param.RefKind == RefKind.In;
                    if (!isRef && !isIn)
                    {
                        diagnostics(
                            DiagnosticInfo.Create(
                                DiagnosticDescriptors.ComponentParameterMustBeInOrRef,
                                param.Locations.FirstOrDefault() ?? methodDecl.GetLocation(),
                                paramName
                            )
                        );
                        return null;
                    }

                    paramSlots.Add(
                        AutoJobParam.Component(
                            paramType,
                            paramName,
                            isRef,
                            bufferIndex,
                            PerformanceCache.GetDisplayString(paramType)
                        )
                    );
                    bufferIndex++;
                    continue;
                }

                // [SingleEntity] parameter — hoists a singleton out of the iteration.
                // The accepting classifications above all skip this kind of param so we're
                // the only place that consumes it.
                if (paramHasSingleEntity)
                {
                    bool hasFromWorldOnSE = PerformanceCache.HasAttributeByName(
                        param,
                        TrecsAttributeNames.FromWorld,
                        TrecsNamespaces.Trecs
                    );
                    if (hasFromWorldOnSE)
                    {
                        diagnostics(
                            DiagnosticInfo.Create(
                                DiagnosticDescriptors.SingleEntityConflictingAttributes,
                                param.Locations.FirstOrDefault() ?? methodDecl.GetLocation(),
                                paramName,
                                "FromWorld"
                            )
                        );
                        return null;
                    }

                    var seParamLoc = param.Locations.FirstOrDefault() ?? methodDecl.GetLocation();
                    var seTagTypes = InlineTagsParser.ParseFromSymbol(
                        param,
                        "SingleEntity",
                        seParamLoc,
                        paramName,
                        ToBridge(diagnostics)
                    );
                    if (seTagTypes == null)
                        return null;
                    if (seTagTypes.Count == 0)
                    {
                        diagnostics(
                            DiagnosticInfo.Create(
                                DiagnosticDescriptors.SingleEntityRequiresInlineTags,
                                seParamLoc,
                                paramName
                            )
                        );
                        return null;
                    }

                    bool seIsAspect = SymbolAnalyzer.ImplementsInterface(
                        paramType,
                        "IAspect",
                        TrecsNamespaces.Trecs
                    );
                    bool seIsComponent = paramType.AllInterfaces.Any(i =>
                        i.Name == "IEntityComponent"
                    );

                    if (!seIsAspect && !seIsComponent)
                    {
                        diagnostics(
                            DiagnosticInfo.Create(
                                DiagnosticDescriptors.SingleEntityWrongType,
                                param.Locations.FirstOrDefault() ?? methodDecl.GetLocation(),
                                paramName,
                                PerformanceCache.GetDisplayString(paramType)
                            )
                        );
                        return null;
                    }

                    if (seIsAspect)
                    {
                        if (param.RefKind != RefKind.In)
                        {
                            diagnostics(
                                DiagnosticInfo.Create(
                                    DiagnosticDescriptors.SingleEntityWrongModifier,
                                    param.Locations.FirstOrDefault() ?? methodDecl.GetLocation(),
                                    paramName
                                )
                            );
                            return null;
                        }
                        if (paramType is not INamedTypeSymbol seAspectType)
                        {
                            diagnostics(
                                DiagnosticInfo.Create(
                                    DiagnosticDescriptors.CouldNotResolveSymbol,
                                    param.Locations.FirstOrDefault() ?? methodDecl.GetLocation(),
                                    paramName
                                )
                            );
                            return null;
                        }
                        var seAspectData = AspectAttributeParser.ParseAspectData(seAspectType);
                        paramSlots.Add(
                            AutoJobParam.SingleEntityAspect(
                                paramType,
                                paramName,
                                PerformanceCache.GetDisplayString(paramType),
                                seTagTypes,
                                seAspectData
                            )
                        );
                        continue;
                    }

                    // Component-typed singleton.
                    bool seIsRef = param.RefKind == RefKind.Ref;
                    bool seIsIn = param.RefKind == RefKind.In;
                    if (!seIsRef && !seIsIn)
                    {
                        diagnostics(
                            DiagnosticInfo.Create(
                                DiagnosticDescriptors.SingleEntityWrongModifier,
                                param.Locations.FirstOrDefault() ?? methodDecl.GetLocation(),
                                paramName
                            )
                        );
                        return null;
                    }
                    paramSlots.Add(
                        AutoJobParam.SingleEntityComponent(
                            paramType,
                            paramName,
                            PerformanceCache.GetDisplayString(paramType),
                            isRef: seIsRef,
                            tagTypes: seTagTypes
                        )
                    );
                    continue;
                }

                // Check for [FromWorld] parameter.
                bool isFromWorld = PerformanceCache.HasAttributeByName(
                    param,
                    TrecsAttributeNames.FromWorld,
                    TrecsNamespaces.Trecs
                );

                if (isFromWorld)
                {
                    // Must be 'in' modifier.
                    if (param.RefKind != RefKind.In)
                    {
                        diagnostics(
                            DiagnosticInfo.Create(
                                DiagnosticDescriptors.InvalidParameterModifiers,
                                param.Locations.FirstOrDefault() ?? methodDecl.GetLocation(),
                                paramName
                            )
                        );
                        return null;
                    }

                    var namedType = paramType as INamedTypeSymbol;
                    if (namedType == null)
                    {
                        diagnostics(
                            DiagnosticInfo.Create(
                                DiagnosticDescriptors.CouldNotResolveSymbol,
                                param.Locations.FirstOrDefault() ?? methodDecl.GetLocation(),
                                paramName
                            )
                        );
                        return null;
                    }

                    var fwKind = FromWorldClassifier.Classify(namedType);

                    // Reject types that already have first-class [WrapAsJob] support or
                    // are otherwise unsupported on [WrapAsJob] parameters.
                    if (
                        fwKind == FromWorldFieldKind.Unsupported
                        || fwKind == FromWorldFieldKind.NativeWorldAccessor
                        || fwKind == FromWorldFieldKind.NativeSetRead
                        || fwKind == FromWorldFieldKind.NativeSetCommandBuffer
                        || fwKind == FromWorldFieldKind.NativeComponentRead
                        || fwKind == FromWorldFieldKind.NativeComponentWrite
                    )
                    {
                        diagnostics(
                            DiagnosticInfo.Create(
                                DiagnosticDescriptors.FromWorldUnsupportedOnWrapAsJob,
                                param.Locations.FirstOrDefault() ?? methodDecl.GetLocation(),
                                paramName,
                                PerformanceCache.GetDisplayString(paramType)
                            )
                        );
                        return null;
                    }

                    // Parse inline Tag/Tags from the [FromWorld] attribute on this param.
                    List<ITypeSymbol>? inlineTagTypes = null;
                    foreach (var attr in PerformanceCache.GetAttributes(param))
                    {
                        if (attr.AttributeClass?.Name != TrecsAttributeNames.FromWorld)
                            continue;

                        var tagTypes = InlineTagsParser.Parse(
                            attr,
                            param.Locations.FirstOrDefault() ?? methodDecl.GetLocation(),
                            paramName,
                            "FromWorld",
                            ToBridge(diagnostics)
                        );
                        if (tagTypes == null)
                            return null;
                        if (tagTypes.Count > 0)
                            inlineTagTypes = tagTypes;
                        break;
                    }

                    // [FromWorld] on [WrapAsJob] requires inline tags for types that
                    // need group resolution — the generated wrapper has no way to
                    // accept runtime TagSets.
                    bool needsGroupResolution =
                        fwKind == FromWorldFieldKind.NativeFactory
                        || fwKind == FromWorldFieldKind.NativeComponentLookupRead
                        || fwKind == FromWorldFieldKind.NativeComponentLookupWrite
                        || fwKind == FromWorldFieldKind.NativeComponentBufferRead
                        || fwKind == FromWorldFieldKind.NativeComponentBufferWrite
                        || fwKind == FromWorldFieldKind.GroupIndex
                        || fwKind == FromWorldFieldKind.NativeEntitySetIndices
                        || fwKind == FromWorldFieldKind.NativeEntityHandleBuffer;

                    if (needsGroupResolution && inlineTagTypes == null)
                    {
                        diagnostics(
                            DiagnosticInfo.Create(
                                DiagnosticDescriptors.FromWorldRequiresInlineTagsOnWrapAsJob,
                                param.Locations.FirstOrDefault() ?? methodDecl.GetLocation(),
                                paramName
                            )
                        );
                        return null;
                    }

                    // For NativeFactory, parse aspect data for dep tracking.
                    AspectAttributeData? fwAspectData = null;
                    if (fwKind == FromWorldFieldKind.NativeFactory)
                    {
                        var aspectSymbol = namedType.ContainingType;
                        if (aspectSymbol != null)
                        {
                            fwAspectData = AspectAttributeParser.ParseAspectData(aspectSymbol);
                        }
                    }

                    ITypeSymbol? genericArg = namedType.IsGenericType
                        ? namedType.TypeArguments[0]
                        : null;

                    // Use the generated struct field name as FieldName so shared emitters
                    // can reference it as __trecs_job.{FieldName}.
                    var fwFieldName = FromWorldEmitter.JobFieldPrefix + "fw_" + paramName;

                    var fromWorldInfo = new FromWorldFieldInfo(
                        fwFieldName,
                        fwKind,
                        namedType,
                        genericArg,
                        fwAspectData,
                        inlineTagTypes
                    );

                    paramSlots.Add(
                        AutoJobParam.FromWorld(
                            paramType,
                            paramName,
                            PerformanceCache.GetDisplayString(paramType),
                            fromWorldInfo
                        )
                    );
                    continue;
                }

                // Unrecognized parameter type without [PassThroughArgument] or [FromWorld].
                diagnostics(
                    DiagnosticInfo.Create(
                        DiagnosticDescriptors.InvalidParameterList,
                        param.Locations.FirstOrDefault() ?? methodDecl.GetLocation(),
                        $"Parameter '{paramName}' of type '{PerformanceCache.GetDisplayString(paramType)}' is not recognized. "
                            + "Expected: IAspect (in), IEntityComponent (in/ref), EntityHandle, EntityIndex, NativeWorldAccessor, NativeSetRead<T>, NativeSetCommandBuffer<T>, [PassThroughArgument], or [FromWorld]."
                    )
                );
                return null;
            }

            // Must have at least one iteration target (aspect or component).
            bool hasComponents = paramSlots.Any(p => p.Role == AutoJobParamRole.Component);
            if (!hasAspect && !hasComponents)
            {
                diagnostics(
                    DiagnosticInfo.Create(
                        DiagnosticDescriptors.EmptyParameters,
                        methodDecl.GetLocation()
                    )
                );
                return null;
            }

            // Cannot mix aspect and component params (already checked above, but belt-and-suspenders).
            if (hasAspect && hasComponents)
            {
                var offending = paramSlots.First(p => p.Role == AutoJobParamRole.Component);
                diagnostics(
                    DiagnosticInfo.Create(
                        DiagnosticDescriptors.MixedAspectAndComponentParams,
                        methodDecl.GetLocation(),
                        methodName,
                        offending.Name,
                        offending.TypeDisplay
                    )
                );
                return null;
            }

            // ── Parse [ForEachEntity] criteria ──

            var criteria = IterationCriteriaParser.ParseIterationAttribute(
                ToBridge(diagnostics),
                methodDecl,
                methodSymbol,
                classSymbol.Name
            );
            if (criteria == null)
                return null;

            // [WrapAsJob] requires at least one criterion (Tags, MatchByComponents, or Set)
            // so the generated ScheduleParallel has a concrete query to run.
            bool hasAnyCriteria =
                criteria.TagTypes.Count > 0
                || criteria.MatchByComponents
                || criteria.SetTypes.Count > 0;
            if (!hasAnyCriteria)
            {
                diagnostics(
                    DiagnosticInfo.Create(
                        DiagnosticDescriptors.WrapAsJobEmptyCriteria,
                        methodDecl.Identifier.GetLocation(),
                        methodName
                    )
                );
                return null;
            }

            // V8: [WrapAsJob] method named "Execute" on an ISystem class must not have
            // PassThrough params — the generated wrapper must match void Execute().
            if (
                methodName == "Execute"
                && paramSlots.Any(p => p.Role == AutoJobParamRole.PassThrough)
                && classSymbol.AllInterfaces.Any(i =>
                    i.Name == "ISystem"
                    && SymbolAnalyzer.IsInNamespace(i.ContainingNamespace, "Trecs")
                )
            )
            {
                diagnostics(
                    DiagnosticInfo.Create(
                        DiagnosticDescriptors.WrapAsJobExecutePassThrough,
                        methodDecl.Identifier.GetLocation(),
                        methodName
                    )
                );
                return null;
            }

            var iterKind = hasAspect
                ? AutoJobIterationKind.Aspect
                : AutoJobIterationKind.Components;

            return new AutoJobInfo(
                classSymbol,
                classDecl,
                methodSymbol,
                methodDecl,
                methodName,
                iterKind,
                paramSlots,
                aspectData,
                criteria
            );
        }

        // ─── Transform → Model projection ──────────────────────────────────────

        /// <summary>
        /// Projects the symbol-bearing <see cref="AutoJobInfo"/> into the value-equatable
        /// <see cref="AutoJobModel"/>. Called at the transform-stage boundary while
        /// symbols are still alive; everything downstream consumes only the model.
        /// </summary>
        static AutoJobModel BuildAutoJobModelFromInfo(
            AutoJobInfo? info,
            IMethodSymbol methodSymbol,
            MethodDeclarationSyntax methodDecl,
            List<DiagnosticInfo> diagnostics
        )
        {
            const string globalNamespaceName = "";

            var classSymbol = methodSymbol.ContainingType;
            var className = classSymbol.Name;
            var ns = PerformanceCache.GetDisplayString(classSymbol.ContainingNamespace);
            var containingTypes = SymbolAnalyzer
                .GetContainingTypeChainInfo(classSymbol)
                .ToEquatableArray();
            var methodName = methodDecl.Identifier.Text;
            var hintFileName = SymbolAnalyzer.GetSafeFileName(classSymbol, $"{methodName}_AutoJob");

            if (info == null)
            {
                return new AutoJobModel(
                    ClassName: className,
                    Namespace: ns,
                    ContainingTypes: containingTypes,
                    HintFileName: hintFileName,
                    MethodName: methodName,
                    IsOnSystemClass: false,
                    IterKind: AutoJobIterationKindModel.Components,
                    Params: EquatableArray<AutoJobParamModel>.Empty,
                    AspectData: AutoJobAspectModel.Empty,
                    Criteria: new IterationCriteriaModel(
                        TagTypeDisplays: EquatableArray<string>.Empty,
                        WithoutTagTypeDisplays: EquatableArray<string>.Empty,
                        SetTypeDisplays: EquatableArray<string>.Empty,
                        MatchByComponents: false
                    ),
                    AttributeCriteriaChain: string.Empty,
                    FromWorldFields: EquatableArray<FromWorldFieldEmitModel>.Empty,
                    SingleEntityFields: EquatableArray<SingleEntityEmitTargetModel>.Empty,
                    AdditionalUsings: EquatableArray<string>.Empty,
                    IsValid: false,
                    Diagnostics: diagnostics.ToEquatableArray()
                );
            }

            // Build the FromWorld emits and SingleEntity targets as flat arrays in
            // param-declaration order, then point each param model at its slot via
            // FromWorldIndex / SingleEntityIndex.
            var fromWorldEmits = new List<FromWorldFieldEmitModel>();
            var singleEntityTargets = new List<SingleEntityEmitTargetModel>();
            var paramModels = new AutoJobParamModel[info.Params.Count];
            var additionalUsings = new HashSet<string>();

            for (int i = 0; i < info.Params.Count; i++)
            {
                var p = info.Params[i];
                int fwIdx = -1;
                int seIdx = -1;
                bool seAspectHasWrites = false;

                switch (p.Role)
                {
                    case AutoJobParamRole.NativeSetRead:
                    case AutoJobParamRole.NativeSetCommandBuffer:
                        if (p.SetTypeArgSymbol != null)
                        {
                            AddNamespace(additionalUsings, p.SetTypeArgSymbol.ContainingNamespace);
                            if (p.SetTypeArgSymbol.ContainingType != null)
                                AddNamespace(
                                    additionalUsings,
                                    p.SetTypeArgSymbol.ContainingType.ContainingNamespace
                                );
                        }
                        break;

                    case AutoJobParamRole.FromWorld:
                        if (p.Type != null)
                        {
                            AddNamespace(additionalUsings, p.Type.ContainingNamespace);
                            if (p.Type is INamedTypeSymbol { ContainingType: { } ct })
                                AddNamespace(additionalUsings, ct.ContainingNamespace);
                        }
                        var fwInfo = p.FromWorldInfo!;
                        if (fwInfo.InlineTagTypes != null)
                        {
                            foreach (var tagType in fwInfo.InlineTagTypes)
                                AddNamespace(additionalUsings, tagType.ContainingNamespace);
                        }
                        if (fwInfo.GenericArgument != null)
                            AddNamespace(
                                additionalUsings,
                                fwInfo.GenericArgument.ContainingNamespace
                            );

                        var fwEmit = FromWorldFieldEmit.Build(fwInfo, suppressScheduleParam: true);
                        fwIdx = fromWorldEmits.Count;
                        fromWorldEmits.Add(
                            FromWorldFieldEmitModel.From(fwEmit, globalNamespaceName)
                        );
                        break;

                    case AutoJobParamRole.SingleEntityAspect:
                        seIdx = singleEntityTargets.Count;
                        seAspectHasWrites =
                            p.SingleEntityAspectData != null
                            && p.SingleEntityAspectData.WriteTypes.Length > 0;
                        singleEntityTargets.Add(ProjectSingleEntityParam(p, globalNamespaceName));
                        break;

                    case AutoJobParamRole.SingleEntityComponentRead:
                    case AutoJobParamRole.SingleEntityComponentWrite:
                        seIdx = singleEntityTargets.Count;
                        singleEntityTargets.Add(ProjectSingleEntityParam(p, globalNamespaceName));
                        break;
                }

                paramModels[i] = new AutoJobParamModel(
                    Role: ProjectRole(p.Role),
                    Name: p.Name,
                    TypeDisplay: p.TypeDisplay,
                    IsRef: p.IsRef,
                    BufferIndex: p.BufferIndex,
                    SetTypeArg: p.SetTypeArg ?? string.Empty,
                    FromWorldIndex: fwIdx,
                    SingleEntityIndex: seIdx,
                    SingleEntityAspectHasWrites: seAspectHasWrites
                );
            }

            // Aspect data (slim — components + name only).
            var aspectModel = AutoJobAspectModel.Empty;
            if (info.IterKind == AutoJobIterationKind.Aspect)
            {
                var ai = info.AspectData!;
                var entries = new AspectBufferEntry[ai.ComponentTypes.Count];
                for (int i = 0; i < ai.ComponentTypes.Count; i++)
                {
                    var t = ai.ComponentTypes[i];
                    entries[i] = new AspectBufferEntry(
                        TypeDisplay: PerformanceCache.GetDisplayString(t),
                        VarName: ComponentTypeHelper.GetComponentVariableName(t),
                        IsWrite: !ai.IsReadOnly(t)
                    );
                }
                aspectModel = new AutoJobAspectModel(
                    AspectTypeName: ai.AspectTypeName,
                    Components: new EquatableArray<AspectBufferEntry>(entries)
                );
            }

            // Attribute-criteria chain — precomputed at the transform boundary so the
            // emitter doesn't need ITypeSymbols.
            IEnumerable<ITypeSymbol> componentTypes =
                info.IterKind == AutoJobIterationKind.Aspect
                    ? (IEnumerable<ITypeSymbol>)info.AspectData!.ComponentTypes
                    : info
                        .Params.Where(p => p.Role == AutoJobParamRole.Component)
                        .Select(p => p.Type!);
            var attributeChain = QueryBuilderHelper.BuildAttributeCriteriaChain(
                info.Criteria.TagTypes,
                info.Criteria.MatchByComponents,
                componentTypes,
                info.Criteria.WithoutTagTypes
            );

            bool isOnSystemClass = classSymbol.AllInterfaces.Any(i =>
                i.Name == "ISystem" && SymbolAnalyzer.IsInNamespace(i.ContainingNamespace, "Trecs")
            );

            return new AutoJobModel(
                ClassName: className,
                Namespace: ns,
                ContainingTypes: containingTypes,
                HintFileName: hintFileName,
                MethodName: methodName,
                IsOnSystemClass: isOnSystemClass,
                IterKind: info.IterKind == AutoJobIterationKind.Aspect
                    ? AutoJobIterationKindModel.Aspect
                    : AutoJobIterationKindModel.Components,
                Params: new EquatableArray<AutoJobParamModel>(paramModels),
                AspectData: aspectModel,
                Criteria: JobModelBuilders.BuildCriteria(info.Criteria),
                AttributeCriteriaChain: attributeChain,
                FromWorldFields: fromWorldEmits.ToEquatableArray(),
                SingleEntityFields: singleEntityTargets.ToEquatableArray(),
                AdditionalUsings: additionalUsings.OrderBy(u => u).ToEquatableArray(),
                IsValid: true,
                Diagnostics: diagnostics.ToEquatableArray()
            );
        }

        static void AddNamespace(HashSet<string> usings, INamespaceSymbol? ns)
        {
            if (ns == null)
                return;
            var s = PerformanceCache.GetDisplayString(ns);
            if (!string.IsNullOrEmpty(s) && s != "<global namespace>")
                usings.Add(s);
        }

        static AutoJobParamRoleKind ProjectRole(AutoJobParamRole r) =>
            r switch
            {
                AutoJobParamRole.Aspect => AutoJobParamRoleKind.Aspect,
                AutoJobParamRole.Component => AutoJobParamRoleKind.Component,
                AutoJobParamRole.EntityIndex => AutoJobParamRoleKind.EntityIndex,
                AutoJobParamRole.EntityHandle => AutoJobParamRoleKind.EntityHandle,
                AutoJobParamRole.GlobalIndex => AutoJobParamRoleKind.GlobalIndex,
                AutoJobParamRole.NativeWorldAccessor => AutoJobParamRoleKind.NativeWorldAccessor,
                AutoJobParamRole.PassThrough => AutoJobParamRoleKind.PassThrough,
                AutoJobParamRole.NativeSetRead => AutoJobParamRoleKind.NativeSetRead,
                AutoJobParamRole.NativeSetCommandBuffer =>
                    AutoJobParamRoleKind.NativeSetCommandBuffer,
                AutoJobParamRole.FromWorld => AutoJobParamRoleKind.FromWorld,
                AutoJobParamRole.SingleEntityAspect => AutoJobParamRoleKind.SingleEntityAspect,
                AutoJobParamRole.SingleEntityComponentRead =>
                    AutoJobParamRoleKind.SingleEntityComponentRead,
                AutoJobParamRole.SingleEntityComponentWrite =>
                    AutoJobParamRoleKind.SingleEntityComponentWrite,
                _ => AutoJobParamRoleKind.Component,
            };

        static SingleEntityEmitTargetModel ProjectSingleEntityParam(
            AutoJobParam p,
            string globalNamespaceName
        )
        {
            string aspectTypeDisplay =
                p.Role == AutoJobParamRole.SingleEntityAspect ? p.TypeDisplay : string.Empty;
            string componentTypeDisplay = p.Role
                is AutoJobParamRole.SingleEntityComponentRead
                    or AutoJobParamRole.SingleEntityComponentWrite
                ? p.TypeDisplay
                : string.Empty;
            string lhs = p.Role switch
            {
                AutoJobParamRole.SingleEntityAspect =>
                    $"{FromWorldEmitter.JobFieldPrefix}se_{p.Name}",
                AutoJobParamRole.SingleEntityComponentRead =>
                    $"{FromWorldEmitter.JobFieldPrefix}se_{p.Name}_read",
                AutoJobParamRole.SingleEntityComponentWrite =>
                    $"{FromWorldEmitter.JobFieldPrefix}se_{p.Name}_write",
                _ => p.Name,
            };
            var aspectData =
                p.SingleEntityAspectData != null
                    ? AspectAttributeDataModelBuilder.FromData(
                        p.SingleEntityAspectData,
                        globalNamespaceName
                    )
                    : AspectAttributeDataModel.Empty;
            var tags = (p.SingleEntityTags ?? new List<ITypeSymbol>())
                .Select(PerformanceCache.GetDisplayString)
                .ToEquatableArray();
            return new SingleEntityEmitTargetModel(
                LocalNameRoot: p.Name,
                JobFieldAssignmentLhs: lhs,
                IsAspect: p.Role == AutoJobParamRole.SingleEntityAspect,
                IsComponentWrite: p.Role == AutoJobParamRole.SingleEntityComponentWrite,
                TagTypeDisplays: tags,
                AspectData: aspectData,
                AspectTypeDisplay: aspectTypeDisplay,
                ComponentTypeDisplay: componentTypeDisplay
            );
        }

        // ─── Emission ──────────────────────────────────────────────────────────

        static string BufferFieldName(int index) => FromWorldEmitter.JobFieldPrefix + "buf" + index;

        static string GenerateSource(in AutoJobModel model)
        {
            var sb = new StringBuilder();
            var usings = new HashSet<string>(CommonUsings.Namespaces)
            {
                "Unity.Collections",
                "Unity.Jobs",
            };
            foreach (var u in model.AdditionalUsings)
                usings.Add(u);

            foreach (var u in usings.OrderBy(u => u))
                sb.AppendLine($"using {u};");
            sb.AppendLine();

            sb.AppendLine($"namespace {model.Namespace}");
            sb.AppendLine("{");

            int indent = 1;
            foreach (var enclosing in model.ContainingTypes)
            {
                string enclosingInd = new(' ', indent * 4);
                sb.AppendLine($"{enclosingInd}partial {enclosing.Kind} {enclosing.Name}");
                sb.AppendLine($"{enclosingInd}{{");
                indent++;
            }

            string ind = new(' ', indent * 4);
            sb.AppendLine($"{ind}partial class {model.ClassName}");
            sb.AppendLine($"{ind}{{");
            indent++;
            ind = new string(' ', indent * 4);

            EmitJobStruct(sb, model, ind);
            sb.AppendLine();
            if (model.HasSets)
            {
                EmitSparseShim(sb, model, ind);
                sb.AppendLine();
            }
            EmitScheduleOverloads(sb, model, ind);
            sb.AppendLine();
            EmitWrapperMethod(sb, model, ind);

            indent--;
            ind = new string(' ', indent * 4);
            sb.AppendLine($"{ind}}}");

            for (int i = 0; i < model.ContainingTypes.Length; i++)
            {
                indent--;
                string closingInd = new(' ', indent * 4);
                sb.AppendLine($"{closingInd}}}");
            }

            sb.AppendLine("}");

            return sb.ToString();
        }

        // ─── Job struct emission ───────────────────────────────────────────────

        static void EmitJobStruct(StringBuilder sb, in AutoJobModel model, string ind)
        {
            var jobStructName = $"_{model.MethodName}_AutoJob";

            sb.AppendLine($"{ind}[Unity.Burst.BurstCompile]");
            sb.AppendLine($"{ind}{GeneratedCodeAttributes.Line}");
            sb.AppendLine($"{ind}private partial struct {jobStructName} : IJobParallelForBatch");
            sb.AppendLine($"{ind}{{");

            string fieldInd = ind + "    ";

            // Buffer fields for iteration.
            var buffers = model.IterationBuffers;
            for (int i = 0; i < buffers.Count; i++)
            {
                var (typeName, readOnly) = buffers[i];
                if (!readOnly)
                    sb.AppendLine(
                        $"{fieldInd}[Unity.Collections.NativeDisableParallelForRestriction]"
                    );
                var bufferType = readOnly
                    ? $"NativeComponentBufferRead<{typeName}>"
                    : $"NativeComponentBufferWrite<{typeName}>";
                sb.AppendLine($"{fieldInd}internal {bufferType} {BufferFieldName(i)};");
            }

            PerGroupHiddenFieldEmitter.EmitDeclarations(
                sb,
                fieldInd,
                visibility: "internal",
                needsGroupField: model.NeedsGroupField,
                needsGlobalIndexOffset: model.NeedsGlobalIndexOffset,
                hasNativeWorldAccessor: model.HasNativeWorldAccessor,
                needsEntityHandleBuffer: model.NeedsEntityHandleBuffer
            );

            // PassThrough fields.
            foreach (var p in model.Params)
            {
                if (p.Role == AutoJobParamRoleKind.PassThrough)
                    sb.AppendLine(
                        $"{fieldInd}public {p.TypeDisplay} {FromWorldEmitter.JobFieldPrefix}pt_{p.Name};"
                    );
            }

            // NativeSetRead fields.
            foreach (var p in model.Params)
            {
                if (p.Role == AutoJobParamRoleKind.NativeSetRead)
                    sb.AppendLine(
                        $"{fieldInd}public NativeSetRead<{p.SetTypeArg}> {FromWorldEmitter.JobFieldPrefix}nsr_{p.Name};"
                    );
            }

            // NativeSetCommandBuffer fields.
            foreach (var p in model.Params)
            {
                if (p.Role == AutoJobParamRoleKind.NativeSetCommandBuffer)
                    sb.AppendLine(
                        $"{fieldInd}public NativeSetCommandBuffer<{p.SetTypeArg}> {FromWorldEmitter.JobFieldPrefix}nscb_{p.Name};"
                    );
            }

            // FromWorld fields.
            foreach (var p in model.Params)
            {
                if (p.Role != AutoJobParamRoleKind.FromWorld)
                    continue;
                var fwModel = model.FromWorldFields[p.FromWorldIndex];
                bool isWrite =
                    fwModel.Kind == FromWorldFieldKind.NativeComponentBufferWrite
                    || fwModel.Kind == FromWorldFieldKind.NativeComponentLookupWrite;
                if (isWrite)
                    sb.AppendLine(
                        $"{fieldInd}[Unity.Collections.NativeDisableParallelForRestriction]"
                    );
                sb.AppendLine(
                    $"{fieldInd}public {p.TypeDisplay} {FromWorldEmitter.JobFieldPrefix}fw_{p.Name};"
                );
            }

            // [SingleEntity] fields.
            foreach (var p in model.Params)
            {
                switch (p.Role)
                {
                    case AutoJobParamRoleKind.SingleEntityAspect:
                        if (p.SingleEntityAspectHasWrites)
                            sb.AppendLine(
                                $"{fieldInd}[Unity.Collections.NativeDisableParallelForRestriction]"
                            );
                        sb.AppendLine(
                            $"{fieldInd}public {p.TypeDisplay} {FromWorldEmitter.JobFieldPrefix}se_{p.Name};"
                        );
                        break;
                    case AutoJobParamRoleKind.SingleEntityComponentRead:
                        sb.AppendLine(
                            $"{fieldInd}public NativeComponentRead<{p.TypeDisplay}> {FromWorldEmitter.JobFieldPrefix}se_{p.Name}_read;"
                        );
                        break;
                    case AutoJobParamRoleKind.SingleEntityComponentWrite:
                        sb.AppendLine(
                            $"{fieldInd}[Unity.Collections.NativeDisableParallelForRestriction]"
                        );
                        sb.AppendLine(
                            $"{fieldInd}public NativeComponentWrite<{p.TypeDisplay}> {FromWorldEmitter.JobFieldPrefix}se_{p.Name}_write;"
                        );
                        break;
                }
            }

            // Per-worker timing buffer + thread index. See RuntimeJobScheduler's
            // RegisterJobTimings doc for the buffer layout. Both fields are
            // populated by the generated ScheduleParallel_ method; the Execute
            // shim writes ProfilerUnsafeUtility.Timestamp values into the buffer.
            sb.AppendLine($"{fieldInd}#if SVKJ_IS_PROFILING");
            sb.AppendLine($"{fieldInd}[Unity.Collections.NativeDisableParallelForRestriction]");
            sb.AppendLine(
                $"{fieldInd}internal Unity.Collections.NativeArray<long> {FromWorldEmitter.JobFieldPrefix}timing;"
            );
            sb.AppendLine($"{fieldInd}[Unity.Collections.LowLevel.Unsafe.NativeSetThreadIndex]");
            sb.AppendLine($"{fieldInd}internal int {FromWorldEmitter.JobFieldPrefix}threadIndex;");
            sb.AppendLine($"{fieldInd}#endif");

            sb.AppendLine();

            EmitExecuteShim(sb, model, fieldInd);

            sb.AppendLine($"{ind}}}");
        }

        static void EmitExecuteShim(StringBuilder sb, in AutoJobModel model, string ind)
        {
            // IJobParallelForBatch: Execute is called once per batch with (start, count).
            // The inner for-loop iterates the batch using `i`, which downstream emit code
            // already references (buffer indexing, EntityIndex construction, etc.). Burst
            // typically inlines this loop just like it would the per-index IJobFor thunk,
            // so release-mode codegen is effectively equivalent; the win is a single
            // pair of timestamp reads per batch instead of per-index when profiling is on.
            sb.AppendLine($"{ind}public void Execute(int {GenPrefix}start, int {GenPrefix}count)");
            sb.AppendLine($"{ind}{{");
            string outer = ind + "    ";

            // Start-of-batch worker timestamp (one read per Execute call, not per-index).
            sb.AppendLine($"{outer}#if SVKJ_IS_PROFILING");
            sb.AppendLine(
                $"{outer}var {GenPrefix}t0 = Unity.Profiling.LowLevel.Unsafe.ProfilerUnsafeUtility.Timestamp;"
            );
            sb.AppendLine($"{outer}#endif");

            sb.AppendLine(
                $"{outer}for (int i = {GenPrefix}start; i < {GenPrefix}start + {GenPrefix}count; i++)"
            );
            sb.AppendLine($"{outer}{{");
            string body = outer + "    ";

            bool needsEntityIndex =
                model.HasEntityIndex || model.IterKind == AutoJobIterationKindModel.Aspect;
            if (needsEntityIndex)
                sb.AppendLine(
                    $"{body}var {GenPrefix}ei = new EntityIndex(i, {FromWorldEmitter.JobFieldPrefix}GroupIndex);"
                );

            if (model.IterKind == AutoJobIterationKindModel.Aspect)
            {
                var ad = model.AspectData;
                var ctorArgs = string.Join(
                    ", ",
                    Enumerable.Range(0, ad.Components.Length).Select(BufferFieldName)
                );
                sb.AppendLine(
                    $"{body}var {GenPrefix}view = new {ad.AspectTypeName}({GenPrefix}ei, {ctorArgs});"
                );
            }
            else
            {
                foreach (var p in model.Params)
                {
                    if (p.Role != AutoJobParamRoleKind.Component)
                        continue;
                    var refKind = p.IsRef ? "ref" : "ref readonly";
                    sb.AppendLine(
                        $"{body}{refKind} var {GenPrefix}v{p.BufferIndex} = ref {BufferFieldName(p.BufferIndex)}[i];"
                    );
                }
            }

            // [SingleEntity] preamble. Component-typed singletons get a ref/ref-readonly
            // alias into the wrapper's .Value so the user method's parameter binds to
            // the buffer slot rather than a copy.
            foreach (var p in model.Params)
            {
                switch (p.Role)
                {
                    case AutoJobParamRoleKind.SingleEntityComponentRead:
                        sb.AppendLine(
                            $"{body}ref readonly var {GenPrefix}se_{p.Name} = ref {FromWorldEmitter.JobFieldPrefix}se_{p.Name}_read.Value;"
                        );
                        break;
                    case AutoJobParamRoleKind.SingleEntityComponentWrite:
                        sb.AppendLine(
                            $"{body}ref var {GenPrefix}se_{p.Name} = ref {FromWorldEmitter.JobFieldPrefix}se_{p.Name}_write.Value;"
                        );
                        break;
                }
            }

            var className =
                model.ContainingTypes.Length == 0
                    ? model.Namespace + "." + model.ClassName
                    : BuildClassChainDisplay(model);
            var callArgs = new List<string>();
            foreach (var p in model.Params)
            {
                switch (p.Role)
                {
                    case AutoJobParamRoleKind.Aspect:
                        callArgs.Add($"in {GenPrefix}view");
                        break;
                    case AutoJobParamRoleKind.Component:
                        var prefix = p.IsRef ? "ref" : "in";
                        callArgs.Add($"{prefix} {GenPrefix}v{p.BufferIndex}");
                        break;
                    case AutoJobParamRoleKind.EntityIndex:
                        callArgs.Add($"{GenPrefix}ei");
                        break;
                    case AutoJobParamRoleKind.EntityHandle:
                        callArgs.Add($"{FromWorldEmitter.JobFieldPrefix}EntityHandles[i]");
                        break;
                    case AutoJobParamRoleKind.GlobalIndex:
                        callArgs.Add($"{FromWorldEmitter.JobFieldPrefix}GlobalIndexOffset + i");
                        break;
                    case AutoJobParamRoleKind.NativeWorldAccessor:
                        callArgs.Add($"in {FromWorldEmitter.JobFieldPrefix}nwa");
                        break;
                    case AutoJobParamRoleKind.PassThrough:
                        callArgs.Add($"{FromWorldEmitter.JobFieldPrefix}pt_{p.Name}");
                        break;
                    case AutoJobParamRoleKind.NativeSetRead:
                        callArgs.Add($"in {FromWorldEmitter.JobFieldPrefix}nsr_{p.Name}");
                        break;
                    case AutoJobParamRoleKind.NativeSetCommandBuffer:
                        callArgs.Add($"in {FromWorldEmitter.JobFieldPrefix}nscb_{p.Name}");
                        break;
                    case AutoJobParamRoleKind.FromWorld:
                        callArgs.Add($"in {FromWorldEmitter.JobFieldPrefix}fw_{p.Name}");
                        break;
                    case AutoJobParamRoleKind.SingleEntityAspect:
                        callArgs.Add($"in {FromWorldEmitter.JobFieldPrefix}se_{p.Name}");
                        break;
                    case AutoJobParamRoleKind.SingleEntityComponentRead:
                        callArgs.Add($"in {GenPrefix}se_{p.Name}");
                        break;
                    case AutoJobParamRoleKind.SingleEntityComponentWrite:
                        callArgs.Add($"ref {GenPrefix}se_{p.Name}");
                        break;
                }
            }

            sb.AppendLine($"{body}{className}.{model.MethodName}({string.Join(", ", callArgs)});");

            // Close the per-batch for-loop.
            sb.AppendLine($"{outer}}}");

            // End-of-batch worker timestamp. Per JobTimingEntry layout:
            //   slot[0] = first-seen timestamp on this thread (sentinel 0 if untouched)
            //   slot[1] = last-seen timestamp (last call wins)
            //   slot[2] += delta — accumulates per-batch CPU on this worker
            sb.AppendLine($"{outer}#if SVKJ_IS_PROFILING");
            sb.AppendLine(
                $"{outer}var {GenPrefix}t1 = Unity.Profiling.LowLevel.Unsafe.ProfilerUnsafeUtility.Timestamp;"
            );
            sb.AppendLine(
                $"{outer}var {GenPrefix}tbase = {FromWorldEmitter.JobFieldPrefix}threadIndex * 3;"
            );
            sb.AppendLine(
                $"{outer}if ({FromWorldEmitter.JobFieldPrefix}timing[{GenPrefix}tbase] == 0)"
            );
            sb.AppendLine(
                $"{outer}    {FromWorldEmitter.JobFieldPrefix}timing[{GenPrefix}tbase] = {GenPrefix}t0;"
            );
            sb.AppendLine(
                $"{outer}{FromWorldEmitter.JobFieldPrefix}timing[{GenPrefix}tbase + 1] = {GenPrefix}t1;"
            );
            sb.AppendLine(
                $"{outer}{FromWorldEmitter.JobFieldPrefix}timing[{GenPrefix}tbase + 2] += {GenPrefix}t1 - {GenPrefix}t0;"
            );
            sb.AppendLine($"{outer}#endif");

            sb.AppendLine($"{ind}}}");
        }

        // Builds the user-class display chain for the static call inside the Execute
        // shim. Mirrors the symbol-based PerformanceCache.GetDisplayString output:
        // "Namespace.Outer.Inner.ClassName".
        static string BuildClassChainDisplay(in AutoJobModel model)
        {
            var sb = new StringBuilder();
            if (!string.IsNullOrEmpty(model.Namespace))
                sb.Append(model.Namespace).Append('.');
            // ContainingTypes is outer-to-inner.
            foreach (var ct in model.ContainingTypes)
                sb.Append(ct.Name).Append('.');
            sb.Append(model.ClassName);
            return sb.ToString();
        }

        // ─── Schedule overloads ────────────────────────────────────────────────

        static void EmitScheduleOverloads(StringBuilder sb, in AutoJobModel model, string ind)
        {
            var passThroughParams = model
                .Params.Where(p => p.Role == AutoJobParamRoleKind.PassThrough)
                .ToList();
            var ptParamDecl =
                passThroughParams.Count == 0
                    ? ""
                    : ", "
                        + string.Join(
                            ", ",
                            passThroughParams.Select(p => $"{p.TypeDisplay} {p.Name}")
                        );

            // (1) Convenience overload: WorldAccessor entry. Always emitted because
            // validation ensures criteria is non-empty (TRECS096).
            EmitWorldAccessorOverload(sb, model, ind, ptParamDecl, passThroughParams);
            sb.AppendLine();

            if (model.HasSets)
            {
                // (2a) Sparse ScheduleParallel: SparseQueryBuilder entry.
                EmitSparseQueryBuilderOverload(sb, model, ind, ptParamDecl, passThroughParams);
            }
            else
            {
                // (2b) Dense ScheduleParallel: QueryBuilder entry.
                EmitQueryBuilderOverload(sb, model, ind, ptParamDecl, passThroughParams);
            }
        }

        static void EmitWorldAccessorOverload(
            StringBuilder sb,
            in AutoJobModel model,
            string ind,
            string ptParamDecl,
            List<AutoJobParamModel> ptParams
        )
        {
            sb.AppendLine($"{ind}{GeneratedCodeAttributes.Line}");
            sb.AppendLine(
                $"{ind}private JobHandle {GenPrefix}ScheduleParallel_{model.MethodName}(WorldAccessor {GenPrefix}world{ptParamDecl}, JobHandle {GenPrefix}extraDeps = default)"
            );
            sb.AppendLine($"{ind}{{");
            string body = ind + "    ";

            if (model.HasSets)
            {
                var firstSetName = model.Criteria.SetTypeDisplays[0];
                var args = new List<string> { $"{GenPrefix}world.Query().InSet<{firstSetName}>()" };
                args.AddRange(ptParams.Select(p => p.Name));
                args.Add($"{GenPrefix}extraDeps");
                sb.AppendLine(
                    $"{body}return {GenPrefix}ScheduleParallel_{model.MethodName}({string.Join(", ", args)});"
                );
            }
            else
            {
                var args = new List<string> { $"{GenPrefix}world.Query()" };
                args.AddRange(ptParams.Select(p => p.Name));
                args.Add($"{GenPrefix}extraDeps");
                sb.AppendLine(
                    $"{body}return {GenPrefix}ScheduleParallel_{model.MethodName}({string.Join(", ", args)});"
                );
            }

            sb.AppendLine($"{ind}}}");
        }

        static void EmitQueryBuilderOverload(
            StringBuilder sb,
            in AutoJobModel model,
            string ind,
            string ptParamDecl,
            List<AutoJobParamModel> ptParams
        )
        {
            sb.AppendLine($"{ind}{GeneratedCodeAttributes.Line}");
            sb.AppendLine(
                $"{ind}private JobHandle {GenPrefix}ScheduleParallel_{model.MethodName}(QueryBuilder {GenPrefix}builder{ptParamDecl}, JobHandle {GenPrefix}extraDeps = default)"
            );
            sb.AppendLine($"{ind}{{");
            string body = ind + "    ";

            if (model.AttributeCriteriaChain.Length > 0)
                sb.AppendLine(
                    $"{body}{GenPrefix}builder = {GenPrefix}builder{model.AttributeCriteriaChain};"
                );

            sb.AppendLine(
                $"{body}TrecsDebugAssert.That({GenPrefix}builder.HasAnyCriteria, \"_{model.MethodName}_AutoJob.ScheduleParallel requires query criteria.\");"
            );

            sb.AppendLine($"{body}var {GenPrefix}world = {GenPrefix}builder.World;");
            sb.AppendLine(
                $"{body}var {GenPrefix}scheduler = {GenPrefix}world.GetJobSchedulerForJob();"
            );
            sb.AppendLine(
                $"{body}const string {GenPrefix}jobName = \"{model.ClassName}._{model.MethodName}_AutoJob\";"
            );
            sb.AppendLine($"{body}var {GenPrefix}allJobs = {GenPrefix}extraDeps;");

            if (model.NeedsGlobalIndexOffset)
                sb.AppendLine($"{body}var {GenPrefix}queryIndexOffset = 0;");

            var fwEmits = model.FromWorldFields.ToList();
            var seTargets = model.SingleEntityFields.ToList();
            if (fwEmits.Count > 0)
                FromWorldEmitter.EmitFromWorldHoistedSetup(sb, body, fwEmits);
            SingleEntityEmitter.EmitHoistedSetup(sb, body, seTargets);

            sb.AppendLine(
                $"{body}foreach (var {GenPrefix}slice in {GenPrefix}builder.GroupSlices())"
            );
            sb.AppendLine($"{body}{{");
            string innerBody = body + "    ";

            sb.AppendLine($"{innerBody}var {GenPrefix}group = {GenPrefix}slice.GroupIndex;");
            sb.AppendLine($"{innerBody}var {GenPrefix}count = {GenPrefix}slice.Count;");
            sb.AppendLine($"{innerBody}if ({GenPrefix}count == 0) continue;");
            sb.AppendLine();

            sb.AppendLine($"{innerBody}var {GenPrefix}deps = {GenPrefix}extraDeps;");

            var buffers = model.IterationBuffers;
            IterationBufferEmitter.EmitDepRegistration(sb, innerBody, buffers);

            foreach (var p in model.Params)
            {
                if (p.Role == AutoJobParamRoleKind.NativeSetRead)
                    sb.AppendLine(
                        $"{innerBody}{GenPrefix}deps = {GenPrefix}world.IncludeNativeSetReadDepsForJob<{p.SetTypeArg}>({GenPrefix}deps);"
                    );
                else if (p.Role == AutoJobParamRoleKind.NativeSetCommandBuffer)
                    sb.AppendLine(
                        $"{innerBody}{GenPrefix}deps = {GenPrefix}world.IncludeNativeSetCommandBufferDepsForJob<{p.SetTypeArg}>({GenPrefix}deps);"
                    );
            }

            if (fwEmits.Count > 0)
                FromWorldEmitter.EmitFromWorldDepRegistration(sb, innerBody, fwEmits);
            SingleEntityEmitter.EmitDepRegistration(sb, innerBody, seTargets);

            IterationBufferEmitter.EmitMaterialization(sb, innerBody, buffers);

            var jobStructName = $"_{model.MethodName}_AutoJob";
            sb.AppendLine($"{innerBody}var {GenPrefix}job = new {jobStructName}();");

            for (int i = 0; i < buffers.Count; i++)
                sb.AppendLine(
                    $"{innerBody}{GenPrefix}job.{BufferFieldName(i)} = {GenPrefix}buf{i}_value;"
                );

            PerGroupHiddenFieldEmitter.EmitAssignments(
                sb,
                innerBody,
                needsGroupField: model.NeedsGroupField,
                needsGlobalIndexOffset: model.NeedsGlobalIndexOffset,
                hasNativeWorldAccessor: model.HasNativeWorldAccessor,
                needsEntityHandleBuffer: model.NeedsEntityHandleBuffer
            );

            foreach (var p in ptParams)
                sb.AppendLine(
                    $"{innerBody}{GenPrefix}job.{FromWorldEmitter.JobFieldPrefix}pt_{p.Name} = {p.Name};"
                );

            foreach (var p in model.Params)
            {
                if (p.Role == AutoJobParamRoleKind.NativeSetRead)
                    sb.AppendLine(
                        $"{innerBody}{GenPrefix}job.{FromWorldEmitter.JobFieldPrefix}nsr_{p.Name} = {GenPrefix}world.CreateNativeSetReadForJob<{p.SetTypeArg}>();"
                    );
                else if (p.Role == AutoJobParamRoleKind.NativeSetCommandBuffer)
                    sb.AppendLine(
                        $"{innerBody}{GenPrefix}job.{FromWorldEmitter.JobFieldPrefix}nscb_{p.Name} = {GenPrefix}world.CreateNativeSetCommandBufferForJob<{p.SetTypeArg}>();"
                    );
            }

            if (fwEmits.Count > 0)
                FromWorldEmitter.EmitFromWorldFieldAssignments(sb, innerBody, fwEmits);
            SingleEntityEmitter.EmitFieldAssignment(sb, innerBody, seTargets);

            // Rent and attach the per-worker timing buffer just before scheduling.
            // Decoded + returned to the pool by RuntimeJobScheduler.CompleteAllOutstanding.
            sb.AppendLine($"{innerBody}#if SVKJ_IS_PROFILING");
            sb.AppendLine(
                $"{innerBody}var {GenPrefix}timing = {GenPrefix}scheduler.RentTimingBuffer();"
            );
            sb.AppendLine(
                $"{innerBody}{GenPrefix}job.{FromWorldEmitter.JobFieldPrefix}timing = {GenPrefix}timing;"
            );
            sb.AppendLine($"{innerBody}#endif");

            sb.AppendLine(
                $"{innerBody}var {GenPrefix}handle = {GenPrefix}job.ScheduleParallel({GenPrefix}count, JobsUtil.ChooseBatchSize({GenPrefix}count), {GenPrefix}deps);"
            );

            IterationBufferEmitter.EmitOutputTracking(sb, innerBody, buffers);

            foreach (var p in model.Params)
            {
                if (p.Role == AutoJobParamRoleKind.NativeSetRead)
                    sb.AppendLine(
                        $"{innerBody}{GenPrefix}world.TrackNativeSetReadDepsForJob<{p.SetTypeArg}>({GenPrefix}handle);"
                    );
                else if (p.Role == AutoJobParamRoleKind.NativeSetCommandBuffer)
                    sb.AppendLine(
                        $"{innerBody}{GenPrefix}world.TrackNativeSetCommandBufferDepsForJob<{p.SetTypeArg}>({GenPrefix}handle);"
                    );
            }

            if (fwEmits.Count > 0)
                FromWorldEmitter.EmitFromWorldTracking(sb, innerBody, fwEmits);
            SingleEntityEmitter.EmitTracking(sb, innerBody, seTargets);

            if (model.HasNativeWorldAccessor)
                sb.AppendLine(
                    $"{innerBody}{GenPrefix}scheduler.TrackJob({GenPrefix}handle, {GenPrefix}jobName);"
                );

            sb.AppendLine($"{innerBody}#if SVKJ_IS_PROFILING");
            sb.AppendLine(
                $"{innerBody}{GenPrefix}scheduler.RegisterJobTimings({GenPrefix}handle, {GenPrefix}jobName, {GenPrefix}timing);"
            );
            sb.AppendLine($"{innerBody}#endif");

            sb.AppendLine(
                $"{innerBody}{GenPrefix}allJobs = JobHandle.CombineDependencies({GenPrefix}allJobs, {GenPrefix}handle);"
            );

            if (model.NeedsGlobalIndexOffset)
                sb.AppendLine($"{innerBody}{GenPrefix}queryIndexOffset += {GenPrefix}count;");

            sb.AppendLine($"{body}}}");
            sb.AppendLine($"{body}return {GenPrefix}allJobs;");
            sb.AppendLine($"{ind}}}");
        }

        // ─── Sparse schedule overload ──────────────────────────────────────────

        static void EmitSparseQueryBuilderOverload(
            StringBuilder sb,
            in AutoJobModel model,
            string ind,
            string ptParamDecl,
            List<AutoJobParamModel> ptParams
        )
        {
            sb.AppendLine($"{ind}{GeneratedCodeAttributes.Line}");
            sb.AppendLine(
                $"{ind}private JobHandle {GenPrefix}ScheduleParallel_{model.MethodName}(SparseQueryBuilder {GenPrefix}builder{ptParamDecl}, JobHandle {GenPrefix}extraDeps = default)"
            );
            sb.AppendLine($"{ind}{{");
            string body = ind + "    ";

            if (model.AttributeCriteriaChain.Length > 0)
                sb.AppendLine(
                    $"{body}{GenPrefix}builder = {GenPrefix}builder{model.AttributeCriteriaChain};"
                );

            sb.AppendLine($"{body}var {GenPrefix}world = {GenPrefix}builder.World;");
            sb.AppendLine(
                $"{body}var {GenPrefix}scheduler = {GenPrefix}world.GetJobSchedulerForJob();"
            );
            sb.AppendLine(
                $"{body}const string {GenPrefix}jobName = \"{model.ClassName}._{model.MethodName}_AutoJob\";"
            );
            sb.AppendLine($"{body}var {GenPrefix}allJobs = {GenPrefix}extraDeps;");

            if (model.NeedsGlobalIndexOffset)
                sb.AppendLine($"{body}var {GenPrefix}queryIndexOffset = 0;");

            var fwEmits = model.FromWorldFields.ToList();
            var seTargets = model.SingleEntityFields.ToList();
            if (fwEmits.Count > 0)
                FromWorldEmitter.EmitFromWorldHoistedSetup(sb, body, fwEmits);
            SingleEntityEmitter.EmitHoistedSetup(sb, body, seTargets);

            sb.AppendLine(
                $"{body}foreach (var {GenPrefix}slice in {GenPrefix}builder.GroupSlices())"
            );
            sb.AppendLine($"{body}{{");
            string innerBody = body + "    ";

            sb.AppendLine($"{innerBody}var {GenPrefix}group = {GenPrefix}slice.GroupIndex;");
            sb.AppendLine();

            sb.AppendLine(
                $"{innerBody}var ({GenPrefix}indices, {GenPrefix}indicesLifetime, {GenPrefix}count) = {GenPrefix}world.AllocateSparseIndicesForJob({GenPrefix}slice);"
            );
            sb.AppendLine($"{innerBody}if ({GenPrefix}count == 0)");
            sb.AppendLine($"{innerBody}{{");
            sb.AppendLine($"{innerBody}    {GenPrefix}indicesLifetime.Dispose();");
            sb.AppendLine($"{innerBody}    continue;");
            sb.AppendLine($"{innerBody}}}");
            sb.AppendLine();

            sb.AppendLine($"{innerBody}var {GenPrefix}deps = {GenPrefix}extraDeps;");

            var buffers = model.IterationBuffers;
            IterationBufferEmitter.EmitDepRegistration(sb, innerBody, buffers);

            foreach (var p in model.Params)
            {
                if (p.Role == AutoJobParamRoleKind.NativeSetRead)
                    sb.AppendLine(
                        $"{innerBody}{GenPrefix}deps = {GenPrefix}world.IncludeNativeSetReadDepsForJob<{p.SetTypeArg}>({GenPrefix}deps);"
                    );
                else if (p.Role == AutoJobParamRoleKind.NativeSetCommandBuffer)
                    sb.AppendLine(
                        $"{innerBody}{GenPrefix}deps = {GenPrefix}world.IncludeNativeSetCommandBufferDepsForJob<{p.SetTypeArg}>({GenPrefix}deps);"
                    );
            }

            if (fwEmits.Count > 0)
                FromWorldEmitter.EmitFromWorldDepRegistration(sb, innerBody, fwEmits);
            SingleEntityEmitter.EmitDepRegistration(sb, innerBody, seTargets);

            IterationBufferEmitter.EmitMaterialization(sb, innerBody, buffers);

            var jobStructName = $"_{model.MethodName}_AutoJob";
            sb.AppendLine($"{innerBody}var {GenPrefix}job = new {jobStructName}();");

            for (int i = 0; i < buffers.Count; i++)
                sb.AppendLine(
                    $"{innerBody}{GenPrefix}job.{BufferFieldName(i)} = {GenPrefix}buf{i}_value;"
                );

            PerGroupHiddenFieldEmitter.EmitAssignments(
                sb,
                innerBody,
                needsGroupField: model.NeedsGroupField,
                needsGlobalIndexOffset: model.NeedsGlobalIndexOffset,
                hasNativeWorldAccessor: model.HasNativeWorldAccessor,
                needsEntityHandleBuffer: model.NeedsEntityHandleBuffer
            );

            foreach (var p in ptParams)
                sb.AppendLine(
                    $"{innerBody}{GenPrefix}job.{FromWorldEmitter.JobFieldPrefix}pt_{p.Name} = {p.Name};"
                );

            foreach (var p in model.Params)
            {
                if (p.Role == AutoJobParamRoleKind.NativeSetRead)
                    sb.AppendLine(
                        $"{innerBody}{GenPrefix}job.{FromWorldEmitter.JobFieldPrefix}nsr_{p.Name} = {GenPrefix}world.CreateNativeSetReadForJob<{p.SetTypeArg}>();"
                    );
                else if (p.Role == AutoJobParamRoleKind.NativeSetCommandBuffer)
                    sb.AppendLine(
                        $"{innerBody}{GenPrefix}job.{FromWorldEmitter.JobFieldPrefix}nscb_{p.Name} = {GenPrefix}world.CreateNativeSetCommandBufferForJob<{p.SetTypeArg}>();"
                    );
            }

            if (fwEmits.Count > 0)
                FromWorldEmitter.EmitFromWorldFieldAssignments(sb, innerBody, fwEmits);
            SingleEntityEmitter.EmitFieldAssignment(sb, innerBody, seTargets);

            // Rent the timing buffer and attach to the Inner job BEFORE wrapping in
            // the Shim (Shim copies Inner by value). Decoded + returned to the pool
            // by RuntimeJobScheduler.CompleteAllOutstanding.
            sb.AppendLine($"{innerBody}#if SVKJ_IS_PROFILING");
            sb.AppendLine(
                $"{innerBody}var {GenPrefix}timing = {GenPrefix}scheduler.RentTimingBuffer();"
            );
            sb.AppendLine(
                $"{innerBody}{GenPrefix}job.{FromWorldEmitter.JobFieldPrefix}timing = {GenPrefix}timing;"
            );
            sb.AppendLine($"{innerBody}#endif");

            var shimName = $"_{model.MethodName}_SparseShim";
            sb.AppendLine(
                $"{innerBody}var {GenPrefix}shim = new {shimName} {{ Inner = {GenPrefix}job, Indices = {GenPrefix}indices }};"
            );
            sb.AppendLine(
                $"{innerBody}var {GenPrefix}handle = {GenPrefix}shim.ScheduleParallel({GenPrefix}count, JobsUtil.ChooseBatchSize({GenPrefix}count), {GenPrefix}deps);"
            );

            IterationBufferEmitter.EmitOutputTracking(sb, innerBody, buffers);

            foreach (var p in model.Params)
            {
                if (p.Role == AutoJobParamRoleKind.NativeSetRead)
                    sb.AppendLine(
                        $"{innerBody}{GenPrefix}world.TrackNativeSetReadDepsForJob<{p.SetTypeArg}>({GenPrefix}handle);"
                    );
                else if (p.Role == AutoJobParamRoleKind.NativeSetCommandBuffer)
                    sb.AppendLine(
                        $"{innerBody}{GenPrefix}world.TrackNativeSetCommandBufferDepsForJob<{p.SetTypeArg}>({GenPrefix}handle);"
                    );
            }

            if (fwEmits.Count > 0)
                FromWorldEmitter.EmitFromWorldTracking(sb, innerBody, fwEmits);
            SingleEntityEmitter.EmitTracking(sb, innerBody, seTargets);

            if (model.HasNativeWorldAccessor)
                sb.AppendLine(
                    $"{innerBody}{GenPrefix}scheduler.TrackJob({GenPrefix}handle, {GenPrefix}jobName);"
                );

            // Register worker-execution timing against the user job's handle (not the
            // dispose-handle chained off it) so the recorded timings reflect the actual
            // worker execution window, not the dispose-job tail.
            sb.AppendLine($"{innerBody}#if SVKJ_IS_PROFILING");
            sb.AppendLine(
                $"{innerBody}{GenPrefix}scheduler.RegisterJobTimings({GenPrefix}handle, {GenPrefix}jobName, {GenPrefix}timing);"
            );
            sb.AppendLine($"{innerBody}#endif");

            sb.AppendLine(
                $"{innerBody}var {GenPrefix}disposeHandle = {GenPrefix}indicesLifetime.Dispose({GenPrefix}handle);"
            );
            sb.AppendLine(
                $"{innerBody}{GenPrefix}scheduler.TrackJob({GenPrefix}disposeHandle, {GenPrefix}jobName);"
            );
            sb.AppendLine(
                $"{innerBody}{GenPrefix}allJobs = JobHandle.CombineDependencies({GenPrefix}allJobs, {GenPrefix}disposeHandle);"
            );

            if (model.NeedsGlobalIndexOffset)
                sb.AppendLine($"{innerBody}{GenPrefix}queryIndexOffset += {GenPrefix}count;");

            sb.AppendLine($"{body}}}");
            sb.AppendLine($"{body}return {GenPrefix}allJobs;");
            sb.AppendLine($"{ind}}}");
        }

        // ─── Sparse shim struct emission ───────────────────────────────────────

        static void EmitSparseShim(StringBuilder sb, in AutoJobModel model, string ind)
        {
            var jobStructName = $"_{model.MethodName}_AutoJob";
            var shimName = $"_{model.MethodName}_SparseShim";
            string innerInd = ind + "    ";
            string bodyInd = innerInd + "    ";

            sb.AppendLine($"{ind}[Unity.Burst.BurstCompile]");
            sb.AppendLine($"{ind}{GeneratedCodeAttributes.Line}");
            sb.AppendLine($"{ind}private struct {shimName} : IJobParallelForBatch");
            sb.AppendLine($"{ind}{{");
            sb.AppendLine($"{innerInd}public {jobStructName} Inner;");
            sb.AppendLine($"{innerInd}public JobSparseIndices Indices;");
            sb.AppendLine();
            // Per-batch shim: iterate the dense [start, start+count) window of sparse
            // indices and dispatch each one to Inner as a single-item batch. Inner
            // is also IJobParallelForBatch — calling Inner.Execute(sparseIdx, 1) runs
            // its body once with `i = sparseIdx`. Profiling is per-call to Inner
            // here (effectively per sparse index), since the sparse mapping precludes
            // contiguous-range dispatch — same cost profile as the IJobFor sparse shim.
            sb.AppendLine($"{innerInd}public void Execute(int start, int count)");
            sb.AppendLine($"{innerInd}{{");
            sb.AppendLine($"{bodyInd}for (int i = start; i < start + count; i++)");
            sb.AppendLine($"{bodyInd}{{");
            sb.AppendLine($"{bodyInd}    Inner.Execute(Indices[i], 1);");
            sb.AppendLine($"{bodyInd}}}");
            sb.AppendLine($"{innerInd}}}");
            sb.AppendLine($"{ind}}}");
        }

        // ─── Wrapper method ────────────────────────────────────────────────────

        static void EmitWrapperMethod(StringBuilder sb, in AutoJobModel model, string ind)
        {
            var ptParams = model
                .Params.Where(p => p.Role == AutoJobParamRoleKind.PassThrough)
                .ToList();
            var ptParamDecl =
                ptParams.Count == 0
                    ? ""
                    : string.Join(", ", ptParams.Select(p => $"{p.TypeDisplay} {p.Name}"));

            var visibility = model.MethodName == "Execute" ? "public " : "";
            sb.AppendLine($"{ind}{GeneratedCodeAttributes.Line}");
            sb.AppendLine($"{ind}{visibility}void {model.MethodName}({ptParamDecl})");
            sb.AppendLine($"{ind}{{");
            string body = ind + "    ";

            var args = new List<string> { "this.World" };
            args.AddRange(ptParams.Select(p => p.Name));

            sb.AppendLine(
                $"{body}{GenPrefix}ScheduleParallel_{model.MethodName}({string.Join(", ", args)});"
            );
            sb.AppendLine($"{ind}}}");
        }

        // ─── Data classes ──────────────────────────────────────────────────────

        sealed class AutoJobData
        {
            public ClassDeclarationSyntax ClassDecl { get; }
            public MethodDeclarationSyntax MethodDecl { get; }
            public IMethodSymbol MethodSymbol { get; }

            public AutoJobData(
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

        enum AutoJobIterationKind
        {
            Aspect,
            Components,
        }

        enum AutoJobParamRole
        {
            Aspect,
            Component,
            EntityIndex,

            /// <summary>
            /// <c>EntityHandle</c> parameter — materialized per iteration from a hidden
            /// <c>NativeEntityHandleBuffer</c> field populated per-group at schedule time.
            /// Forwarded as <c>_trecs_EntityHandles[i]</c> at the call site.
            /// </summary>
            EntityHandle,

            /// <summary>
            /// <c>int</c> parameter marked <c>[GlobalIndex]</c>. The Execute shim forwards
            /// <c>_trecs_GlobalIndexOffset + i</c> at the call site; the schedule overload
            /// accumulates the offset across the per-group loop. Mirrors JobGenerator's
            /// <c>ComponentsParamRole.GlobalIndex</c>.
            /// </summary>
            GlobalIndex,
            NativeWorldAccessor,
            PassThrough,
            NativeSetRead,
            NativeSetCommandBuffer,
            FromWorld,

            /// <summary>
            /// Aspect-typed parameter marked <c>[SingleEntity(Tag/Tags)]</c>. Becomes a
            /// hidden <c>NativeFactory</c> + <c>EntityIndex</c> field pair on the generated
            /// job. The scheduler resolves the singleton via
            /// <c>Query().WithTags&lt;...&gt;().SingleIndex()</c> once per call;
            /// <c>Execute(int)</c> materializes the aspect via <c>factory.Create(index)</c>.
            /// </summary>
            SingleEntityAspect,

            /// <summary>
            /// <c>in</c>-typed component parameter marked <c>[SingleEntity(Tag/Tags)]</c>.
            /// Becomes <c>NativeComponentRead&lt;T&gt;</c> + <c>EntityIndex</c> fields;
            /// <c>Execute(int)</c> takes a <c>ref readonly</c> alias.
            /// </summary>
            SingleEntityComponentRead,

            /// <summary>
            /// <c>ref</c>-typed component parameter marked <c>[SingleEntity(Tag/Tags)]</c>.
            /// Becomes <c>NativeComponentWrite&lt;T&gt;</c> + <c>EntityIndex</c> fields;
            /// <c>Execute(int)</c> takes a <c>ref</c> alias.
            /// </summary>
            SingleEntityComponentWrite,
        }

        sealed class AutoJobParam
        {
            public AutoJobParamRole Role { get; }
            public ITypeSymbol? Type { get; }
            public string Name { get; }
            public string TypeDisplay { get; }
            public bool IsRef { get; }
            public int BufferIndex { get; }

            /// <summary>
            /// For NativeSetRead/NativeSetCommandBuffer roles, the generic type argument display string
            /// (e.g. "MyNamespace.MySet"). Null for other roles.
            /// </summary>
            public string? SetTypeArg { get; }

            /// <summary>
            /// For NativeSetRead/NativeSetCommandBuffer roles, the resolved type symbol for the generic
            /// type argument. Used for namespace resolution. Null for other roles.
            /// </summary>
            public ITypeSymbol? SetTypeArgSymbol { get; }

            /// <summary>
            /// For FromWorld role, the parsed [FromWorld] field info used for emission.
            /// Null for other roles.
            /// </summary>
            public FromWorldFieldInfo? FromWorldInfo { get; }

            /// <summary>
            /// For <c>SingleEntity*</c> roles, the inline <c>Tag</c> / <c>Tags</c> from
            /// <c>[SingleEntity(...)]</c>. Null for other roles. Always non-empty when
            /// non-null (TRECS114 enforces inline tags are required).
            /// </summary>
            public List<ITypeSymbol>? SingleEntityTags { get; }

            /// <summary>
            /// For <c>SingleEntityAspect</c> role, the parsed aspect's read/write component
            /// types (used to emit per-(component, group) lookups, dep tracking and the
            /// <c>NativeFactory</c> ctor). Null for other roles.
            /// </summary>
            public AspectAttributeData? SingleEntityAspectData { get; }

            AutoJobParam(
                AutoJobParamRole role,
                ITypeSymbol? type,
                string name,
                string typeDisplay,
                bool isRef = false,
                int bufferIndex = -1,
                string? setTypeArg = null,
                ITypeSymbol? setTypeArgSymbol = null,
                FromWorldFieldInfo? fromWorldInfo = null,
                List<ITypeSymbol>? singleEntityTags = null,
                AspectAttributeData? singleEntityAspectData = null
            )
            {
                Role = role;
                Type = type;
                Name = name;
                TypeDisplay = typeDisplay;
                IsRef = isRef;
                BufferIndex = bufferIndex;
                SetTypeArg = setTypeArg;
                SetTypeArgSymbol = setTypeArgSymbol;
                FromWorldInfo = fromWorldInfo;
                SingleEntityTags = singleEntityTags;
                SingleEntityAspectData = singleEntityAspectData;
            }

            public static AutoJobParam Aspect(ITypeSymbol type, string name, string typeDisplay) =>
                new(AutoJobParamRole.Aspect, type, name, typeDisplay);

            public static AutoJobParam Component(
                ITypeSymbol type,
                string name,
                bool isRef,
                int bufferIndex,
                string typeDisplay
            ) => new(AutoJobParamRole.Component, type, name, typeDisplay, isRef, bufferIndex);

            public static AutoJobParam EntityIndex(string name) =>
                new(AutoJobParamRole.EntityIndex, null, name, "EntityIndex");

            public static AutoJobParam EntityHandle(string name) =>
                new(AutoJobParamRole.EntityHandle, null, name, "EntityHandle");

            public static AutoJobParam GlobalIndex(string name) =>
                new(AutoJobParamRole.GlobalIndex, null, name, "int");

            public static AutoJobParam NativeWorldAccessor(string name) =>
                new(AutoJobParamRole.NativeWorldAccessor, null, name, "NativeWorldAccessor");

            public static AutoJobParam PassThrough(
                ITypeSymbol type,
                string name,
                string typeDisplay
            ) => new(AutoJobParamRole.PassThrough, type, name, typeDisplay);

            public static AutoJobParam NativeSetRead(
                string name,
                string setTypeArg,
                ITypeSymbol setTypeArgSymbol
            ) =>
                new(
                    AutoJobParamRole.NativeSetRead,
                    null,
                    name,
                    $"NativeSetRead<{setTypeArg}>",
                    setTypeArg: setTypeArg,
                    setTypeArgSymbol: setTypeArgSymbol
                );

            public static AutoJobParam NativeSetCommandBuffer(
                string name,
                string setTypeArg,
                ITypeSymbol setTypeArgSymbol
            ) =>
                new(
                    AutoJobParamRole.NativeSetCommandBuffer,
                    null,
                    name,
                    $"NativeSetCommandBuffer<{setTypeArg}>",
                    setTypeArg: setTypeArg,
                    setTypeArgSymbol: setTypeArgSymbol
                );

            public static AutoJobParam FromWorld(
                ITypeSymbol type,
                string name,
                string typeDisplay,
                FromWorldFieldInfo fromWorldInfo
            ) =>
                new(
                    AutoJobParamRole.FromWorld,
                    type,
                    name,
                    typeDisplay,
                    fromWorldInfo: fromWorldInfo
                );

            public static AutoJobParam SingleEntityAspect(
                ITypeSymbol type,
                string name,
                string typeDisplay,
                List<ITypeSymbol> tagTypes,
                AspectAttributeData aspectData
            ) =>
                new(
                    AutoJobParamRole.SingleEntityAspect,
                    type,
                    name,
                    typeDisplay,
                    singleEntityTags: tagTypes,
                    singleEntityAspectData: aspectData
                );

            public static AutoJobParam SingleEntityComponent(
                ITypeSymbol type,
                string name,
                string typeDisplay,
                bool isRef,
                List<ITypeSymbol> tagTypes
            ) =>
                new(
                    isRef
                        ? AutoJobParamRole.SingleEntityComponentWrite
                        : AutoJobParamRole.SingleEntityComponentRead,
                    type,
                    name,
                    typeDisplay,
                    isRef: isRef,
                    singleEntityTags: tagTypes
                );
        }

        sealed class AspectIterationData
        {
            public string AspectTypeName { get; }
            public INamedTypeSymbol AspectTypeSymbol { get; }
            public List<ITypeSymbol> ComponentTypes { get; }
            public List<ITypeSymbol> ReadComponentTypes { get; }
            public List<ITypeSymbol> WriteComponentTypes { get; }

            public AspectIterationData(
                string aspectTypeName,
                INamedTypeSymbol aspectTypeSymbol,
                List<ITypeSymbol> componentTypes,
                List<ITypeSymbol> readComponentTypes,
                List<ITypeSymbol> writeComponentTypes
            )
            {
                AspectTypeName = aspectTypeName;
                AspectTypeSymbol = aspectTypeSymbol;
                ComponentTypes = componentTypes;
                ReadComponentTypes = readComponentTypes;
                WriteComponentTypes = writeComponentTypes;
            }

            public bool IsReadOnly(ITypeSymbol type)
            {
                return ReadComponentTypes.Any(t => SymbolEqualityComparer.Default.Equals(t, type))
                    && !WriteComponentTypes.Any(t =>
                        SymbolEqualityComparer.Default.Equals(t, type)
                    );
            }
        }

        sealed class AutoJobInfo
        {
            public INamedTypeSymbol ClassSymbol { get; }
            public ClassDeclarationSyntax ClassDecl { get; }
            public IMethodSymbol MethodSymbol { get; }
            public MethodDeclarationSyntax MethodDecl { get; }
            public string MethodName { get; }
            public AutoJobIterationKind IterKind { get; }
            public List<AutoJobParam> Params { get; }
            public AspectIterationData? AspectData { get; }
            public IterationCriteria Criteria { get; }

            public bool HasEntityIndex => Params.Any(p => p.Role == AutoJobParamRole.EntityIndex);
            public bool HasEntityHandle => Params.Any(p => p.Role == AutoJobParamRole.EntityHandle);
            public bool HasGlobalIndex => Params.Any(p => p.Role == AutoJobParamRole.GlobalIndex);
            public bool HasNativeWorldAccessor =>
                Params.Any(p => p.Role == AutoJobParamRole.NativeWorldAccessor);

            /// <summary>
            /// True if the generated job needs a <c>_trecs_EntityHandles</c>
            /// <c>NativeEntityHandleBuffer</c> field, populated per-group at schedule time
            /// and dereferenced as <c>_trecs_EntityHandles[i]</c> in the Execute shim.
            /// </summary>
            public bool NeedsEntityHandleBuffer => HasEntityHandle;

            /// <summary>
            /// True if the generated job needs a <c>_trecs_Group</c> field. Aspect always
            /// needs it (for the aspect ctor). Components only need it when the user took
            /// an EntityIndex parameter.
            /// </summary>
            public bool NeedsGroupField =>
                IterKind == AutoJobIterationKind.Aspect || HasEntityIndex;

            /// <summary>
            /// True if the generated job needs a <c>_trecs_GlobalIndexOffset</c> field.
            /// Triggered by any <c>[GlobalIndex]</c> parameter; the schedule overload
            /// accumulates the offset across groups so the Execute shim's
            /// <c>_trecs_GlobalIndexOffset + i</c> packs uniquely over the full query.
            /// </summary>
            public bool NeedsGlobalIndexOffset => HasGlobalIndex;

            public bool HasSets => Criteria.SetTypes.Count > 0;

            public AutoJobInfo(
                INamedTypeSymbol classSymbol,
                ClassDeclarationSyntax classDecl,
                IMethodSymbol methodSymbol,
                MethodDeclarationSyntax methodDecl,
                string methodName,
                AutoJobIterationKind iterKind,
                List<AutoJobParam> paramSlots,
                AspectIterationData? aspectData,
                IterationCriteria criteria
            )
            {
                ClassSymbol = classSymbol;
                ClassDecl = classDecl;
                MethodSymbol = methodSymbol;
                MethodDecl = methodDecl;
                MethodName = methodName;
                IterKind = iterKind;
                Params = paramSlots;
                AspectData = aspectData;
                Criteria = criteria;
            }
        }
    }
}
