#nullable enable

using System;
using System.Collections.Generic;
using System.Linq;
using Microsoft.CodeAnalysis;
using Trecs.SourceGen.Aspect;
using Trecs.SourceGen.Performance;

namespace Trecs.SourceGen.Shared
{
    /// <summary>
    /// Iteration kind for a Trecs job. Mirror of <c>JobGenerator.JobKind</c>, but
    /// hoisted to <c>Shared</c> so both the job generators and the value-equatable
    /// pipeline models can reference it. Promoting here also means
    /// <c>JobGenerator</c> and <c>AutoJobGenerator</c> don't need to redeclare it.
    /// </summary>
    internal enum JobIterationKind
    {
        Aspect,
        Components,
        CustomNonIteration,
        CustomParallelIteration,
    }

    /// <summary>
    /// Order-tracking for the optional entity-shaped parameters following the
    /// aspect param in an aspect-mode <c>[ForEachEntity]</c> job method. The
    /// underlying values mirror what <c>JobGenerator.AspectExtraParamKind</c>
    /// once exposed; wrapped in a record-struct (rather than a raw enum) so
    /// <see cref="EquatableArray{T}"/> can carry it.
    /// </summary>
    internal readonly record struct AspectExtraParamSlot(int Kind)
    {
        public const int EntityIndexKind = 0;
        public const int EntityHandleKind = 1;

        public static readonly AspectExtraParamSlot EntityIndex = new(EntityIndexKind);
        public static readonly AspectExtraParamSlot EntityHandle = new(EntityHandleKind);
    }

    /// <summary>
    /// One iteration-parameter slot in a components-mode <c>[ForEachEntity]</c>
    /// job. Mirrors <c>JobGenerator.ComponentsParam</c>/<c>ComponentsParamRole</c>
    /// but stores strings only.
    /// </summary>
    internal readonly record struct ComponentsParamModel(
        ComponentsParamRoleKind Role,
        string TypeDisplay,
        string Name,
        bool IsRef,
        bool IsIn,
        int BufferIndex
    );

    internal enum ComponentsParamRoleKind
    {
        Component,
        EntityIndex,
        EntityHandle,
        GlobalIndex,
    }

    /// <summary>
    /// Value-equatable iteration criteria for jobs — Tag types, "without" tag
    /// types, Set types, and MatchByComponents flag, all projected to display
    /// strings. Mirrors <see cref="IterationCriteria"/>.
    /// </summary>
    internal readonly record struct IterationCriteriaModel(
        EquatableArray<string> TagTypeDisplays,
        EquatableArray<string> WithoutTagTypeDisplays,
        EquatableArray<string> SetTypeDisplays,
        bool MatchByComponents
    )
    {
        public bool HasTags => TagTypeDisplays.Length > 0;
        public bool HasWithoutTags => WithoutTagTypeDisplays.Length > 0;
        public bool HasSets => SetTypeDisplays.Length > 0;
    }

    /// <summary>
    /// Aspect-mode iteration job model. The codegen path consumes
    /// <see cref="AspectComponents"/> (in canonical AllComponentTypes order) for
    /// buffer-field emission; <see cref="ReadComponentDisplays"/> and
    /// <see cref="WriteComponentDisplays"/> drive dep tracking. Everything is a
    /// string.
    /// </summary>
    internal readonly record struct AspectIterationModel(
        string AspectTypeName,
        EquatableArray<AspectBufferEntry> AspectComponents,
        EquatableArray<string> ReadComponentDisplays,
        EquatableArray<string> WriteComponentDisplays,
        bool HasEntityIndexParameter,
        bool HasEntityHandleParameter,
        EquatableArray<AspectExtraParamSlot> ExtraParamOrder,
        IterationCriteriaModel Criteria
    );

    /// <summary>
    /// Components-mode iteration job model. Parameters list preserves the user's
    /// declaration order so the Execute shim's call args land in the same slots
    /// they wrote.
    /// </summary>
    internal readonly record struct ComponentsIterationModel(
        EquatableArray<ComponentsParamModel> Parameters,
        IterationCriteriaModel Criteria,
        bool HasEntityIndexParameter,
        bool HasEntityHandleParameter,
        bool HasGlobalIndexParameter
    );

