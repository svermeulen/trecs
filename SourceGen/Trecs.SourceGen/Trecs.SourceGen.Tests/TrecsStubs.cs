namespace Trecs.SourceGen.Tests;

/// <summary>
/// Hand-written stubs for the subset of the Trecs runtime API that source-generated code
/// references at compile time. Lives here so source-gen tests can compile generated output
/// without dragging in Unity (Unity assemblies aren't available in CI and the real Trecs
/// runtime depends on UnityEngine, Unity.Collections, Unity.Burst, etc.).
///
/// Coverage is deliberately incremental: types are added as new tests need them, not all
/// upfront. If a new compile-cleanliness test fails with "type X not found", add the
/// minimum stub here to make it resolve. Bodies are empty / trivial — these stubs only
/// need to satisfy the C# compiler's name and shape resolution, not actually run.
/// </summary>
internal static class TrecsStubs
{
    public const string Source = """
        #nullable enable

        // Polyfill for record types on test-time targets (kept for parity with the source-gen project).
        namespace System.Runtime.CompilerServices
        {
            internal static class IsExternalInit { }
        }

        // Empty namespace declarations so generated `using` directives resolve. The source-gen
        // project's CommonUsings emits `using Trecs;`, `using Trecs.Internal;`,
        // `using Trecs.Collections;` into every generated file — they all have
        // to exist as namespaces or every generated file fails to compile.
        namespace Trecs.Collections { }

        // Subset of Unity.Collections used by attributes the generators emit. Real type is
        // [NativeDisableParallelForRestriction] from com.unity.collections — we only need
        // the attribute shell so the emitted decoration parses.
        namespace Unity.Collections
        {
            [System.AttributeUsage(System.AttributeTargets.Field)]
            public class NativeDisableParallelForRestrictionAttribute : System.Attribute { }

            // Allocator enum — generated job code passes Allocator.TempJob into
            // CreateNativeComponentLookup*ForJob.
            public enum Allocator { Invalid, None, Temp, TempJob, Persistent }

            // NativeList<T> used by InterpolatorJob to collect handles before disposal.
            public struct NativeList<T> : System.IDisposable where T : unmanaged
            {
                public NativeList(int capacity, Allocator allocator) { }
                public void Add(T item) { }
                public void Dispose() { }
            }
        }

        // [BurstCompile] is emitted on the auto-generated job struct by AutoJobGenerator
        // (and on the embedded job inside InterpolatorJob). Real type is in com.unity.burst.
        namespace Unity.Burst
        {
            [System.AttributeUsage(System.AttributeTargets.Struct | System.AttributeTargets.Method | System.AttributeTargets.Class)]
            public class BurstCompileAttribute : System.Attribute { }
        }

        // Subset of Unity.Jobs — generators emit `using Unity.Jobs;` and reference JobHandle
        // / IJob / IJobFor in the larger job-related code paths (Job, AutoJob, InterpolatorJob).
        namespace Unity.Jobs
        {
            public struct JobHandle
            {
                public static JobHandle CombineDependencies(JobHandle a, JobHandle b) => default;
                public static JobHandle CombineDependencies(Unity.Collections.NativeArray<JobHandle> handles) => default;
            }
            public interface IJob { void Execute(); }
            public interface IJobFor { void Execute(int index); }

            // Generated job code calls `_trecs_job.ScheduleParallel(count, batchSize, deps)` —
            // mirrors Unity's IJobForExtensions.ScheduleParallel<T>(this T, int, int, JobHandle).
            public static class IJobForExtensions
            {
                public static JobHandle ScheduleParallel<T>(
                    this T jobData, int arrayLength, int innerloopBatchCount, JobHandle dependsOn
                ) where T : struct, IJobFor => default;
                public static JobHandle Schedule<T>(this T jobData, JobHandle dependsOn)
                    where T : struct, IJob => default;
            }
        }

        // Tiny shim so Unity.Jobs.JobHandle.CombineDependencies(NativeArray<>) resolves —
        // generators reference NativeArray indirectly via this overload. Real type is
        // Unity.Collections.NativeArray<T>.
        namespace Unity.Collections
        {
            public struct NativeArray<T> where T : struct { }
        }

        namespace Trecs
        {
            using Trecs.Internal;

            // Marker interfaces consumed by EntityComponentGenerator and
            // AspectGenerator base-list scanning.
            public interface IEntityComponent { }
            public interface ITag { }
            public interface IEntitySet { }
            public interface ITemplate { }

            // Note: every "Null" / "IsNull" member is expression-bodied (or omitted via
            // `=> default`) so additional constructors below don't have to chain to a
            // synthetic backing field — keeps stubs short.
            public readonly struct GroupIndex
            {
                public int Index => 0;
                public bool IsNull => true;
                public static GroupIndex Null => default;
            }

            public readonly struct EntityHandle
            {
                public readonly int Id;
                public readonly int Version;
                public static EntityHandle Null => default;
                public bool IsNull => false;

                // Aspect WorldAccessor+EntityHandle ctor delegates via `entityHandle.ToIndex(world)`.
                public EntityIndex ToIndex(WorldAccessor world) => default;
                public EntityIndex ToIndex(NativeWorldAccessor world) => default;
            }

            // EntityAccessor — main-thread live entity reference. Mirrors the real
            // type's `ref struct` shape so test scenarios that try to box / capture /
            // store one would surface as compile errors here too.
            public readonly ref struct EntityAccessor { }

            public readonly struct Tag { }
            public readonly struct TagSet
            {
                // Used by [FromWorld]-bearing jobs to combine the field's declared tags with
                // an optional caller-supplied override.
                public TagSet CombineWith(TagSet other) => default;
            }

            // IAspect contract — see Packages/com.trecs.core/Scripts/SourceGen/IAspect.cs.
            // The EntityIndex member is required because aspect-interface generation depends on it.
            public interface IAspect { EntityIndex EntityIndex { get; } }

            // IRead/IWrite arity 1..8 — matches the real runtime. Generators emit references
            // up to this arity when an aspect declares many components.
            public interface IRead<T1> { }
            public interface IRead<T1, T2> { }
            public interface IRead<T1, T2, T3> { }
            public interface IRead<T1, T2, T3, T4> { }
            public interface IRead<T1, T2, T3, T4, T5> { }
            public interface IRead<T1, T2, T3, T4, T5, T6> { }
            public interface IRead<T1, T2, T3, T4, T5, T6, T7> { }
            public interface IRead<T1, T2, T3, T4, T5, T6, T7, T8> { }

            public interface IWrite<T1> { }
            public interface IWrite<T1, T2> { }
            public interface IWrite<T1, T2, T3> { }
            public interface IWrite<T1, T2, T3, T4> { }
            public interface IWrite<T1, T2, T3, T4, T5> { }
            public interface IWrite<T1, T2, T3, T4, T5, T6> { }
            public interface IWrite<T1, T2, T3, T4, T5, T6, T7> { }
            public interface IWrite<T1, T2, T3, T4, T5, T6, T7, T8> { }

            // Template composition interfaces — TemplateDefinitionGenerator validates
            // their use; arity 1..4 mirrors TemplateAttributes.cs in the runtime.
            public interface IExtends<T1> where T1 : class, ITemplate { }
            public interface IExtends<T1, T2> where T1 : class, ITemplate where T2 : class, ITemplate { }
            public interface IExtends<T1, T2, T3> where T1 : class, ITemplate where T2 : class, ITemplate where T3 : class, ITemplate { }
            public interface IExtends<T1, T2, T3, T4> where T1 : class, ITemplate where T2 : class, ITemplate where T3 : class, ITemplate where T4 : class, ITemplate { }

            // [Unwrap] — applied to single-field IEntityComponent structs to opt into
            // an emitted convenience constructor.
            [System.AttributeUsage(System.AttributeTargets.Struct, AllowMultiple = false)]
            public class UnwrapAttribute : System.Attribute { }

            // [VariableUpdateOnly] — see Packages/com.trecs.core/Scripts/SourceGen/TemplateAttributes.cs.
            // Field on a template component, or class on a template. Mirror the runtime
            // AttributeUsage exactly so the analyzer tests catch struct-target misuses
            // via CS0592 (the C# compiler) rather than via TRECS035.
            [System.AttributeUsage(
                System.AttributeTargets.Field | System.AttributeTargets.Class,
                AllowMultiple = false
            )]
            public class VariableUpdateOnlyAttribute : System.Attribute { }

            // [Interpolated] / [Constant] / [Input] — template component-field attributes.
            // The TemplateAttributeParser detects them by class name and the
            // TemplateValidator emits TRECS032 on conflicting combinations
            // (e.g. [Interpolated] + [Constant]). Mirror the runtime
            // AttributeUsage from Packages/com.trecs.core/Scripts/SourceGen/TemplateAttributes.cs
            // so misuse on non-field targets is caught by C# (CS0592) rather
            // than producing a confusing TRECS error.
            [System.AttributeUsage(System.AttributeTargets.Field, AllowMultiple = false)]
            public sealed class InterpolatedAttribute : System.Attribute { }

            [System.AttributeUsage(System.AttributeTargets.Field, AllowMultiple = false)]
            public sealed class ConstantAttribute : System.Attribute { }

            // InputAttribute mirrors the runtime ctor: (MissingInputBehavior).
            // The parser reads the enum arg via ConstructorArguments[0]; signature has to match.
            [System.AttributeUsage(System.AttributeTargets.Field, AllowMultiple = false)]
            public sealed class InputAttribute : System.Attribute
            {
                public MissingInputBehavior OnMissing { get; }

                public InputAttribute(MissingInputBehavior onMissing)
                {
                    OnMissing = onMissing;
                }
            }

            // Built-in tag/template chain used to mark the singleton globals entity.
            // TemplateAttributeParser.IsGlobalsTemplate looks up
            // ITagged<TrecsTags.Globals> by name + containing-type name, so the
            // shapes here just need to satisfy that lookup.
            public static class TrecsTags
            {
                public struct Globals : ITag { }
            }

            public interface ITagged<T1> where T1 : struct, ITag { }
            public interface ITagged<T1, T2> where T1 : struct, ITag where T2 : struct, ITag { }
            public interface ITagged<T1, T2, T3>
                where T1 : struct, ITag where T2 : struct, ITag where T3 : struct, ITag { }
            public interface ITagged<T1, T2, T3, T4>
                where T1 : struct, ITag where T2 : struct, ITag where T3 : struct, ITag where T4 : struct, ITag { }

            // Partition-dimension declarations. The source generator inspects these on
            // template classes to compute cross-product partitions and dim metadata.
            public interface IPartitionedBy<T1> where T1 : struct, ITag { }
            public interface IPartitionedBy<T1, T2> where T1 : struct, ITag where T2 : struct, ITag { }
            public interface IPartitionedBy<T1, T2, T3>
                where T1 : struct, ITag where T2 : struct, ITag where T3 : struct, ITag { }
            public interface IPartitionedBy<T1, T2, T3, T4>
                where T1 : struct, ITag where T2 : struct, ITag where T3 : struct, ITag where T4 : struct, ITag { }

            // Iteration / job attributes consumed by ForEach / Job / RunOnce generators.
            // Properties match the real declarations in Packages/com.trecs.core/Scripts/SourceGen/.

            [System.AttributeUsage(System.AttributeTargets.Method, AllowMultiple = false)]
            public sealed class ForEachEntityAttribute : System.Attribute
            {
                public System.Type[]? Tags { get; set; }
                public System.Type? Tag { get; set; }
                public System.Type? Set { get; set; }
                public System.Type? Without { get; set; }
                public System.Type[]? Withouts { get; set; }
                public bool MatchByComponents { get; set; }
                public ForEachEntityAttribute() { }
                public ForEachEntityAttribute(params System.Type[] tags) { Tags = tags; }
            }
            // C# 11 generic-attribute variants. Source-gen reads tags from
            // AttributeClass.TypeArguments.
            [System.AttributeUsage(System.AttributeTargets.Method, AllowMultiple = false)]
            public sealed class ForEachEntityAttribute<T1> : System.Attribute
            {
                public System.Type? Set { get; set; }
                public bool MatchByComponents { get; set; }
            }
            [System.AttributeUsage(System.AttributeTargets.Method, AllowMultiple = false)]
            public sealed class ForEachEntityAttribute<T1, T2> : System.Attribute
            {
                public System.Type? Set { get; set; }
                public bool MatchByComponents { get; set; }
            }
            [System.AttributeUsage(System.AttributeTargets.Method, AllowMultiple = false)]
            public sealed class ForEachEntityAttribute<T1, T2, T3> : System.Attribute
            {
                public System.Type? Set { get; set; }
                public bool MatchByComponents { get; set; }
            }
            [System.AttributeUsage(System.AttributeTargets.Method, AllowMultiple = false)]
            public sealed class ForEachEntityAttribute<T1, T2, T3, T4> : System.Attribute
            {
                public System.Type? Set { get; set; }
                public bool MatchByComponents { get; set; }
            }

            [System.AttributeUsage(System.AttributeTargets.Parameter | System.AttributeTargets.Field, AllowMultiple = false)]
            public sealed class SingleEntityAttribute : System.Attribute
            {
                public System.Type[]? Tags { get; set; }
                public System.Type? Tag { get; set; }
                public SingleEntityAttribute() { }
                public SingleEntityAttribute(params System.Type[] tags) { Tags = tags; }
            }
            [System.AttributeUsage(System.AttributeTargets.Parameter | System.AttributeTargets.Field, AllowMultiple = false)]
            public sealed class SingleEntityAttribute<T1> : System.Attribute { }
            [System.AttributeUsage(System.AttributeTargets.Parameter | System.AttributeTargets.Field, AllowMultiple = false)]
            public sealed class SingleEntityAttribute<T1, T2> : System.Attribute { }
            [System.AttributeUsage(System.AttributeTargets.Parameter | System.AttributeTargets.Field, AllowMultiple = false)]
            public sealed class SingleEntityAttribute<T1, T2, T3> : System.Attribute { }
            [System.AttributeUsage(System.AttributeTargets.Parameter | System.AttributeTargets.Field, AllowMultiple = false)]
            public sealed class SingleEntityAttribute<T1, T2, T3, T4> : System.Attribute { }

            [System.AttributeUsage(System.AttributeTargets.Method, AllowMultiple = false)]
            public sealed class WrapAsJobAttribute : System.Attribute { }

            [System.AttributeUsage(System.AttributeTargets.Field | System.AttributeTargets.Parameter, AllowMultiple = false)]
            public sealed class FromWorldAttribute : System.Attribute
            {
                public System.Type[]? Tags { get; set; }
                public System.Type? Tag { get; set; }
                public FromWorldAttribute() { }
                public FromWorldAttribute(params System.Type[] tags) { Tags = tags; }
            }
            [System.AttributeUsage(System.AttributeTargets.Field | System.AttributeTargets.Parameter, AllowMultiple = false)]
            public sealed class FromWorldAttribute<T1> : System.Attribute { }
            [System.AttributeUsage(System.AttributeTargets.Field | System.AttributeTargets.Parameter, AllowMultiple = false)]
            public sealed class FromWorldAttribute<T1, T2> : System.Attribute { }
            [System.AttributeUsage(System.AttributeTargets.Field | System.AttributeTargets.Parameter, AllowMultiple = false)]
            public sealed class FromWorldAttribute<T1, T2, T3> : System.Attribute { }
            [System.AttributeUsage(System.AttributeTargets.Field | System.AttributeTargets.Parameter, AllowMultiple = false)]
            public sealed class FromWorldAttribute<T1, T2, T3, T4> : System.Attribute { }

            [System.AttributeUsage(System.AttributeTargets.Parameter, AllowMultiple = false)]
            public class PassThroughArgumentAttribute : System.Attribute { }

            // [GlobalIndex] — see Packages/com.trecs.core/Scripts/SourceGen/GlobalIndexAttribute.cs.
            // Marks an int parameter on a [ForEachEntity] Execute method to receive the
            // entity's global index across all groups iterated by the call.
            [System.AttributeUsage(System.AttributeTargets.Parameter, AllowMultiple = false, Inherited = false)]
            public class GlobalIndexAttribute : System.Attribute { }

            // ISystem contract — see Packages/com.trecs.core/Scripts/Systems/ISystem.cs.
            // Used by AutoSystemGenerator to detect classes that need ISystemInternal wiring.
            public interface ISystem
            {
                void Execute();
            }

            // ----- Aspect / ForEach / RunOnce surface -----
            // Aspects emit substantial machinery that touches a wide slice of the runtime:
            // component buffer indexing, the full QueryBuilder DSL, dense + sparse slice
            // iteration, NativeFactory for cross-entity Burst lookup, and MoveTo / Set
            // surface routed through both WorldAccessor and NativeWorldAccessor.
            // The stubs below are body-empty / default-returning; they exist to satisfy
            // C# name and shape resolution, not to actually run.

            public readonly struct SetId { }

            public readonly struct EntitySet
            {
                public SetId Id => default;
            }

            public static class EntitySet<T> where T : struct, IEntitySet
            {
                public static EntitySet Value => default;
            }

            public readonly struct EntitySetIndices
            {
                public int Count => 0;
                public int this[int index] => 0;
                // Sparse iteration foreach-loops over the index list.
                public System.Collections.Generic.IEnumerator<int> GetEnumerator()
                    => System.Linq.Enumerable.Empty<int>().GetEnumerator();
            }

            // Buffer types — Read indexer returns `ref readonly T`, Write returns `ref T`.
            // These are the field/parameter types on aspect structs (read-only or read-write
            // depending on IRead vs IWrite for that component).
            public readonly struct NativeComponentBufferRead<T>
                where T : unmanaged, IEntityComponent
            {
                public ref readonly T this[int index] => ref Trecs.Internal.RefStash<T>.Slot;
            }

            public readonly struct NativeComponentBufferWrite<T>
                where T : unmanaged, IEntityComponent
            {
                public ref T this[int index] => ref Trecs.Internal.RefStash<T>.Slot;
            }

            // ComponentBufferAccessor exposes Read/Write properties that downcast to the
            // buffer types above. Aspect ctors copy `.Read` (or `.Write`) into a field.
            public readonly ref struct ComponentBufferAccessor<T>
                where T : unmanaged, IEntityComponent
            {
                public NativeComponentBufferRead<T> Read => default;
                public NativeComponentBufferWrite<T> Write => default;
            }

            // Lookup types — used by the NativeFactory nested in each aspect for cross-entity
            // access from Burst code. Have a Dispose() method that the factory calls.
            public readonly struct NativeComponentLookupRead<T>
                where T : unmanaged, IEntityComponent
            {
                public void Dispose() { }
            }

            public readonly struct NativeComponentLookupWrite<T>
                where T : unmanaged, IEntityComponent
            {
                public void Dispose() { }
            }

            // Single-entity native component access — [FromWorld] field types that
            // resolve a single component via an EntityIndex (rather than a buffer).
            // Trigger TRECS082 when [FromWorld] is given inline tags. Real types live in
            // com.trecs.core/Scripts/Native/.
            public readonly struct NativeComponentRead<T>
                where T : unmanaged, IEntityComponent
            {
                public ref readonly T this[EntityIndex entityIndex]
                    => ref Trecs.Internal.RefStash<T>.Slot;
            }

            public readonly struct NativeComponentWrite<T>
                where T : unmanaged, IEntityComponent
            {
                public ref T this[EntityIndex entityIndex]
                    => ref Trecs.Internal.RefStash<T>.Slot;
            }

            // Job-only set accessors. ParameterClassifier emits TRECS099 when one
            // of these appears in a main-thread iteration method. Real types live in
            // com.trecs.core/Scripts/Native/.
            public readonly struct NativeSetRead<T> where T : struct, IEntitySet { }
            public readonly struct NativeSetCommandBuffer<T>
                where T : struct, IEntitySet
            {
                public void Add(EntityIndex entityIndex) { }
                public void Add(EntityHandle entityHandle, in NativeWorldAccessor world) { }
                public void Remove(EntityIndex entityIndex) { }
                public void Remove(EntityHandle entityHandle, in NativeWorldAccessor world) { }
                public void Clear() { }
            }

            // Main-thread set accessors. ParameterClassifier recognizes these in
            // [WrapAsJob] methods and emits TRECS098. SetAccessor is the gateway
            // exposing .Defer / .Read / .Write. Real types live in com.trecs.core/Scripts/Sets/.
            public readonly struct SetAccessor<T> where T : struct, IEntitySet
            {
                public SetDefer<T> Defer => default;
                public SetRead<T> Read => default;
                public SetWrite<T> Write => default;
            }
            public readonly struct SetDefer<T> where T : struct, IEntitySet
            {
                public void Add(EntityIndex entityIndex) { }
                public void Remove(EntityIndex entityIndex) { }
                public void Clear() { }
            }
            public readonly struct SetRead<T> where T : struct, IEntitySet { }
            public readonly struct SetWrite<T> where T : struct, IEntitySet { }

            // NativeUniquePtr<T> — minimal shape for NativeUniquePtrCopyAnalyzer
            // (TRECS110 / TRECS111). The analyzer matches by name + namespace + arity,
            // not by member signatures, so the body just needs to be a generic struct.
            // Real type lives at Packages/com.trecs.core/Scripts/Heap/NativeUniquePtr.cs.
            public readonly struct NativeUniquePtr<T> where T : unmanaged
            {
            }

            // Slice types yielded by the dense / sparse iterators below.
            public readonly struct DenseGroupSlice
            {
                public GroupIndex GroupIndex => default;
                public int Count => 0;
            }
            public readonly struct SparseGroupSlice
            {
                public GroupIndex GroupIndex => default;
                public EntitySetIndices Indices => default;
            }

            // Iterators are used in two ways by emitted code:
            //   - manual: while (iter.MoveNext()) { ... iter.Current ... }
            //   - foreach: `foreach (var __slice in builder.GroupSlices()) { ... }`
            // The second form needs GetEnumerator(); duck-typing returns `this`.
            public struct DenseGroupSliceIterator
            {
                public bool MoveNext() => false;
                public DenseGroupSlice Current => default;
                public DenseGroupSliceIterator GetEnumerator() => this;
            }

            public struct SparseGroupSliceIterator
            {
                public bool MoveNext() => false;
                public SparseGroupSlice Current => default;
                public SparseGroupSliceIterator GetEnumerator() => this;
            }

            // EntityRange — used by ForEach generator's iteration code paths to express
            // a contiguous index range inside a group. Real type lives at
            // Packages/com.trecs.core/Scripts/Query/.
            public readonly struct EntityRange
            {
                public int Start => 0;
                public int Count => 0;
                public int End => 0;
            }

            // QueryBuilder — the fluent DSL aspects wrap into their per-aspect AspectQuery.
            // Arity 1..4 on WithTags / WithoutTags / WithComponents matches the runtime.
            public ref struct QueryBuilder
            {
                public WorldAccessor World => default!;
                public bool HasAnyCriteria => false;

                public QueryBuilder WithTags<T1>() where T1 : struct, ITag => this;
                public QueryBuilder WithTags<T1, T2>() where T1 : struct, ITag where T2 : struct, ITag => this;
                public QueryBuilder WithTags<T1, T2, T3>() where T1 : struct, ITag where T2 : struct, ITag where T3 : struct, ITag => this;
                public QueryBuilder WithTags<T1, T2, T3, T4>() where T1 : struct, ITag where T2 : struct, ITag where T3 : struct, ITag where T4 : struct, ITag => this;
                public QueryBuilder WithTags(TagSet tags) => this;

                public QueryBuilder WithoutTags<T1>() where T1 : struct, ITag => this;
                public QueryBuilder WithoutTags<T1, T2>() where T1 : struct, ITag where T2 : struct, ITag => this;
                public QueryBuilder WithoutTags<T1, T2, T3>() where T1 : struct, ITag where T2 : struct, ITag where T3 : struct, ITag => this;
                public QueryBuilder WithoutTags<T1, T2, T3, T4>() where T1 : struct, ITag where T2 : struct, ITag where T3 : struct, ITag where T4 : struct, ITag => this;
                public QueryBuilder WithoutTags(TagSet tags) => this;

                public QueryBuilder WithComponents<T1>() where T1 : unmanaged, IEntityComponent => this;
                public QueryBuilder WithComponents<T1, T2>() where T1 : unmanaged, IEntityComponent where T2 : unmanaged, IEntityComponent => this;
                public QueryBuilder WithComponents<T1, T2, T3>() where T1 : unmanaged, IEntityComponent where T2 : unmanaged, IEntityComponent where T3 : unmanaged, IEntityComponent => this;
                public QueryBuilder WithComponents<T1, T2, T3, T4>() where T1 : unmanaged, IEntityComponent where T2 : unmanaged, IEntityComponent where T3 : unmanaged, IEntityComponent where T4 : unmanaged, IEntityComponent => this;

                public SparseQueryBuilder InSet<T>() where T : struct, IEntitySet => default;
                public SparseQueryBuilder InSet(EntitySet entitySet) => default;
                public SparseQueryBuilder InSet(SetId setId) => default;

                public DenseGroupSliceIterator GroupSlices() => default;
                public int Count() => 0;
                public EntityIndex SingleEntityIndex() => default;
                public bool TrySingleEntityIndex(out EntityIndex entityIndex) { entityIndex = default; return false; }
            }

            // SparseQueryBuilder mirrors most of QueryBuilder's filtering surface — emitted
            // ForEach code chains WithTags/etc. on either kind of builder uniformly.
            public ref struct SparseQueryBuilder
            {
                public WorldAccessor World => default!;
                public bool HasAnyCriteria => false;
                public SparseGroupSliceIterator GroupSlices() => default;
                public int Count() => 0;
                public EntityIndex SingleEntityIndex() => default;
                public bool TrySingleEntityIndex(out EntityIndex entityIndex) { entityIndex = default; return false; }

                public SparseQueryBuilder WithTags<T1>() where T1 : struct, ITag => this;
                public SparseQueryBuilder WithTags<T1, T2>() where T1 : struct, ITag where T2 : struct, ITag => this;
                public SparseQueryBuilder WithTags<T1, T2, T3>() where T1 : struct, ITag where T2 : struct, ITag where T3 : struct, ITag => this;
                public SparseQueryBuilder WithTags<T1, T2, T3, T4>() where T1 : struct, ITag where T2 : struct, ITag where T3 : struct, ITag where T4 : struct, ITag => this;
                public SparseQueryBuilder WithTags(TagSet tags) => this;
                public SparseQueryBuilder WithoutTags<T1>() where T1 : struct, ITag => this;
                public SparseQueryBuilder WithoutTags<T1, T2>() where T1 : struct, ITag where T2 : struct, ITag => this;
                public SparseQueryBuilder WithoutTags<T1, T2, T3>() where T1 : struct, ITag where T2 : struct, ITag where T3 : struct, ITag => this;
                public SparseQueryBuilder WithoutTags<T1, T2, T3, T4>() where T1 : struct, ITag where T2 : struct, ITag where T3 : struct, ITag where T4 : struct, ITag => this;
                public SparseQueryBuilder WithoutTags(TagSet tags) => this;
                public SparseQueryBuilder WithComponents<T1>() where T1 : unmanaged, IEntityComponent => this;
                public SparseQueryBuilder WithComponents<T1, T2>() where T1 : unmanaged, IEntityComponent where T2 : unmanaged, IEntityComponent => this;
                public SparseQueryBuilder WithComponents<T1, T2, T3>() where T1 : unmanaged, IEntityComponent where T2 : unmanaged, IEntityComponent where T3 : unmanaged, IEntityComponent => this;
                public SparseQueryBuilder WithComponents<T1, T2, T3, T4>() where T1 : unmanaged, IEntityComponent where T2 : unmanaged, IEntityComponent where T3 : unmanaged, IEntityComponent where T4 : unmanaged, IEntityComponent => this;
            }

            // WorldAccessor — declared as a class so AutoSystemGenerator's emitted
            // `WorldAccessor _world;` field and `WorldAccessor World => _world;` property
            // resolve. Aspect machinery additionally needs Query/ComponentBuffer/MoveTo/Set;
            // job machinery needs the *ForJob accessors that return tuples (the buffer +
            // its current count for jobs that read the count).
            public class WorldAccessor
            {
                public QueryBuilder Query() => default;
                public ComponentBufferAccessor<T> ComponentBuffer<T>(GroupIndex group)
                    where T : unmanaged, IEntityComponent => default;

                public void MoveTo<T1>(EntityIndex entityIndex) where T1 : struct, ITag { }
                public void MoveTo<T1, T2>(EntityIndex entityIndex) where T1 : struct, ITag where T2 : struct, ITag { }
                public void MoveTo<T1, T2, T3>(EntityIndex entityIndex) where T1 : struct, ITag where T2 : struct, ITag where T3 : struct, ITag { }
                public void MoveTo<T1, T2, T3, T4>(EntityIndex entityIndex) where T1 : struct, ITag where T2 : struct, ITag where T3 : struct, ITag where T4 : struct, ITag { }
                public void SetTag<T>(EntityIndex entityIndex) where T : struct, ITag { }
                public void UnsetTag<T>(EntityIndex entityIndex) where T : struct, ITag { }

                // Set gateway: Set<T>() returns SetAccessor<T> with .Defer / .Read / .Write properties.
                public SetAccessor<TSet> Set<TSet>() where TSet : struct, IEntitySet => default;

                // Job-scheduling surface (real impls live in com.trecs.core/Scripts/Jobs).
                public Trecs.Internal.JobScheduler GetJobSchedulerForJob() => default!;
                public (NativeComponentBufferRead<T>, int) GetBufferReadForJob<T>(GroupIndex group)
                    where T : unmanaged, IEntityComponent => default;
                public (NativeComponentBufferWrite<T>, int) GetBufferWriteForJob<T>(GroupIndex group)
                    where T : unmanaged, IEntityComponent => default;
                public NativeComponentLookupRead<T> GetNativeComponentReadForJob<T>()
                    where T : unmanaged, IEntityComponent => default;
                public NativeComponentLookupWrite<T> GetNativeComponentWriteForJob<T>()
                    where T : unmanaged, IEntityComponent => default;
                // Returns (indices, lifetime, count) — emitted code deconstructs the tuple.
                public (Trecs.Internal.JobSparseIndices, Trecs.Internal.JobIndicesLifetime, int)
                    AllocateSparseIndicesForJob(SparseGroupSlice slice) => default;

                // [FromWorld] cross-entity factories: jobs allocate per-group lookups via
                // these and dispose them via JobScheduler.RegisterPendingDispose.
                public NativeComponentLookupRead<T> CreateNativeComponentLookupReadForJob<T>(
                    System.Collections.Generic.IEnumerable<GroupIndex> groups,
                    Unity.Collections.Allocator allocator
                ) where T : unmanaged, IEntityComponent => default;
                public NativeComponentLookupWrite<T> CreateNativeComponentLookupWriteForJob<T>(
                    System.Collections.Generic.IEnumerable<GroupIndex> groups,
                    Unity.Collections.Allocator allocator
                ) where T : unmanaged, IEntityComponent => default;

                // WorldInfo gives access to world-level group/tag introspection.
                public Trecs.WorldInfo WorldInfo => default!;

                // ToNative() yields a Burst-compatible value-type view onto the same world.
                // AutoJobGenerator's ScheduleParallel shim calls this when forwarding into
                // a Burst job that takes `in NativeWorldAccessor`.
                public NativeWorldAccessor ToNative() => default;

                // Per-entity helpers used by source-gen-emitted iteration code when the
                // user takes `EntityHandle` / `EntityAccessor` parameters. The real
                // versions live in WorldAccessorInternalExtensions (Trecs.Internal) but
                // generators emit `world.GetEntityHandle(...)` / `world.Entity(...)`
                // and the C# compiler resolves them either way.
                public EntityHandle GetEntityHandle(EntityIndex entityIndex) => default;
                public EntityAccessor Entity(EntityIndex entityIndex) => default;

                // Job-side per-group EntityHandle buffer. Source-gen-emitted job code
                // calls `__world.GetEntityHandleBufferForJob(group)` once per group at
                // schedule time and stashes the result in a hidden field; the inner
                // Execute(int i) shim then dereferences `__EntityHandles[i]` to pass the
                // handle into a user method that took an EntityHandle parameter.
                public NativeEntityHandleBuffer GetEntityHandleBufferForJob(GroupIndex group) =>
                    default;
            }

            // NativeEntityHandleBuffer — read-only per-group view. Source-gen-emitted
            // job structs hold one as a hidden field, indexed per-iteration to materialize
            // the user's EntityHandle parameter.
            public readonly struct NativeEntityHandleBuffer
            {
                public EntityHandle this[int index] => default;
                public int Length => 0;
            }

            public class WorldInfo
            {
                public System.Collections.Generic.IEnumerable<GroupIndex> GetGroupsWithTags(TagSet tags) =>
                    System.Linq.Enumerable.Empty<GroupIndex>();
                public System.Collections.Generic.IEnumerable<GroupIndex> GetGroupsWithComponents<T1>()
                    where T1 : unmanaged, IEntityComponent =>
                    System.Linq.Enumerable.Empty<GroupIndex>();
                public System.Collections.Generic.IEnumerable<GroupIndex> GetGroupsWithComponents<T1, T2>()
                    where T1 : unmanaged, IEntityComponent where T2 : unmanaged, IEntityComponent =>
                    System.Linq.Enumerable.Empty<GroupIndex>();
                public System.Collections.Generic.IEnumerable<GroupIndex> GetGroupsWithComponents<T1, T2, T3>()
                    where T1 : unmanaged, IEntityComponent where T2 : unmanaged, IEntityComponent where T3 : unmanaged, IEntityComponent =>
                    System.Linq.Enumerable.Empty<GroupIndex>();
            }

            // NativeWorldAccessor — Burst-compatible value-type counterpart. Aspect MoveTo /
            // Set helpers also expose `(in NativeWorldAccessor)` overloads.
            public readonly struct NativeWorldAccessor
            {
                public void MoveTo<T1>(EntityIndex entityIndex) where T1 : struct, ITag { }
                public void MoveTo<T1, T2>(EntityIndex entityIndex) where T1 : struct, ITag where T2 : struct, ITag { }
                public void MoveTo<T1, T2, T3>(EntityIndex entityIndex) where T1 : struct, ITag where T2 : struct, ITag where T3 : struct, ITag { }
                public void MoveTo<T1, T2, T3, T4>(EntityIndex entityIndex) where T1 : struct, ITag where T2 : struct, ITag where T3 : struct, ITag where T4 : struct, ITag { }
                public void SetTag<T>(EntityIndex entityIndex) where T : struct, ITag { }
                public void UnsetTag<T>(EntityIndex entityIndex) where T : struct, ITag { }
                public void SetAdd<TSet>(EntityIndex entityIndex) where TSet : struct, IEntitySet { }
                public void SetRemove<TSet>(EntityIndex entityIndex) where TSet : struct, IEntitySet { }
            }

            // ----- Template generator surface -----
            // TemplateDefinitionGenerator emits:
            //     public static readonly Template Template = new Template(
            //         debugName: "...", localBaseTemplates: ...,
            //         partitions: ..., localComponentDeclarations: ...,
            //         localTags: ...);
            // The minimum stub matches that constructor shape so emitted code parses and
            // resolves; bodies don't matter.

            public interface IComponentDeclaration { }

            public class ComponentDeclaration<T> : IComponentDeclaration
                where T : unmanaged, IEntityComponent
            {
                public ComponentDeclaration(
                    bool? variableUpdateOnly,
                    bool? isInput,
                    object? inputFrameBehaviour,
                    bool? isConstant,
                    bool? isInterpolated,
                    object? defaultValue
                ) { }
            }

            public class Template
            {
                public Template(
                    string debugName,
                    Template[] localBaseTemplates,
                    TagSet[] partitions,
                    IComponentDeclaration[] localComponentDeclarations,
                    Tag[] localTags,
                    bool localVariableUpdateOnly = false,
                    TagSet[] dimensions = null
                ) { }
            }

            // Tag<T>.Value and TagSet<T1..T4>.Value — emitted code uses these as
            // constants when the template lists tags / partitions.
            public static class Tag<T> where T : struct, ITag
            {
                public static Tag Value { get; }
            }
            public static class TagSet<T1> where T1 : struct, ITag
            {
                public static TagSet Value { get; }
            }
            public static class TagSet<T1, T2> where T1 : struct, ITag where T2 : struct, ITag
            {
                public static TagSet Value { get; }
            }
            public static class TagSet<T1, T2, T3>
                where T1 : struct, ITag where T2 : struct, ITag where T3 : struct, ITag
            {
                public static TagSet Value { get; }
            }
            public static class TagSet<T1, T2, T3, T4>
                where T1 : struct, ITag where T2 : struct, ITag where T3 : struct, ITag where T4 : struct, ITag
            {
                public static TagSet Value { get; }
            }

            public enum MissingInputBehavior { Reset, Retain }

            // ----- InterpolatorInstaller surface -----
            // InterpolatorInstallerGenerator emits a WorldBuilder extension method
            // that calls AddInterpolatedPreviousSaver<T>() and AddSystem(systemName).

            [System.AttributeUsage(System.AttributeTargets.Method, AllowMultiple = false)]
            public sealed class GenerateInterpolatorSystemAttribute : System.Attribute
            {
                public GenerateInterpolatorSystemAttribute(string systemName, string groupName) { }
            }

            public class InterpolatedPreviousSaver<T> where T : unmanaged, IEntityComponent { }

            // Interpolation wrapper components. The InterpolatorJob generator emits a job
            // that reads `previous.Value`, the buffer's current item, and writes
            // `interpolated.Value` — so .Value is the inner T.
            public partial struct InterpolatedPrevious<T> : IEntityComponent where T : unmanaged, IEntityComponent
            {
                public T Value;
            }
            public partial struct Interpolated<T> : IEntityComponent where T : unmanaged, IEntityComponent
            {
                public T Value;
            }

            // System-level attributes emitted on the generated InterpolatorSystem class.
            public enum SystemPhase { FixedUpdate, Presentation, Input }
            [System.AttributeUsage(System.AttributeTargets.Class)]
            public class ExecuteInAttribute : System.Attribute
            {
                public ExecuteInAttribute(SystemPhase phase) { }
            }
            [System.AttributeUsage(System.AttributeTargets.Class)]
            public class ExecutePriorityAttribute : System.Attribute
            {
                public ExecutePriorityAttribute(int priority) { }
            }
            [System.AttributeUsage(System.AttributeTargets.Class)]
            public class AllowMultipleAttribute : System.Attribute { }

            public class WorldBuilder
            {
                public WorldBuilder AddInterpolatedPreviousSaver<T>(InterpolatedPreviousSaver<T> saver)
                    where T : unmanaged, IEntityComponent => this;
                public WorldBuilder AddSystem(ISystem system) => this;
            }
        }

        namespace Trecs.Internal
        {
            // EntityIndex lives in Trecs.Internal — the public Trecs API uses EntityHandle.
            // The type stays public so source-generated code in user assemblies can reference it.
            public readonly struct EntityIndex
            {
                public readonly int Index;
                public readonly GroupIndex GroupIndex;
                public static EntityIndex Null => default;
                public bool IsNull => false;

                public EntityIndex(int index, GroupIndex group) { Index = index; GroupIndex = group; }

                // Aspect ctors call `_entityIndex.WithIndex(...)` to advance the index field
                // during iteration without rebuilding the GroupIndex.
                public EntityIndex WithIndex(int index) => new EntityIndex(index, GroupIndex);
            }

            // Real signature lives at Packages/com.trecs.core/Scripts/Util/UnmanagedUtil.cs.
            // Generated equality / operator overloads in EntityComponentGenerator
            // call BlittableEquals; the body doesn't matter for compile-cleanliness tests.
            public static class UnmanagedUtil
            {
                public static bool BlittableEquals<T>(in T lhs, in T rhs) where T : unmanaged => false;
                public static int BlittableHashCode<T>(in T value) where T : unmanaged => 0;
            }

            // ISystemInternal — internal extension point that AutoSystemGenerator emits an
            // explicit interface implementation for. Real signature in
            // Packages/com.trecs.core/Scripts/Systems/ISystem.cs.
            public interface ISystemInternal
            {
                Trecs.WorldAccessor World { get; set; }
                void Ready();
                void Shutdown();
            }

            // Assert.That — generated code (e.g. AutoSystemGenerator's World setter, aspect
            // Query DSLs) uses this for runtime invariants. Public-repo signature lives at
            // com.trecs.core/Scripts/Util/Assert.cs (sourced from sv-unity-core, renamespaced
            // by the exporter from `Svkj` to `Trecs.Internal`).
            public static class Assert
            {
                public static void That(bool condition) { }
                public static void That(bool condition, string message) { }
            }

            // Test-only helper: NativeComponentBufferRead/Write indexers must return a `ref`
            // to *some* T. There is no real backing storage in test-land, so we hand back
            // a static slot. Tests never read or write through this — they only assert that
            // the generated code compiles, which requires the indexer signature to be valid.
            internal static class RefStash<T> where T : unmanaged
            {
                internal static T Slot;
            }

            // Aspect.NativeFactory.Create() converts a per-group lookup into a per-group
            // buffer via this helper. Real impl lives in com.trecs.core/Scripts/Native.
            public static class JobGenSchedulingExtensions
            {
                public static NativeComponentBufferRead<T> GetBufferForGroupForJob<T>(
                    NativeComponentLookupRead<T> lookup, GroupIndex group
                ) where T : unmanaged, IEntityComponent => default;

                public static NativeComponentBufferWrite<T> GetBufferForGroupForJob<T>(
                    NativeComponentLookupWrite<T> lookup, GroupIndex group
                ) where T : unmanaged, IEntityComponent => default;
            }

            // ----- Job scheduling surface -----
            // Generated job code threads dependency tracking through a JobScheduler returned
            // by WorldAccessor.GetJobSchedulerForJob(). ResourceId / ComponentTypeId<T>
            // identify which component-on-which-group a job touches.

            public readonly struct ResourceId
            {
                public static ResourceId Component(ComponentTypeId componentType) => default;
            }

            public readonly struct ComponentTypeId { }

            public static class ComponentTypeId<T> where T : unmanaged, IEntityComponent
            {
                public static ComponentTypeId Value => default;
            }

            public class JobScheduler
            {
                public Unity.Jobs.JobHandle IncludeReadDep(
                    Unity.Jobs.JobHandle deps, ResourceId res, GroupIndex group
                ) => default;
                public Unity.Jobs.JobHandle IncludeWriteDep(
                    Unity.Jobs.JobHandle deps, ResourceId res, GroupIndex group
                ) => default;
                public void TrackJobRead(Unity.Jobs.JobHandle handle, ResourceId res, GroupIndex group) { }
                public void TrackJobWrite(Unity.Jobs.JobHandle handle, ResourceId res, GroupIndex group) { }
                public void TrackJob(Unity.Jobs.JobHandle handle) { }

                // Disposes a per-job lookup once all in-flight jobs that touched it complete.
                // Generic so it accepts the lookup-of-T returned by CreateNativeComponentLookup*ForJob.
                public void RegisterPendingDispose<T>(NativeComponentLookupRead<T> lookup)
                    where T : unmanaged, IEntityComponent { }
                public void RegisterPendingDispose<T>(NativeComponentLookupWrite<T> lookup)
                    where T : unmanaged, IEntityComponent { }
            }

            // Sparse-iteration job uses these to thread per-entity index arrays through to
            // a Burst job. Lifetime is disposed once the job completes.
            public readonly struct JobSparseIndices
            {
                public int this[int index] => 0;
            }

            public readonly struct JobIndicesLifetime
            {
                public void Dispose() { }
                public Unity.Jobs.JobHandle Dispose(Unity.Jobs.JobHandle deps) => default;
            }

            // Batch-size heuristic — emitted ScheduleParallel calls pass JobsUtil.ChooseBatchSize(count).
            public static class JobsUtil
            {
                public static int ChooseBatchSize(int count) => 64;
            }

            // Interpolation helper — InterpolatorJob calls this once per Execute() to compute
            // the variable-update interpolation factor.
            public static class InterpolationUtil
            {
                public static float CalculatePercentThroughFixedFrame(Trecs.WorldAccessor world) => 0f;
            }
        }
        """;
}
