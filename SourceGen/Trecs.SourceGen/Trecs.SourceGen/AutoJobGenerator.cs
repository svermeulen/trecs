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
        const string GenPrefix = "_trecs_";

        public void Initialize(IncrementalGeneratorInitializationContext context)
        {
            var methodProvider = context
                .SyntaxProvider.CreateSyntaxProvider(
                    predicate: static (s, _) => IsCandidateMethod(s),
                    transform: static (ctx, _) => GetAutoJobData(ctx)
                )
                .Where(static d => d is not null);

            var methodWithCompilation = methodProvider.Combine(context.CompilationProvider);

            context.RegisterSourceOutput(
                methodWithCompilation,
                static (spc, source) => GenerateAutoJobSource(spc, source.Left!, source.Right)
            );
        }

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
                    else if (name == TrecsAttributeNames.EntityFilter)
                        hasEntityFilter = true;
                }
            }

            return hasWrapAsJob && hasEntityFilter;
        }

        // ─── Transform (syntax → data) ────────────────────────────────────────

        static AutoJobData? GetAutoJobData(GeneratorSyntaxContext context)
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

            return new AutoJobData(classDecl, methodDecl, methodSymbol);
        }

        // ─── Source output ─────────────────────────────────────────────────────

        static void GenerateAutoJobSource(
            SourceProductionContext context,
            AutoJobData data,
            Compilation compilation
        )
        {
            var location = data.MethodDecl.GetLocation();
            try
            {
                using var _t = SourceGenTimer.Time("AutoJobGenerator.Total");

                var info = Validate(context, data, compilation);
                if (info == null)
                    return;

                var source = GenerateSource(info);
                if (source == null)
                    return;

                var fileName = SymbolAnalyzer.GetSafeFileName(
                    data.MethodSymbol.ContainingType,
                    $"{data.MethodDecl.Identifier.Text}_AutoJob"
                );
                context.AddSource(fileName, source);
                SourceGenLogger.WriteGeneratedFile(fileName, source);
            }
            catch (Exception ex)
            {
                ErrorRecovery.ReportError(
                    context,
                    location,
                    $"AutoJob {data.MethodSymbol.ContainingType.Name}.{data.MethodDecl.Identifier.Text}",
                    ex
                );
            }
        }

        // ─── Validation ────────────────────────────────────────────────────────

        static AutoJobInfo? Validate(
            SourceProductionContext context,
            AutoJobData data,
            Compilation compilation
        )
        {
            var methodDecl = data.MethodDecl;
            var classDecl = data.ClassDecl;
            var methodSymbol = data.MethodSymbol;
            var classSymbol = methodSymbol.ContainingType;
            var methodName = methodDecl.Identifier.Text;
            var semanticModel = compilation.GetSemanticModel(methodDecl.SyntaxTree);

            // V1: Must be on a class, not a struct.
            if (classSymbol.TypeKind == TypeKind.Struct)
            {
                context.ReportDiagnostic(
                    Diagnostic.Create(
                        DiagnosticDescriptors.WrapAsJobOnStruct,
                        methodDecl.Identifier.GetLocation(),
                        methodName
                    )
                );
                return null;
            }

            // V2: Must be static.
            if (!methodSymbol.IsStatic)
            {
                context.ReportDiagnostic(
                    Diagnostic.Create(
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
                context.ReportDiagnostic(
                    Diagnostic.Create(
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
                context.ReportDiagnostic(
                    Diagnostic.Create(
                        DiagnosticDescriptors.InvalidReturnType,
                        methodDecl.ReturnType.GetLocation()
                    )
                );
                return null;
            }

            // V5: Must have at least one parameter.
            if (methodSymbol.Parameters.Length == 0)
            {
                context.ReportDiagnostic(
                    Diagnostic.Create(
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
                    context.ReportDiagnostic(
                        Diagnostic.Create(
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
            bool hasNwa = false;
            AspectIterationData? aspectData = null;

            foreach (var param in methodSymbol.Parameters)
            {
                var paramType = param.Type;
                var paramName = param.Name;

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
                        context.ReportDiagnostic(
                            Diagnostic.Create(
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
                        context.ReportDiagnostic(
                            Diagnostic.Create(
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

                // Check for WorldAccessor (forbidden on [WrapAsJob]).
                if (SymbolAnalyzer.IsExactType(paramType, "WorldAccessor", TrecsNamespaces.Trecs))
                {
                    context.ReportDiagnostic(
                        Diagnostic.Create(
                            DiagnosticDescriptors.WrapAsJobWorldAccessorParam,
                            param.Locations.FirstOrDefault() ?? methodDecl.GetLocation(),
                            methodName
                        )
                    );
                    return null;
                }

                // Check for NativeWorldAccessor.
                if (
                    SymbolAnalyzer.IsExactType(
                        paramType,
                        "NativeWorldAccessor",
                        TrecsNamespaces.Trecs
                    )
                )
                {
                    if (param.RefKind != RefKind.In)
                    {
                        context.ReportDiagnostic(
                            Diagnostic.Create(
                                DiagnosticDescriptors.InvalidParameterModifiers,
                                param.Locations.FirstOrDefault() ?? methodDecl.GetLocation(),
                                paramName
                            )
                        );
                        return null;
                    }
                    if (hasNwa)
                    {
                        context.ReportDiagnostic(
                            Diagnostic.Create(
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
                    paramType is INamedTypeSymbol namedNsr
                    && namedNsr.Name == "NativeSetRead"
                    && namedNsr.TypeArguments.Length == 1
                    && PerformanceCache.GetDisplayString(namedNsr.ContainingNamespace) == "Trecs"
                )
                {
                    if (param.RefKind != RefKind.In)
                    {
                        context.ReportDiagnostic(
                            Diagnostic.Create(
                                DiagnosticDescriptors.InvalidParameterModifiers,
                                param.Locations.FirstOrDefault() ?? methodDecl.GetLocation(),
                                paramName
                            )
                        );
                        return null;
                    }
                    var setTypeArgSymbol = namedNsr.TypeArguments[0];
                    var setTypeArg = PerformanceCache.GetDisplayString(setTypeArgSymbol);
                    paramSlots.Add(AutoJobParam.NativeSetRead(paramName, setTypeArg, setTypeArgSymbol));
                    continue;
                }

                // Check for NativeSetWrite<T>.
                if (
                    paramType is INamedTypeSymbol namedNsw
                    && namedNsw.Name == "NativeSetWrite"
                    && namedNsw.TypeArguments.Length == 1
                    && PerformanceCache.GetDisplayString(namedNsw.ContainingNamespace) == "Trecs"
                )
                {
                    if (param.RefKind != RefKind.In)
                    {
                        context.ReportDiagnostic(
                            Diagnostic.Create(
                                DiagnosticDescriptors.InvalidParameterModifiers,
                                param.Locations.FirstOrDefault() ?? methodDecl.GetLocation(),
                                paramName
                            )
                        );
                        return null;
                    }
                    var setTypeArgSymbol = namedNsw.TypeArguments[0];
                    var setTypeArg = PerformanceCache.GetDisplayString(setTypeArgSymbol);
                    paramSlots.Add(AutoJobParam.NativeSetWrite(paramName, setTypeArg, setTypeArgSymbol));
                    continue;
                }

                // Check for SetAccessor<T> / SetRead<T> / SetWrite<T> (main-thread only — forbidden in [WrapAsJob]).
                if (
                    paramType is INamedTypeSymbol namedSa
                    && (namedSa.Name == "SetAccessor" || namedSa.Name == "SetRead" || namedSa.Name == "SetWrite")
                    && namedSa.TypeArguments.Length == 1
                    && PerformanceCache.GetDisplayString(namedSa.ContainingNamespace) == "Trecs"
                )
                {
                    var saTypeArg = PerformanceCache.GetDisplayString(namedSa.TypeArguments[0]);
                    context.ReportDiagnostic(
                        Diagnostic.Create(
                            DiagnosticDescriptors.SetAccessorNotAllowedInJob,
                            param.Locations.FirstOrDefault() ?? methodDecl.GetLocation(),
                            paramName,
                            saTypeArg
                        )
                    );
                    return null;
                }

                // Check for EntityIndex.
                if (SymbolAnalyzer.IsExactType(paramType, "EntityIndex", TrecsNamespaces.Trecs))
                {
                    if (param.RefKind != RefKind.None)
                    {
                        context.ReportDiagnostic(
                            Diagnostic.Create(
                                DiagnosticDescriptors.ParameterMustBeByValue,
                                param.Locations.FirstOrDefault() ?? methodDecl.GetLocation(),
                                paramName
                            )
                        );
                        return null;
                    }
                    if (hasEntityIndex)
                    {
                        context.ReportDiagnostic(
                            Diagnostic.Create(
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

                // Check for IAspect.
                if (SymbolAnalyzer.ImplementsInterface(paramType, "IAspect", TrecsNamespaces.Trecs))
                {
                    if (param.RefKind == RefKind.Ref)
                    {
                        context.ReportDiagnostic(
                            Diagnostic.Create(
                                DiagnosticDescriptors.AspectParamMustBeIn,
                                param.Locations.FirstOrDefault() ?? methodDecl.GetLocation(),
                                paramName
                            )
                        );
                        return null;
                    }
                    if (param.RefKind != RefKind.In)
                    {
                        context.ReportDiagnostic(
                            Diagnostic.Create(
                                DiagnosticDescriptors.InvalidParameterModifiers,
                                param.Locations.FirstOrDefault() ?? methodDecl.GetLocation(),
                                paramName
                            )
                        );
                        return null;
                    }
                    if (hasAspect)
                    {
                        context.ReportDiagnostic(
                            Diagnostic.Create(
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
                        context.ReportDiagnostic(
                            Diagnostic.Create(
                                DiagnosticDescriptors.CouldNotResolveSymbol,
                                param.Locations.FirstOrDefault() ?? methodDecl.GetLocation(),
                                paramName
                            )
                        );
                        return null;
                    }

                    var parsed = AspectAttributeParser.ParseAspectData(
                        aspectNamedType,
                        context.ReportDiagnostic,
                        methodDecl.GetLocation()
                    );
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

                // Check for IEntityComponent.
                if (
                    SymbolAnalyzer.ImplementsInterface(
                        paramType,
                        "IEntityComponent",
                        TrecsNamespaces.Trecs
                    )
                )
                {
                    if (hasAspect)
                    {
                        // Mixed aspect and component params are not supported.
                        context.ReportDiagnostic(
                            Diagnostic.Create(
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
                        context.ReportDiagnostic(
                            Diagnostic.Create(
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
                        context.ReportDiagnostic(
                            Diagnostic.Create(
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
                        context.ReportDiagnostic(
                            Diagnostic.Create(
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
                    if (fwKind == FromWorldFieldKind.Unsupported
                        || fwKind == FromWorldFieldKind.NativeWorldAccessor
                        || fwKind == FromWorldFieldKind.NativeSetRead
                        || fwKind == FromWorldFieldKind.NativeSetWrite
                        || fwKind == FromWorldFieldKind.NativeComponentRead
                        || fwKind == FromWorldFieldKind.NativeComponentWrite)
                    {
                        context.ReportDiagnostic(
                            Diagnostic.Create(
                                DiagnosticDescriptors.FromWorldUnsupportedOnWrapAsJob,
                                param.Locations.FirstOrDefault() ?? methodDecl.GetLocation(),
                                paramName,
                                PerformanceCache.GetDisplayString(paramType)
                            )
                        );
                        return null;
                    }

                    // Parse inline Tag/Tags from [FromWorld(Tag=..., Tags=...)].
                    List<ITypeSymbol>? inlineTagTypes = null;
                    foreach (var attr in PerformanceCache.GetAttributes(param))
                    {
                        if (attr.AttributeClass?.Name != TrecsAttributeNames.FromWorld)
                            continue;

                        var tagTypes = new List<ITypeSymbol>();
                        ITypeSymbol? singleTag = null;

                        foreach (var named in attr.NamedArguments)
                        {
                            switch (named.Key)
                            {
                                case "Tags" when named.Value.Kind == TypedConstantKind.Array:
                                    foreach (var element in named.Value.Values)
                                        if (
                                            element.Kind == TypedConstantKind.Type
                                            && element.Value is ITypeSymbol et
                                        )
                                            tagTypes.Add(et);
                                    break;
                                case "Tag"
                                    when named.Value.Kind == TypedConstantKind.Type
                                        && named.Value.Value is ITypeSymbol t1:
                                    singleTag = t1;
                                    break;
                            }
                        }

                        if (singleTag != null && tagTypes.Count > 0)
                        {
                            context.ReportDiagnostic(
                                Diagnostic.Create(
                                    DiagnosticDescriptors.TagAndTagsBothSpecified,
                                    param.Locations.FirstOrDefault() ?? methodDecl.GetLocation(),
                                    paramName,
                                    "FromWorld"
                                )
                            );
                            return null;
                        }

                        if (singleTag != null)
                            tagTypes.Add(singleTag);

                        if (tagTypes.Count > 4)
                        {
                            context.ReportDiagnostic(
                                Diagnostic.Create(
                                    DiagnosticDescriptors.FromWorldTooManyInlineTags,
                                    param.Locations.FirstOrDefault() ?? methodDecl.GetLocation(),
                                    paramName,
                                    tagTypes.Count
                                )
                            );
                            return null;
                        }

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
                        || fwKind == FromWorldFieldKind.Group
                        || fwKind == FromWorldFieldKind.NativeEntitySetIndices
                        || fwKind == FromWorldFieldKind.NativeEntityHandleBuffer;

                    if (needsGroupResolution && inlineTagTypes == null)
                    {
                        context.ReportDiagnostic(
                            Diagnostic.Create(
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
                            fwAspectData = AspectAttributeParser.ParseAspectData(
                                aspectSymbol,
                                context.ReportDiagnostic,
                                methodDecl.GetLocation()
                            );
                            if (fwAspectData == null)
                                return null;
                        }
                    }

                    ITypeSymbol? genericArg = namedType.IsGenericType
                        ? namedType.TypeArguments[0]
                        : null;

                    // Use the generated struct field name as FieldName so shared emitters
                    // can reference it as _trecs_job.{FieldName}.
                    var fwFieldName = GenPrefix + "fw_" + paramName;

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
                context.ReportDiagnostic(
                    Diagnostic.Create(
                        DiagnosticDescriptors.InvalidParameterList,
                        param.Locations.FirstOrDefault() ?? methodDecl.GetLocation(),
                        $"Parameter '{paramName}' of type '{PerformanceCache.GetDisplayString(paramType)}' is not recognized. "
                            + "Expected: IAspect (in), IEntityComponent (in/ref), EntityIndex, NativeWorldAccessor, NativeSetRead<T>, NativeSetWrite<T>, [PassThroughArgument], or [FromWorld]."
                    )
                );
                return null;
            }

            // Must have at least one iteration target (aspect or component).
            bool hasComponents = paramSlots.Any(p => p.Role == AutoJobParamRole.Component);
            if (!hasAspect && !hasComponents)
            {
                context.ReportDiagnostic(
                    Diagnostic.Create(
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
                context.ReportDiagnostic(
                    Diagnostic.Create(
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
                context,
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
                context.ReportDiagnostic(
                    Diagnostic.Create(
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
                context.ReportDiagnostic(
                    Diagnostic.Create(
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

        // ─── Emission ──────────────────────────────────────────────────────────

        static string GenerateSource(AutoJobInfo info)
        {
            var sb = new StringBuilder();
            var usings = new HashSet<string>
            {
                "Unity.Collections",
                "Trecs",
                "Trecs.Internal",
                "Trecs.Collections",
                "Unity.Jobs",
            };

            // Add namespaces for NativeSetRead/NativeSetWrite type arguments.
            foreach (var p in info.Params)
            {
                if (
                    (p.Role == AutoJobParamRole.NativeSetRead || p.Role == AutoJobParamRole.NativeSetWrite)
                    && p.SetTypeArgSymbol != null
                )
                {
                    var ns2 = PerformanceCache.GetDisplayString(p.SetTypeArgSymbol.ContainingNamespace);
                    if (!string.IsNullOrEmpty(ns2) && ns2 != "<global namespace>")
                        usings.Add(ns2);
                    if (p.SetTypeArgSymbol.ContainingType != null)
                    {
                        var ctNs = PerformanceCache.GetDisplayString(
                            p.SetTypeArgSymbol.ContainingType.ContainingNamespace
                        );
                        if (!string.IsNullOrEmpty(ctNs) && ctNs != "<global namespace>")
                            usings.Add(ctNs);
                    }
                }
            }

            // Add namespaces for [FromWorld] parameter types and their inline tag types.
            foreach (var p in info.Params)
            {
                if (p.Role != AutoJobParamRole.FromWorld || p.Type == null)
                    continue;
                // The parameter type itself (e.g., MealNutritionView.NativeFactory).
                var fwNs = PerformanceCache.GetDisplayString(p.Type.ContainingNamespace);
                if (!string.IsNullOrEmpty(fwNs) && fwNs != "<global namespace>")
                    usings.Add(fwNs);
                if (p.Type is INamedTypeSymbol { ContainingType: { } ct })
                {
                    var ctNs = PerformanceCache.GetDisplayString(ct.ContainingNamespace);
                    if (!string.IsNullOrEmpty(ctNs) && ctNs != "<global namespace>")
                        usings.Add(ctNs);
                }
                // Inline tag types.
                var fwInfo = p.FromWorldInfo;
                if (fwInfo?.InlineTagTypes != null)
                {
                    foreach (var tagType in fwInfo.InlineTagTypes)
                    {
                        var tagNs = PerformanceCache.GetDisplayString(tagType.ContainingNamespace);
                        if (!string.IsNullOrEmpty(tagNs) && tagNs != "<global namespace>")
                            usings.Add(tagNs);
                    }
                }
                // Generic argument type.
                if (fwInfo?.GenericArgument != null)
                {
                    var gaNs = PerformanceCache.GetDisplayString(fwInfo.GenericArgument.ContainingNamespace);
                    if (!string.IsNullOrEmpty(gaNs) && gaNs != "<global namespace>")
                        usings.Add(gaNs);
                }
            }

            foreach (var u in usings.OrderBy(u => u))
                sb.AppendLine($"using {u};");
            sb.AppendLine();

            var ns = PerformanceCache.GetDisplayString(info.ClassSymbol.ContainingNamespace);
            sb.AppendLine($"namespace {ns}");
            sb.AppendLine("{");

            // Walk up containing types so the partial class is emitted in its proper
            // nesting context (matching the user's source declaration).
            var nesting = new List<INamedTypeSymbol>();
            for (var t = info.ClassSymbol.ContainingType; t != null; t = t.ContainingType)
                nesting.Add(t);
            nesting.Reverse();

            int indent = 1;
            foreach (var enclosing in nesting)
            {
                string enclosingInd = new(' ', indent * 4);
                string enclosingKind = enclosing.TypeKind == TypeKind.Struct ? "struct" : "class";
                sb.AppendLine($"{enclosingInd}partial {enclosingKind} {enclosing.Name}");
                sb.AppendLine($"{enclosingInd}{{");
                indent++;
            }

            string ind = new(' ', indent * 4);
            sb.AppendLine($"{ind}partial class {info.ClassSymbol.Name}");
            sb.AppendLine($"{ind}{{");
            indent++;
            ind = new string(' ', indent * 4);

            EmitJobStruct(sb, info, ind, indent);
            sb.AppendLine();
            if (info.HasSets)
            {
                EmitSparseShim(sb, info, ind);
                sb.AppendLine();
            }
            EmitScheduleOverloads(sb, info, ind);
            sb.AppendLine();
            EmitWrapperMethod(sb, info, ind);

            indent--;
            ind = new string(' ', indent * 4);
            sb.AppendLine($"{ind}}}");

            // Close enclosing types.
            for (int i = 0; i < nesting.Count; i++)
            {
                indent--;
                string closingInd = new(' ', indent * 4);
                sb.AppendLine($"{closingInd}}}");
            }

            sb.AppendLine("}");

            return sb.ToString();
        }

        // ─── Job struct emission ───────────────────────────────────────────────

        static string BufferFieldName(int index) => GenPrefix + "buf" + index;

        static void EmitJobStruct(StringBuilder sb, AutoJobInfo info, string ind, int indent)
        {
            var jobStructName = $"_{info.MethodName}_AutoJob";

            sb.AppendLine($"{ind}[Unity.Burst.BurstCompile]");
            sb.AppendLine($"{ind}private partial struct {jobStructName} : IJobFor");
            sb.AppendLine($"{ind}{{");

            string fieldInd = ind + "    ";

            // Buffer fields for iteration.
            var buffers = GetIterationBuffers(info);
            for (int i = 0; i < buffers.Count; i++)
            {
                var (type, readOnly) = buffers[i];
                if (!readOnly)
                    sb.AppendLine($"{fieldInd}[Unity.Collections.NativeDisableParallelForRestriction]");
                var bufferType = readOnly
                    ? $"NativeComponentBufferRead<{PerformanceCache.GetDisplayString(type)}>"
                    : $"NativeComponentBufferWrite<{PerformanceCache.GetDisplayString(type)}>";
                sb.AppendLine($"{fieldInd}internal {bufferType} {BufferFieldName(i)};");
            }

            // Group field — always needed for aspect (for EntityIndex ctor), or for
            // components when an EntityIndex parameter is present.
            if (info.NeedsGroupField)
                sb.AppendLine($"{fieldInd}internal Group {GenPrefix}Group;");

            // NativeWorldAccessor field.
            if (info.HasNativeWorldAccessor)
                sb.AppendLine($"{fieldInd}public NativeWorldAccessor {GenPrefix}nwa;");

            // PassThrough fields.
            foreach (var p in info.Params)
            {
                if (p.Role == AutoJobParamRole.PassThrough)
                    sb.AppendLine($"{fieldInd}public {p.TypeDisplay} {GenPrefix}pt_{p.Name};");
            }

            // NativeSetRead fields.
            foreach (var p in info.Params)
            {
                if (p.Role == AutoJobParamRole.NativeSetRead)
                    sb.AppendLine($"{fieldInd}public NativeSetRead<{p.SetTypeArg}> {GenPrefix}nsr_{p.Name};");
            }

            // NativeSetWrite fields.
            foreach (var p in info.Params)
            {
                if (p.Role == AutoJobParamRole.NativeSetWrite)
                    sb.AppendLine($"{fieldInd}public NativeSetWrite<{p.SetTypeArg}> {GenPrefix}nsw_{p.Name};");
            }

            // FromWorld fields.
            foreach (var p in info.Params)
            {
                if (p.Role != AutoJobParamRole.FromWorld)
                    continue;
                var fwKind = p.FromWorldInfo!.Kind;
                bool isWrite = fwKind == FromWorldFieldKind.NativeComponentBufferWrite
                    || fwKind == FromWorldFieldKind.NativeComponentLookupWrite;
                if (isWrite)
                    sb.AppendLine($"{fieldInd}[Unity.Collections.NativeDisableParallelForRestriction]");
                sb.AppendLine($"{fieldInd}public {p.TypeDisplay} {GenPrefix}fw_{p.Name};");
            }

            sb.AppendLine();

            // Execute(int i) shim.
            EmitExecuteShim(sb, info, fieldInd, indent + 1);

            sb.AppendLine($"{ind}}}");
        }

        static void EmitExecuteShim(StringBuilder sb, AutoJobInfo info, string ind, int indent)
        {
            sb.AppendLine($"{ind}public void Execute(int i)");
            sb.AppendLine($"{ind}{{");
            string body = ind + "    ";

            // Build EntityIndex if needed by any param.
            bool needsEntityIndex =
                info.HasEntityIndex || info.IterKind == AutoJobIterationKind.Aspect;
            if (needsEntityIndex)
                sb.AppendLine($"{body}var {GenPrefix}ei = new EntityIndex(i, {GenPrefix}Group);");

            if (info.IterKind == AutoJobIterationKind.Aspect)
            {
                // Construct the aspect from EntityIndex + buffer fields.
                var aspectInfo = info.AspectData!;
                var ctorArgs = string.Join(
                    ", ",
                    Enumerable.Range(0, aspectInfo.ComponentTypes.Count).Select(BufferFieldName)
                );
                sb.AppendLine(
                    $"{body}var {GenPrefix}view = new {aspectInfo.AspectTypeName}({GenPrefix}ei, {ctorArgs});"
                );
            }
            else
            {
                // Components path: create ref locals from buffers.
                foreach (var p in info.Params)
                {
                    if (p.Role != AutoJobParamRole.Component)
                        continue;
                    var refKind = p.IsRef ? "ref" : "ref readonly";
                    sb.AppendLine(
                        $"{body}{refKind} var {GenPrefix}v{p.BufferIndex} = ref {BufferFieldName(p.BufferIndex)}[i];"
                    );
                }
            }

            // Build the call arguments in original parameter order.
            var className = PerformanceCache.GetDisplayString(info.ClassSymbol);
            var callArgs = new List<string>();
            foreach (var p in info.Params)
            {
                switch (p.Role)
                {
                    case AutoJobParamRole.Aspect:
                        callArgs.Add($"in {GenPrefix}view");
                        break;
                    case AutoJobParamRole.Component:
                        var prefix = p.IsRef ? "ref" : "in";
                        callArgs.Add($"{prefix} {GenPrefix}v{p.BufferIndex}");
                        break;
                    case AutoJobParamRole.EntityIndex:
                        callArgs.Add($"{GenPrefix}ei");
                        break;
                    case AutoJobParamRole.NativeWorldAccessor:
                        callArgs.Add($"in {GenPrefix}nwa");
                        break;
                    case AutoJobParamRole.PassThrough:
                        callArgs.Add($"{GenPrefix}pt_{p.Name}");
                        break;
                    case AutoJobParamRole.NativeSetRead:
                        callArgs.Add($"in {GenPrefix}nsr_{p.Name}");
                        break;
                    case AutoJobParamRole.NativeSetWrite:
                        callArgs.Add($"in {GenPrefix}nsw_{p.Name}");
                        break;
                    case AutoJobParamRole.FromWorld:
                        callArgs.Add($"in {GenPrefix}fw_{p.Name}");
                        break;
                }
            }

            sb.AppendLine($"{body}{className}.{info.MethodName}({string.Join(", ", callArgs)});");
            sb.AppendLine($"{ind}}}");
        }

        // ─── Schedule overloads ────────────────────────────────────────────────

        static void EmitScheduleOverloads(StringBuilder sb, AutoJobInfo info, string ind)
        {
            var passThroughParams = info
                .Params.Where(p => p.Role == AutoJobParamRole.PassThrough)
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
            EmitWorldAccessorOverload(sb, info, ind, ptParamDecl, passThroughParams);
            sb.AppendLine();

            if (info.HasSets)
            {
                // (2a) Sparse ScheduleParallel: SparseQueryBuilder entry.
                EmitSparseQueryBuilderOverload(sb, info, ind, ptParamDecl, passThroughParams);
            }
            else
            {
                // (2b) Dense ScheduleParallel: QueryBuilder entry.
                EmitQueryBuilderOverload(sb, info, ind, ptParamDecl, passThroughParams);
            }
        }

        static void EmitWorldAccessorOverload(
            StringBuilder sb,
            AutoJobInfo info,
            string ind,
            string ptParamDecl,
            List<AutoJobParam> ptParams
        )
        {
            sb.AppendLine(
                $"{ind}private JobHandle {GenPrefix}ScheduleParallel_{info.MethodName}(WorldAccessor {GenPrefix}world{ptParamDecl}, JobHandle {GenPrefix}extraDeps = default)"
            );
            sb.AppendLine($"{ind}{{");
            string body = ind + "    ";

            if (info.HasSets)
            {
                // Build a SparseQueryBuilder by chaining .InSet<T>() for the first set type.
                var firstSetName = PerformanceCache.GetDisplayString(info.Criteria.SetTypes[0]);
                var args = new List<string>
                {
                    $"{GenPrefix}world.Query().InSet<{firstSetName}>()",
                };
                args.AddRange(ptParams.Select(p => p.Name));
                args.Add($"{GenPrefix}extraDeps");
                sb.AppendLine(
                    $"{body}return {GenPrefix}ScheduleParallel_{info.MethodName}({string.Join(", ", args)});"
                );
            }
            else
            {
                // Pass a bare QueryBuilder — the QueryBuilder overload applies attribute criteria.
                var args = new List<string> { $"{GenPrefix}world.Query()" };
                args.AddRange(ptParams.Select(p => p.Name));
                args.Add($"{GenPrefix}extraDeps");
                sb.AppendLine(
                    $"{body}return {GenPrefix}ScheduleParallel_{info.MethodName}({string.Join(", ", args)});"
                );
            }

            sb.AppendLine($"{ind}}}");
        }

        static void EmitQueryBuilderOverload(
            StringBuilder sb,
            AutoJobInfo info,
            string ind,
            string ptParamDecl,
            List<AutoJobParam> ptParams
        )
        {
            sb.AppendLine(
                $"{ind}private JobHandle {GenPrefix}ScheduleParallel_{info.MethodName}(QueryBuilder {GenPrefix}builder{ptParamDecl}, JobHandle {GenPrefix}extraDeps = default)"
            );
            sb.AppendLine($"{ind}{{");
            string body = ind + "    ";

            // Merge attribute criteria into the builder (for calls via the QBuilder entry directly).
            var chain = BuildAttributeCriteriaChain(info);
            if (chain.Length > 0)
                sb.AppendLine($"{body}{GenPrefix}builder = {GenPrefix}builder{chain};");

            sb.AppendLine(
                $"{body}Assert.That({GenPrefix}builder.HasAnyCriteria, \"_{info.MethodName}_AutoJob.ScheduleParallel requires query criteria.\");"
            );

            sb.AppendLine($"{body}var {GenPrefix}world = {GenPrefix}builder.World;");
            sb.AppendLine(
                $"{body}var {GenPrefix}scheduler = {GenPrefix}world.GetJobSchedulerForJob();"
            );
            sb.AppendLine($"{body}var {GenPrefix}allJobs = {GenPrefix}extraDeps;");

            // [FromWorld] hoisted setup: resolve TagSets and groups before the loop.
            var fwEmits = GetFromWorldEmits(info);
            if (fwEmits.Count > 0)
                FromWorldEmitter.EmitFromWorldHoistedSetup(sb, body, fwEmits);

            sb.AppendLine(
                $"{body}foreach (var {GenPrefix}slice in {GenPrefix}builder.GroupSlices())"
            );
            sb.AppendLine($"{body}{{");
            string innerBody = body + "    ";

            sb.AppendLine($"{innerBody}var {GenPrefix}group = {GenPrefix}slice.Group;");
            sb.AppendLine($"{innerBody}var {GenPrefix}count = {GenPrefix}slice.Count;");
            sb.AppendLine($"{innerBody}if ({GenPrefix}count == 0) continue;");
            sb.AppendLine();

            // Per-group deps.
            sb.AppendLine($"{innerBody}var {GenPrefix}deps = {GenPrefix}extraDeps;");

            var buffers = GetIterationBuffers(info);
            for (int i = 0; i < buffers.Count; i++)
            {
                var (type, readOnly) = buffers[i];
                var rid =
                    $"ResourceId.Component(ComponentTypeId<{PerformanceCache.GetDisplayString(type)}>.Value)";
                var method = readOnly ? "IncludeReadDep" : "IncludeWriteDep";
                sb.AppendLine(
                    $"{innerBody}{GenPrefix}deps = {GenPrefix}scheduler.{method}({GenPrefix}deps, {rid}, {GenPrefix}group);"
                );
            }

            // NativeSet dependency includes.
            foreach (var p in info.Params)
            {
                if (p.Role == AutoJobParamRole.NativeSetRead)
                    sb.AppendLine(
                        $"{innerBody}{GenPrefix}deps = {GenPrefix}world.IncludeNativeSetReadDepsForJob<{p.SetTypeArg}>({GenPrefix}deps);"
                    );
                else if (p.Role == AutoJobParamRole.NativeSetWrite)
                    sb.AppendLine(
                        $"{innerBody}{GenPrefix}deps = {GenPrefix}world.IncludeNativeSetWriteDepsForJob<{p.SetTypeArg}>({GenPrefix}deps);"
                    );
            }

            // [FromWorld] dependency registration.
            if (fwEmits.Count > 0)
                FromWorldEmitter.EmitFromWorldDepRegistration(sb, innerBody, fwEmits);

            // Materialise iteration buffers.
            for (int i = 0; i < buffers.Count; i++)
            {
                var (type, readOnly) = buffers[i];
                var typeName = PerformanceCache.GetDisplayString(type);
                var ext = readOnly ? "GetBufferReadForJob" : "GetBufferWriteForJob";
                sb.AppendLine(
                    $"{innerBody}var ({GenPrefix}buf{i}_value, _) = {GenPrefix}world.{ext}<{typeName}>({GenPrefix}group);"
                );
            }

            // Construct the job.
            var jobStructName = $"_{info.MethodName}_AutoJob";
            sb.AppendLine($"{innerBody}var {GenPrefix}job = new {jobStructName}();");

            // Assign iteration buffers.
            for (int i = 0; i < buffers.Count; i++)
                sb.AppendLine(
                    $"{innerBody}{GenPrefix}job.{BufferFieldName(i)} = {GenPrefix}buf{i}_value;"
                );

            if (info.NeedsGroupField)
                sb.AppendLine($"{innerBody}{GenPrefix}job.{GenPrefix}Group = {GenPrefix}group;");

            if (info.HasNativeWorldAccessor)
                sb.AppendLine(
                    $"{innerBody}{GenPrefix}job.{GenPrefix}nwa = {GenPrefix}world.ToNative();"
                );

            // Assign passthrough fields.
            foreach (var p in ptParams)
                sb.AppendLine($"{innerBody}{GenPrefix}job.{GenPrefix}pt_{p.Name} = {p.Name};");

            // Assign NativeSet fields.
            foreach (var p in info.Params)
            {
                if (p.Role == AutoJobParamRole.NativeSetRead)
                    sb.AppendLine(
                        $"{innerBody}{GenPrefix}job.{GenPrefix}nsr_{p.Name} = {GenPrefix}world.CreateNativeSetReadForJob<{p.SetTypeArg}>();"
                    );
                else if (p.Role == AutoJobParamRole.NativeSetWrite)
                    sb.AppendLine(
                        $"{innerBody}{GenPrefix}job.{GenPrefix}nsw_{p.Name} = {GenPrefix}world.CreateNativeSetWriteForJob<{p.SetTypeArg}>();"
                    );
            }

            // Assign [FromWorld] fields.
            if (fwEmits.Count > 0)
                FromWorldEmitter.EmitFromWorldFieldAssignments(sb, innerBody, fwEmits);

            // Schedule.
            sb.AppendLine(
                $"{innerBody}var {GenPrefix}handle = {GenPrefix}job.ScheduleParallel({GenPrefix}count, JobsUtil.ChooseBatchSize({GenPrefix}count), {GenPrefix}deps);"
            );

            // Track outputs.
            for (int i = 0; i < buffers.Count; i++)
            {
                var (type, readOnly) = buffers[i];
                var rid =
                    $"ResourceId.Component(ComponentTypeId<{PerformanceCache.GetDisplayString(type)}>.Value)";
                var method = readOnly ? "TrackJobRead" : "TrackJobWrite";
                sb.AppendLine(
                    $"{innerBody}{GenPrefix}scheduler.{method}({GenPrefix}handle, {rid}, {GenPrefix}group);"
                );
            }

            // Track NativeSet deps.
            foreach (var p in info.Params)
            {
                if (p.Role == AutoJobParamRole.NativeSetRead)
                    sb.AppendLine(
                        $"{innerBody}{GenPrefix}world.TrackNativeSetReadDepsForJob<{p.SetTypeArg}>({GenPrefix}handle);"
                    );
                else if (p.Role == AutoJobParamRole.NativeSetWrite)
                    sb.AppendLine(
                        $"{innerBody}{GenPrefix}world.TrackNativeSetWriteDepsForJob<{p.SetTypeArg}>({GenPrefix}handle);"
                    );
            }

            // Track [FromWorld] deps.
            if (fwEmits.Count > 0)
                FromWorldEmitter.EmitFromWorldTracking(sb, innerBody, fwEmits);

            // NativeWorldAccessor performs structural operations (add/remove/move)
            // that write to shared native queues. The job must complete before
            // SubmitEntities processes those queues at the next phase boundary.
            if (info.HasNativeWorldAccessor)
                sb.AppendLine(
                    $"{innerBody}{GenPrefix}scheduler.TrackJob({GenPrefix}handle);"
                );

            sb.AppendLine(
                $"{innerBody}{GenPrefix}allJobs = JobHandle.CombineDependencies({GenPrefix}allJobs, {GenPrefix}handle);"
            );

            sb.AppendLine($"{body}}}");
            sb.AppendLine($"{body}return {GenPrefix}allJobs;");
            sb.AppendLine($"{ind}}}");
        }

        // ─── Sparse schedule overload ──────────────────────────────────────────

        static void EmitSparseQueryBuilderOverload(
            StringBuilder sb,
            AutoJobInfo info,
            string ind,
            string ptParamDecl,
            List<AutoJobParam> ptParams
        )
        {
            sb.AppendLine(
                $"{ind}private JobHandle {GenPrefix}ScheduleParallel_{info.MethodName}(SparseQueryBuilder {GenPrefix}builder{ptParamDecl}, JobHandle {GenPrefix}extraDeps = default)"
            );
            sb.AppendLine($"{ind}{{");
            string body = ind + "    ";

            // Merge attribute Tags + Components into the builder. Sets are NOT merged here —
            // they were already added by the convenience overload via .InSet<>().
            var chain = BuildAttributeCriteriaChain(info);
            if (chain.Length > 0)
                sb.AppendLine($"{body}{GenPrefix}builder = {GenPrefix}builder{chain};");

            sb.AppendLine($"{body}var {GenPrefix}world = {GenPrefix}builder.World;");
            sb.AppendLine(
                $"{body}var {GenPrefix}scheduler = {GenPrefix}world.GetJobSchedulerForJob();"
            );
            sb.AppendLine($"{body}var {GenPrefix}allJobs = {GenPrefix}extraDeps;");

            // [FromWorld] hoisted setup: resolve TagSets and groups before the loop.
            var fwEmits = GetFromWorldEmits(info);
            if (fwEmits.Count > 0)
                FromWorldEmitter.EmitFromWorldHoistedSetup(sb, body, fwEmits);

            sb.AppendLine(
                $"{body}foreach (var {GenPrefix}slice in {GenPrefix}builder.GroupSlices())"
            );
            sb.AppendLine($"{body}{{");
            string innerBody = body + "    ";

            sb.AppendLine($"{innerBody}var {GenPrefix}group = {GenPrefix}slice.Group;");
            sb.AppendLine();

            // Pre-walk the slice into a TempJob-backed sparse-indices buffer.
            sb.AppendLine(
                $"{innerBody}var ({GenPrefix}indices, {GenPrefix}indicesLifetime, {GenPrefix}count) = {GenPrefix}world.AllocateSparseIndicesForJob({GenPrefix}slice);"
            );
            sb.AppendLine($"{innerBody}if ({GenPrefix}count == 0)");
            sb.AppendLine($"{innerBody}{{");
            sb.AppendLine($"{innerBody}    {GenPrefix}indicesLifetime.Dispose();");
            sb.AppendLine($"{innerBody}    continue;");
            sb.AppendLine($"{innerBody}}}");
            sb.AppendLine();

            // Per-group deps.
            sb.AppendLine($"{innerBody}var {GenPrefix}deps = {GenPrefix}extraDeps;");

            var buffers = GetIterationBuffers(info);
            for (int i = 0; i < buffers.Count; i++)
            {
                var (type, readOnly) = buffers[i];
                var rid =
                    $"ResourceId.Component(ComponentTypeId<{PerformanceCache.GetDisplayString(type)}>.Value)";
                var method = readOnly ? "IncludeReadDep" : "IncludeWriteDep";
                sb.AppendLine(
                    $"{innerBody}{GenPrefix}deps = {GenPrefix}scheduler.{method}({GenPrefix}deps, {rid}, {GenPrefix}group);"
                );
            }

            // NativeSet dependency includes.
            foreach (var p in info.Params)
            {
                if (p.Role == AutoJobParamRole.NativeSetRead)
                    sb.AppendLine(
                        $"{innerBody}{GenPrefix}deps = {GenPrefix}world.IncludeNativeSetReadDepsForJob<{p.SetTypeArg}>({GenPrefix}deps);"
                    );
                else if (p.Role == AutoJobParamRole.NativeSetWrite)
                    sb.AppendLine(
                        $"{innerBody}{GenPrefix}deps = {GenPrefix}world.IncludeNativeSetWriteDepsForJob<{p.SetTypeArg}>({GenPrefix}deps);"
                    );
            }

            // [FromWorld] dependency registration.
            if (fwEmits.Count > 0)
                FromWorldEmitter.EmitFromWorldDepRegistration(sb, innerBody, fwEmits);

            // Materialise iteration buffers.
            for (int i = 0; i < buffers.Count; i++)
            {
                var (type, readOnly) = buffers[i];
                var typeName = PerformanceCache.GetDisplayString(type);
                var ext = readOnly ? "GetBufferReadForJob" : "GetBufferWriteForJob";
                sb.AppendLine(
                    $"{innerBody}var ({GenPrefix}buf{i}_value, _) = {GenPrefix}world.{ext}<{typeName}>({GenPrefix}group);"
                );
            }

            // Construct the job.
            var jobStructName = $"_{info.MethodName}_AutoJob";
            sb.AppendLine($"{innerBody}var {GenPrefix}job = new {jobStructName}();");

            // Assign iteration buffers.
            for (int i = 0; i < buffers.Count; i++)
                sb.AppendLine(
                    $"{innerBody}{GenPrefix}job.{BufferFieldName(i)} = {GenPrefix}buf{i}_value;"
                );

            if (info.NeedsGroupField)
                sb.AppendLine($"{innerBody}{GenPrefix}job.{GenPrefix}Group = {GenPrefix}group;");

            if (info.HasNativeWorldAccessor)
                sb.AppendLine(
                    $"{innerBody}{GenPrefix}job.{GenPrefix}nwa = {GenPrefix}world.ToNative();"
                );

            // Assign passthrough fields.
            foreach (var p in ptParams)
                sb.AppendLine($"{innerBody}{GenPrefix}job.{GenPrefix}pt_{p.Name} = {p.Name};");

            // Assign NativeSet fields.
            foreach (var p in info.Params)
            {
                if (p.Role == AutoJobParamRole.NativeSetRead)
                    sb.AppendLine(
                        $"{innerBody}{GenPrefix}job.{GenPrefix}nsr_{p.Name} = {GenPrefix}world.CreateNativeSetReadForJob<{p.SetTypeArg}>();"
                    );
                else if (p.Role == AutoJobParamRole.NativeSetWrite)
                    sb.AppendLine(
                        $"{innerBody}{GenPrefix}job.{GenPrefix}nsw_{p.Name} = {GenPrefix}world.CreateNativeSetWriteForJob<{p.SetTypeArg}>();"
                    );
            }

            // Assign [FromWorld] fields.
            if (fwEmits.Count > 0)
                FromWorldEmitter.EmitFromWorldFieldAssignments(sb, innerBody, fwEmits);

            // Wrap in sparse shim and schedule.
            var shimName = $"_{info.MethodName}_SparseShim";
            sb.AppendLine(
                $"{innerBody}var {GenPrefix}shim = new {shimName} {{ Inner = {GenPrefix}job, Indices = {GenPrefix}indices }};"
            );
            sb.AppendLine(
                $"{innerBody}var {GenPrefix}handle = {GenPrefix}shim.ScheduleParallel({GenPrefix}count, JobsUtil.ChooseBatchSize({GenPrefix}count), {GenPrefix}deps);"
            );

            // Track outputs.
            for (int i = 0; i < buffers.Count; i++)
            {
                var (type, readOnly) = buffers[i];
                var rid =
                    $"ResourceId.Component(ComponentTypeId<{PerformanceCache.GetDisplayString(type)}>.Value)";
                var method = readOnly ? "TrackJobRead" : "TrackJobWrite";
                sb.AppendLine(
                    $"{innerBody}{GenPrefix}scheduler.{method}({GenPrefix}handle, {rid}, {GenPrefix}group);"
                );
            }

            // Track NativeSet deps.
            foreach (var p in info.Params)
            {
                if (p.Role == AutoJobParamRole.NativeSetRead)
                    sb.AppendLine(
                        $"{innerBody}{GenPrefix}world.TrackNativeSetReadDepsForJob<{p.SetTypeArg}>({GenPrefix}handle);"
                    );
                else if (p.Role == AutoJobParamRole.NativeSetWrite)
                    sb.AppendLine(
                        $"{innerBody}{GenPrefix}world.TrackNativeSetWriteDepsForJob<{p.SetTypeArg}>({GenPrefix}handle);"
                    );
            }

            // Track [FromWorld] deps.
            if (fwEmits.Count > 0)
                FromWorldEmitter.EmitFromWorldTracking(sb, innerBody, fwEmits);

            // NativeWorldAccessor performs structural operations (add/remove/move)
            // that write to shared native queues. The job must complete before
            // SubmitEntities processes those queues at the next phase boundary.
            if (info.HasNativeWorldAccessor)
                sb.AppendLine(
                    $"{innerBody}{GenPrefix}scheduler.TrackJob({GenPrefix}handle);"
                );

            // Dispose the indices lifetime after the job completes.
            sb.AppendLine(
                $"{innerBody}var {GenPrefix}disposeHandle = {GenPrefix}indicesLifetime.Dispose({GenPrefix}handle);"
            );
            sb.AppendLine($"{innerBody}{GenPrefix}scheduler.TrackJob({GenPrefix}disposeHandle);");
            sb.AppendLine(
                $"{innerBody}{GenPrefix}allJobs = JobHandle.CombineDependencies({GenPrefix}allJobs, {GenPrefix}disposeHandle);"
            );

            sb.AppendLine($"{body}}}");
            sb.AppendLine($"{body}return {GenPrefix}allJobs;");
            sb.AppendLine($"{ind}}}");
        }

        static void EmitSparseShim(StringBuilder sb, AutoJobInfo info, string ind)
        {
            var jobStructName = $"_{info.MethodName}_AutoJob";
            var shimName = $"_{info.MethodName}_SparseShim";
            string innerInd = ind + "    ";
            string bodyInd = innerInd + "    ";

            sb.AppendLine($"{ind}[Unity.Burst.BurstCompile]");
            sb.AppendLine($"{ind}private struct {shimName} : IJobFor");
            sb.AppendLine($"{ind}{{");
            sb.AppendLine($"{innerInd}public {jobStructName} Inner;");
            sb.AppendLine($"{innerInd}public JobSparseIndices Indices;");
            sb.AppendLine();
            sb.AppendLine($"{innerInd}public void Execute(int i)");
            sb.AppendLine($"{innerInd}{{");
            sb.AppendLine($"{bodyInd}Inner.Execute(Indices[i]);");
            sb.AppendLine($"{innerInd}}}");
            sb.AppendLine($"{ind}}}");
        }

        // ─── Wrapper method ────────────────────────────────────────────────────

        static void EmitWrapperMethod(StringBuilder sb, AutoJobInfo info, string ind)
        {
            var ptParams = info.Params.Where(p => p.Role == AutoJobParamRole.PassThrough).ToList();
            var ptParamDecl =
                ptParams.Count == 0
                    ? ""
                    : string.Join(", ", ptParams.Select(p => $"{p.TypeDisplay} {p.Name}"));

            var visibility = info.MethodName == "Execute" ? "public " : "";
            sb.AppendLine($"{ind}{visibility}void {info.MethodName}({ptParamDecl})");
            sb.AppendLine($"{ind}{{");
            string body = ind + "    ";

            var args = new List<string> { "this.World" };
            args.AddRange(ptParams.Select(p => p.Name));

            sb.AppendLine(
                $"{body}{GenPrefix}ScheduleParallel_{info.MethodName}({string.Join(", ", args)});"
            );
            sb.AppendLine($"{ind}}}");
        }

        // ─── Helpers ───────────────────────────────────────────────────────────

        static List<FromWorldFieldEmit> GetFromWorldEmits(AutoJobInfo info)
        {
            return info.Params
                .Where(p => p.Role == AutoJobParamRole.FromWorld)
                .Select(p => FromWorldFieldEmit.Build(p.FromWorldInfo!, suppressScheduleParam: true))
                .ToList();
        }

        static IReadOnlyList<(ITypeSymbol Type, bool ReadOnly)> GetIterationBuffers(
            AutoJobInfo info
        )
        {
            if (info.IterKind == AutoJobIterationKind.Aspect)
            {
                var ai = info.AspectData!;
                var result = new List<(ITypeSymbol, bool)>(ai.ComponentTypes.Count);
                foreach (var t in ai.ComponentTypes)
                    result.Add((t, ai.IsReadOnly(t)));
                return result;
            }
            else
            {
                var result = new List<(ITypeSymbol, bool)>();
                foreach (var p in info.Params)
                {
                    if (p.Role == AutoJobParamRole.Component)
                        result.Add((p.Type!, !p.IsRef));
                }
                return result;
            }
        }

        static string BuildAttributeCriteriaChain(AutoJobInfo info)
        {
            var c = info.Criteria;
            var componentTypes = info.IterKind == AutoJobIterationKind.Aspect
                ? (IEnumerable<ITypeSymbol>)info.AspectData!.ComponentTypes
                : info.Params
                    .Where(p => p.Role == AutoJobParamRole.Component)
                    .Select(p => p.Type!);
            return QueryBuilderHelper.BuildAttributeCriteriaChain(
                c.TagTypes, c.MatchByComponents, componentTypes);
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
            NativeWorldAccessor,
            PassThrough,
            NativeSetRead,
            NativeSetWrite,
            FromWorld,
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
            /// For NativeSetRead/NativeSetWrite roles, the generic type argument display string
            /// (e.g. "MyNamespace.MySet"). Null for other roles.
            /// </summary>
            public string? SetTypeArg { get; }

            /// <summary>
            /// For NativeSetRead/NativeSetWrite roles, the resolved type symbol for the generic
            /// type argument. Used for namespace resolution. Null for other roles.
            /// </summary>
            public ITypeSymbol? SetTypeArgSymbol { get; }

            /// <summary>
            /// For FromWorld role, the parsed [FromWorld] field info used for emission.
            /// Null for other roles.
            /// </summary>
            public FromWorldFieldInfo? FromWorldInfo { get; }

            AutoJobParam(
                AutoJobParamRole role,
                ITypeSymbol? type,
                string name,
                string typeDisplay,
                bool isRef = false,
                int bufferIndex = -1,
                string? setTypeArg = null,
                ITypeSymbol? setTypeArgSymbol = null,
                FromWorldFieldInfo? fromWorldInfo = null
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

            public static AutoJobParam NativeSetWrite(
                string name,
                string setTypeArg,
                ITypeSymbol setTypeArgSymbol
            ) =>
                new(
                    AutoJobParamRole.NativeSetWrite,
                    null,
                    name,
                    $"NativeSetWrite<{setTypeArg}>",
                    setTypeArg: setTypeArg,
                    setTypeArgSymbol: setTypeArgSymbol
                );

            public static AutoJobParam FromWorld(
                ITypeSymbol type,
                string name,
                string typeDisplay,
                FromWorldFieldInfo fromWorldInfo
            ) => new(AutoJobParamRole.FromWorld, type, name, typeDisplay, fromWorldInfo: fromWorldInfo);
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
            public bool HasNativeWorldAccessor =>
                Params.Any(p => p.Role == AutoJobParamRole.NativeWorldAccessor);

            /// <summary>
            /// True if the generated job needs a <c>_trecs_Group</c> field. Aspect always
            /// needs it (for the aspect ctor). Components only need it when the user took
            /// an EntityIndex parameter.
            /// </summary>
            public bool NeedsGroupField =>
                IterKind == AutoJobIterationKind.Aspect || HasEntityIndex;

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