    /// <summary>
    /// Value-equatable carrier shape consumed by <see cref="SingleEntityEmitter"/>.
    /// Both <c>JobGenerator</c>'s hand-written <c>[SingleEntity]</c> job fields and
    /// <c>AutoJobGenerator</c>'s <c>[SingleEntity]</c> wrapper params project into
    /// this shape at the transform-phase boundary.
    /// </summary>
    internal readonly record struct SingleEntityEmitTargetModel(
        string LocalNameRoot,
        string JobFieldAssignmentLhs,
        bool IsAspect,
        bool IsComponentWrite,
        EquatableArray<string> TagTypeDisplays,
        AspectAttributeDataModel AspectData,
        string AspectTypeDisplay,
        string ComponentTypeDisplay
    );

    /// <summary>
    /// Per-field rendering helper for <c>[FromWorld]</c> emission as a
    /// value-equatable record. Strings + an
    /// <see cref="AspectAttributeDataModel"/> only — no symbols. Mirrors
    /// <see cref="FromWorldFieldEmit"/> for the model-taking emit path.
    /// </summary>
    internal readonly record struct FromWorldFieldEmitModel(
        FromWorldFieldKind Kind,
        string FieldName,
        string GenericArgDisplay,
        AspectAttributeDataModel AspectData,
        bool HasAspectData,
        bool HasScheduleParam,
        string ScheduleParamType,
        string ScheduleParamName,
        bool IsOptionalParam,
        bool NeedsHoistedSingleGroup,
        bool NeedsHoistedGroups,
        string HoistedSingleGroupLocal,
        string HoistedGroupsLocal,
        string InlineTagSetExpression,
        string TagSetExpression
    )
    {
        /// <summary>
        /// Project a symbol-bearing <see cref="FromWorldFieldEmit"/> into the
        /// equatable model. Call at the transform-phase boundary, where the
        /// original symbols are still alive.
        /// </summary>
        public static FromWorldFieldEmitModel From(FromWorldFieldEmit e, string globalNamespaceName)
        {
            var aspectModel =
                e.AspectData != null
                    ? AspectAttributeDataModelBuilder.FromData(e.AspectData, globalNamespaceName)
                    : AspectAttributeDataModel.Empty;
            return new FromWorldFieldEmitModel(
                Kind: e.Kind,
                FieldName: e.FieldName,
                GenericArgDisplay: e.GenericArgDisplay,
                AspectData: aspectModel,
                HasAspectData: e.AspectData != null,
                HasScheduleParam: e.HasScheduleParam,
                ScheduleParamType: e.ScheduleParamType,
                ScheduleParamName: e.ScheduleParamName,
                IsOptionalParam: e.IsOptionalParam,
                NeedsHoistedSingleGroup: e.NeedsHoistedSingleGroup,
                NeedsHoistedGroups: e.NeedsHoistedGroups,
                HoistedSingleGroupLocal: e.HoistedSingleGroupLocal,
                HoistedGroupsLocal: e.HoistedGroupsLocal,
                InlineTagSetExpression: e.InlineTagSetExpression,
                TagSetExpression: e.TagSetExpression
            );
        }
    }

    /// <summary>
    /// A single hand-written <c>[SingleEntity]</c> field on a Trecs job struct,
    /// projected into the value-equatable pipeline. The <see cref="JobModel"/>'s
    /// <c>SingleEntityFields</c> list materializes via
    /// <see cref="SingleEntityEmitter.IEmitTarget"/>-compatible
    /// <see cref="SingleEntityEmitTargetModel"/>s for emit; this carrier is the
    /// JobGenerator-specific shape, with the field name doubling as both
    /// <c>LocalNameRoot</c> and <c>JobFieldAssignmentLhs</c> per the existing
    /// <c>SingleEntityFieldEntry</c> convention.
    /// </summary>
    internal readonly record struct SingleEntityFieldModel(
        string FieldName,
        bool IsAspect,
        EquatableArray<string> TagTypeDisplays,
        AspectAttributeDataModel AspectData,
        bool HasAspectData,
        string AspectTypeDisplay,
        string ComponentTypeDisplay,
        bool IsRef
    )
    {
        /// <summary>
        /// View this field as a <see cref="SingleEntityEmitTargetModel"/> for
        /// <see cref="SingleEntityEmitter"/>'s model-taking overloads — Phase 4
        /// emits directly to the user's field (so root, LHS, and field name are
        /// all the same), and write-ness derives from the <c>ref</c> flag the
        /// validator recorded.
        /// </summary>
        public SingleEntityEmitTargetModel ToEmitTarget() =>
            new(
                LocalNameRoot: FieldName,
                JobFieldAssignmentLhs: FieldName,
                IsAspect: IsAspect,
                IsComponentWrite: IsRef,
                TagTypeDisplays: TagTypeDisplays,
                AspectData: AspectData,
                AspectTypeDisplay: AspectTypeDisplay,
                ComponentTypeDisplay: ComponentTypeDisplay
            );
    }

