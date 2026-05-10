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
            // Detect partial structs that look like Trecs jobs in the new pattern.
            //
            // We discriminate at the syntax level on the struct's declaration:
            //   1. Has at least one [ForEachEntity] on a method, or
            //   2. Has at least one [FromWorld] on a field.
            //
            // The semantic-model verification happens in the transform stage.
            var jobStructProvider = context
                .SyntaxProvider.CreateSyntaxProvider(
                    predicate: static (s, _) => IsCandidateJobStruct(s),
                    transform: static (ctx, _) => GetJobStructData(ctx)
                )
                .Where(static d => d is not null);

            var jobStructWithCompilation = jobStructProvider.Combine(context.CompilationProvider);

            context.RegisterSourceOutput(
                jobStructWithCompilation,
                static (spc, source) => GenerateJobSource(spc, source.Left!, source.Right)
            );
        }

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

        static JobStructData? GetJobStructData(GeneratorSyntaxContext context)
        {
            var structDecl = (StructDeclarationSyntax)context.Node;
            var symbol = context.SemanticModel.GetDeclaredSymbol(structDecl) as INamedTypeSymbol;
            if (symbol == null)
                return null;
            return new JobStructData(structDecl, symbol);
        }

        static void GenerateJobSource(
            SourceProductionContext context,
            JobStructData data,
            Compilation compilation
        )
        {
            var location = data.StructDecl.GetLocation();
            try
            {
                using var _t = SourceGenTimer.Time("JobGenerator.Total");

                var info = ValidateJobStruct(context, data, compilation);
                if (info == null)
                    return;

                var source = GenerateSource(info, compilation);
                if (source == null)
                    return;

                var fileName = SymbolAnalyzer.GetSafeFileName(data.Symbol, "Job");
                context.AddSource(fileName, source);
                SourceGenLogger.WriteGeneratedFile(fileName, source);
            }
            catch (Exception ex)
            {
                ErrorRecovery.ReportError(context, location, $"Job {data.Symbol.Name}", ex);
            }
        }

        // ─── Validation ─────────────────────────────────────────────────────────

        static JobInfo? ValidateJobStruct(
            SourceProductionContext context,
            JobStructData data,
            Compilation compilation
        )
        {
            var structDecl = data.StructDecl;
            var symbol = data.Symbol;
            var semanticModel = compilation.GetSemanticModel(structDecl.SyntaxTree);

            // Reject jobs nested inside a generic enclosing type — the partial we emit
            // would need to redeclare the outer type's type parameters and constraints,
            // which the generator doesn't currently support. Move the job out of the
            // generic outer type, or factor it into a helper.
            for (var t = symbol.ContainingType; t != null; t = t.ContainingType)
            {
                if (t.IsGenericType)
                {
                    context.ReportDiagnostic(
                        Diagnostic.Create(
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
                context.ReportDiagnostic(
                    Diagnostic.Create(
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
                    context.ReportDiagnostic(
                        Diagnostic.Create(
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
                        context,
                        structDecl,
                        symbol,
                        semanticModel
                    );

                return ValidateCustomNonIterationJob(context, structDecl, symbol, semanticModel);
            }

            // Validate the iteration method shape.
            if (iterationMethod.ReturnType.ToString() != "void")
            {
                context.ReportDiagnostic(
                    Diagnostic.Create(
                        DiagnosticDescriptors.InvalidReturnType,
                        iterationMethod.ReturnType.GetLocation()
                    )
                );
                return null;
            }
            if (iterationMethod.Modifiers.Any(m => m.IsKind(SyntaxKind.StaticKeyword)))
            {
                context.ReportDiagnostic(
                    Diagnostic.Create(
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
                context,
                structDecl,
                semanticModel,
                isParallelJob: true
            );
            if (fromWorldFields == null)
                return null;

            var singleEntityFields = ScanSingleEntityFields(
                context,
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
                    context,
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
                    context,
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
            SourceProductionContext context,
            StructDeclarationSyntax structDecl,
            MethodDeclarationSyntax method,
            INamedTypeSymbol structSymbol,
            SemanticModel semanticModel
        )
        {
            var parameters = method.ParameterList.Parameters;
            if (parameters.Count == 0)
            {
                context.ReportDiagnostic(
                    Diagnostic.Create(DiagnosticDescriptors.EmptyParameters, method.GetLocation())
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
                context.ReportDiagnostic(
                    Diagnostic.Create(
                        DiagnosticDescriptors.InvalidParameterList,
                        firstParam.GetLocation(),
                        "First parameter of [ForEachEntity] Execute on a job must implement IAspect"
                    )
                );
                return null;
            }
            if (!firstParam.Modifiers.Any(m => m.IsKind(SyntaxKind.InKeyword)))
            {
                context.ReportDiagnostic(
                    Diagnostic.Create(
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

            // Optional EntityIndex second parameter.
            bool hasEntityIndex = false;
            if (parameters.Count >= 2)
            {
                if (parameters[1].Type?.ToString() == "EntityIndex")
                    hasEntityIndex = true;
            }
            if (parameters.Count > (hasEntityIndex ? 2 : 1))
            {
                // Detect the "mixed signature" case where an aspect parameter is followed
                // by a direct component parameter — this is intentionally not supported.
                // Aspects are the canonical way to declare a method's component requirements
                // in Trecs; if you need an additional component, add it to the aspect's
                // IRead<T> / IWrite<T> interface list. Aspects are typically defined per
                // method, so this is usually a one-line addition.
                int extraStart = hasEntityIndex ? 2 : 1;
                ParameterSyntax? offendingComponentParam = null;
                ITypeSymbol? offendingComponentType = null;
                for (int i = extraStart; i < parameters.Count; i++)
                {
                    var paramType =
                        parameters[i].Type != null
                            ? semanticModel.GetTypeInfo(parameters[i].Type!).Type
                            : null;
                    if (
                        paramType != null
                        && SymbolAnalyzer.ImplementsInterface(
                            paramType,
                            "IEntityComponent",
                            TrecsNamespaces.Trecs
                        )
                    )
                    {
                        offendingComponentParam = parameters[i];
                        offendingComponentType = paramType;
                        break;
                    }
                }

                if (offendingComponentParam != null)
                {
                    context.ReportDiagnostic(
                        Diagnostic.Create(
                            DiagnosticDescriptors.MixedAspectAndComponentParams,
                            offendingComponentParam.GetLocation(),
                            method.Identifier.Text,
                            offendingComponentParam.Identifier.Text,
                            PerformanceCache.GetDisplayString(offendingComponentType!)
                        )
                    );
                }
                else
                {
                    context.ReportDiagnostic(
                        Diagnostic.Create(
                            DiagnosticDescriptors.InvalidJobParameterList,
                            method.GetLocation(),
                            "[ForEachEntity] iteration method on a job takes (in AspectType, EntityIndex?). Extra parameters are not supported."
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
                context.ReportDiagnostic,
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
                criteria: criteria
            );
        }

        static ComponentsIterationInfo? ValidateForEachComponentsMethod(
            SourceProductionContext context,
            StructDeclarationSyntax structDecl,
            MethodDeclarationSyntax method,
            INamedTypeSymbol structSymbol,
            SemanticModel semanticModel
        )
        {
            var parameters = method.ParameterList.Parameters;
            if (parameters.Count == 0)
            {
                context.ReportDiagnostic(
                    Diagnostic.Create(DiagnosticDescriptors.EmptyParameters, method.GetLocation())
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
            bool hasGlobalIndex = false;
            foreach (var p in parameters)
            {
                var paramType = p.Type != null ? semanticModel.GetTypeInfo(p.Type).Type : null;
                if (paramType == null)
                {
                    context.ReportDiagnostic(
                        Diagnostic.Create(
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
                        context.ReportDiagnostic(
                            Diagnostic.Create(
                                DiagnosticDescriptors.InvalidJobParameterList,
                                p.GetLocation(),
                                $"Method '{method.Identifier.Text}' has more than one [GlobalIndex] parameter — only one is allowed."
                            )
                        );
                        return null;
                    }
                    if (paramType.SpecialType != SpecialType.System_Int32)
                    {
                        context.ReportDiagnostic(
                            Diagnostic.Create(
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
                        == TrecsNamespaces.TrecsInternal
                )
                {
                    if (hasEntityIndex)
                    {
                        context.ReportDiagnostic(
                            Diagnostic.Create(
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

                // Otherwise expect an in/ref IEntityComponent parameter.
                bool isRef = p.Modifiers.Any(m => m.IsKind(SyntaxKind.RefKeyword));
                bool isIn = p.Modifiers.Any(m => m.IsKind(SyntaxKind.InKeyword));
                if (!isRef && !isIn)
                {
                    context.ReportDiagnostic(
                        Diagnostic.Create(
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
                    context.ReportDiagnostic(
                        Diagnostic.Create(
                            DiagnosticDescriptors.InvalidParameterList,
                            p.GetLocation(),
                            $"Parameter '{p.Identifier.Text}' must implement IEntityComponent (or be EntityIndex / [GlobalIndex] int) — Trecs jobs do not support custom pass-through arguments."
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
                context.ReportDiagnostic(
                    Diagnostic.Create(DiagnosticDescriptors.EmptyParameters, method.GetLocation())
                );
                return null;
            }

            var methodSymbol = semanticModel.GetDeclaredSymbol(method) as IMethodSymbol;
            if (methodSymbol == null)
                return null;
            var criteria = IterationCriteriaParser.ParseIterationAttribute(
                context.ReportDiagnostic,
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
            SourceProductionContext context,
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
                    context.ReportDiagnostic(
                        Diagnostic.Create(
                            DiagnosticDescriptors.CustomJobMissingExecuteMethod,
                            structDecl.Identifier.GetLocation(),
                            symbol.Name
                        )
                    );
                }
                else
                {
                    context.ReportDiagnostic(
                        Diagnostic.Create(
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
                context.ReportDiagnostic(
                    Diagnostic.Create(
                        DiagnosticDescriptors.InvalidReturnType,
                        executeMethod.ReturnType.GetLocation()
                    )
                );
                return null;
            }

            if (executeMethod.Modifiers.Any(m => m.IsKind(SyntaxKind.StaticKeyword)))
            {
                context.ReportDiagnostic(
                    Diagnostic.Create(
                        DiagnosticDescriptors.IterationMethodCannotBeStatic,
                        executeMethod.Identifier.GetLocation(),
                        executeMethod.Identifier.Text
                    )
                );
                return null;
            }

            if (!executeMethod.Modifiers.Any(m => m.IsKind(SyntaxKind.PublicKeyword)))
            {
                context.ReportDiagnostic(
                    Diagnostic.Create(
                        DiagnosticDescriptors.CustomJobExecuteMustBePublic,
                        executeMethod.Identifier.GetLocation(),
                        symbol.Name
                    )
                );
                return null;
            }

            // Custom non-iteration jobs (parameterless Execute → IJob) are NOT scheduled parallel.
            var fromWorldFields = ScanFromWorldFields(
                context,
                structDecl,
                semanticModel,
                isParallelJob: false
            );
            if (fromWorldFields == null)
                return null;

            var singleEntityFields = ScanSingleEntityFields(
                context,
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
            SourceProductionContext context,
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
                context.ReportDiagnostic(
                    Diagnostic.Create(
                        DiagnosticDescriptors.InvalidReturnType,
                        executeMethod.ReturnType.GetLocation()
                    )
                );
                return null;
            }

            if (executeMethod.Modifiers.Any(m => m.IsKind(SyntaxKind.StaticKeyword)))
            {
                context.ReportDiagnostic(
                    Diagnostic.Create(
                        DiagnosticDescriptors.IterationMethodCannotBeStatic,
                        executeMethod.Identifier.GetLocation(),
                        executeMethod.Identifier.Text
                    )
                );
                return null;
            }

            if (!executeMethod.Modifiers.Any(m => m.IsKind(SyntaxKind.PublicKeyword)))
            {
                context.ReportDiagnostic(
                    Diagnostic.Create(
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
                context,
                structDecl,
                semanticModel,
                isParallelJob: true
            );
            if (fromWorldFields == null)
                return null;

            var singleEntityFields = ScanSingleEntityFields(
                context,
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
            SourceProductionContext context,
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
                    context.ReportDiagnostic(
                        Diagnostic.Create(
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
                    context.ReportDiagnostic(
                        Diagnostic.Create(
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
                    context.ReportDiagnostic(
                        Diagnostic.Create(
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
                    context,
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
                            context.ReportDiagnostic
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
                            context.ReportDiagnostic(
                                Diagnostic.Create(
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
                            context.ReportDiagnostic(
                                Diagnostic.Create(
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
            CheckForUntrackedContainerFields(context, structDecl, semanticModel);

            return result;
        }

        /// <summary>
        /// Scans the job struct for fields decorated with <c>[SingleEntity(Tag/Tags)]</c>.
        /// Each entry becomes a hidden hoist + assignment in the generated <c>ScheduleParallel</c>
        /// path. Validates: type is an <c>IAspect</c> or a <c>NativeComponentRead/Write&lt;T&gt;</c>;
        /// inline tags are present; not stacked with <c>[FromWorld]</c>.
        /// </summary>
        static List<SingleEntityFieldEntry>? ScanSingleEntityFields(
            SourceProductionContext context,
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
                    context.ReportDiagnostic(
                        Diagnostic.Create(
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
                    context.ReportDiagnostic(
                        Diagnostic.Create(
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
                    context.ReportDiagnostic(
                        Diagnostic.Create(
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
                    context.ReportDiagnostic(
                        Diagnostic.Create(
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
                    context.ReportDiagnostic
                );
                if (tagTypes == null)
                    return null;
                if (tagTypes.Count == 0)
                {
                    context.ReportDiagnostic(
                        Diagnostic.Create(
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
                    context.ReportDiagnostic(
                        Diagnostic.Create(
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
                        context,
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

        // ─── [SingleEntity] field emit helpers ─────────────────────────────────
        //
        // Used by all four schedule overload paths (iteration jobs dense/sparse,
        // custom non-iteration, custom parallel iteration). Hoisting runs once per
        // schedule call (the singleton's EntityIndex is fixed for the call). Dep
        // registration / field assignment / tracking run per-job-instance (which is
        // per-iteration-group for iteration jobs, once for custom jobs).

        static void EmitSingleEntityFieldsHoistedSetup(
            StringBuilder sb,
            string body,
            List<SingleEntityFieldEntry> entries
        ) => SingleEntityEmitter.EmitHoistedSetup(sb, body, entries);

        static void EmitSingleEntityFieldsDepRegistration(
            StringBuilder sb,
            string body,
            List<SingleEntityFieldEntry> entries
        ) => SingleEntityEmitter.EmitDepRegistration(sb, body, entries);

        /// <summary>
        /// Emits the field assignments for [SingleEntity] fields on the per-job-instance
        /// (typically named <c>_trecs_job</c>). For aspect fields, fetches per-component
        /// buffers and constructs the aspect; for component-{Read,Write} wrappers, calls
        /// the runtime helper that resolves the wrapper from the EntityIndex.
        /// </summary>
        static void EmitSingleEntityFieldsAssignment(
            StringBuilder sb,
            string body,
            List<SingleEntityFieldEntry> entries
        ) => SingleEntityEmitter.EmitFieldAssignment(sb, body, entries);

        static void EmitSingleEntityFieldsTracking(
            StringBuilder sb,
            string body,
            List<SingleEntityFieldEntry> entries
        ) => SingleEntityEmitter.EmitTracking(sb, body, entries);

        /// <summary>
        /// Emits TRECS081 for any field on a Trecs job struct whose type is a recognized
        /// Trecs container (NativeComponentBufferRead, NativeComponentLookupWrite, etc.) but
        /// is NOT marked [FromWorld]. Such fields bypass the scheduler's dependency
        /// tracking and can cause race conditions.
        /// </summary>
        static void CheckForUntrackedContainerFields(
            SourceProductionContext context,
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
                    context.ReportDiagnostic(
                        Diagnostic.Create(
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
            SourceProductionContext context,
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
                context.ReportDiagnostic(
                    Diagnostic.Create(
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
            SourceProductionContext context,
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
                context.ReportDiagnostic(
                    Diagnostic.Create(
                        DiagnosticDescriptors.SingleEntityWriteAspectMissingNativeDisableParallelForRestriction,
                        field.GetLocation(),
                        variable.Identifier.Text,
                        structName,
                        PerformanceCache.GetDisplayString(aspectTypeSymbol)
                    )
                );
            }
        }

        // ─── Emission ───────────────────────────────────────────────────────────

        static string GenerateSource(JobInfo info, Compilation compilation)
        {
            var sb = new StringBuilder();
            sb.AppendLine("using Unity.Collections;");
            sb.AppendLine("using Unity.Jobs;");
            sb.AppendLine("using System;");
            CommonUsings.AppendTo(sb);
            sb.AppendLine();

            var ns = PerformanceCache.GetDisplayString(info.Symbol.ContainingNamespace);
            sb.AppendLine($"namespace {ns}");
            sb.AppendLine("{");

            // Walk up containing types so the partial struct is emitted in its
            // proper nesting context (matching the user's source declaration).
            var nesting = new List<INamedTypeSymbol>();
            for (var t = info.Symbol.ContainingType; t != null; t = t.ContainingType)
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

            var structName = info.Symbol.Name;
            var jobInterface = info.Kind == JobKind.CustomNonIteration ? "IJob" : "IJobFor";
            sb.AppendLine($"{ind}partial struct {structName} : {jobInterface}");
            sb.AppendLine($"{ind}{{");
            indent++;
            ind = new string(' ', indent * 4);

            if (info.Kind == JobKind.CustomNonIteration)
            {
                // Custom jobs have no iteration buffers, no _trecs_Group field, and no
                // Execute shim. The user's `public void Execute()` directly satisfies
                // IJob.Execute() — we just declare the interface here.
                EmitCustomScheduleOverload(sb, info, ind);
            }
            else if (info.Kind == JobKind.CustomParallelIteration)
            {
                // Custom parallel jobs have no iteration buffers, no _trecs_Group field,
                // and no Execute shim. The user's `public void Execute(int i)` directly
                // satisfies IJobFor.Execute(int) — we just declare the interface and emit
                // the schedule overload.
                EmitCustomParallelScheduleOverload(sb, info, ind);
            }
            else
            {
                EmitGeneratedFields(sb, info, ind);
                sb.AppendLine();
                EmitExecuteShim(sb, info, ind);
                sb.AppendLine();
                EmitScheduleOverloads(sb, info, ind);
            }

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

        // Per-iteration component buffer field. Uses a positional `_trecs_buf{i}` name
        // so the prefix can never collide with user fields and the layout is the same
        // for both aspect and components iteration kinds.
        static string BufferFieldName(int index) => FromWorldEmitter.GenPrefix + "buf" + index;

        static IReadOnlyList<(ITypeSymbol Type, bool ReadOnly)> GetIterationBuffers(JobInfo info)
        {
            if (
                info.Kind == JobKind.CustomNonIteration
                || info.Kind == JobKind.CustomParallelIteration
            )
                return Array.Empty<(ITypeSymbol, bool)>();
            if (info.Kind == JobKind.Aspect)
            {
                var ai = info.Aspect!;
                var result = new List<(ITypeSymbol, bool)>(ai.ComponentTypes.Count);
                foreach (var t in ai.ComponentTypes)
                    result.Add((t, ai.IsReadOnly(t)));
                return result;
            }
            else
            {
                var ci = info.Components!;
                var result = new List<(ITypeSymbol, bool)>();
                foreach (var p in ci.Parameters)
                    if (p.Role == ComponentsParamRole.Component)
                        result.Add((p.Type!, !p.IsRef));
                return result;
            }
        }

        static void EmitGeneratedFields(StringBuilder sb, JobInfo info, string ind)
        {
            var buffers = GetIterationBuffers(info);
            for (int i = 0; i < buffers.Count; i++)
            {
                var (type, readOnly) = buffers[i];
                if (!readOnly)
                    sb.AppendLine($"{ind}[Unity.Collections.NativeDisableParallelForRestriction]");
                var bufferType = readOnly
                    ? $"NativeComponentBufferRead<{PerformanceCache.GetDisplayString(type)}>"
                    : $"NativeComponentBufferWrite<{PerformanceCache.GetDisplayString(type)}>";
                sb.AppendLine($"{ind}private {bufferType} {BufferFieldName(i)};");
            }

            if (info.NeedsGroupField)
                sb.AppendLine($"{ind}private GroupIndex {FromWorldEmitter.GenPrefix}GroupIndex;");

            if (info.NeedsGlobalIndexOffset)
                sb.AppendLine($"{ind}private int {FromWorldEmitter.GenPrefix}GlobalIndexOffset;");
        }

        static void EmitExecuteShim(StringBuilder sb, JobInfo info, string ind)
        {
            sb.AppendLine($"{ind}public void Execute(int i)");
            sb.AppendLine($"{ind}{{");
            string body = ind + "    ";

            if (info.Kind == JobKind.Aspect)
            {
                var ai = info.Aspect!;
                sb.AppendLine(
                    $"{body}var {FromWorldEmitter.GenPrefix}ei = new EntityIndex(i, {FromWorldEmitter.GenPrefix}GroupIndex);"
                );
                var ctorArgs = string.Join(
                    ", ",
                    Enumerable.Range(0, ai.ComponentTypes.Count).Select(BufferFieldName)
                );
                sb.AppendLine(
                    $"{body}var {FromWorldEmitter.GenPrefix}view = new {ai.AspectTypeName}({FromWorldEmitter.GenPrefix}ei, {ctorArgs});"
                );
                sb.Append($"{body}Execute(in {FromWorldEmitter.GenPrefix}view");
                if (ai.HasEntityIndexParameter)
                    sb.Append($", {FromWorldEmitter.GenPrefix}ei");
                sb.AppendLine(");");
            }
            else
            {
                var ci = info.Components!;
                // Per-component ref locals (only emitted if a Component slot will reference them).
                foreach (var p in ci.Parameters)
                {
                    if (p.Role != ComponentsParamRole.Component)
                        continue;
                    var refKind = p.IsRef ? "ref" : "ref readonly";
                    sb.AppendLine(
                        $"{body}{refKind} var {FromWorldEmitter.GenPrefix}v{p.BufferIndex} = ref {BufferFieldName(p.BufferIndex)}[i];"
                    );
                }
                if (ci.HasEntityIndexParameter)
                    sb.AppendLine(
                        $"{body}var {FromWorldEmitter.GenPrefix}ei = new EntityIndex(i, {FromWorldEmitter.GenPrefix}GroupIndex);"
                    );

                // Build call args in original parameter order so the user can mix
                // components / EntityIndex / [GlobalIndex] freely.
                var callArgs = new List<string>(ci.Parameters.Count);
                foreach (var p in ci.Parameters)
                {
                    switch (p.Role)
                    {
                        case ComponentsParamRole.Component:
                        {
                            var prefix = p.IsRef ? "ref" : "in";
                            callArgs.Add($"{prefix} {FromWorldEmitter.GenPrefix}v{p.BufferIndex}");
                            break;
                        }
                        case ComponentsParamRole.EntityIndex:
                            callArgs.Add($"{FromWorldEmitter.GenPrefix}ei");
                            break;
                        case ComponentsParamRole.GlobalIndex:
                            callArgs.Add($"{FromWorldEmitter.GenPrefix}GlobalIndexOffset + i");
                            break;
                    }
                }
                sb.AppendLine($"{body}Execute({string.Join(", ", callArgs)});");
            }

            sb.AppendLine($"{ind}}}");
        }

        static void EmitScheduleOverloads(StringBuilder sb, JobInfo info, string ind)
        {
            // Build the [FromWorld] field params (shared across all schedule overloads).
            // Some field kinds (e.g. NativeSetCommandBuffer<TSet>) take no schedule param —
            // their type info lives entirely on the field's generic argument. We keep
            // those in `fromWorldFields` (for dep tracking emission) but exclude them
            // from `paramFields` (the actual schedule-method parameters).
            var fromWorldFields = info.FromWorldFields;
            var paramFields = new List<FromWorldFieldEmit>();
            foreach (var f in fromWorldFields)
            {
                var emit = FromWorldFieldEmit.Build(f);
                if (emit.HasScheduleParam)
                    paramFields.Add(emit);
            }

            // Construct the schedule-method parameter list (after the builder/world arg,
            // before the JobHandle extraDeps tail).
            var fromWorldParamDecl = FormatFromWorldParamDecl(paramFields);

            // Pre-compute the full ordered emit list — needed by the per-group body so it
            // emits dep registration / field assignment / tracking in declaration order.
            var orderedEmits = fromWorldFields.Select(f => FromWorldFieldEmit.Build(f)).ToList();

            bool hasSets = info.IterationCriteria.SetTypes.Count > 0;
            bool hasAnyAttributeCriteria =
                info.IterationCriteria.TagTypes.Count > 0
                || hasSets
                || info.IterationCriteria.MatchByComponents;

            // (1) Convenience (WorldAccessor) overload — only when the attribute supplies at
            // least one criterion. Routes to (QueryBuilder) for the dense path or
            // (SparseQueryBuilder) for the sparse path. Sets are added here exactly once
            // (via .InSet<>() chain) so the typed entries don't need to re-add them.
            if (hasAnyAttributeCriteria)
            {
                sb.AppendLine(
                    $"{ind}public JobHandle ScheduleParallel(WorldAccessor {FromWorldEmitter.GenPrefix}world{fromWorldParamDecl}, JobHandle {FromWorldEmitter.GenPrefix}extraDeps = default)"
                );
                sb.AppendLine($"{ind}{{");
                // Build the call expression. When attribute has sets, transition to
                // SparseQueryBuilder via the first .InSet<>() call (returns SparseQueryBuilder)
                // and chain the rest. Tags + Components are added by the typed entry.
                var builderExpr = new StringBuilder($"{FromWorldEmitter.GenPrefix}world.Query()");
                if (hasSets)
                {
                    foreach (var st in info.IterationCriteria.SetTypes)
                        builderExpr.Append($".InSet<{PerformanceCache.GetDisplayString(st)}>()");
                }
                var args = new List<string> { builderExpr.ToString() };
                args.AddRange(paramFields.Select(p => p.ScheduleParamName));
                args.Add($"{FromWorldEmitter.GenPrefix}extraDeps");
                sb.AppendLine($"{ind}    return ScheduleParallel({string.Join(", ", args)});");
                sb.AppendLine($"{ind}}}");
                sb.AppendLine();
            }

            // (2) Public dense (QueryBuilder) overload — only when attribute imposes no Sets.
            // (When attribute has Sets, iteration is forced through the sparse path; the user
            // must pass a SparseQueryBuilder explicitly or use the convenience overload.)
            if (!hasSets)
            {
                EmitDenseScheduleOverload(sb, info, ind, orderedEmits, fromWorldParamDecl);
                sb.AppendLine();
            }

            // (3) Public sparse (SparseQueryBuilder) overload — always emitted, so callers
            // can compose set criteria at the call site (e.g. via .InSet<X>()).
            EmitSparseScheduleOverload(sb, info, ind, orderedEmits, fromWorldParamDecl);
            sb.AppendLine();

            // (4) Sparse shim struct — nested IJobFor that wraps the user job by value plus
            // a NativeArray<int> of indices, and forwards Execute(int i) through the indirection.
            EmitSparseShim(sb, info, ind);
        }

        static void EmitDenseScheduleOverload(
            StringBuilder sb,
            JobInfo info,
            string ind,
            List<FromWorldFieldEmit> orderedEmits,
            string fromWorldParamDecl
        )
        {
            sb.AppendLine(
                $"{ind}public JobHandle ScheduleParallel(QueryBuilder {FromWorldEmitter.GenPrefix}builder{fromWorldParamDecl}, JobHandle {FromWorldEmitter.GenPrefix}extraDeps = default)"
            );
            sb.AppendLine($"{ind}{{");
            string body = ind + "    ";

            // Merge attribute criteria into the builder.
            var chain = BuildAttributeCriteriaChain(info);
            if (chain.Length > 0)
                sb.AppendLine(
                    $"{body}{FromWorldEmitter.GenPrefix}builder = {FromWorldEmitter.GenPrefix}builder{chain};"
                );

            // Fail loud rather than walking every group in the world. The wording mentions
            // only the criteria the job actually supports today (Sets are rejected up-front
            // by the validator with a separate diagnostic, so we don't suggest them here).
            sb.AppendLine(
                $"{body}Assert.That({FromWorldEmitter.GenPrefix}builder.HasAnyCriteria, \"{info.Symbol.Name}.ScheduleParallel requires query criteria — pass a builder with at least one tag or component constraint, or specify Tags/MatchByComponents on the [{info.IterationAttributeShortName}] attribute.\");"
            );

            sb.AppendLine(
                $"{body}var {FromWorldEmitter.GenPrefix}world = {FromWorldEmitter.GenPrefix}builder.World;"
            );
            sb.AppendLine(
                $"{body}var {FromWorldEmitter.GenPrefix}scheduler = {FromWorldEmitter.GenPrefix}world.GetJobSchedulerForJob();"
            );
            sb.AppendLine(
                $"{body}var {FromWorldEmitter.GenPrefix}allJobs = {FromWorldEmitter.GenPrefix}extraDeps;"
            );

            if (info.NeedsGlobalIndexOffset)
                sb.AppendLine($"{body}var {FromWorldEmitter.GenPrefix}queryIndexOffset = 0;");

            FromWorldEmitter.EmitFromWorldHoistedSetup(sb, body, orderedEmits);
            EmitSingleEntityFieldsHoistedSetup(sb, body, info.SingleEntityFields);

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

            EmitPerGroupBody(sb, info, innerBody, orderedEmits);

            if (info.NeedsGlobalIndexOffset)
                sb.AppendLine(
                    $"{innerBody}{FromWorldEmitter.GenPrefix}queryIndexOffset += {FromWorldEmitter.GenPrefix}count;"
                );

            sb.AppendLine($"{body}}}");
            sb.AppendLine($"{body}return {FromWorldEmitter.GenPrefix}allJobs;");
            sb.AppendLine($"{ind}}}");
        }

        /// <summary>
        /// Formats the [FromWorld] parameter declarations for a schedule method signature.
        /// Required params come first, then optional params (with = null default).
        /// Returns "" if no params, or ", Type name, Type? name2 = null" (leading comma).
        /// </summary>
        static string FormatFromWorldParamDecl(List<FromWorldFieldEmit> paramFields)
        {
            if (paramFields.Count == 0)
                return "";

            // Required params first, then optional — C# requires this ordering.
            var sorted = paramFields.OrderBy(p => p.IsOptionalParam).ToList();
            var parts = sorted.Select(p =>
                p.IsOptionalParam
                    ? $"{p.ScheduleParamType} {p.ScheduleParamName} = null"
                    : $"{p.ScheduleParamType} {p.ScheduleParamName}"
            );
            return ", " + string.Join(", ", parts);
        }

        static void EmitPerGroupBody(
            StringBuilder sb,
            JobInfo info,
            string body,
            List<FromWorldFieldEmit> orderedEmits
        )
        {
            // Per-group input deps. Start fresh from extraDeps so an earlier group's deps
            // don't bleed into a later group's job (each group schedules independently).
            sb.AppendLine(
                $"{body}var {FromWorldEmitter.GenPrefix}deps = {FromWorldEmitter.GenPrefix}extraDeps;"
            );

            // Iteration buffer deps.
            var buffers = GetIterationBuffers(info);
            for (int i = 0; i < buffers.Count; i++)
            {
                var (type, readOnly) = buffers[i];
                var rid =
                    $"ResourceId.Component(ComponentTypeId<{PerformanceCache.GetDisplayString(type)}>.Value)";
                var method = readOnly ? "IncludeReadDep" : "IncludeWriteDep";
                sb.AppendLine(
                    $"{body}{FromWorldEmitter.GenPrefix}deps = {FromWorldEmitter.GenPrefix}scheduler.{method}({FromWorldEmitter.GenPrefix}deps, {rid}, {FromWorldEmitter.GenPrefix}group);"
                );
            }

            // [FromWorld] dep registration.
            FromWorldEmitter.EmitFromWorldDepRegistration(sb, body, orderedEmits);
            EmitSingleEntityFieldsDepRegistration(sb, body, info.SingleEntityFields);

            // Materialise iteration buffers.
            for (int i = 0; i < buffers.Count; i++)
            {
                var (type, readOnly) = buffers[i];
                var typeName = PerformanceCache.GetDisplayString(type);
                var ext = readOnly ? "GetBufferReadForJob" : "GetBufferWriteForJob";
                sb.AppendLine(
                    $"{body}var ({FromWorldEmitter.GenPrefix}buf{i}_value, _) = {FromWorldEmitter.GenPrefix}world.{ext}<{typeName}>({FromWorldEmitter.GenPrefix}group);"
                );
            }

            sb.AppendLine($"{body}var {FromWorldEmitter.GenPrefix}job = this;");

            // Assign iteration buffers to the per-iteration job copy.
            for (int i = 0; i < buffers.Count; i++)
                sb.AppendLine(
                    $"{body}{FromWorldEmitter.GenPrefix}job.{BufferFieldName(i)} = {FromWorldEmitter.GenPrefix}buf{i}_value;"
                );

            if (info.NeedsGroupField)
                sb.AppendLine(
                    $"{body}{FromWorldEmitter.GenPrefix}job.{FromWorldEmitter.GenPrefix}GroupIndex = {FromWorldEmitter.GenPrefix}group;"
                );

            if (info.NeedsGlobalIndexOffset)
                sb.AppendLine(
                    $"{body}{FromWorldEmitter.GenPrefix}job.{FromWorldEmitter.GenPrefix}GlobalIndexOffset = {FromWorldEmitter.GenPrefix}queryIndexOffset;"
                );

            // Assign [FromWorld] field values.
            FromWorldEmitter.EmitFromWorldFieldAssignments(sb, body, orderedEmits);
            EmitSingleEntityFieldsAssignment(sb, body, info.SingleEntityFields);

            // Schedule via Unity's IJobFor extension.
            sb.AppendLine(
                $"{body}var {FromWorldEmitter.GenPrefix}handle = {FromWorldEmitter.GenPrefix}job.ScheduleParallel({FromWorldEmitter.GenPrefix}count, JobsUtil.ChooseBatchSize({FromWorldEmitter.GenPrefix}count), {FromWorldEmitter.GenPrefix}deps);"
            );

            // Track outputs.
            for (int i = 0; i < buffers.Count; i++)
            {
                var (type, readOnly) = buffers[i];
                var rid =
                    $"ResourceId.Component(ComponentTypeId<{PerformanceCache.GetDisplayString(type)}>.Value)";
                var method = readOnly ? "TrackJobRead" : "TrackJobWrite";
                sb.AppendLine(
                    $"{body}{FromWorldEmitter.GenPrefix}scheduler.{method}({FromWorldEmitter.GenPrefix}handle, {rid}, {FromWorldEmitter.GenPrefix}group);"
                );
            }
            FromWorldEmitter.EmitFromWorldTracking(sb, body, orderedEmits);
            EmitSingleEntityFieldsTracking(sb, body, info.SingleEntityFields);

            sb.AppendLine(
                $"{body}{FromWorldEmitter.GenPrefix}allJobs = JobHandle.CombineDependencies({FromWorldEmitter.GenPrefix}allJobs, {FromWorldEmitter.GenPrefix}handle);"
            );
        }

        // ─── Sparse path: SparseQueryBuilder schedule overload ───────────────────

        static void EmitSparseScheduleOverload(
            StringBuilder sb,
            JobInfo info,
            string ind,
            List<FromWorldFieldEmit> orderedEmits,
            string fromWorldParamDecl
        )
        {
            sb.AppendLine(
                $"{ind}public JobHandle ScheduleParallel(SparseQueryBuilder {FromWorldEmitter.GenPrefix}builder{fromWorldParamDecl}, JobHandle {FromWorldEmitter.GenPrefix}extraDeps = default)"
            );
            sb.AppendLine($"{ind}{{");
            string body = ind + "    ";

            // Merge attribute Tags + Components into the builder. Sets are NOT merged here:
            // they were already added by whichever entry called us (the (WorldAccessor)
            // convenience overload pre-adds them via .InSet<>(), or the user composed them
            // at the call site). Re-adding them here would overflow the builder's FixedList4.
            var chain = BuildAttributeCriteriaChain(info);
            if (chain.Length > 0)
                sb.AppendLine(
                    $"{body}{FromWorldEmitter.GenPrefix}builder = {FromWorldEmitter.GenPrefix}builder{chain};"
                );

            // SparseQueryBuilder always has at least one set (constructed via .InSet<>()),
            // so HasAnyCriteria is trivially true. No assert needed.

            sb.AppendLine(
                $"{body}var {FromWorldEmitter.GenPrefix}world = {FromWorldEmitter.GenPrefix}builder.World;"
            );
            sb.AppendLine(
                $"{body}var {FromWorldEmitter.GenPrefix}scheduler = {FromWorldEmitter.GenPrefix}world.GetJobSchedulerForJob();"
            );
            sb.AppendLine(
                $"{body}var {FromWorldEmitter.GenPrefix}allJobs = {FromWorldEmitter.GenPrefix}extraDeps;"
            );

            if (info.NeedsGlobalIndexOffset)
                sb.AppendLine($"{body}var {FromWorldEmitter.GenPrefix}queryIndexOffset = 0;");

            FromWorldEmitter.EmitFromWorldHoistedSetup(sb, body, orderedEmits);
            EmitSingleEntityFieldsHoistedSetup(sb, body, info.SingleEntityFields);

            sb.AppendLine(
                $"{body}foreach (var {FromWorldEmitter.GenPrefix}slice in {FromWorldEmitter.GenPrefix}builder.GroupSlices())"
            );
            sb.AppendLine($"{body}{{");
            string innerBody = body + "    ";

            sb.AppendLine(
                $"{innerBody}var {FromWorldEmitter.GenPrefix}group = {FromWorldEmitter.GenPrefix}slice.GroupIndex;"
            );
            sb.AppendLine();

            // Pre-walk the slice into a TempJob-backed sparse-indices buffer. The
            // helper hides Unity.Collections types behind Trecs wrappers so generated
            // code can run in user assemblies that don't reference Unity.Collections
            // directly.
            sb.AppendLine(
                $"{innerBody}var ({FromWorldEmitter.GenPrefix}indices, {FromWorldEmitter.GenPrefix}indicesLifetime, {FromWorldEmitter.GenPrefix}count) = {FromWorldEmitter.GenPrefix}world.AllocateSparseIndicesForJob({FromWorldEmitter.GenPrefix}slice);"
            );
            sb.AppendLine($"{innerBody}if ({FromWorldEmitter.GenPrefix}count == 0)");
            sb.AppendLine($"{innerBody}{{");
            sb.AppendLine($"{innerBody}    {FromWorldEmitter.GenPrefix}indicesLifetime.Dispose();");
            sb.AppendLine($"{innerBody}    continue;");
            sb.AppendLine($"{innerBody}}}");
            sb.AppendLine();

            EmitPerGroupBodyForSparse(sb, info, innerBody, orderedEmits);

            if (info.NeedsGlobalIndexOffset)
                sb.AppendLine(
                    $"{innerBody}{FromWorldEmitter.GenPrefix}queryIndexOffset += {FromWorldEmitter.GenPrefix}count;"
                );

            sb.AppendLine($"{body}}}");
            sb.AppendLine($"{body}return {FromWorldEmitter.GenPrefix}allJobs;");
            sb.AppendLine($"{ind}}}");
        }

        static void EmitPerGroupBodyForSparse(
            StringBuilder sb,
            JobInfo info,
            string body,
            List<FromWorldFieldEmit> orderedEmits
        )
        {
            // Per-group input deps.
            sb.AppendLine(
                $"{body}var {FromWorldEmitter.GenPrefix}deps = {FromWorldEmitter.GenPrefix}extraDeps;"
            );

            // Iteration buffer deps.
            var buffers = GetIterationBuffers(info);
            for (int i = 0; i < buffers.Count; i++)
            {
                var (type, readOnly) = buffers[i];
                var rid =
                    $"ResourceId.Component(ComponentTypeId<{PerformanceCache.GetDisplayString(type)}>.Value)";
                var method = readOnly ? "IncludeReadDep" : "IncludeWriteDep";
                sb.AppendLine(
                    $"{body}{FromWorldEmitter.GenPrefix}deps = {FromWorldEmitter.GenPrefix}scheduler.{method}({FromWorldEmitter.GenPrefix}deps, {rid}, {FromWorldEmitter.GenPrefix}group);"
                );
            }

            // [FromWorld] dep registration.
            FromWorldEmitter.EmitFromWorldDepRegistration(sb, body, orderedEmits);
            EmitSingleEntityFieldsDepRegistration(sb, body, info.SingleEntityFields);

            // Materialise iteration buffers.
            for (int i = 0; i < buffers.Count; i++)
            {
                var (type, readOnly) = buffers[i];
                var typeName = PerformanceCache.GetDisplayString(type);
                var ext = readOnly ? "GetBufferReadForJob" : "GetBufferWriteForJob";
                sb.AppendLine(
                    $"{body}var ({FromWorldEmitter.GenPrefix}buf{i}_value, _) = {FromWorldEmitter.GenPrefix}world.{ext}<{typeName}>({FromWorldEmitter.GenPrefix}group);"
                );
            }

            sb.AppendLine($"{body}var {FromWorldEmitter.GenPrefix}job = this;");

            // Assign iteration buffers to the per-iteration job copy.
            for (int i = 0; i < buffers.Count; i++)
                sb.AppendLine(
                    $"{body}{FromWorldEmitter.GenPrefix}job.{BufferFieldName(i)} = {FromWorldEmitter.GenPrefix}buf{i}_value;"
                );

            if (info.NeedsGroupField)
                sb.AppendLine(
                    $"{body}{FromWorldEmitter.GenPrefix}job.{FromWorldEmitter.GenPrefix}GroupIndex = {FromWorldEmitter.GenPrefix}group;"
                );

            if (info.NeedsGlobalIndexOffset)
                sb.AppendLine(
                    $"{body}{FromWorldEmitter.GenPrefix}job.{FromWorldEmitter.GenPrefix}GlobalIndexOffset = {FromWorldEmitter.GenPrefix}queryIndexOffset;"
                );

            // Assign [FromWorld] field values.
            FromWorldEmitter.EmitFromWorldFieldAssignments(sb, body, orderedEmits);
            EmitSingleEntityFieldsAssignment(sb, body, info.SingleEntityFields);

            // Wrap the configured user-job copy in the sparse shim and schedule. The shim
            // forwards Execute(int i) to job.Execute(Indices[i]), giving us a sparse
            // iteration on top of an IJobFor that wants a contiguous [0, count) range.
            sb.AppendLine(
                $"{body}var {FromWorldEmitter.GenPrefix}shim = new {FromWorldEmitter.GenPrefix}SparseShim {{ Inner = {FromWorldEmitter.GenPrefix}job, Indices = {FromWorldEmitter.GenPrefix}indices }};"
            );
            sb.AppendLine(
                $"{body}var {FromWorldEmitter.GenPrefix}handle = {FromWorldEmitter.GenPrefix}shim.ScheduleParallel({FromWorldEmitter.GenPrefix}count, JobsUtil.ChooseBatchSize({FromWorldEmitter.GenPrefix}count), {FromWorldEmitter.GenPrefix}deps);"
            );

            // Track outputs against the user job's resource accesses.
            for (int i = 0; i < buffers.Count; i++)
            {
                var (type, readOnly) = buffers[i];
                var rid =
                    $"ResourceId.Component(ComponentTypeId<{PerformanceCache.GetDisplayString(type)}>.Value)";
                var method = readOnly ? "TrackJobRead" : "TrackJobWrite";
                sb.AppendLine(
                    $"{body}{FromWorldEmitter.GenPrefix}scheduler.{method}({FromWorldEmitter.GenPrefix}handle, {rid}, {FromWorldEmitter.GenPrefix}group);"
                );
            }
            FromWorldEmitter.EmitFromWorldTracking(sb, body, orderedEmits);
            EmitSingleEntityFieldsTracking(sb, body, info.SingleEntityFields);

            // Schedule disposal of the indices list AFTER the job completes. Track the
            // dispose handle so the framework completes it at the next phase boundary
            // (otherwise the TempJob memory leaks until next domain reload).
            sb.AppendLine(
                $"{body}var {FromWorldEmitter.GenPrefix}disposeHandle = {FromWorldEmitter.GenPrefix}indicesLifetime.Dispose({FromWorldEmitter.GenPrefix}handle);"
            );
            sb.AppendLine(
                $"{body}{FromWorldEmitter.GenPrefix}scheduler.TrackJob({FromWorldEmitter.GenPrefix}disposeHandle);"
            );
            sb.AppendLine(
                $"{body}{FromWorldEmitter.GenPrefix}allJobs = JobHandle.CombineDependencies({FromWorldEmitter.GenPrefix}allJobs, {FromWorldEmitter.GenPrefix}disposeHandle);"
            );
        }

        // ─── Custom non-iteration job: single Schedule(WorldAccessor, ...) overload ──

        static void EmitCustomScheduleOverload(StringBuilder sb, JobInfo info, string ind)
        {
            // Build per-field emit info; only fields that need a schedule param contribute
            // a method parameter (NativeSetCommandBuffer<TSet> contributes none).
            var orderedEmits = info
                .FromWorldFields.Select(f => FromWorldFieldEmit.Build(f))
                .ToList();
            var paramFields = orderedEmits.Where(e => e.HasScheduleParam).ToList();

            var fromWorldParamDecl = FormatFromWorldParamDecl(paramFields);

            sb.AppendLine(
                $"{ind}public JobHandle Schedule(WorldAccessor {FromWorldEmitter.GenPrefix}world{fromWorldParamDecl}, JobHandle {FromWorldEmitter.GenPrefix}extraDeps = default)"
            );
            sb.AppendLine($"{ind}{{");
            string body = ind + "    ";

            sb.AppendLine(
                $"{body}var {FromWorldEmitter.GenPrefix}scheduler = {FromWorldEmitter.GenPrefix}world.GetJobSchedulerForJob();"
            );

            // Hoist [FromWorld] groups (single-group / multi-group resolutions). Same
            // helper as the iteration paths.
            FromWorldEmitter.EmitFromWorldHoistedSetup(sb, body, orderedEmits);
            EmitSingleEntityFieldsHoistedSetup(sb, body, info.SingleEntityFields);

            // Single, flat dep accumulation — no per-group loop.
            sb.AppendLine(
                $"{body}var {FromWorldEmitter.GenPrefix}deps = {FromWorldEmitter.GenPrefix}extraDeps;"
            );
            FromWorldEmitter.EmitFromWorldDepRegistration(sb, body, orderedEmits);
            EmitSingleEntityFieldsDepRegistration(sb, body, info.SingleEntityFields);

            // Configure a job copy with materialised [FromWorld] field values.
            sb.AppendLine($"{body}var {FromWorldEmitter.GenPrefix}job = this;");
            FromWorldEmitter.EmitFromWorldFieldAssignments(sb, body, orderedEmits);
            EmitSingleEntityFieldsAssignment(sb, body, info.SingleEntityFields);

            // Schedule via Unity's IJobExtensions.Schedule(JobHandle).
            sb.AppendLine(
                $"{body}var {FromWorldEmitter.GenPrefix}handle = {FromWorldEmitter.GenPrefix}job.Schedule({FromWorldEmitter.GenPrefix}deps);"
            );

            // Track outputs against the user job's resource accesses.
            FromWorldEmitter.EmitFromWorldTracking(sb, body, orderedEmits);
            EmitSingleEntityFieldsTracking(sb, body, info.SingleEntityFields);

            sb.AppendLine($"{body}return {FromWorldEmitter.GenPrefix}handle;");
            sb.AppendLine($"{ind}}}");
        }

        static void EmitCustomParallelScheduleOverload(StringBuilder sb, JobInfo info, string ind)
        {
            // Like EmitCustomScheduleOverload but for IJobFor: takes an explicit `int count`
            // for the parallel iteration range and dispatches via Unity's ScheduleParallel
            // instead of Schedule. The user iterates an external NativeArray (or any other
            // index-keyed source) inside their public Execute(int i); the [FromWorld] fields
            // give them dependency-tracked component access by EntityIndex (typically via
            // NativeComponentLookupRead/Write).
            var orderedEmits = info
                .FromWorldFields.Select(f => FromWorldFieldEmit.Build(f))
                .ToList();
            var paramFields = orderedEmits.Where(e => e.HasScheduleParam).ToList();

            var fromWorldParamDecl = FormatFromWorldParamDecl(paramFields);

            sb.AppendLine(
                $"{ind}public JobHandle ScheduleParallel(WorldAccessor {FromWorldEmitter.GenPrefix}world, int count{fromWorldParamDecl}, JobHandle {FromWorldEmitter.GenPrefix}extraDeps = default)"
            );
            sb.AppendLine($"{ind}{{");
            string body = ind + "    ";

            sb.AppendLine($"{body}if (count <= 0) return {FromWorldEmitter.GenPrefix}extraDeps;");
            sb.AppendLine(
                $"{body}var {FromWorldEmitter.GenPrefix}scheduler = {FromWorldEmitter.GenPrefix}world.GetJobSchedulerForJob();"
            );

            // Hoist [FromWorld] groups (single-group / multi-group resolutions). Same
            // helper as the iteration paths.
            FromWorldEmitter.EmitFromWorldHoistedSetup(sb, body, orderedEmits);
            EmitSingleEntityFieldsHoistedSetup(sb, body, info.SingleEntityFields);

            // Single, flat dep accumulation — no per-group loop.
            sb.AppendLine(
                $"{body}var {FromWorldEmitter.GenPrefix}deps = {FromWorldEmitter.GenPrefix}extraDeps;"
            );
            FromWorldEmitter.EmitFromWorldDepRegistration(sb, body, orderedEmits);
            EmitSingleEntityFieldsDepRegistration(sb, body, info.SingleEntityFields);

            // Configure a job copy with materialised [FromWorld] field values.
            sb.AppendLine($"{body}var {FromWorldEmitter.GenPrefix}job = this;");
            FromWorldEmitter.EmitFromWorldFieldAssignments(sb, body, orderedEmits);
            EmitSingleEntityFieldsAssignment(sb, body, info.SingleEntityFields);

            // Schedule via Unity's IJobForExtensions.ScheduleParallel.
            sb.AppendLine(
                $"{body}var {FromWorldEmitter.GenPrefix}handle = {FromWorldEmitter.GenPrefix}job.ScheduleParallel(count, JobsUtil.ChooseBatchSize(count), {FromWorldEmitter.GenPrefix}deps);"
            );

            // Track outputs against the user job's resource accesses.
            FromWorldEmitter.EmitFromWorldTracking(sb, body, orderedEmits);
            EmitSingleEntityFieldsTracking(sb, body, info.SingleEntityFields);

            sb.AppendLine($"{body}return {FromWorldEmitter.GenPrefix}handle;");
            sb.AppendLine($"{ind}}}");
        }

        // ─── Sparse shim struct emission ───────────────────────────────────────────

        static void EmitSparseShim(StringBuilder sb, JobInfo info, string ind)
        {
            string innerInd = ind + "    ";
            string bodyInd = innerInd + "    ";

            // Mirror the user struct's [BurstCompile] decoration so the shim's trampoline
            // is burst-compiled iff the user job is. The shim contains the user job by value,
            // so its Execute compiles `Inner.Execute(Indices[i])` directly into the burst
            // codegen of the user's Execute(int).
            //
            // The Indices field is JobSparseIndices (a Trecs wrapper around NativeArray<int>),
            // not NativeArray<int> directly, so generated code never has to name
            // Unity.Collections types and the shim compiles in user assemblies that don't
            // reference Unity.Collections. The wrapper carries the [ReadOnly] internally
            // — Unity's job safety system walks struct fields recursively and finds it.
            if (HasBurstCompile(info.Symbol))
                sb.AppendLine($"{ind}[Unity.Burst.BurstCompile]");
            sb.AppendLine($"{ind}private struct {FromWorldEmitter.GenPrefix}SparseShim : IJobFor");
            sb.AppendLine($"{ind}{{");
            sb.AppendLine($"{innerInd}public {info.Symbol.Name} Inner;");
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

        static string BuildAttributeCriteriaChain(JobInfo info)
        {
            var c = info.IterationCriteria;
            var componentTypes =
                info.Kind == JobKind.Aspect
                    ? (IEnumerable<ITypeSymbol>)info.Aspect!.ComponentTypes
                    : info.Components!.Components.Select(p => p.Type!);
            return QueryBuilderHelper.BuildAttributeCriteriaChain(
                c.TagTypes,
                c.MatchByComponents,
                componentTypes
            );
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
            /// resolved at schedule time via <c>Query().WithTags&lt;...&gt;().SingleEntityIndex()</c>
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
        internal class SingleEntityFieldEntry : SingleEntityEmitter.IEmitTarget
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

            // SingleEntityEmitter.IEmitTarget — Phase 4 emits to the user-declared
            // field directly, so the local-name root and the LHS of the per-job
            // assignment are both the user's field name.
            string SingleEntityEmitter.IEmitTarget.LocalNameRoot => FieldName;
            string SingleEntityEmitter.IEmitTarget.JobFieldAssignmentLhs => FieldName;
            bool SingleEntityEmitter.IEmitTarget.IsComponentWrite => IsRef;
            IReadOnlyList<ITypeSymbol> SingleEntityEmitter.IEmitTarget.TagTypes => TagTypes;

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
            public IterationCriteria Criteria { get; }

            public AspectIterationInfo(
                string aspectTypeName,
                INamedTypeSymbol aspectTypeSymbol,
                List<ITypeSymbol> componentTypes,
                List<ITypeSymbol> readComponentTypes,
                List<ITypeSymbol> writeComponentTypes,
                bool hasEntityIndexParameter,
                IterationCriteria criteria
            )
            {
                AspectTypeName = aspectTypeName;
                AspectTypeSymbol = aspectTypeSymbol;
                ComponentTypes = componentTypes;
                ReadComponentTypes = readComponentTypes;
                WriteComponentTypes = writeComponentTypes;
                HasEntityIndexParameter = hasEntityIndexParameter;
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
            public bool HasGlobalIndexParameter =>
                Parameters.Any(p => p.Role == ComponentsParamRole.GlobalIndex);
            public IEnumerable<ComponentsParam> Components =>
                Parameters.Where(p => p.Role == ComponentsParamRole.Component);
        }

        internal enum ComponentsParamRole
        {
            Component,
            EntityIndex,
            GlobalIndex,
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

            public static ComponentsParam GlobalIndexParam(string name) =>
                new(ComponentsParamRole.GlobalIndex, null, false, false, name, -1);
        }
    }
}
