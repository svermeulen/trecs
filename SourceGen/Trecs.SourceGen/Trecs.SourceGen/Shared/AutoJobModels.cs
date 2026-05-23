#nullable enable

using System.Collections.Generic;
using Trecs.SourceGen.Aspect;
using Trecs.SourceGen.Performance;

namespace Trecs.SourceGen.Shared
{
    /// <summary>
    /// Top-level iteration kind for an <c>[WrapAsJob]</c> + <c>[ForEachEntity]</c>
    /// user method. Components-mode iterates per-(in/ref) component param; aspect-mode
    /// constructs an aspect from the method's first parameter.
    /// </summary>
    internal enum AutoJobIterationKindModel
    {
        Aspect,
        Components,
    }

    /// <summary>
    /// Role of one parameter on the user's <c>[WrapAsJob]</c> static method, projected
    /// from the symbol-bearing <c>AutoJobParamRole</c> at the transform-stage boundary.
    /// Drives field emission, the Execute shim's call-arg construction, and per-group
    /// schedule-overload field assignment.
    /// </summary>
    internal enum AutoJobParamRoleKind
    {
        Aspect,
        Component,
        EntityIndex,
        EntityHandle,
        GlobalIndex,
        NativeWorldAccessor,
        PassThrough,
        NativeSetRead,
        NativeSetCommandBuffer,
        FromWorld,
        SingleEntityAspect,
        SingleEntityComponentRead,
        SingleEntityComponentWrite,
    }

    /// <summary>
    /// One parameter on the user's <c>[WrapAsJob]</c> static method as a value-equatable
    /// record. Parameters retain their original declaration order so the Execute shim's
    /// call args and the wrapper method's parameter list land in the same slots the user
    /// wrote. <see cref="FromWorldIndex"/> and <see cref="SingleEntityIndex"/> reference
    /// the parallel arrays on <see cref="AutoJobModel"/>; -1 means "not applicable."
    /// </summary>
    internal readonly record struct AutoJobParamModel(
        AutoJobParamRoleKind Role,
        string Name,
        string TypeDisplay,
        bool IsRef,
        int BufferIndex,
        string SetTypeArg,
        int FromWorldIndex,
        int SingleEntityIndex,
        bool SingleEntityAspectHasWrites
    );

    /// <summary>
    /// Aspect-mode iteration data for AutoJob: aspect type name plus the per-component
    /// buffer entries the Execute shim's aspect ctor and the iteration-buffer setup
    /// emit from. Mirrors the slim subset of <see cref="AspectIterationModel"/> that
    /// AutoJob actually consumes — AutoJob's EntityIndex/EntityHandle/GlobalIndex are
    /// separate <see cref="AutoJobParamModel"/> roles, not extra-param slots.
    /// </summary>
    internal readonly record struct AutoJobAspectModel(
        string AspectTypeName,
        EquatableArray<AspectBufferEntry> Components
    )
    {
        public static readonly AutoJobAspectModel Empty = new(
            AspectTypeName: string.Empty,
            Components: EquatableArray<AspectBufferEntry>.Empty
        );
    }

    /// <summary>
    /// Top-level value-equatable model carried through the AutoJobGenerator pipeline.
    /// Holds everything the terminal stage needs to emit source: no Roslyn symbols,
    /// syntax nodes, or raw <see cref="Microsoft.CodeAnalysis.Diagnostic"/>s.
    ///
    /// <para>
    /// <see cref="FromWorldFields"/> and <see cref="SingleEntityFields"/> are flat
    /// arrays; <see cref="AutoJobParamModel.FromWorldIndex"/> and
    /// <see cref="AutoJobParamModel.SingleEntityIndex"/> on the per-param entries point
    /// back into them. <see cref="AdditionalUsings"/> collects all the namespaces the
    /// generated source needs to import (NativeSet type-arg namespaces, FromWorld param
    /// containers, inline tag namespaces) — these are inspected at the symbol-bearing
    /// transform boundary and projected once.
    /// </para>
    /// </summary>
    internal readonly record struct AutoJobModel(
        string ClassName,
        string Namespace,
        EquatableArray<ContainingTypeInfo> ContainingTypes,
        string HintFileName,
        string MethodName,
        bool IsOnSystemClass,
        AutoJobIterationKindModel IterKind,
        EquatableArray<AutoJobParamModel> Params,
        AutoJobAspectModel AspectData,
        IterationCriteriaModel Criteria,
        string AttributeCriteriaChain,
        EquatableArray<FromWorldFieldEmitModel> FromWorldFields,
        EquatableArray<SingleEntityEmitTargetModel> SingleEntityFields,
        EquatableArray<string> AdditionalUsings,
        bool IsValid,
        EquatableArray<DiagnosticInfo> Diagnostics
    )
    {
        public bool HasEntityIndex
        {
            get
            {
                foreach (var p in Params)
                    if (p.Role == AutoJobParamRoleKind.EntityIndex)
                        return true;
                return false;
            }
        }

        public bool HasEntityHandle
        {
            get
            {
                foreach (var p in Params)
                    if (p.Role == AutoJobParamRoleKind.EntityHandle)
                        return true;
                return false;
            }
        }

        public bool HasGlobalIndex
        {
            get
            {
                foreach (var p in Params)
                    if (p.Role == AutoJobParamRoleKind.GlobalIndex)
                        return true;
                return false;
            }
        }

        public bool HasNativeWorldAccessor
        {
            get
            {
                foreach (var p in Params)
                    if (p.Role == AutoJobParamRoleKind.NativeWorldAccessor)
                        return true;
                return false;
            }
        }

        public bool NeedsEntityHandleBuffer => HasEntityHandle;

        /// <summary>
        /// True if the generated job needs a <c>_trecs_Group</c> field. Aspect always
        /// needs it (for the aspect ctor); components only when the user took an
        /// <c>EntityIndex</c> parameter.
        /// </summary>
        public bool NeedsGroupField =>
            IterKind == AutoJobIterationKindModel.Aspect || HasEntityIndex;

        public bool NeedsGlobalIndexOffset => HasGlobalIndex;

        public bool HasSets => Criteria.HasSets;

        /// <summary>
        /// Per-iteration component buffer projection for the codegen path. For
        /// aspect-mode jobs each entry maps to one of the aspect's declared components;
        /// for components-mode it's the in/ref parameter list filtered to the Component
        /// slots. Drives the buffer-field emission, dep registration, materialization,
        /// and output tracking calls.
        /// </summary>
        public IReadOnlyList<(string TypeDisplay, bool ReadOnly)> IterationBuffers
        {
            get
            {
                if (IterKind == AutoJobIterationKindModel.Aspect)
                {
                    var result = new List<(string, bool)>(AspectData.Components.Length);
                    foreach (var e in AspectData.Components)
                        result.Add((e.TypeDisplay, !e.IsWrite));
                    return result;
                }
                else
                {
                    var result = new List<(string, bool)>();
                    foreach (var p in Params)
                        if (p.Role == AutoJobParamRoleKind.Component)
                            result.Add((p.TypeDisplay, !p.IsRef));
                    return result;
                }
            }
        }
    }
}