    /// <summary>
    /// Top-level value-equatable model carried through the JobGenerator pipeline.
    /// Holds everything the terminal stage needs to emit source: no Roslyn
    /// symbols, syntax nodes, or raw <see cref="Diagnostic"/>s.
    /// </summary>
    internal readonly record struct JobModel(
        string StructName,
        string Namespace,
        EquatableArray<ContainingTypeInfo> ContainingTypes,
        string HintFileName,
        JobIterationKind Kind,
        AspectIterationModel AspectIteration,
        ComponentsIterationModel ComponentsIteration,
        EquatableArray<FromWorldFieldEmitModel> FromWorldFields,
        EquatableArray<SingleEntityFieldModel> SingleEntityFields,
        string AttributeCriteriaChain,
        bool HasBurstCompile,
        bool IsValid,
        EquatableArray<DiagnosticInfo> Diagnostics
    )
    {
        public bool NeedsGroupField =>
            Kind == JobIterationKind.Aspect
            || (Kind == JobIterationKind.Components && ComponentsIteration.HasEntityIndexParameter);

        public bool NeedsGlobalIndexOffset =>
            Kind == JobIterationKind.Components && ComponentsIteration.HasGlobalIndexParameter;

        public bool NeedsEntityHandleBuffer =>
            (Kind == JobIterationKind.Aspect && AspectIteration.HasEntityHandleParameter)
            || (
                Kind == JobIterationKind.Components && ComponentsIteration.HasEntityHandleParameter
            );

        public IterationCriteriaModel IterationCriteria =>
            Kind switch
            {
                JobIterationKind.Aspect => AspectIteration.Criteria,
                JobIterationKind.Components => ComponentsIteration.Criteria,
                _ => throw new InvalidOperationException(
                    "IterationCriteria is only meaningful for iteration jobs"
                ),
            };

        /// <summary>
        /// Per-iteration component buffer projection for the codegen path. For
        /// aspect-mode jobs each entry maps directly to one of the aspect's
        /// declared components (in canonical AllComponentTypes order); for
        /// components-mode it's the in/ref parameter list filtered to the
        /// IEntityComponent slots. Custom-jobs return empty.
        /// </summary>
        public IReadOnlyList<(string TypeDisplay, bool ReadOnly)> IterationBuffers
        {
            get
            {
                switch (Kind)
                {
                    case JobIterationKind.Aspect:
                    {
                        var result = new List<(string, bool)>(
                            AspectIteration.AspectComponents.Length
                        );
                        foreach (var e in AspectIteration.AspectComponents)
                            result.Add((e.TypeDisplay, !e.IsWrite));
                        return result;
                    }
                    case JobIterationKind.Components:
                    {
                        var result = new List<(string, bool)>();
                        foreach (var p in ComponentsIteration.Parameters)
                            if (p.Role == ComponentsParamRoleKind.Component)
                                result.Add((p.TypeDisplay, !p.IsRef));
                        return result;
                    }
                    default:
                        return Array.Empty<(string, bool)>();
                }
            }
        }
    }

