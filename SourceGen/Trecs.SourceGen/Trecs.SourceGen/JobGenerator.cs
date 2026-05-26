#nullable enable

using System;
using System.Collections.Generic;
using System.Linq;
using System.Text;
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
    /// Source generator for Trecs jobs declared as <c>partial struct</c>s with either:
    /// <list type="bullet">
    /// <item><description>An <c>Execute</c> method decorated with <c>[ForEachEntity]</c>
    /// (iteration job — auto-routes to component-iteration or aspect-iteration based on
    /// the parameter type), or</description></item>
    /// <item><description>One or more <c>[FromWorld]</c>-decorated fields with no
    /// iteration attribute (custom non-iteration job; emits an <c>IJob</c> wrapper).</description></item>
    /// </list>
    /// <para>
    /// The generator emits <c>ScheduleParallel(WorldAccessor)</c> /
    /// <c>(QueryBuilder)</c> / <c>(SparseQueryBuilder)</c> overloads for iteration
    /// jobs and a single <c>Schedule(WorldAccessor, ...)</c> for custom jobs. All
    /// dependency tracking is emitted inline (no <c>JobQueryContext</c> accumulation),
    /// so a job can have any number of <c>[FromWorld]</c> fields with no fixed cap.
    /// </para>
    /// </summary>
    [Generator]
    public class JobGenerator : IIncrementalGenerator
    {
        const string EXECUTE_METHOD_NAME = "Execute";

        public void Initialize(IncrementalGeneratorInitializationContext context)
        {
            // Equatable-pipeline shape: the transform stage produces a value-equatable
            // JobModel (strings + EquatableArrays — no symbols, no syntax nodes, no raw
            // Diagnostics). The terminal RegisterSourceOutput stage materializes
            // diagnostics and emits source. The compilation's global-namespace name
            // folds in via a lightweight string Combine — required by FromWorldFieldEmit
            // projection's namespace filtering, even though the JobGenerator itself
            // doesn't emit using statements based on collected namespaces.
            var modelsRaw = context
                .SyntaxProvider.CreateSyntaxProvider(
                    predicate: static (s, _) => IsCandidateJobStruct(s),
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
                static (spc, source) => GenerateJobSource(spc, source.Left, source.Right)
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

        // Syntactic type-name prefixes that identify Trecs container types. Used by
        // IsCandidateJobStruct to catch structs with container fields but no [FromWorld].
        // Semantic validation (namespace check) happens later in FromWorldClassifier.Classify.
        static readonly string[] ContainerTypePrefixes =
        {
            "NativeComponentBufferRead",
            "NativeComponentBufferWrite",
            "NativeComponentRead",
            "NativeComponentWrite",
            "NativeComponentLookupRead",
            "NativeComponentLookupWrite",
            "NativeSetCommandBuffer",
            "NativeEntitySetIndices",
            "NativeSetRead",
        };

        static bool IsCandidateJobStruct(SyntaxNode node)
        {
            if (node is not StructDeclarationSyntax structDecl)
                return false;
            // Has [ForEachEntity] on a method?
            var hasIterationAttr = structDecl
                .Members.OfType<MethodDeclarationSyntax>()
                .Any(m =>
                    m.AttributeLists.SelectMany(al => al.Attributes)
                        .Any(attr =>
                            IterationCriteriaParser.ExtractAttributeName(attr.Name.ToString())
                            == TrecsAttributeNames.ForEachEntity
                        )
                );
            if (hasIterationAttr)
                return true;

            // Has [FromWorld] on any field?
            var hasFromWorldField = structDecl
                .Members.OfType<FieldDeclarationSyntax>()
                .Any(f =>
                    f.AttributeLists.SelectMany(al => al.Attributes)
                        .Any(attr =>
                            IterationCriteriaParser.ExtractAttributeName(attr.Name.ToString())
                            == TrecsAttributeNames.FromWorld
                        )
                );
            if (hasFromWorldField)
                return true;

            // Has an Execute method AND a field whose type name looks like a Trecs
            // container? This catches structs where the user forgot [FromWorld] on ALL
            // container fields — without this check, the generator would never see them
            // and TRECS081 would not fire. Only match partial structs here: non-partial
            // structs with container fields but no [FromWorld]/[ForEachEntity] may be
            // intentionally manually scheduled (e.g. InterpolatedPreviousSaver.SavePreviousJob).
            if (!structDecl.Modifiers.Any(m => m.IsKind(SyntaxKind.PartialKeyword)))
                return false;
            var hasExecute = structDecl
                .Members.OfType<MethodDeclarationSyntax>()
                .Any(m => m.Identifier.Text == "Execute");
            if (hasExecute)
            {
                var hasContainerField = structDecl
                    .Members.OfType<FieldDeclarationSyntax>()
                    .Any(f => HasContainerTypeName(f.Declaration.Type));
                if (hasContainerField)
                    return true;
            }
            return false;
        }

        static bool HasContainerTypeName(TypeSyntax typeSyntax)
        {
            // Extract the identifier from the type syntax (handles both simple and
            // generic names, e.g. "NativeComponentBufferRead<CPosition>").
            string typeName;
            if (typeSyntax is GenericNameSyntax gns)
                typeName = gns.Identifier.Text;
            else if (typeSyntax is QualifiedNameSyntax qns && qns.Right is GenericNameSyntax qgns)
                typeName = qgns.Identifier.Text;
            else
                return false;

            foreach (var prefix in ContainerTypePrefixes)
            {
                if (typeName == prefix)
                    return true;
            }
            return false;
        }

        /// <summary>
        /// Transform stage: validates a candidate job struct and projects the result
        /// (success or failure) into a value-equatable <see cref="JobModel"/>. The
        /// terminal stage materializes diagnostics and emits source from the model.
        /// </summary>
        static JobModel? BuildModel(GeneratorSyntaxContext context)
        {
            var structDecl = (StructDeclarationSyntax)context.Node;
            var symbol = context.SemanticModel.GetDeclaredSymbol(structDecl) as INamedTypeSymbol;
            if (symbol == null)
                return null;

            var diagnostics = new List<DiagnosticInfo>();
            Action<DiagnosticInfo> reporter = diagnostics.Add;

            JobInfo? info;
            try
            {
                info = ValidateJobStruct(
                    reporter,
                    new JobStructData(structDecl, symbol),
                    context.SemanticModel
                );
            }
            catch (Exception ex)
            {
                diagnostics.Add(
                    DiagnosticInfo.Create(
                        DiagnosticDescriptors.SourceGenerationError,
                        structDecl.GetLocation(),
                        $"Job {symbol.Name}",
                        ex.Message
                    )
                );
                info = null;
            }

            return BuildJobModelFromInfo(info, symbol, diagnostics);
        }

        /// <summary>
        /// Terminal stage: surface accumulated diagnostics, then emit source from the
        /// JobModel. Skips emission when validation failed (so the user sees the
        /// diagnostics without also being yelled at by a downstream CS error from a
        /// half-formed partial declaration).
        /// </summary>
        static void GenerateJobSource(
            SourceProductionContext context,
            JobModel model,
            string globalNamespaceName
        )
        {
            foreach (var d in model.Diagnostics)
                context.ReportDiagnostic(d.ToDiagnostic());

            if (!model.IsValid)
                return;

            try
            {
                using var _t = SourceGenTimer.Time("JobGenerator.Total");

                var source = GenerateSource(model);
                if (source == null)
                    return;

                context.AddSource(model.HintFileName, source);
                SourceGenLogger.WriteGeneratedFile(model.HintFileName, source);
            }
            catch (Exception ex)
            {
                ErrorRecovery.ReportError(context, Location.None, $"Job {model.StructName}", ex);
            }
        }

        // ─── Validation ─────────────────────────────────────────────────────────

        static JobInfo? ValidateJobStruct(
            Action<DiagnosticInfo> diagnostics,
            JobStructData data,
            SemanticModel semanticModel
        )
        {
            var structDecl = data.StructDecl;
            var symbol = data.Symbol;

            // Reject jobs nested inside a generic enclosing type — the partial we emit
            // would need to redeclare the outer type's type parameters and constraints,
            // which the generator doesn't currently support. Move the job out of the
            // generic outer type, or factor it into a helper.
            for (var t = symbol.ContainingType; t != null; t = t.ContainingType)
            {
                if (t.IsGenericType)
                {
                    diagnostics(
                        DiagnosticInfo.Create(
                            DiagnosticDescriptors.JobInsideGenericOuterTypeNotSupported,
                            structDecl.Identifier.GetLocation(),
                            symbol.Name,
                            t.Name
                        )
                    );
                    return null;
                }
            }

            // Must be partial — the generator emits a partial declaration with schedule
            // methods and additional fields. Without partial, nothing is generated.
            if (!structDecl.Modifiers.Any(m => m.IsKind(SyntaxKind.PartialKeyword)))
            {
                diagnostics(
                    DiagnosticInfo.Create(
                        DiagnosticDescriptors.JobMustBePartial,
                        structDecl.Identifier.GetLocation(),
                        symbol.Name
                    )
                );
                return null;
            }

            // Find the (single) iteration-attribute Execute method. There must be at most
            // one [ForEachEntity] Execute and it must be void / non-static.
            MethodDeclarationSyntax? iterationMethod = null;
            foreach (var method in structDecl.Members.OfType<MethodDeclarationSyntax>())
            {
                if (method.Identifier.Text != EXECUTE_METHOD_NAME)
                    continue;

                bool hasEntityFilter = method
                    .AttributeLists.SelectMany(al => al.Attributes)
                    .Any(attr =>
                        IterationCriteriaParser.ExtractAttributeName(attr.Name.ToString())
                        == TrecsAttributeNames.ForEachEntity
                    );

                if (!hasEntityFilter)
                    continue;

                if (iterationMethod != null)
                {
                    diagnostics(
                        DiagnosticInfo.Create(
                            DiagnosticDescriptors.EntityJobGroupMultipleExecuteMethods,
                            structDecl.Identifier.GetLocation(),
                            symbol.Name
                        )
                    );
                    return null;
                }
                iterationMethod = method;
            }

            // Custom jobs: [FromWorld] fields with no [ForEachX] on any Execute method.
            //
            // Two flavors based on the user's Execute signature:
            //   - 'public void Execute()'      → CustomNonIteration → IJob
            //   - 'public void Execute(int i)' → CustomParallelIteration → IJobFor
            //
            // Neither emits an Execute shim — the user's public Execute directly satisfies
            // IJob.Execute() / IJobFor.Execute(int) respectively.
            if (iterationMethod == null)
            {
                bool hasFromWorld = structDecl
                    .Members.OfType<FieldDeclarationSyntax>()
                    .Any(f =>
                        f.AttributeLists.SelectMany(al => al.Attributes)
                            .Any(attr =>
                                IterationCriteriaParser.ExtractAttributeName(attr.Name.ToString())
                                == TrecsAttributeNames.FromWorld
                            )
                    );
                if (!hasFromWorld)
                    return null; // Defensive — predicate already filters this out.

                // Discriminate by Execute parameter shape: parameterless → IJob,
                // single int parameter → IJobFor.
                bool hasParallelExecute = structDecl
                    .Members.OfType<MethodDeclarationSyntax>()
                    .Any(m =>
                        m.Identifier.Text == EXECUTE_METHOD_NAME
                        && m.ParameterList.Parameters.Count == 1
                        && m.ParameterList.Parameters[0].Type?.ToString() == "int"
                    );

                if (hasParallelExecute)
                    return ValidateCustomParallelIterationJob(
                        diagnostics,
                        structDecl,
                        symbol,
                        semanticModel
                    );

                return ValidateCustomNonIterationJob(
                    diagnostics,
                    structDecl,
                    symbol,
                    semanticModel
                );
            }

            // Validate the iteration method shape.
            if (iterationMethod.ReturnType.ToString() != "void")
            {
                diagnostics(
                    DiagnosticInfo.Create(
                        DiagnosticDescriptors.InvalidReturnType,
                        iterationMethod.ReturnType.GetLocation()
                    )
                );
                return null;
            }
            if (iterationMethod.Modifiers.Any(m => m.IsKind(SyntaxKind.StaticKeyword)))
            {
                diagnostics(
                    DiagnosticInfo.Create(
                        DiagnosticDescriptors.IterationMethodCannotBeStatic,
                        iterationMethod.Identifier.GetLocation(),
                        iterationMethod.Identifier.Text
                    )
                );
                return null;
            }

            // Iteration jobs (Aspect / Components kind) are always scheduled via
            // ScheduleParallel — flag isParallelJob=true so the E1 analyzer fires on
            // missing [NativeDisableParallelForRestriction] for write-side fields.
            var fromWorldFields = ScanFromWorldFields(
                diagnostics,
                structDecl,
                semanticModel,
                isParallelJob: true
            );
            if (fromWorldFields == null)
                return null;

            var singleEntityFields = ScanSingleEntityFields(
                diagnostics,
                structDecl,
                semanticModel,
                isParallelJob: true
            );
            if (singleEntityFields == null)
                return null;

            // Decide aspect-vs-components routing: aspect path iff the first parameter
            // implements IAspect, otherwise components path. (The aspect parameter is
            // always first by convention; the source-gen rejects mixed signatures via
            // the TRECS026 diagnostic emitted in ValidateForEachEntityAspectMethod.)
            //
            // A first parameter marked [PassThroughArgument] is forwarded verbatim from
            // the call site and is not the iteration target, even if its type happens to
            // implement IAspect — so we skip it for routing purposes and fall through to
            // the components path.
            var firstParamSyntax =
                iterationMethod.ParameterList.Parameters.Count > 0
                    ? iterationMethod.ParameterList.Parameters[0]
                    : null;
            var firstParamType =
                firstParamSyntax?.Type != null
                    ? semanticModel.GetTypeInfo(firstParamSyntax.Type).Type
                    : null;
            var firstParamIsPassThrough =
                firstParamSyntax != null
                && firstParamSyntax
                    .AttributeLists.SelectMany(al => al.Attributes)
                    .Any(attr =>
                        IterationCriteriaParser.ExtractAttributeName(attr.Name.ToString())
                        == TrecsAttributeNames.PassThroughArgument
                    );
            var useAspectPath =
                firstParamType != null
                && !firstParamIsPassThrough
                && SymbolAnalyzer.ImplementsInterface(
                    firstParamType,
                    "IAspect",
                    TrecsNamespaces.Trecs
                );

            if (useAspectPath)
            {
                var aspectInfo = ValidateForEachEntityAspectMethod(
                    diagnostics,
                    structDecl,
                    iterationMethod,
                    symbol,
                    semanticModel
                );
                if (aspectInfo == null)
                    return null;
                return new JobInfo(
                    symbol,
                    structDecl,
                    JobKind.Aspect,
                    aspectInfo,
                    null,
                    fromWorldFields,
                    singleEntityFields
                );
            }
            else
            {
                var componentsInfo = ValidateForEachComponentsMethod(
                    diagnostics,
                    structDecl,
                    iterationMethod,
                    symbol,
                    semanticModel
                );
                if (componentsInfo == null)
                    return null;
                return new JobInfo(
                    symbol,
                    structDecl,
                    JobKind.Components,
                    null,
                    componentsInfo,
                    fromWorldFields,
                    singleEntityFields
                );
            }
        }

        static AspectIterationInfo? ValidateForEachEntityAspectMethod(
            Action<DiagnosticInfo> diagnostics,
            StructDeclarationSyntax structDecl,
            MethodDeclarationSyntax method,
            INamedTypeSymbol structSymbol,
            SemanticModel semanticModel
        )
        {
            var parameters = method.ParameterList.Parameters;
            if (parameters.Count == 0)
            {
                diagnostics(
                    DiagnosticInfo.Create(
                        DiagnosticDescriptors.EmptyParameters,
                        method.GetLocation()
                    )
                );
                return null;
            }

            var firstParam = parameters[0];
            var firstParamType =
                firstParam.Type != null ? semanticModel.GetTypeInfo(firstParam.Type).Type : null;
            if (firstParamType == null)
                return null;

            if (
                !SymbolAnalyzer.ImplementsInterface(
                    firstParamType,
                    "IAspect",
                    TrecsNamespaces.Trecs
                )
            )
            {
                diagnostics(
                    DiagnosticInfo.Create(
                        DiagnosticDescriptors.InvalidParameterList,
                        firstParam.GetLocation(),
                        "First parameter of [ForEachEntity] Execute on a job must implement IAspect"
                    )
                );
                return null;
            }
            if (!firstParam.Modifiers.Any(m => m.IsKind(SyntaxKind.InKeyword)))
            {
                diagnostics(
                    DiagnosticInfo.Create(
                        DiagnosticDescriptors.InvalidParameterModifiers,
                        firstParam.GetLocation(),
                        firstParam.Identifier.Text
                    )
                );
                return null;
            }

            var firstParamNamed = firstParamType as INamedTypeSymbol;
            if (firstParamNamed == null)
                return null;
            var aspectData = AspectAttributeParser.ParseAspectData(firstParamNamed);

            var componentTypes = new List<ITypeSymbol>();
            componentTypes.AddRange(aspectData.ReadTypes);
            componentTypes.AddRange(aspectData.WriteTypes);
            componentTypes = PerformanceCache.GetDistinctTypes(componentTypes).ToList();

            // Optional EntityIndex / EntityHandle parameters following the aspect.
            // Either, both, or neither — order is preserved so the Execute call args
            // line up with the user's parameter order. Anything else after the aspect
            // is rejected.
            bool hasEntityIndex = false;
            bool hasEntityHandle = false;
            var extraParamOrder = new List<AspectExtraParamKind>();
            ParameterSyntax? offendingExtraParam = null;
            ITypeSymbol? offendingExtraType = null;
            bool offendingIsComponent = false;
            for (int i = 1; i < parameters.Count; i++)
            {
                var paramType =
                    parameters[i].Type != null
                        ? semanticModel.GetTypeInfo(parameters[i].Type!).Type
                        : null;
                if (paramType == null)
                {
                    offendingExtraParam = parameters[i];
                    break;
                }

                bool isEntityIndex =
                    paramType.Name == "EntityIndex"
                    && PerformanceCache.GetDisplayString(paramType.ContainingNamespace)
                        == TrecsNamespaces.Trecs;
                bool isEntityHandleParam =
                    paramType.Name == "EntityHandle"
                    && PerformanceCache.GetDisplayString(paramType.ContainingNamespace)
                        == TrecsNamespaces.Trecs;

                if (isEntityIndex)
                {
                    if (hasEntityIndex)
                    {
                        diagnostics(
                            DiagnosticInfo.Create(
                                DiagnosticDescriptors.InvalidJobParameterList,
                                parameters[i].GetLocation(),
                                $"Method '{method.Identifier.Text}' has more than one EntityIndex parameter — only one is allowed."
                            )
                        );
                        return null;
                    }
                    hasEntityIndex = true;
                    extraParamOrder.Add(AspectExtraParamKind.EntityIndex);
                    continue;
                }

                if (isEntityHandleParam)
                {
                    if (hasEntityHandle)
                    {
                        diagnostics(
                            DiagnosticInfo.Create(
                                DiagnosticDescriptors.InvalidJobParameterList,
                                parameters[i].GetLocation(),
                                $"Method '{method.Identifier.Text}' has more than one EntityHandle parameter — only one is allowed."
                            )
                        );
                        return null;
                    }
                    hasEntityHandle = true;
                    extraParamOrder.Add(AspectExtraParamKind.EntityHandle);
                    continue;
                }

                offendingExtraParam = parameters[i];
                offendingExtraType = paramType;
                offendingIsComponent = SymbolAnalyzer.ImplementsInterface(
                    paramType,
                    "IEntityComponent",
                    TrecsNamespaces.Trecs
                );
                break;
            }

            if (offendingExtraParam != null)
            {
                if (offendingIsComponent)
                {
                    // The "mixed signature" case where an aspect parameter is followed
                    // by a direct component parameter — intentionally not supported.
                    // Aspects are the canonical way to declare a method's component
                    // requirements in Trecs; add the component to the aspect's IRead<T>
                    // / IWrite<T> interface list instead.
                    diagnostics(
                        DiagnosticInfo.Create(
                            DiagnosticDescriptors.MixedAspectAndComponentParams,
                            offendingExtraParam.GetLocation(),
                            method.Identifier.Text,
                            offendingExtraParam.Identifier.Text,
                            PerformanceCache.GetDisplayString(offendingExtraType!)
                        )
                    );
                }
                else
                {
                    diagnostics(
                        DiagnosticInfo.Create(
                            DiagnosticDescriptors.InvalidJobParameterList,
                            method.GetLocation(),
                            "[ForEachEntity] iteration method on a job takes (in AspectType, EntityIndex?, EntityHandle?). Extra parameters are not supported."
                        )
                    );
                }
                return null;
            }

            // Parse the [ForEachEntity] attribute on the method for tags / sets / MatchByComponents.
            var methodSymbol = semanticModel.GetDeclaredSymbol(method) as IMethodSymbol;
            if (methodSymbol == null)
                return null;
            var criteria = IterationCriteriaParser.ParseIterationAttribute(
                ToBridge(diagnostics),
                method,
                methodSymbol,
                structSymbol.Name
            );
            if (criteria == null)
                return null;

            return new AspectIterationInfo(
                aspectTypeName: PerformanceCache.GetDisplayString(firstParamType),
                aspectTypeSymbol: firstParamNamed!,
                componentTypes: componentTypes,
                readComponentTypes: aspectData.ReadTypes.ToList(),
                writeComponentTypes: aspectData.WriteTypes.ToList(),
                hasEntityIndexParameter: hasEntityIndex,
                hasEntityHandleParameter: hasEntityHandle,
                extraParamOrder: extraParamOrder,
                criteria: criteria
            );
        }

        static ComponentsIterationInfo? ValidateForEachComponentsMethod(
            Action<DiagnosticInfo> diagnostics,
            StructDeclarationSyntax structDecl,
            MethodDeclarationSyntax method,
            INamedTypeSymbol structSymbol,
            SemanticModel semanticModel
        )
        {
            var parameters = method.ParameterList.Parameters;
            if (parameters.Count == 0)
            {
                diagnostics(
                    DiagnosticInfo.Create(
                        DiagnosticDescriptors.EmptyParameters,
                        method.GetLocation()
                    )
                );
                return null;
            }

            // Parse parameters in their original declaration order. Each parameter is
            // classified as Component (in/ref + IEntityComponent), EntityIndex, or
            // [GlobalIndex] int. The user may mix them in any order — the shim emits
            // the call to the user's Execute in this same order.
            var paramSlots = new List<ComponentsParam>();
            int bufferIndex = 0;
            bool hasEntityIndex = false;
            bool hasEntityHandle = false;
            bool hasGlobalIndex = false;
            foreach (var p in parameters)
            {
                var paramType = p.Type != null ? semanticModel.GetTypeInfo(p.Type).Type : null;
                if (paramType == null)
                {
                    diagnostics(
                        DiagnosticInfo.Create(
                            DiagnosticDescriptors.CouldNotResolveSymbol,
                            p.GetLocation(),
                            p.Identifier.Text
                        )
                    );
                    return null;
                }

                // [GlobalIndex] takes precedence over the type check (an int with the
                // attribute is the global index, regardless of position).
                bool isGlobalIndex = p
                    .AttributeLists.SelectMany(al => al.Attributes)
                    .Any(a =>
                        IterationCriteriaParser.ExtractAttributeName(a.Name.ToString())
                        == TrecsAttributeNames.GlobalIndex
                    );
                if (isGlobalIndex)
                {
                    if (hasGlobalIndex)
                    {
                        diagnostics(
                            DiagnosticInfo.Create(
                                DiagnosticDescriptors.InvalidJobParameterList,
                                p.GetLocation(),
                                $"Method '{method.Identifier.Text}' has more than one [GlobalIndex] parameter — only one is allowed."
                            )
                        );
                        return null;
                    }
                    if (paramType.SpecialType != SpecialType.System_Int32)
                    {
                        diagnostics(
                            DiagnosticInfo.Create(
                                DiagnosticDescriptors.GlobalIndexParamMustBeInt,
                                p.GetLocation(),
                                p.Identifier.Text,
                                method.Identifier.Text,
                                paramType.ToDisplayString()
                            )
                        );
                        return null;
                    }
                    hasGlobalIndex = true;
                    paramSlots.Add(ComponentsParam.GlobalIndexParam(p.Identifier.Text));
                    continue;
                }

                if (
                    paramType.Name == "EntityIndex"
                    && PerformanceCache.GetDisplayString(paramType.ContainingNamespace)
                        == TrecsNamespaces.Trecs
                )
                {
                    if (hasEntityIndex)
                    {
                        diagnostics(
                            DiagnosticInfo.Create(
                                DiagnosticDescriptors.InvalidJobParameterList,
                                p.GetLocation(),
                                $"Method '{method.Identifier.Text}' has more than one EntityIndex parameter — only one is allowed."
                            )
                        );
                        return null;
                    }
                    hasEntityIndex = true;
                    paramSlots.Add(ComponentsParam.EntityIndexParam(p.Identifier.Text));
                    continue;
                }

                bool isEntityHandle =
                    paramType.Name == "EntityHandle"
                    && PerformanceCache.GetDisplayString(paramType.ContainingNamespace)
                        == TrecsNamespaces.Trecs;
                if (isEntityHandle)
                {
                    if (hasEntityHandle)
                    {
                        diagnostics(
                            DiagnosticInfo.Create(
                                DiagnosticDescriptors.InvalidJobParameterList,
                                p.GetLocation(),
                                $"Method '{method.Identifier.Text}' has more than one EntityHandle parameter — only one is allowed."
                            )
                        );
                        return null;
                    }
                    hasEntityHandle = true;
                    paramSlots.Add(ComponentsParam.EntityHandleParam(p.Identifier.Text));
                    continue;
                }

                // Otherwise expect an in/ref IEntityComponent parameter.
                bool isRef = p.Modifiers.Any(m => m.IsKind(SyntaxKind.RefKeyword));
                bool isIn = p.Modifiers.Any(m => m.IsKind(SyntaxKind.InKeyword));
                if (!isRef && !isIn)
                {
                    diagnostics(
                        DiagnosticInfo.Create(
                            DiagnosticDescriptors.ComponentParameterMustBeInOrRef,
                            p.GetLocation(),
                            p.Identifier.Text
                        )
                    );
                    return null;
                }

                bool isComponent = paramType.AllInterfaces.Any(i => i.Name == "IEntityComponent");
                if (!isComponent)
                {
                    diagnostics(
                        DiagnosticInfo.Create(
                            DiagnosticDescriptors.InvalidParameterList,
                            p.GetLocation(),
                            $"Parameter '{p.Identifier.Text}' must implement IEntityComponent (or be EntityHandle / EntityIndex / [GlobalIndex] int) — Trecs jobs do not support custom pass-through arguments."
                        )
                    );
                    return null;
                }

                paramSlots.Add(
                    ComponentsParam.Component(
                        paramType,
                        p.Identifier.Text,
                        isRef,
                        isIn,
                        bufferIndex
                    )
                );
                bufferIndex++;
            }

            if (bufferIndex == 0)
            {
                diagnostics(
                    DiagnosticInfo.Create(
                        DiagnosticDescriptors.EmptyParameters,
                        method.GetLocation()
                    )
                );
                return null;
            }

            var methodSymbol = semanticModel.GetDeclaredSymbol(method) as IMethodSymbol;
            if (methodSymbol == null)
                return null;
            var criteria = IterationCriteriaParser.ParseIterationAttribute(
                ToBridge(diagnostics),
                method,
                methodSymbol,
                structSymbol.Name
            );
            if (criteria == null)
                return null;

            return new ComponentsIterationInfo(parameters: paramSlots, criteria: criteria);
        }

        // Validate a custom non-iteration job: a partial struct with [FromWorld] fields
        // and a parameterless 'void Execute()' method (no [ForEach*] attribute). Emits a
        // JobInfo of kind CustomNonIteration that the GenerateSource branch wraps as an
        // IJob (not IJobFor) — Unity's IJob.Execute() signature matches the user's
        // 'void Execute()' exactly, so no shim is needed.
        static JobInfo? ValidateCustomNonIterationJob(
            Action<DiagnosticInfo> diagnostics,
            StructDeclarationSyntax structDecl,
            INamedTypeSymbol symbol,
            SemanticModel semanticModel
        )
        {
            // Locate the parameterless 'void Execute()' method specifically. Other
            // 'Execute' overloads (e.g. a private 'void Execute(int i)' helper) are
            // legal C# and ignored — only the parameterless one is the IJob entry.
            MethodDeclarationSyntax? executeMethod = null;
            MethodDeclarationSyntax? anyExecuteMethod = null;
            foreach (var method in structDecl.Members.OfType<MethodDeclarationSyntax>())
            {
                if (method.Identifier.Text != EXECUTE_METHOD_NAME)
                    continue;
                anyExecuteMethod ??= method;
                if (method.ParameterList.Parameters.Count == 0)
                {
                    executeMethod = method;
                    break;
                }
            }

            if (executeMethod == null)
            {
                // Distinguish "no Execute at all" (TRECS076) from "Execute exists but
                // takes parameters" (TRECS077) so the user gets a precise message.
                if (anyExecuteMethod == null)
                {
                    diagnostics(
                        DiagnosticInfo.Create(
                            DiagnosticDescriptors.CustomJobMissingExecuteMethod,
                            structDecl.Identifier.GetLocation(),
                            symbol.Name
                        )
                    );
                }
                else
                {
                    diagnostics(
                        DiagnosticInfo.Create(
                            DiagnosticDescriptors.CustomJobExecuteMustBeParameterless,
                            anyExecuteMethod.Identifier.GetLocation(),
                            symbol.Name
                        )
                    );
                }
                return null;
            }

            if (executeMethod.ReturnType.ToString() != "void")
            {
                diagnostics(
                    DiagnosticInfo.Create(
                        DiagnosticDescriptors.InvalidReturnType,
                        executeMethod.ReturnType.GetLocation()
                    )
                );
                return null;
            }

            if (executeMethod.Modifiers.Any(m => m.IsKind(SyntaxKind.StaticKeyword)))
            {
                diagnostics(
                    DiagnosticInfo.Create(
                        DiagnosticDescriptors.IterationMethodCannotBeStatic,
                        executeMethod.Identifier.GetLocation(),
                        executeMethod.Identifier.Text
                    )
                );
                return null;
            }

            if (!executeMethod.Modifiers.Any(m => m.IsKind(SyntaxKind.PublicKeyword)))
            {
                diagnostics(
                    DiagnosticInfo.Create(
                        DiagnosticDescriptors.CustomJobExecuteMustBePublic,
                        executeMethod.Identifier.GetLocation(),
                        symbol.Name
                    )
                );
                return null;
            }

            // Custom non-iteration jobs (parameterless Execute → IJob) are NOT scheduled parallel.
            var fromWorldFields = ScanFromWorldFields(
                diagnostics,
                structDecl,
                semanticModel,
                isParallelJob: false
            );
            if (fromWorldFields == null)
                return null;

            var singleEntityFields = ScanSingleEntityFields(
                diagnostics,
                structDecl,
                semanticModel,
                isParallelJob: false
            );
            if (singleEntityFields == null)
                return null;

            return new JobInfo(
                symbol,
                structDecl,
                JobKind.CustomNonIteration,
                aspect: null,
                components: null,
                fromWorldFields: fromWorldFields,
                singleEntityFields: singleEntityFields
            );
        }

        static JobInfo? ValidateCustomParallelIterationJob(
            Action<DiagnosticInfo> diagnostics,
            StructDeclarationSyntax structDecl,
            INamedTypeSymbol symbol,
            SemanticModel semanticModel
        )
        {
            // Locate the 'public void Execute(int i)' method. Other Execute overloads are
            // legal C# helpers and ignored — we only care about the IJobFor entry point.
            MethodDeclarationSyntax? executeMethod = null;
            foreach (var method in structDecl.Members.OfType<MethodDeclarationSyntax>())
            {
                if (method.Identifier.Text != EXECUTE_METHOD_NAME)
                    continue;
                if (method.ParameterList.Parameters.Count != 1)
                    continue;
                if (method.ParameterList.Parameters[0].Type?.ToString() != "int")
                    continue;
                executeMethod = method;
                break;
            }

            if (executeMethod == null)
                return null; // Caller already verified this.

            if (executeMethod.ReturnType.ToString() != "void")
            {
                diagnostics(
                    DiagnosticInfo.Create(
                        DiagnosticDescriptors.InvalidReturnType,
                        executeMethod.ReturnType.GetLocation()
                    )
                );
                return null;
            }

            if (executeMethod.Modifiers.Any(m => m.IsKind(SyntaxKind.StaticKeyword)))
            {
                diagnostics(
                    DiagnosticInfo.Create(
                        DiagnosticDescriptors.IterationMethodCannotBeStatic,
                        executeMethod.Identifier.GetLocation(),
                        executeMethod.Identifier.Text
                    )
                );
                return null;
            }

            if (!executeMethod.Modifiers.Any(m => m.IsKind(SyntaxKind.PublicKeyword)))
            {
                diagnostics(
                    DiagnosticInfo.Create(
                        DiagnosticDescriptors.CustomJobExecuteMustBePublic,
                        executeMethod.Identifier.GetLocation(),
                        symbol.Name
                    )
                );
                return null;
            }

            // Custom parallel jobs (Execute(int) → IJobFor) are scheduled parallel —
            // E1a applies, so isParallelJob=true.
            var fromWorldFields = ScanFromWorldFields(
                diagnostics,
                structDecl,
                semanticModel,
                isParallelJob: true
            );
            if (fromWorldFields == null)
                return null;

            var singleEntityFields = ScanSingleEntityFields(
                diagnostics,
                structDecl,
                semanticModel,
                isParallelJob: true
            );
            if (singleEntityFields == null)
                return null;

            return new JobInfo(
                symbol,
                structDecl,
                JobKind.CustomParallelIteration,
                aspect: null,
                components: null,
                fromWorldFields: fromWorldFields,
                singleEntityFields: singleEntityFields
            );
        }

        static List<FromWorldFieldInfo>? ScanFromWorldFields(
            Action<DiagnosticInfo> diagnostics,
            StructDeclarationSyntax structDecl,
            SemanticModel semanticModel,
            bool isParallelJob
        )
        {
            var result = new List<FromWorldFieldInfo>();
            foreach (var field in structDecl.Members.OfType<FieldDeclarationSyntax>())
            {
                bool hasFromWorld = field
                    .AttributeLists.SelectMany(al => al.Attributes)
                    .Any(attr =>
                        IterationCriteriaParser.ExtractAttributeName(attr.Name.ToString())
                        == TrecsAttributeNames.FromWorld
                    );
                if (!hasFromWorld)
                    continue;

                // Reject `[FromWorld] T A, B;` — each [FromWorld] field must be on its own
                // declaration so the generator unambiguously knows which variable to wire up.
                if (field.Declaration.Variables.Count != 1)
                {
                    diagnostics(
                        DiagnosticInfo.Create(
                            DiagnosticDescriptors.MultiVariableFromWorldFieldNotSupported,
                            field.GetLocation(),
                            field.Declaration.Variables[0].Identifier.Text
                        )
                    );
                    return null;
                }

                var typeSyntax = field.Declaration.Type;
                var typeSymbol = semanticModel.GetTypeInfo(typeSyntax).Type as INamedTypeSymbol;
                if (typeSymbol == null)
                {
                    diagnostics(
                        DiagnosticInfo.Create(
                            DiagnosticDescriptors.CouldNotResolveSymbol,
                            typeSyntax.GetLocation(),
                            typeSyntax.ToString()
                        )
                    );
                    return null;
                }

                var kind = FromWorldClassifier.Classify(typeSymbol);
                if (kind == FromWorldFieldKind.Unsupported)
                {
                    diagnostics(
                        DiagnosticInfo.Create(
                            DiagnosticDescriptors.UnsupportedFromWorldFieldType,
                            field.GetLocation(),
                            PerformanceCache.GetDisplayString(typeSymbol)
                        )
                    );
                    return null;
                }

                // Check that write-side [NativeContainer] fields have
                // [NativeDisableParallelForRestriction] — Unity's walker requires it.
                CheckFromWorldFieldWriteAttributes(
                    diagnostics,
                    field,
                    typeSymbol,
                    kind,
                    structName: structDecl.Identifier.Text,
                    isParallelJob: isParallelJob
                );

                // Generic [FromWorld] types have exactly one type parameter.
                // Non-generic types (NativeWorldAccessor, GroupIndex) have no generic argument.
                ITypeSymbol? genericArg = typeSymbol.IsGenericType
                    ? typeSymbol.TypeArguments[0]
                    : null;

                // For NativeFactory fields, parse the containing aspect's IRead/IWrite
                // components for dep tracking and lookup creation at emit time.
                AspectAttributeData? aspectData = null;
                if (kind == FromWorldFieldKind.NativeFactory)
                {
                    var aspectSymbol = typeSymbol.ContainingType;
                    if (aspectSymbol != null)
                    {
                        aspectData = AspectAttributeParser.ParseAspectData(aspectSymbol);
                    }
                }

                var v = field.Declaration.Variables[0];

                // Parse inline Tag/Tags from [FromWorld] attribute. Uses the semantic
                // model to get AttributeData, which gives us resolved type symbols.
                List<ITypeSymbol>? inlineTagTypes = null;
                var fieldSymbol = semanticModel.GetDeclaredSymbol(v) as IFieldSymbol;
                if (fieldSymbol != null)
                {
                    foreach (var attr in PerformanceCache.GetAttributes(fieldSymbol))
                    {
                        if (attr.AttributeClass?.Name != TrecsAttributeNames.FromWorld)
                            continue;

                        var tagTypes = InlineTagsParser.Parse(
                            attr,
                            field.GetLocation(),
                            v.Identifier.Text,
                            "FromWorld",
                            ToBridge(diagnostics)
                        );
                        if (tagTypes == null)
                            return null;

                        // Validate: inline tags not supported on NativeComponentRead/Write
                        // (these require an EntityIndex, not a TagSet).
                        if (
                            tagTypes.Count > 0
                            && (
                                kind == FromWorldFieldKind.NativeComponentRead
                                || kind == FromWorldFieldKind.NativeComponentWrite
                            )
                        )
                        {
                            diagnostics(
                                DiagnosticInfo.Create(
                                    DiagnosticDescriptors.FromWorldInlineTagsNotSupportedForEntityIndex,
                                    field.GetLocation(),
                                    v.Identifier.Text
                                )
                            );
                            return null;
                        }

                        // Validate: inline tags not supported on NativeWorldAccessor
                        // (it's just world.ToNative(), no group resolution).
                        if (tagTypes.Count > 0 && kind == FromWorldFieldKind.NativeWorldAccessor)
                        {
                            diagnostics(
                                DiagnosticInfo.Create(
                                    DiagnosticDescriptors.FromWorldInlineTagsNotSupportedForNativeWorldAccessor,
                                    field.GetLocation(),
                                    v.Identifier.Text
                                )
                            );
                            return null;
                        }

                        if (tagTypes.Count > 0)
                            inlineTagTypes = tagTypes;

                        break;
                    }
                }

                result.Add(
                    new FromWorldFieldInfo(
                        fieldName: v.Identifier.Text,
                        kind: kind,
                        fieldType: typeSymbol,
                        genericArgument: genericArg,
                        aspectData: aspectData,
                        inlineTagTypes: inlineTagTypes
                    )
                );
            }

            // TRECS081 — check for Trecs container fields that are missing [FromWorld].
            // These are untracked by the scheduler, which can cause race conditions.
            CheckForUntrackedContainerFields(diagnostics, structDecl, semanticModel);

            return result;
        }

        /// <summary>
        /// Scans the job struct for fields decorated with <c>[SingleEntity(Tag/Tags)]</c>.
        /// Each entry becomes a hidden hoist + assignment in the generated <c>ScheduleParallel</c>
        /// path. Validates: type is an <c>IAspect</c> or a <c>NativeComponentRead/Write&lt;T&gt;</c>;
        /// inline tags are present; not stacked with <c>[FromWorld]</c>.
        /// </summary>
        static List<SingleEntityFieldEntry>? ScanSingleEntityFields(
            Action<DiagnosticInfo> diagnostics,
            StructDeclarationSyntax structDecl,
            SemanticModel semanticModel,
            bool isParallelJob
        )
        {
            var result = new List<SingleEntityFieldEntry>();
            foreach (var field in structDecl.Members.OfType<FieldDeclarationSyntax>())
            {
                bool hasSingleEntity = field
                    .AttributeLists.SelectMany(al => al.Attributes)
                    .Any(attr =>
                        IterationCriteriaParser.ExtractAttributeName(attr.Name.ToString())
                        == TrecsAttributeNames.SingleEntity
                    );
                if (!hasSingleEntity)
                    continue;

                bool hasFromWorld = field
                    .AttributeLists.SelectMany(al => al.Attributes)
                    .Any(attr =>
                        IterationCriteriaParser.ExtractAttributeName(attr.Name.ToString())
                        == TrecsAttributeNames.FromWorld
                    );
                if (hasFromWorld)
                {
                    diagnostics(
                        DiagnosticInfo.Create(
                            DiagnosticDescriptors.SingleEntityConflictingAttributes,
                            field.GetLocation(),
                            field.Declaration.Variables[0].Identifier.Text,
                            "FromWorld"
                        )
                    );
                    return null;
                }

                if (field.Declaration.Variables.Count != 1)
                {
                    diagnostics(
                        DiagnosticInfo.Create(
                            DiagnosticDescriptors.MultiVariableFromWorldFieldNotSupported,
                            field.GetLocation(),
                            field.Declaration.Variables[0].Identifier.Text
                        )
                    );
                    return null;
                }

                var v = field.Declaration.Variables[0];
                var typeSyntax = field.Declaration.Type;
                var typeSymbol = semanticModel.GetTypeInfo(typeSyntax).Type as INamedTypeSymbol;
                if (typeSymbol == null)
                {
                    diagnostics(
                        DiagnosticInfo.Create(
                            DiagnosticDescriptors.CouldNotResolveSymbol,
                            typeSyntax.GetLocation(),
                            typeSyntax.ToString()
                        )
                    );
                    return null;
                }

                var fieldSymbol = semanticModel.GetDeclaredSymbol(v) as IFieldSymbol;
                if (fieldSymbol == null)
                {
                    diagnostics(
                        DiagnosticInfo.Create(
                            DiagnosticDescriptors.CouldNotResolveSymbol,
                            field.GetLocation(),
                            v.Identifier.Text
                        )
                    );
                    return null;
                }
                var tagTypes = InlineTagsParser.ParseFromSymbol(
                    fieldSymbol,
                    "SingleEntity",
                    field.GetLocation(),
                    v.Identifier.Text,
                    ToBridge(diagnostics)
                );
                if (tagTypes == null)
                    return null;
                if (tagTypes.Count == 0)
                {
                    diagnostics(
                        DiagnosticInfo.Create(
                            DiagnosticDescriptors.SingleEntityRequiresInlineTags,
                            field.GetLocation(),
                            v.Identifier.Text
                        )
                    );
                    return null;
                }

                // Classify the field type. Aspect → store the materialized aspect.
                // NativeComponentRead<T> / NativeComponentWrite<T> → store the wrapper.
                bool isAspect = SymbolAnalyzer.ImplementsInterface(
                    typeSymbol,
                    "IAspect",
                    TrecsNamespaces.Trecs
                );
                bool isComponentRead =
                    typeSymbol.Name == "NativeComponentRead"
                    && typeSymbol.IsGenericType
                    && typeSymbol.TypeArguments.Length == 1
                    && PerformanceCache.GetDisplayString(typeSymbol.ContainingNamespace) == "Trecs";
                bool isComponentWrite =
                    typeSymbol.Name == "NativeComponentWrite"
                    && typeSymbol.IsGenericType
                    && typeSymbol.TypeArguments.Length == 1
                    && PerformanceCache.GetDisplayString(typeSymbol.ContainingNamespace) == "Trecs";

                if (!isAspect && !isComponentRead && !isComponentWrite)
                {
                    diagnostics(
                        DiagnosticInfo.Create(
                            DiagnosticDescriptors.SingleEntityWrongType,
                            field.GetLocation(),
                            v.Identifier.Text,
                            PerformanceCache.GetDisplayString(typeSymbol)
                        )
                    );
                    return null;
                }

                if (isAspect)
                {
                    var aspectData = AspectAttributeParser.ParseAspectData(typeSymbol);
                    CheckSingleEntityAspectWriteAttributes(
                        diagnostics,
                        field,
                        typeSymbol,
                        aspectData,
                        structDecl.Identifier.Text,
                        isParallelJob
                    );
                    result.Add(
                        new SingleEntityFieldEntry(
                            fieldName: v.Identifier.Text,
                            isAspect: true,
                            tagTypes: tagTypes,
                            aspectTypeDisplay: PerformanceCache.GetDisplayString(typeSymbol),
                            aspectData: aspectData
                        )
                    );
                }
                else
                {
                    var compType = typeSymbol.TypeArguments[0];
                    result.Add(
                        new SingleEntityFieldEntry(
                            fieldName: v.Identifier.Text,
                            isAspect: false,
                            tagTypes: tagTypes,
                            componentTypeDisplay: PerformanceCache.GetDisplayString(compType),
                            componentTypeSymbol: compType,
                            isRef: isComponentWrite
                        )
                    );
                }
            }
            return result;
        }

        /// <summary>
        /// Emits TRECS081 for any field on a Trecs job struct whose type is a recognized
        /// Trecs container (NativeComponentBufferRead, NativeComponentLookupWrite, etc.) but
        /// is NOT marked [FromWorld]. Such fields bypass the scheduler's dependency
        /// tracking and can cause race conditions.
        /// </summary>
        static void CheckForUntrackedContainerFields(
            Action<DiagnosticInfo> diagnostics,
            StructDeclarationSyntax structDecl,
            SemanticModel semanticModel
        )
        {
            foreach (var field in structDecl.Members.OfType<FieldDeclarationSyntax>())
            {
                bool hasFromWorld = field
                    .AttributeLists.SelectMany(al => al.Attributes)
                    .Any(attr =>
                        IterationCriteriaParser.ExtractAttributeName(attr.Name.ToString())
                        == TrecsAttributeNames.FromWorld
                    );
                bool hasSingleEntity = field
                    .AttributeLists.SelectMany(al => al.Attributes)
                    .Any(attr =>
                        IterationCriteriaParser.ExtractAttributeName(attr.Name.ToString())
                        == TrecsAttributeNames.SingleEntity
                    );
                if (hasFromWorld || hasSingleEntity)
                    continue;

                var typeSyntax = field.Declaration.Type;
                var typeSymbol = semanticModel.GetTypeInfo(typeSyntax).Type as INamedTypeSymbol;
                if (typeSymbol == null)
                    continue;

                var kind = FromWorldClassifier.Classify(typeSymbol);
                if (kind == FromWorldFieldKind.Unsupported)
                    continue;

                // NativeWorldAccessor, GroupIndex, and NativeEntityHandleBuffer have no dependency
                // tracking — omitting [FromWorld] on them is less convenient but not a
                // race-condition risk.
                if (
                    kind == FromWorldFieldKind.NativeWorldAccessor
                    || kind == FromWorldFieldKind.GroupIndex
                    || kind == FromWorldFieldKind.NativeEntityHandleBuffer
                )
                    continue;

                // This field is a Trecs container type without [FromWorld] — emit TRECS081.
                foreach (var v in field.Declaration.Variables)
                {
                    diagnostics(
                        DiagnosticInfo.Create(
                            DiagnosticDescriptors.MissingFromWorldOnContainerField,
                            v.GetLocation(),
                            v.Identifier.Text,
                            PerformanceCache.GetDisplayString(typeSymbol),
                            structDecl.Identifier.Text
                        )
                    );
                }
            }
        }

        // Write-side [NativeContainer] fields (NativeComponentBufferWrite, NativeComponentLookupWrite)
        // need [NativeDisableParallelForRestriction] on parallel jobs.
        // Read-side containers do NOT need [ReadOnly] — their [NativeContainerIsReadOnly] type
        // attribute is sufficient for IJobFor.
        static void CheckFromWorldFieldWriteAttributes(
            Action<DiagnosticInfo> diagnostics,
            FieldDeclarationSyntax field,
            INamedTypeSymbol typeSymbol,
            FromWorldFieldKind kind,
            string structName,
            bool isParallelJob
        )
        {
            bool isWriteContainer = kind switch
            {
                FromWorldFieldKind.NativeComponentBufferWrite => true,
                FromWorldFieldKind.NativeComponentLookupWrite => true,
                _ => false,
            };
            if (!isParallelJob || !isWriteContainer)
                return;

            bool hasNativeDisableParallel = false;
            foreach (var attrList in field.AttributeLists)
            {
                foreach (var attr in attrList.Attributes)
                {
                    if (
                        IterationCriteriaParser.ExtractAttributeName(attr.Name.ToString())
                        == "NativeDisableParallelForRestrictionAttribute"
                    )
                        hasNativeDisableParallel = true;
                }
            }

            if (!hasNativeDisableParallel)
            {
                var variable = field.Declaration.Variables[0];
                diagnostics(
                    DiagnosticInfo.Create(
                        DiagnosticDescriptors.MissingNativeDisableParallelForRestriction,
                        field.GetLocation(),
                        structName,
                        variable.Identifier.Text,
                        PerformanceCache.GetDisplayString(typeSymbol)
                    )
                );
            }
        }

        // Mirror of CheckFromWorldFieldWriteAttributes for hand-written
        // [SingleEntity] aspect fields. If the aspect carries IWrite
        // components the materialized aspect stores a NativeComponentBufferWrite,
        // which Unity's parallel-job safety walker rejects on a field without
        // [NativeDisableParallelForRestriction]. Auto-generated [WrapAsJob]
        // wrappers add the attribute themselves; hand-written job structs need
        // this warning so users learn at compile time rather than via a Burst
        // safety error at runtime.
        static void CheckSingleEntityAspectWriteAttributes(
            Action<DiagnosticInfo> diagnostics,
            FieldDeclarationSyntax field,
            INamedTypeSymbol aspectTypeSymbol,
            AspectAttributeData aspectData,
            string structName,
            bool isParallelJob
        )
        {
            if (!isParallelJob)
                return;
            if (aspectData == null || aspectData.WriteTypes.IsDefaultOrEmpty)
                return;

            bool hasNativeDisableParallel = false;
            foreach (var attrList in field.AttributeLists)
            {
                foreach (var attr in attrList.Attributes)
                {
                    if (
                        IterationCriteriaParser.ExtractAttributeName(attr.Name.ToString())
                        == "NativeDisableParallelForRestrictionAttribute"
                    )
                        hasNativeDisableParallel = true;
                }
            }

            if (!hasNativeDisableParallel)
            {
                var variable = field.Declaration.Variables[0];
                diagnostics(
                    DiagnosticInfo.Create(
                        DiagnosticDescriptors.SingleEntityWriteAspectMissingNativeDisableParallelForRestriction,
                        field.GetLocation(),
                        variable.Identifier.Text,
                        structName,
                        PerformanceCache.GetDisplayString(aspectTypeSymbol)
                    )
                );
            }
        }

        // ─── JobInfo → JobModel projection ──────────────────────────────────────

        /// <summary>
        /// Projects the transient validation output (<see cref="JobInfo"/>, which
        /// carries <see cref="ITypeSymbol"/> arrays under the hood) into the
        /// value-equatable <see cref="JobModel"/>. When validation failed
        /// (<paramref name="info"/> is null), build a placeholder model carrying just
        /// the accumulated diagnostics; the terminal stage reports the diagnostics
        /// and skips emission on <c>!IsValid</c>.
        /// <para>
        /// <paramref name="globalNamespaceName"/> defaults to <c>""</c> here because
        /// JobGenerator's emitted file uses fixed <c>using</c>s — the aspect
        /// namespace set captured by <see cref="AspectAttributeDataModel.Namespaces"/>
        /// is never read by codegen. <see cref="AspectAttributeDataModelBuilder.FromData"/>
        /// already filters the global namespace via its <c>IsNullOrEmpty</c> check,
        /// so an empty <c>globalNamespaceName</c> argument is harmless.
        /// </para>
        /// </summary>
        static JobModel BuildJobModelFromInfo(
            JobInfo? info,
            INamedTypeSymbol symbol,
            List<DiagnosticInfo> diagnostics
        )
        {
            const string globalNamespaceName = "";

            var structName = symbol.Name;
            var ns = PerformanceCache.GetDisplayString(symbol.ContainingNamespace);
            var containingTypes = SymbolAnalyzer
                .GetContainingTypeChainInfo(symbol)
                .ToEquatableArray();
            var hintFileName = SymbolAnalyzer.GetSafeFileName(symbol, "Job");

            if (info == null)
            {
                return new JobModel(
                    StructName: structName,
                    Namespace: ns,
                    ContainingTypes: containingTypes,
                    HintFileName: hintFileName,
                    Kind: JobIterationKind.CustomNonIteration,
                    AspectIteration: JobModelBuilders.EmptyAspectIteration,
                    ComponentsIteration: JobModelBuilders.EmptyComponentsIteration,
                    FromWorldFields: EquatableArray<FromWorldFieldEmitModel>.Empty,
                    SingleEntityFields: EquatableArray<SingleEntityFieldModel>.Empty,
                    AttributeCriteriaChain: string.Empty,
                    HasBurstCompile: false,
                    IsValid: false,
                    Diagnostics: diagnostics.ToEquatableArray()
                );
            }

            var kind = info.Kind switch
            {
                JobKind.Aspect => JobIterationKind.Aspect,
                JobKind.Components => JobIterationKind.Components,
                JobKind.CustomNonIteration => JobIterationKind.CustomNonIteration,
                JobKind.CustomParallelIteration => JobIterationKind.CustomParallelIteration,
                _ => throw new InvalidOperationException($"Unhandled JobKind: {info.Kind}"),
            };

            var aspectIteration = JobModelBuilders.EmptyAspectIteration;
            if (info.Kind == JobKind.Aspect)
            {
                var ai = info.Aspect!;
                var extraSlots = ai
                    .ExtraParamOrder.Select(k =>
                        k == AspectExtraParamKind.EntityHandle
                            ? AspectExtraParamSlot.EntityHandle
                            : AspectExtraParamSlot.EntityIndex
                    )
                    .ToList();
                aspectIteration = JobModelBuilders.BuildAspectIteration(
                    aspectTypeName: ai.AspectTypeName,
                    componentTypes: ai.ComponentTypes,
                    readTypes: ai.ReadComponentTypes,
                    writeTypes: ai.WriteComponentTypes,
                    hasEntityIndexParameter: ai.HasEntityIndexParameter,
                    hasEntityHandleParameter: ai.HasEntityHandleParameter,
                    extraParamOrder: extraSlots,
                    criteria: ai.Criteria
                );
            }

            var componentsIteration = JobModelBuilders.EmptyComponentsIteration;
            if (info.Kind == JobKind.Components)
            {
                var ci = info.Components!;
                var paramModels = ci
                    .Parameters.Select(p => new ComponentsParamModel(
                        Role: p.Role switch
                        {
                            ComponentsParamRole.Component => ComponentsParamRoleKind.Component,
                            ComponentsParamRole.EntityIndex => ComponentsParamRoleKind.EntityIndex,
                            ComponentsParamRole.EntityHandle =>
                                ComponentsParamRoleKind.EntityHandle,
                            ComponentsParamRole.GlobalIndex => ComponentsParamRoleKind.GlobalIndex,
                            _ => ComponentsParamRoleKind.Component,
                        },
                        TypeDisplay: p.Type != null
                            ? PerformanceCache.GetDisplayString(p.Type)
                            : string.Empty,
                        Name: p.Name,
                        IsRef: p.IsRef,
                        IsIn: p.IsIn,
                        BufferIndex: p.BufferIndex
                    ))
                    .ToArray();
                componentsIteration = new ComponentsIterationModel(
                    Parameters: new EquatableArray<ComponentsParamModel>(paramModels),
                    Criteria: JobModelBuilders.BuildCriteria(ci.Criteria),
                    HasEntityIndexParameter: ci.HasEntityIndexParameter,
                    HasEntityHandleParameter: ci.HasEntityHandleParameter,
                    HasGlobalIndexParameter: ci.HasGlobalIndexParameter
                );
            }

            var fromWorldEmits = info
                .FromWorldFields.Select(f =>
                    FromWorldFieldEmitModel.From(FromWorldFieldEmit.Build(f), globalNamespaceName)
                )
                .ToArray();

            var singleEntityModels = info
                .SingleEntityFields.Select(f => ProjectSingleEntityField(f, globalNamespaceName))
                .ToArray();

            string attributeChain = string.Empty;
            if (info.Kind == JobKind.Aspect || info.Kind == JobKind.Components)
            {
                var c = info.IterationCriteria;
                var componentTypes =
                    info.Kind == JobKind.Aspect
                        ? (IEnumerable<ITypeSymbol>)info.Aspect!.ComponentTypes
                        : info.Components!.Components.Select(p => p.Type!);
                attributeChain = QueryBuilderHelper.BuildAttributeCriteriaChain(
                    c.TagTypes,
                    c.MatchByComponents,
                    componentTypes,
                    c.WithoutTagTypes
                );
            }

            return new JobModel(
                StructName: structName,
                Namespace: ns,
                ContainingTypes: containingTypes,
                HintFileName: hintFileName,
                Kind: kind,
                AspectIteration: aspectIteration,
                ComponentsIteration: componentsIteration,
                FromWorldFields: new EquatableArray<FromWorldFieldEmitModel>(fromWorldEmits),
                SingleEntityFields: new EquatableArray<SingleEntityFieldModel>(singleEntityModels),
                AttributeCriteriaChain: attributeChain,
                HasBurstCompile: HasBurstCompile(symbol),
                IsValid: true,
                Diagnostics: diagnostics.ToEquatableArray()
            );
        }

        static SingleEntityFieldModel ProjectSingleEntityField(
            SingleEntityFieldEntry entry,
            string globalNamespaceName
        )
        {
            var aspectModel =
                entry.AspectData != null
                    ? AspectAttributeDataModelBuilder.FromData(
                        entry.AspectData,
                        globalNamespaceName
                    )
                    : AspectAttributeDataModel.Empty;
            return new SingleEntityFieldModel(
                FieldName: entry.FieldName,
                IsAspect: entry.IsAspect,
                TagTypeDisplays: entry
                    .TagTypes.Select(PerformanceCache.GetDisplayString)
                    .ToEquatableArray(),
                AspectData: aspectModel,
                HasAspectData: entry.AspectData != null,
                AspectTypeDisplay: entry.AspectTypeDisplay ?? string.Empty,
                ComponentTypeDisplay: entry.ComponentTypeDisplay ?? string.Empty,
                IsRef: entry.IsRef
            );
        }

        // ─── Emission ───────────────────────────────────────────────────────────

        static string GenerateSource(JobModel model)
        {
            var sb = new StringBuilder();
            sb.AppendLine("using Unity.Collections;");
            sb.AppendLine("using Unity.Jobs;");
            sb.AppendLine("using System;");
            CommonUsings.AppendTo(sb);
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
            var structName = model.StructName;
            var jobInterface =
                model.Kind == JobIterationKind.CustomNonIteration ? "IJob" : "IJobFor";
            sb.AppendLine($"{ind}partial struct {structName} : {jobInterface}");
            sb.AppendLine($"{ind}{{");
            indent++;
            ind = new string(' ', indent * 4);

            switch (model.Kind)
            {
                case JobIterationKind.CustomNonIteration:
                    EmitCustomScheduleOverload(sb, model, ind);
                    break;
                case JobIterationKind.CustomParallelIteration:
                    EmitCustomParallelScheduleOverload(sb, model, ind);
                    break;
                default:
                    EmitGeneratedFields(sb, model, ind);
                    sb.AppendLine();
                    EmitExecuteShim(sb, model, ind);
                    sb.AppendLine();
                    EmitScheduleOverloads(sb, model, ind);
                    break;
            }

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

        static string BufferFieldName(int index) => FromWorldEmitter.JobFieldPrefix + "buf" + index;

        static void EmitGeneratedFields(StringBuilder sb, in JobModel model, string ind)
        {
            var buffers = model.IterationBuffers;
            for (int i = 0; i < buffers.Count; i++)
            {
                var (type, readOnly) = buffers[i];
                if (!readOnly)
                    sb.AppendLine($"{ind}[Unity.Collections.NativeDisableParallelForRestriction]");
                var bufferType = readOnly
                    ? $"NativeComponentBufferRead<{type}>"
                    : $"NativeComponentBufferWrite<{type}>";
                sb.AppendLine($"{ind}{GeneratedCodeAttributes.Line}");
                sb.AppendLine($"{ind}private {bufferType} {BufferFieldName(i)};");
            }

            PerGroupHiddenFieldEmitter.EmitDeclarations(
                sb,
                ind,
                visibility: "private",
                needsGroupField: model.NeedsGroupField,
                needsGlobalIndexOffset: model.NeedsGlobalIndexOffset,
                hasNativeWorldAccessor: false,
                needsEntityHandleBuffer: model.NeedsEntityHandleBuffer
            );

            sb.AppendLine($"{ind}#if SVKJ_IS_PROFILING");
            sb.AppendLine($"{ind}[Unity.Collections.NativeDisableParallelForRestriction]");
            sb.AppendLine(
                $"{ind}internal Unity.Collections.NativeArray<long> {FromWorldEmitter.JobFieldPrefix}timing;"
            );
            sb.AppendLine($"{ind}[Unity.Collections.LowLevel.Unsafe.NativeSetThreadIndex]");
            sb.AppendLine($"{ind}internal int {FromWorldEmitter.JobFieldPrefix}threadIndex;");
            sb.AppendLine($"{ind}#endif");
        }

        static void EmitExecuteShim(StringBuilder sb, in JobModel model, string ind)
        {
            sb.AppendLine($"{ind}{GeneratedCodeAttributes.Line}");
            sb.AppendLine($"{ind}public void Execute(int i)");
            sb.AppendLine($"{ind}{{");
            string body = ind + "    ";

            sb.AppendLine($"{body}#if SVKJ_IS_PROFILING");
            sb.AppendLine(
                $"{body}var {FromWorldEmitter.GenPrefix}t0 = Unity.Profiling.LowLevel.Unsafe.ProfilerUnsafeUtility.Timestamp;"
            );
            sb.AppendLine($"{body}#endif");

            if (model.Kind == JobIterationKind.Aspect)
            {
                var ai = model.AspectIteration;
                sb.AppendLine(
                    $"{body}var {FromWorldEmitter.GenPrefix}ei = new EntityIndex(i, {FromWorldEmitter.JobFieldPrefix}GroupIndex);"
                );
                var ctorArgs = string.Join(
                    ", ",
                    Enumerable.Range(0, ai.AspectComponents.Length).Select(BufferFieldName)
                );
                sb.AppendLine(
                    $"{body}var {FromWorldEmitter.GenPrefix}view = new {ai.AspectTypeName}({FromWorldEmitter.GenPrefix}ei, {ctorArgs});"
                );
                sb.Append($"{body}Execute(in {FromWorldEmitter.GenPrefix}view");
                foreach (var slot in ai.ExtraParamOrder)
                {
                    if (slot.Kind == AspectExtraParamSlot.EntityIndexKind)
                        sb.Append($", {FromWorldEmitter.GenPrefix}ei");
                    else if (slot.Kind == AspectExtraParamSlot.EntityHandleKind)
                        sb.Append($", {FromWorldEmitter.JobFieldPrefix}EntityHandles[i]");
                }
                sb.AppendLine(");");
            }
            else
            {
                var ci = model.ComponentsIteration;
                foreach (var p in ci.Parameters)
                {
                    if (p.Role != ComponentsParamRoleKind.Component)
                        continue;
                    var refKind = p.IsRef ? "ref" : "ref readonly";
                    sb.AppendLine(
                        $"{body}{refKind} var {FromWorldEmitter.GenPrefix}v{p.BufferIndex} = ref {BufferFieldName(p.BufferIndex)}[i];"
                    );
                }
                if (ci.HasEntityIndexParameter)
                    sb.AppendLine(
                        $"{body}var {FromWorldEmitter.GenPrefix}ei = new EntityIndex(i, {FromWorldEmitter.JobFieldPrefix}GroupIndex);"
                    );

                var callArgs = new List<string>(ci.Parameters.Length);
                foreach (var p in ci.Parameters)
                {
                    switch (p.Role)
                    {
                        case ComponentsParamRoleKind.Component:
                        {
                            var prefix = p.IsRef ? "ref" : "in";
                            callArgs.Add($"{prefix} {FromWorldEmitter.GenPrefix}v{p.BufferIndex}");
                            break;
                        }
                        case ComponentsParamRoleKind.EntityIndex:
                            callArgs.Add($"{FromWorldEmitter.GenPrefix}ei");
                            break;
                        case ComponentsParamRoleKind.EntityHandle:
                            callArgs.Add($"{FromWorldEmitter.JobFieldPrefix}EntityHandles[i]");
                            break;
                        case ComponentsParamRoleKind.GlobalIndex:
                            callArgs.Add($"{FromWorldEmitter.JobFieldPrefix}GlobalIndexOffset + i");
                            break;
                    }
                }
                sb.AppendLine($"{body}Execute({string.Join(", ", callArgs)});");
            }

            sb.AppendLine($"{body}#if SVKJ_IS_PROFILING");
            sb.AppendLine(
                $"{body}var {FromWorldEmitter.GenPrefix}t1 = Unity.Profiling.LowLevel.Unsafe.ProfilerUnsafeUtility.Timestamp;"
            );
            sb.AppendLine(
                $"{body}var {FromWorldEmitter.GenPrefix}tbase = {FromWorldEmitter.JobFieldPrefix}threadIndex * 3;"
            );
            sb.AppendLine(
                $"{body}if ({FromWorldEmitter.JobFieldPrefix}timing[{FromWorldEmitter.GenPrefix}tbase] == 0)"
            );
            sb.AppendLine(
                $"{body}    {FromWorldEmitter.JobFieldPrefix}timing[{FromWorldEmitter.GenPrefix}tbase] = {FromWorldEmitter.GenPrefix}t0;"
            );
            sb.AppendLine(
                $"{body}{FromWorldEmitter.JobFieldPrefix}timing[{FromWorldEmitter.GenPrefix}tbase + 1] = {FromWorldEmitter.GenPrefix}t1;"
            );
            sb.AppendLine(
                $"{body}{FromWorldEmitter.JobFieldPrefix}timing[{FromWorldEmitter.GenPrefix}tbase + 2] += {FromWorldEmitter.GenPrefix}t1 - {FromWorldEmitter.GenPrefix}t0;"
            );
            sb.AppendLine($"{body}#endif");

            sb.AppendLine($"{ind}}}");
        }

        static void EmitScheduleOverloads(StringBuilder sb, in JobModel model, string ind)
        {
            var fromWorldEmits = model.FromWorldFields.ToList();
            var paramFields = fromWorldEmits.Where(e => e.HasScheduleParam).ToList();
            var fromWorldParamDecl = FormatFromWorldParamDecl(paramFields);

            var crit = model.IterationCriteria;
            bool hasSets = crit.HasSets;
            bool hasAnyAttributeCriteria = crit.HasTags || hasSets || crit.MatchByComponents;

            if (hasAnyAttributeCriteria)
            {
                sb.AppendLine($"{ind}{GeneratedCodeAttributes.Line}");
                sb.AppendLine(
                    $"{ind}public JobHandle ScheduleParallel(WorldAccessor world{fromWorldParamDecl}, JobHandle extraDeps = default)"
                );
                sb.AppendLine($"{ind}{{");
                var builderExpr = new StringBuilder("world.Query()");
                if (hasSets)
                {
                    foreach (var st in crit.SetTypeDisplays)
                        builderExpr.Append($".InSet<{st}>()");
                }
                var args = new List<string> { builderExpr.ToString() };
                args.AddRange(paramFields.Select(p => p.ScheduleParamName));
                args.Add("extraDeps");
                sb.AppendLine($"{ind}    return ScheduleParallel({string.Join(", ", args)});");
                sb.AppendLine($"{ind}}}");
                sb.AppendLine();
            }

            if (!hasSets)
            {
                EmitDenseScheduleOverload(sb, model, ind, fromWorldEmits, fromWorldParamDecl);
                sb.AppendLine();
            }

            EmitSparseScheduleOverload(sb, model, ind, fromWorldEmits, fromWorldParamDecl);
            sb.AppendLine();

            EmitSparseShim(sb, model, ind);
        }

        static void EmitDenseScheduleOverload(
            StringBuilder sb,
            in JobModel model,
            string ind,
            List<FromWorldFieldEmitModel> orderedEmits,
            string fromWorldParamDecl
        )
        {
            sb.AppendLine($"{ind}{GeneratedCodeAttributes.Line}");
            sb.AppendLine(
                $"{ind}public JobHandle ScheduleParallel(QueryBuilder builder{fromWorldParamDecl}, JobHandle extraDeps = default)"
            );
            sb.AppendLine($"{ind}{{");
            string body = ind + "    ";

            sb.AppendLine($"{body}var {FromWorldEmitter.GenPrefix}builder = builder;");
            sb.AppendLine($"{body}var {FromWorldEmitter.GenPrefix}extraDeps = extraDeps;");

            if (model.AttributeCriteriaChain.Length > 0)
                sb.AppendLine(
                    $"{body}{FromWorldEmitter.GenPrefix}builder = {FromWorldEmitter.GenPrefix}builder{model.AttributeCriteriaChain};"
                );

            sb.AppendLine(
                $"{body}TrecsDebugAssert.That({FromWorldEmitter.GenPrefix}builder.HasAnyCriteria, \"{model.StructName}.ScheduleParallel requires query criteria — pass a builder with at least one tag or component constraint, or specify Tags/MatchByComponents on the [EntityFilter] attribute.\");"
            );

            sb.AppendLine(
                $"{body}var {FromWorldEmitter.GenPrefix}world = {FromWorldEmitter.GenPrefix}builder.World;"
            );
            sb.AppendLine(
                $"{body}var {FromWorldEmitter.GenPrefix}scheduler = {FromWorldEmitter.GenPrefix}world.GetJobSchedulerForJob();"
            );
            sb.AppendLine(
                $"{body}const string {FromWorldEmitter.GenPrefix}jobName = \"{model.StructName}\";"
            );
            sb.AppendLine(
                $"{body}var {FromWorldEmitter.GenPrefix}allJobs = {FromWorldEmitter.GenPrefix}extraDeps;"
            );

            if (model.NeedsGlobalIndexOffset)
                sb.AppendLine($"{body}var {FromWorldEmitter.GenPrefix}queryIndexOffset = 0;");

            FromWorldEmitter.EmitFromWorldHoistedSetup(sb, body, orderedEmits);
            var singleEntityTargets = model
                .SingleEntityFields.Select(f => f.ToEmitTarget())
                .ToList();
            SingleEntityEmitter.EmitHoistedSetup(sb, body, singleEntityTargets);

            sb.AppendLine(
                $"{body}foreach (var {FromWorldEmitter.GenPrefix}slice in {FromWorldEmitter.GenPrefix}builder.GroupSlices())"
            );
            sb.AppendLine($"{body}{{");
            string innerBody = body + "    ";

            sb.AppendLine(
                $"{innerBody}var {FromWorldEmitter.GenPrefix}group = {FromWorldEmitter.GenPrefix}slice.GroupIndex;"
            );
            sb.AppendLine(
                $"{innerBody}var {FromWorldEmitter.GenPrefix}count = {FromWorldEmitter.GenPrefix}slice.Count;"
            );
            sb.AppendLine($"{innerBody}if ({FromWorldEmitter.GenPrefix}count == 0) continue;");
            sb.AppendLine();

            EmitPerGroupBody(sb, model, innerBody, orderedEmits, singleEntityTargets);

            if (model.NeedsGlobalIndexOffset)
                sb.AppendLine(
                    $"{innerBody}{FromWorldEmitter.GenPrefix}queryIndexOffset += {FromWorldEmitter.GenPrefix}count;"
                );

            sb.AppendLine($"{body}}}");
            sb.AppendLine($"{body}return {FromWorldEmitter.GenPrefix}allJobs;");
            sb.AppendLine($"{ind}}}");
        }

        static string FormatFromWorldParamDecl(List<FromWorldFieldEmitModel> paramFields)
        {
            if (paramFields.Count == 0)
                return "";

            var sorted = paramFields.OrderBy(p => p.IsOptionalParam).ToList();
            var parts = sorted.Select(p =>
                p.IsOptionalParam
                    ? $"{p.ScheduleParamType} {p.ScheduleParamName} = null"
                    : $"{p.ScheduleParamType} {p.ScheduleParamName}"
            );
            return ", " + string.Join(", ", parts);
        }

        // Iteration-buffer materialization is identical between dense and sparse
        // paths — common per-group body covering both. The orderedEmits and
        // singleEntityTargets are pre-projected at the caller so this helper
        // doesn't repeat the projection per group.
        static void EmitPerGroupBody(
            StringBuilder sb,
            in JobModel model,
            string body,
            List<FromWorldFieldEmitModel> orderedEmits,
            List<SingleEntityEmitTargetModel> singleEntityTargets
        )
        {
            sb.AppendLine(
                $"{body}var {FromWorldEmitter.GenPrefix}deps = {FromWorldEmitter.GenPrefix}extraDeps;"
            );

            var buffers = model.IterationBuffers;
            IterationBufferEmitter.EmitDepRegistration(sb, body, buffers);

            FromWorldEmitter.EmitFromWorldDepRegistration(sb, body, orderedEmits);
            SingleEntityEmitter.EmitDepRegistration(sb, body, singleEntityTargets);

            IterationBufferEmitter.EmitMaterialization(sb, body, buffers);

            sb.AppendLine($"{body}var {FromWorldEmitter.GenPrefix}job = this;");

            for (int i = 0; i < buffers.Count; i++)
                sb.AppendLine(
                    $"{body}{FromWorldEmitter.GenPrefix}job.{BufferFieldName(i)} = {FromWorldEmitter.GenPrefix}buf{i}_value;"
                );

            PerGroupHiddenFieldEmitter.EmitAssignments(
                sb,
                body,
                needsGroupField: model.NeedsGroupField,
                needsGlobalIndexOffset: model.NeedsGlobalIndexOffset,
                hasNativeWorldAccessor: false,
                needsEntityHandleBuffer: model.NeedsEntityHandleBuffer
            );

            FromWorldEmitter.EmitFromWorldFieldAssignments(sb, body, orderedEmits);
            SingleEntityEmitter.EmitFieldAssignment(sb, body, singleEntityTargets);

            sb.AppendLine($"{body}#if SVKJ_IS_PROFILING");
            sb.AppendLine(
                $"{body}var {FromWorldEmitter.GenPrefix}timing = {FromWorldEmitter.GenPrefix}scheduler.RentTimingBuffer();"
            );
            sb.AppendLine(
                $"{body}{FromWorldEmitter.GenPrefix}job.{FromWorldEmitter.JobFieldPrefix}timing = {FromWorldEmitter.GenPrefix}timing;"
            );
            sb.AppendLine($"{body}#endif");

            sb.AppendLine(
                $"{body}var {FromWorldEmitter.GenPrefix}handle = {FromWorldEmitter.GenPrefix}job.ScheduleParallel({FromWorldEmitter.GenPrefix}count, JobsUtil.ChooseBatchSize({FromWorldEmitter.GenPrefix}count), {FromWorldEmitter.GenPrefix}deps);"
            );

            sb.AppendLine($"{body}#if SVKJ_IS_PROFILING");
            sb.AppendLine(
                $"{body}{FromWorldEmitter.GenPrefix}scheduler.RegisterJobTimings({FromWorldEmitter.GenPrefix}handle, {FromWorldEmitter.GenPrefix}jobName, {FromWorldEmitter.GenPrefix}timing);"
            );
            sb.AppendLine($"{body}#endif");

            IterationBufferEmitter.EmitOutputTracking(sb, body, buffers);
            FromWorldEmitter.EmitFromWorldTracking(sb, body, orderedEmits);
            SingleEntityEmitter.EmitTracking(sb, body, singleEntityTargets);

            sb.AppendLine(
                $"{body}{FromWorldEmitter.GenPrefix}allJobs = JobHandle.CombineDependencies({FromWorldEmitter.GenPrefix}allJobs, {FromWorldEmitter.GenPrefix}handle);"
            );
        }

        // ─── Sparse path: SparseQueryBuilder schedule overload ───────────────────

        static void EmitSparseScheduleOverload(
            StringBuilder sb,
            in JobModel model,
            string ind,
            List<FromWorldFieldEmitModel> orderedEmits,
            string fromWorldParamDecl
        )
        {
            sb.AppendLine($"{ind}{GeneratedCodeAttributes.Line}");
            sb.AppendLine(
                $"{ind}public JobHandle ScheduleParallel(SparseQueryBuilder builder{fromWorldParamDecl}, JobHandle extraDeps = default)"
            );
            sb.AppendLine($"{ind}{{");
            string body = ind + "    ";

            sb.AppendLine($"{body}var {FromWorldEmitter.GenPrefix}builder = builder;");
            sb.AppendLine($"{body}var {FromWorldEmitter.GenPrefix}extraDeps = extraDeps;");

            if (model.AttributeCriteriaChain.Length > 0)
                sb.AppendLine(
                    $"{body}{FromWorldEmitter.GenPrefix}builder = {FromWorldEmitter.GenPrefix}builder{model.AttributeCriteriaChain};"
                );

            sb.AppendLine(
                $"{body}var {FromWorldEmitter.GenPrefix}world = {FromWorldEmitter.GenPrefix}builder.World;"
            );
            sb.AppendLine(
                $"{body}var {FromWorldEmitter.GenPrefix}scheduler = {FromWorldEmitter.GenPrefix}world.GetJobSchedulerForJob();"
            );
            sb.AppendLine(
                $"{body}const string {FromWorldEmitter.GenPrefix}jobName = \"{model.StructName}\";"
            );
            sb.AppendLine(
                $"{body}var {FromWorldEmitter.GenPrefix}allJobs = {FromWorldEmitter.GenPrefix}extraDeps;"
            );

            if (model.NeedsGlobalIndexOffset)
                sb.AppendLine($"{body}var {FromWorldEmitter.GenPrefix}queryIndexOffset = 0;");

            FromWorldEmitter.EmitFromWorldHoistedSetup(sb, body, orderedEmits);
            var singleEntityTargets = model
                .SingleEntityFields.Select(f => f.ToEmitTarget())
                .ToList();
            SingleEntityEmitter.EmitHoistedSetup(sb, body, singleEntityTargets);

            sb.AppendLine(
                $"{body}foreach (var {FromWorldEmitter.GenPrefix}slice in {FromWorldEmitter.GenPrefix}builder.GroupSlices())"
            );
            sb.AppendLine($"{body}{{");
            string innerBody = body + "    ";

            sb.AppendLine(
                $"{innerBody}var {FromWorldEmitter.GenPrefix}group = {FromWorldEmitter.GenPrefix}slice.GroupIndex;"
            );
            sb.AppendLine();

            sb.AppendLine(
                $"{innerBody}var ({FromWorldEmitter.GenPrefix}indices, {FromWorldEmitter.GenPrefix}indicesLifetime, {FromWorldEmitter.GenPrefix}count) = {FromWorldEmitter.GenPrefix}world.AllocateSparseIndicesForJob({FromWorldEmitter.GenPrefix}slice);"
            );
            sb.AppendLine($"{innerBody}if ({FromWorldEmitter.GenPrefix}count == 0)");
            sb.AppendLine($"{innerBody}{{");
            sb.AppendLine($"{innerBody}    {FromWorldEmitter.GenPrefix}indicesLifetime.Dispose();");
            sb.AppendLine($"{innerBody}    continue;");
            sb.AppendLine($"{innerBody}}}");
            sb.AppendLine();

            EmitPerGroupBodyForSparse(sb, model, innerBody, orderedEmits, singleEntityTargets);

            if (model.NeedsGlobalIndexOffset)
                sb.AppendLine(
                    $"{innerBody}{FromWorldEmitter.GenPrefix}queryIndexOffset += {FromWorldEmitter.GenPrefix}count;"
                );

            sb.AppendLine($"{body}}}");
            sb.AppendLine($"{body}return {FromWorldEmitter.GenPrefix}allJobs;");
            sb.AppendLine($"{ind}}}");
        }

        static void EmitPerGroupBodyForSparse(
            StringBuilder sb,
            in JobModel model,
            string body,
            List<FromWorldFieldEmitModel> orderedEmits,
            List<SingleEntityEmitTargetModel> singleEntityTargets
        )
        {
            sb.AppendLine(
                $"{body}var {FromWorldEmitter.GenPrefix}deps = {FromWorldEmitter.GenPrefix}extraDeps;"
            );

            var buffers = model.IterationBuffers;
            IterationBufferEmitter.EmitDepRegistration(sb, body, buffers);

            FromWorldEmitter.EmitFromWorldDepRegistration(sb, body, orderedEmits);
            SingleEntityEmitter.EmitDepRegistration(sb, body, singleEntityTargets);

            IterationBufferEmitter.EmitMaterialization(sb, body, buffers);

            sb.AppendLine($"{body}var {FromWorldEmitter.GenPrefix}job = this;");

            for (int i = 0; i < buffers.Count; i++)
                sb.AppendLine(
                    $"{body}{FromWorldEmitter.GenPrefix}job.{BufferFieldName(i)} = {FromWorldEmitter.GenPrefix}buf{i}_value;"
                );

            PerGroupHiddenFieldEmitter.EmitAssignments(
                sb,
                body,
                needsGroupField: model.NeedsGroupField,
                needsGlobalIndexOffset: model.NeedsGlobalIndexOffset,
                hasNativeWorldAccessor: false,
                needsEntityHandleBuffer: model.NeedsEntityHandleBuffer
            );

            FromWorldEmitter.EmitFromWorldFieldAssignments(sb, body, orderedEmits);
            SingleEntityEmitter.EmitFieldAssignment(sb, body, singleEntityTargets);

            sb.AppendLine($"{body}#if SVKJ_IS_PROFILING");
            sb.AppendLine(
                $"{body}var {FromWorldEmitter.GenPrefix}timing = {FromWorldEmitter.GenPrefix}scheduler.RentTimingBuffer();"
            );
            sb.AppendLine(
                $"{body}{FromWorldEmitter.GenPrefix}job.{FromWorldEmitter.JobFieldPrefix}timing = {FromWorldEmitter.GenPrefix}timing;"
            );
            sb.AppendLine($"{body}#endif");

            sb.AppendLine(
                $"{body}var {FromWorldEmitter.GenPrefix}shim = new {FromWorldEmitter.JobFieldPrefix}SparseShim {{ Inner = {FromWorldEmitter.GenPrefix}job, Indices = {FromWorldEmitter.GenPrefix}indices }};"
            );
            sb.AppendLine(
                $"{body}var {FromWorldEmitter.GenPrefix}handle = {FromWorldEmitter.GenPrefix}shim.ScheduleParallel({FromWorldEmitter.GenPrefix}count, JobsUtil.ChooseBatchSize({FromWorldEmitter.GenPrefix}count), {FromWorldEmitter.GenPrefix}deps);"
            );

            sb.AppendLine($"{body}#if SVKJ_IS_PROFILING");
            sb.AppendLine(
                $"{body}{FromWorldEmitter.GenPrefix}scheduler.RegisterJobTimings({FromWorldEmitter.GenPrefix}handle, {FromWorldEmitter.GenPrefix}jobName, {FromWorldEmitter.GenPrefix}timing);"
            );
            sb.AppendLine($"{body}#endif");

            IterationBufferEmitter.EmitOutputTracking(sb, body, buffers);
            FromWorldEmitter.EmitFromWorldTracking(sb, body, orderedEmits);
            SingleEntityEmitter.EmitTracking(sb, body, singleEntityTargets);

            sb.AppendLine(
                $"{body}var {FromWorldEmitter.GenPrefix}disposeHandle = {FromWorldEmitter.GenPrefix}indicesLifetime.Dispose({FromWorldEmitter.GenPrefix}handle);"
            );
            sb.AppendLine(
                $"{body}{FromWorldEmitter.GenPrefix}scheduler.TrackJob({FromWorldEmitter.GenPrefix}disposeHandle, {FromWorldEmitter.GenPrefix}jobName);"
            );
            sb.AppendLine(
                $"{body}{FromWorldEmitter.GenPrefix}allJobs = JobHandle.CombineDependencies({FromWorldEmitter.GenPrefix}allJobs, {FromWorldEmitter.GenPrefix}disposeHandle);"
            );
        }

        // ─── Custom non-iteration job: single Schedule(WorldAccessor, ...) overload ──

        static void EmitCustomScheduleOverload(StringBuilder sb, in JobModel model, string ind)
        {
            var orderedEmits = model.FromWorldFields.ToList();
            var paramFields = orderedEmits.Where(e => e.HasScheduleParam).ToList();
            var fromWorldParamDecl = FormatFromWorldParamDecl(paramFields);

            sb.AppendLine($"{ind}{GeneratedCodeAttributes.Line}");
            sb.AppendLine(
                $"{ind}public JobHandle Schedule(WorldAccessor world{fromWorldParamDecl}, JobHandle extraDeps = default)"
            );
            sb.AppendLine($"{ind}{{");
            string body = ind + "    ";

            sb.AppendLine($"{body}var {FromWorldEmitter.GenPrefix}world = world;");
            sb.AppendLine($"{body}var {FromWorldEmitter.GenPrefix}extraDeps = extraDeps;");

            sb.AppendLine(
                $"{body}var {FromWorldEmitter.GenPrefix}scheduler = {FromWorldEmitter.GenPrefix}world.GetJobSchedulerForJob();"
            );
            sb.AppendLine(
                $"{body}const string {FromWorldEmitter.GenPrefix}jobName = \"{model.StructName}\";"
            );

            FromWorldEmitter.EmitFromWorldHoistedSetup(sb, body, orderedEmits);
            var singleEntityTargets = model
                .SingleEntityFields.Select(f => f.ToEmitTarget())
                .ToList();
            SingleEntityEmitter.EmitHoistedSetup(sb, body, singleEntityTargets);

            sb.AppendLine(
                $"{body}var {FromWorldEmitter.GenPrefix}deps = {FromWorldEmitter.GenPrefix}extraDeps;"
            );
            FromWorldEmitter.EmitFromWorldDepRegistration(sb, body, orderedEmits);
            SingleEntityEmitter.EmitDepRegistration(sb, body, singleEntityTargets);

            sb.AppendLine($"{body}var {FromWorldEmitter.GenPrefix}job = this;");
            FromWorldEmitter.EmitFromWorldFieldAssignments(sb, body, orderedEmits);
            SingleEntityEmitter.EmitFieldAssignment(sb, body, singleEntityTargets);

            sb.AppendLine(
                $"{body}var {FromWorldEmitter.GenPrefix}handle = {FromWorldEmitter.GenPrefix}job.Schedule({FromWorldEmitter.GenPrefix}deps);"
            );

            FromWorldEmitter.EmitFromWorldTracking(sb, body, orderedEmits);
            SingleEntityEmitter.EmitTracking(sb, body, singleEntityTargets);

            sb.AppendLine($"{body}return {FromWorldEmitter.GenPrefix}handle;");
            sb.AppendLine($"{ind}}}");
        }

        static void EmitCustomParallelScheduleOverload(
            StringBuilder sb,
            in JobModel model,
            string ind
        )
        {
            var orderedEmits = model.FromWorldFields.ToList();
            var paramFields = orderedEmits.Where(e => e.HasScheduleParam).ToList();
            var fromWorldParamDecl = FormatFromWorldParamDecl(paramFields);

            sb.AppendLine($"{ind}{GeneratedCodeAttributes.Line}");
            sb.AppendLine(
                $"{ind}public JobHandle ScheduleParallel(WorldAccessor world, int count{fromWorldParamDecl}, JobHandle extraDeps = default)"
            );
            sb.AppendLine($"{ind}{{");
            string body = ind + "    ";

            sb.AppendLine($"{body}var {FromWorldEmitter.GenPrefix}world = world;");
            sb.AppendLine($"{body}var {FromWorldEmitter.GenPrefix}extraDeps = extraDeps;");

            sb.AppendLine($"{body}if (count <= 0) return {FromWorldEmitter.GenPrefix}extraDeps;");
            sb.AppendLine(
                $"{body}var {FromWorldEmitter.GenPrefix}scheduler = {FromWorldEmitter.GenPrefix}world.GetJobSchedulerForJob();"
            );
            sb.AppendLine(
                $"{body}const string {FromWorldEmitter.GenPrefix}jobName = \"{model.StructName}\";"
            );

            FromWorldEmitter.EmitFromWorldHoistedSetup(sb, body, orderedEmits);
            var singleEntityTargets = model
                .SingleEntityFields.Select(f => f.ToEmitTarget())
                .ToList();
            SingleEntityEmitter.EmitHoistedSetup(sb, body, singleEntityTargets);

            sb.AppendLine(
                $"{body}var {FromWorldEmitter.GenPrefix}deps = {FromWorldEmitter.GenPrefix}extraDeps;"
            );
            FromWorldEmitter.EmitFromWorldDepRegistration(sb, body, orderedEmits);
            SingleEntityEmitter.EmitDepRegistration(sb, body, singleEntityTargets);

            sb.AppendLine($"{body}var {FromWorldEmitter.GenPrefix}job = this;");
            FromWorldEmitter.EmitFromWorldFieldAssignments(sb, body, orderedEmits);
            SingleEntityEmitter.EmitFieldAssignment(sb, body, singleEntityTargets);

            sb.AppendLine(
                $"{body}var {FromWorldEmitter.GenPrefix}handle = {FromWorldEmitter.GenPrefix}job.ScheduleParallel(count, JobsUtil.ChooseBatchSize(count), {FromWorldEmitter.GenPrefix}deps);"
            );

            FromWorldEmitter.EmitFromWorldTracking(sb, body, orderedEmits);
            SingleEntityEmitter.EmitTracking(sb, body, singleEntityTargets);

            sb.AppendLine($"{body}return {FromWorldEmitter.GenPrefix}handle;");
            sb.AppendLine($"{ind}}}");
        }

        // ─── Sparse shim struct emission ───────────────────────────────────────────

        static void EmitSparseShim(StringBuilder sb, in JobModel model, string ind)
        {
            string innerInd = ind + "    ";
            string bodyInd = innerInd + "    ";

            if (model.HasBurstCompile)
                sb.AppendLine($"{ind}[Unity.Burst.BurstCompile]");
            sb.AppendLine($"{ind}{GeneratedCodeAttributes.Line}");
            sb.AppendLine(
                $"{ind}private struct {FromWorldEmitter.JobFieldPrefix}SparseShim : IJobFor"
            );
            sb.AppendLine($"{ind}{{");
            sb.AppendLine($"{innerInd}public {model.StructName} Inner;");
            sb.AppendLine($"{innerInd}public JobSparseIndices Indices;");
            sb.AppendLine();
            sb.AppendLine($"{innerInd}public void Execute(int i)");
            sb.AppendLine($"{innerInd}{{");
            sb.AppendLine($"{bodyInd}Inner.Execute(Indices[i]);");
            sb.AppendLine($"{innerInd}}}");
            sb.AppendLine($"{ind}}}");
        }

        static bool HasBurstCompile(INamedTypeSymbol structSymbol)
        {
            foreach (var attr in structSymbol.GetAttributes())
            {
                var ac = attr.AttributeClass;
                if (ac == null)
                    continue;
                if (ac.Name != "BurstCompileAttribute")
                    continue;
                var ns = ac.ContainingNamespace?.ToDisplayString();
                if (ns == "Unity.Burst")
                    return true;
            }
            return false;
        }

        // ─── Data classes (nested so they don't pollute the namespace) ───────────

        internal enum JobKind
        {
            Aspect,
            Components,
            CustomNonIteration,
            CustomParallelIteration,
        }

        internal class JobStructData
        {
            public StructDeclarationSyntax StructDecl { get; }
            public INamedTypeSymbol Symbol { get; }

            public JobStructData(StructDeclarationSyntax structDecl, INamedTypeSymbol symbol)
            {
                StructDecl = structDecl;
                Symbol = symbol;
            }
        }

        internal class JobInfo
        {
            public INamedTypeSymbol Symbol { get; }
            public StructDeclarationSyntax StructDecl { get; }
            public JobKind Kind { get; }
            public AspectIterationInfo? Aspect { get; }
            public ComponentsIterationInfo? Components { get; }
            public List<FromWorldFieldInfo> FromWorldFields { get; }

            /// <summary>
            /// <c>[SingleEntity]</c>-decorated fields on the job struct. Each entry is
            /// resolved at schedule time via <c>Query().WithTags&lt;...&gt;().SingleIndex()</c>
            /// and assigned into the per-group job instance. Aspect-typed fields are
            /// constructed from the singleton's group buffers; component fields use
            /// <c>NativeComponentRead/Write&lt;T&gt;</c>.
            /// </summary>
            public List<SingleEntityFieldEntry> SingleEntityFields { get; }

            public IterationCriteria IterationCriteria =>
                Kind switch
                {
                    JobKind.Aspect => Aspect!.Criteria,
                    JobKind.Components => Components!.Criteria,
                    _ => throw new System.InvalidOperationException(
                        "IterationCriteria is only meaningful for iteration jobs"
                    ),
                };
            public string IterationAttributeShortName => "EntityFilter";

            // True if the generated job needs a `_trecs_Group` field. Aspect always needs
            // it (for the aspect ctor + EntityIndex). Components only needs it when the
            // user took an EntityIndex parameter. Custom non-iteration jobs never need it.
            public bool NeedsGroupField =>
                Kind == JobKind.Aspect
                || (Kind == JobKind.Components && Components!.HasEntityIndexParameter);

            public bool NeedsGlobalIndexOffset =>
                Kind == JobKind.Components && Components!.HasGlobalIndexParameter;

            // True if the generated job needs a `_trecs_EntityHandles` NativeEntityHandleBuffer
            // field. Set when the user took an EntityHandle iteration parameter (in either
            // aspect or components mode); the field is populated per-group at schedule time
            // and dereferenced as `__EntityHandles[i]` in the Execute shim.
            public bool NeedsEntityHandleBuffer =>
                (Kind == JobKind.Aspect && Aspect!.HasEntityHandleParameter)
                || (Kind == JobKind.Components && Components!.HasEntityHandleParameter);

            public JobInfo(
                INamedTypeSymbol symbol,
                StructDeclarationSyntax structDecl,
                JobKind kind,
                AspectIterationInfo? aspect,
                ComponentsIterationInfo? components,
                List<FromWorldFieldInfo> fromWorldFields,
                List<SingleEntityFieldEntry> singleEntityFields
            )
            {
                Symbol = symbol;
                StructDecl = structDecl;
                Kind = kind;
                Aspect = aspect;
                Components = components;
                FromWorldFields = fromWorldFields;
                SingleEntityFields = singleEntityFields;
            }
        }

        /// <summary>
        /// Field on a hand-written Trecs job struct that carries <c>[SingleEntity(Tag/Tags)]</c>.
        /// </summary>
        internal class SingleEntityFieldEntry
        {
            public string FieldName { get; }
            public bool IsAspect { get; }
            public List<ITypeSymbol> TagTypes { get; }

            // Aspect:
            public string? AspectTypeDisplay { get; }

            /// <summary>
            /// Parsed aspect data — used for IRead/IWrite component lists and the canonical
            /// <c>AllComponentTypes</c> ordering shared with the aspect's generated
            /// EntityIndex constructor. Null for component fields.
            /// </summary>
            public Aspect.AspectAttributeData? AspectData { get; }

            // Component (the field type is NativeComponentRead<T> or NativeComponentWrite<T>;
            // ComponentTypeSymbol is the inner T, ComponentTypeDisplay is its display string):
            public string? ComponentTypeDisplay { get; }
            public ITypeSymbol? ComponentTypeSymbol { get; }
            public bool IsRef { get; }

            public SingleEntityFieldEntry(
                string fieldName,
                bool isAspect,
                List<ITypeSymbol> tagTypes,
                string? aspectTypeDisplay = null,
                Aspect.AspectAttributeData? aspectData = null,
                string? componentTypeDisplay = null,
                ITypeSymbol? componentTypeSymbol = null,
                bool isRef = false
            )
            {
                FieldName = fieldName;
                IsAspect = isAspect;
                TagTypes = tagTypes;
                AspectTypeDisplay = aspectTypeDisplay;
                AspectData = aspectData;
                ComponentTypeDisplay = componentTypeDisplay;
                ComponentTypeSymbol = componentTypeSymbol;
                IsRef = isRef;
            }
        }

        internal class AspectIterationInfo
        {
            public string AspectTypeName { get; }
            public INamedTypeSymbol AspectTypeSymbol { get; }
            public List<ITypeSymbol> ComponentTypes { get; }
            public List<ITypeSymbol> ReadComponentTypes { get; }
            public List<ITypeSymbol> WriteComponentTypes { get; }
            public bool HasEntityIndexParameter { get; }
            public bool HasEntityHandleParameter { get; }

            // For aspect-mode with extra params, we need to know the original
            // declaration order so the Execute call args land in the same slots
            // the user wrote.
            public List<AspectExtraParamKind> ExtraParamOrder { get; }
            public IterationCriteria Criteria { get; }

            public AspectIterationInfo(
                string aspectTypeName,
                INamedTypeSymbol aspectTypeSymbol,
                List<ITypeSymbol> componentTypes,
                List<ITypeSymbol> readComponentTypes,
                List<ITypeSymbol> writeComponentTypes,
                bool hasEntityIndexParameter,
                bool hasEntityHandleParameter,
                List<AspectExtraParamKind> extraParamOrder,
                IterationCriteria criteria
            )
            {
                AspectTypeName = aspectTypeName;
                AspectTypeSymbol = aspectTypeSymbol;
                ComponentTypes = componentTypes;
                ReadComponentTypes = readComponentTypes;
                WriteComponentTypes = writeComponentTypes;
                HasEntityIndexParameter = hasEntityIndexParameter;
                HasEntityHandleParameter = hasEntityHandleParameter;
                ExtraParamOrder = extraParamOrder;
                Criteria = criteria;
            }

            public bool IsReadOnly(ITypeSymbol type)
            {
                return ReadComponentTypes.Any(t => SymbolEqualityComparer.Default.Equals(t, type))
                    && !WriteComponentTypes.Any(t =>
                        SymbolEqualityComparer.Default.Equals(t, type)
                    );
            }
        }

        internal class ComponentsIterationInfo
        {
            // Parameters in their original declaration order. Each slot is one of:
            //   - a component (in/ref) — Type set, BufferIndex set
            //   - an EntityIndex parameter — Type and BufferIndex unset, Role = EntityIndex
            //   - a [GlobalIndex] int parameter — Type and BufferIndex unset, Role = GlobalIndex
            // The user can mix these in any order; the Execute shim emits the call in this order.
            public List<ComponentsParam> Parameters { get; }
            public IterationCriteria Criteria { get; }

            public ComponentsIterationInfo(
                List<ComponentsParam> parameters,
                IterationCriteria criteria
            )
            {
                Parameters = parameters;
                Criteria = criteria;
            }

            public bool HasEntityIndexParameter =>
                Parameters.Any(p => p.Role == ComponentsParamRole.EntityIndex);
            public bool HasEntityHandleParameter =>
                Parameters.Any(p => p.Role == ComponentsParamRole.EntityHandle);
            public bool HasGlobalIndexParameter =>
                Parameters.Any(p => p.Role == ComponentsParamRole.GlobalIndex);
            public IEnumerable<ComponentsParam> Components =>
                Parameters.Where(p => p.Role == ComponentsParamRole.Component);
        }

        internal enum ComponentsParamRole
        {
            Component,
            EntityIndex,
            EntityHandle,
            GlobalIndex,
        }

        /// <summary>
        /// Order-tracking for the optional entity-shaped parameters following
        /// the aspect param in an aspect-mode <c>[ForEachEntity]</c> job method.
        /// </summary>
        internal enum AspectExtraParamKind
        {
            EntityIndex,
            EntityHandle,
        }

        internal class ComponentsParam
        {
            public ComponentsParamRole Role { get; }
            public ITypeSymbol? Type { get; } // null unless Role == Component
            public bool IsRef { get; } // unused unless Role == Component
            public bool IsIn { get; } // unused unless Role == Component
            public string Name { get; }
            public int BufferIndex { get; } // -1 unless Role == Component (positional index in the component-only subset)

            ComponentsParam(
                ComponentsParamRole role,
                ITypeSymbol? type,
                bool isRef,
                bool isIn,
                string name,
                int bufferIndex
            )
            {
                Role = role;
                Type = type;
                IsRef = isRef;
                IsIn = isIn;
                Name = name;
                BufferIndex = bufferIndex;
            }

            public static ComponentsParam Component(
                ITypeSymbol type,
                string name,
                bool isRef,
                bool isIn,
                int bufferIndex
            ) => new(ComponentsParamRole.Component, type, isRef, isIn, name, bufferIndex);

            public static ComponentsParam EntityIndexParam(string name) =>
                new(ComponentsParamRole.EntityIndex, null, false, false, name, -1);

            public static ComponentsParam EntityHandleParam(string name) =>
                new(ComponentsParamRole.EntityHandle, null, false, false, name, -1);

            public static ComponentsParam GlobalIndexParam(string name) =>
                new(ComponentsParamRole.GlobalIndex, null, false, false, name, -1);
        }
    }
}