    /// <summary>
    /// Builders projecting the symbol-bearing types in <c>JobGenerator</c> into
    /// the equatable <c>*Model</c> shapes. Each builder runs at the transform
    /// boundary while the original symbols are still alive.
    /// </summary>
    internal static class JobModelBuilders
    {
        public static IterationCriteriaModel BuildCriteria(IterationCriteria criteria)
        {
            return new IterationCriteriaModel(
                TagTypeDisplays: criteria
                    .TagTypes.Select(PerformanceCache.GetDisplayString)
                    .ToEquatableArray(),
                WithoutTagTypeDisplays: criteria
                    .WithoutTagTypes.Select(PerformanceCache.GetDisplayString)
                    .ToEquatableArray(),
                SetTypeDisplays: criteria
                    .SetTypes.Select(PerformanceCache.GetDisplayString)
                    .ToEquatableArray(),
                MatchByComponents: criteria.MatchByComponents
            );
        }

        public static AspectIterationModel BuildAspectIteration(
            string aspectTypeName,
            IReadOnlyList<ITypeSymbol> componentTypes,
            IReadOnlyList<ITypeSymbol> readTypes,
            IReadOnlyList<ITypeSymbol> writeTypes,
            bool hasEntityIndexParameter,
            bool hasEntityHandleParameter,
            IReadOnlyList<AspectExtraParamSlot> extraParamOrder,
            IterationCriteria criteria
        )
        {
            // AllComponentTypes-order entries with precomputed buffer var name and write flag.
            // Drives EmitGeneratedFields, EmitExecuteShim's aspect-ctor args, and the
            // per-group iteration-buffer setup.
            var entries = new AspectBufferEntry[componentTypes.Count];
            for (int i = 0; i < componentTypes.Count; i++)
            {
                var t = componentTypes[i];
                bool isWrite = writeTypes.Any(w => SymbolEqualityComparer.Default.Equals(w, t));
                entries[i] = new AspectBufferEntry(
                    TypeDisplay: PerformanceCache.GetDisplayString(t),
                    VarName: ComponentTypeHelper.GetComponentVariableName(t),
                    IsWrite: isWrite
                );
            }
            return new AspectIterationModel(
                AspectTypeName: aspectTypeName,
                AspectComponents: new EquatableArray<AspectBufferEntry>(entries),
                ReadComponentDisplays: readTypes
                    .Select(PerformanceCache.GetDisplayString)
                    .ToEquatableArray(),
                WriteComponentDisplays: writeTypes
                    .Select(PerformanceCache.GetDisplayString)
                    .ToEquatableArray(),
                HasEntityIndexParameter: hasEntityIndexParameter,
                HasEntityHandleParameter: hasEntityHandleParameter,
                ExtraParamOrder: extraParamOrder.ToArray().ToEquatableArray(),
                Criteria: BuildCriteria(criteria)
            );
        }

        /// <summary>
        /// Empty <see cref="AspectIterationModel"/> placeholder for use as the
        /// AspectIteration field of a <see cref="JobModel"/> whose
        /// <see cref="JobModel.Kind"/> isn't <see cref="JobIterationKind.Aspect"/>.
        /// Carries no diagnostic meaning — never inspected unless Kind = Aspect.
        /// </summary>
        public static readonly AspectIterationModel EmptyAspectIteration = new(
            AspectTypeName: string.Empty,
            AspectComponents: EquatableArray<AspectBufferEntry>.Empty,
            ReadComponentDisplays: EquatableArray<string>.Empty,
            WriteComponentDisplays: EquatableArray<string>.Empty,
            HasEntityIndexParameter: false,
            HasEntityHandleParameter: false,
            ExtraParamOrder: EquatableArray<AspectExtraParamSlot>.Empty,
            Criteria: new IterationCriteriaModel(
                TagTypeDisplays: EquatableArray<string>.Empty,
                WithoutTagTypeDisplays: EquatableArray<string>.Empty,
                SetTypeDisplays: EquatableArray<string>.Empty,
                MatchByComponents: false
            )
        );

        public static readonly ComponentsIterationModel EmptyComponentsIteration = new(
            Parameters: EquatableArray<ComponentsParamModel>.Empty,
            Criteria: new IterationCriteriaModel(
                TagTypeDisplays: EquatableArray<string>.Empty,
                WithoutTagTypeDisplays: EquatableArray<string>.Empty,
                SetTypeDisplays: EquatableArray<string>.Empty,
                MatchByComponents: false
            ),
            HasEntityIndexParameter: false,
            HasEntityHandleParameter: false,
            HasGlobalIndexParameter: false
        );
    }
}
