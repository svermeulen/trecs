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

        // [NativeContainer] marker from Unity.Collections.LowLevel.Unsafe — used by the
        // ImmutableAnalyzer's struct-mutation-surface check to fast-path reject writable
        // native containers (NativeArray<T>, NativeSlice<T>, NativeHashMap<K, V>, …) as
        // unsafe to expose on [Immutable] types. Real type lives in com.unity.collections.
        namespace Unity.Collections.LowLevel.Unsafe
        {
            [System.AttributeUsage(System.AttributeTargets.Struct)]
            public sealed class NativeContainerAttribute : System.Attribute { }
        }

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
            public interface IJobParallelForBatch { void Execute(int startIndex, int count); }

            // Generated job code calls `__trecs_job.ScheduleParallel(count, batchSize, deps)` —
            // mirrors Unity's IJobForExtensions.ScheduleParallel<T>(this T, int, int, JobHandle).
            public static class IJobForExtensions
            {
                public static JobHandle ScheduleParallel<T>(
                    this T jobData, int arrayLength, int innerloopBatchCount, JobHandle dependsOn
                ) where T : struct, IJobFor => default;
                public static JobHandle Schedule<T>(this T jobData, JobHandle dependsOn)
                    where T : struct, IJob => default;
            }

            // Mirrors Unity.Jobs.IJobParallelForBatchExtensions.ScheduleParallel<T>
            // (com.unity.collections). AutoJobGenerator's emit calls
            // `job.ScheduleParallel(count, batchSize, deps)` and overload resolution
            // picks this when the job struct implements IJobParallelForBatch.
            public static class IJobParallelForBatchExtensions
            {
                public static JobHandle ScheduleParallel<T>(
                    this T jobData, int arrayLength, int indicesPerJobCount, JobHandle dependsOn
                ) where T : struct, IJobParallelForBatch => default;
            }
        }

        // Subset of Unity.Collections used by the source generators and by the
        // ImmutableAnalyzer's external-library allowlist. Real types live in
        // com.unity.collections (NativeHashMap/HashSet/Parallel*) and in
        // UnityEngine.CoreModule (NativeArray<T>). The stubs mirror the public
        // shape — including the nested `.ReadOnly` views and a writable
        // indexer setter on `NativeArray<T>` — so analyzer/generator tests can
        // reference the types without dragging Unity in.
        //
        // Each nested `.ReadOnly` carries an `internal unsafe void* _buffer`
        // field — Unity's real ReadOnly views store an unmanaged pointer plus
        // a safety handle. Modeling the pointer makes the safe-type walker
        // reject them structurally (pointer types are neither value nor
        // reference types in Roslyn's sense and fall through to "unsafe"),
        // which is what forces the ImmutableAnalyzer's external-library
        // allowlist to do real work. Without the pointer field the structs
        // would be field-less and trivially pass the walker, hiding any
        // regression where the allowlist gets bypassed (TRECS126/127 on
        // these views would silently still pass).
        namespace Unity.Collections
        {
            [Unity.Collections.LowLevel.Unsafe.NativeContainer]
            public struct NativeArray<T> : System.IDisposable where T : struct
            {
                public int Length => 0;
                public T this[int index] { get { return default!; } set { } }
                public void Dispose() { }
                public ReadOnly AsReadOnly() => default;

                public struct ReadOnly
                {
                    public int Length => 0;
                    public T this[int index] => default!;
                    internal unsafe void* _buffer;
                }
            }

            [Unity.Collections.LowLevel.Unsafe.NativeContainer]
            public struct NativeHashMap<TKey, TValue> : System.IDisposable
                where TKey : unmanaged, System.IEquatable<TKey>
                where TValue : unmanaged
            {
                public TValue this[TKey key] { get { return default!; } set { } }
                public void Dispose() { }
                public ReadOnly AsReadOnly() => default;
                public Enumerator GetEnumerator() => default;

                public struct Enumerator
                {
                    public System.Collections.Generic.KeyValuePair<TKey, TValue> Current => default;
                    public bool MoveNext() => false;
                }

                public struct ReadOnly
                {
                    public bool TryGetValue(TKey key, out TValue value) { value = default!; return false; }
                    internal unsafe void* _buffer;
                }
            }

            [Unity.Collections.LowLevel.Unsafe.NativeContainer]
            public struct NativeHashSet<T> : System.IDisposable
                where T : unmanaged, System.IEquatable<T>
            {
                public void Dispose() { }
                public ReadOnly AsReadOnly() => default;
                public Enumerator GetEnumerator() => default;

                public struct Enumerator
                {
                    public T Current => default!;
                    public bool MoveNext() => false;
                }

                public struct ReadOnly
                {
                    public bool Contains(T item) => false;
                    internal unsafe void* _buffer;
                }
            }

            [Unity.Collections.LowLevel.Unsafe.NativeContainer]
            public struct NativeParallelHashMap<TKey, TValue> : System.IDisposable
                where TKey : unmanaged, System.IEquatable<TKey>
                where TValue : unmanaged
            {
                public TValue this[TKey key] { get { return default!; } set { } }
                public void Dispose() { }
                public ReadOnly AsReadOnly() => default;
                public Enumerator GetEnumerator() => default;

                public struct Enumerator
                {
                    public System.Collections.Generic.KeyValuePair<TKey, TValue> Current => default;
                    public bool MoveNext() => false;
                }

                public struct ReadOnly
                {
                    public bool TryGetValue(TKey key, out TValue value) { value = default!; return false; }
                    internal unsafe void* _buffer;
                }
            }

            [Unity.Collections.LowLevel.Unsafe.NativeContainer]
            public struct NativeParallelMultiHashMap<TKey, TValue> : System.IDisposable
                where TKey : unmanaged, System.IEquatable<TKey>
                where TValue : unmanaged
            {
                public void Dispose() { }
                public ReadOnly AsReadOnly() => default;

                public struct ReadOnly
                {
                    internal unsafe void* _buffer;
                }
            }

            // NativeSlice<T> is NOT in the allowlist — its indexer has a setter.
            // Stub included so the negative test can name the type.
            [Unity.Collections.LowLevel.Unsafe.NativeContainer]
            public struct NativeSlice<T> where T : struct
            {
                public int Length => 0;
                public T this[int index] { get { return default!; } set { } }
            }
        }

        // UnityEngine stubs for DictionaryIterationAnalyzer / FixedUpdateDeterminismAnalyzer tests.
        // Real types live in UnityEngine.CoreModule.
        namespace UnityEngine
        {
            public static class Time
            {
                public static float time => 0f;
                public static float deltaTime => 0f;
                public static float unscaledTime => 0f;
                public static float realtimeSinceStartup => 0f;
            }

            public static class Random
            {
                public static float value => 0f;
                public static float Range(float min, float max) => 0f;
                public static int Range(int min, int max) => 0;
            }
        }

        namespace Trecs
        {
            // Marker interfaces consumed by EntityComponentGenerator and
            // AspectGenerator base-list scanning.
            public interface IEntityComponent { }
            public interface ITag { }
            public interface IEntitySet { }
            public interface ITemplate { }

            // Assembly-level settings — see Packages/com.trecs.core/Scripts/SourceGen/TrecsSourceGenSettingsAttribute.cs
            [System.AttributeUsage(System.AttributeTargets.Assembly, AllowMultiple = false)]
            public sealed class TrecsSourceGenSettingsAttribute : System.Attribute
            {
                public string? ComponentPrefix { get; set; }
                public bool GlobalCollectionIterationCheck { get; set; }
            }

            // [NonCopyable] / [Copyable] — picked up by NonCopyableAnalyzer
            // (TRECS118-120) via attribute + IEntityComponent lookup on the target.
            // Real types live at com.trecs.core/Scripts/SourceGen/NonCopyableAttribute.cs
            // and CopyableAttribute.cs.
            [System.AttributeUsage(System.AttributeTargets.Struct)]
            public sealed class NonCopyableAttribute : System.Attribute { }

            [System.AttributeUsage(System.AttributeTargets.Struct)]
            public sealed class CopyableAttribute : System.Attribute { }

            // EntityIndex — handle used by aspect / iteration plumbing.
            // Real type lives at com.trecs.core/Scripts/Entities/EntityIndex.cs in the
            // Trecs namespace. Source generators look it up by `Trecs.EntityIndex`.
            public readonly struct EntityIndex
            {
                public readonly int Index;
                public readonly GroupIndex GroupIndex;
                public static EntityIndex Null => default;
                public bool IsNull => false;

                public EntityIndex(int index, GroupIndex group) { Index = index; GroupIndex = group; }

                // Aspect ctors call `__entityIndex.WithIndex(...)` to advance the index field
                // during iteration without rebuilding the GroupIndex.
                public EntityIndex WithIndex(int index) => new EntityIndex(index, GroupIndex);

                // EntityRefEmitter emits `__entityIndex.ToHandle(__world)` for iteration
                // callbacks that declare an `EntityHandle` parameter.
                public EntityHandle ToHandle(WorldAccessor world) => default;
                public EntityHandle ToHandle(NativeWorldAccessor world) => default;

                // Entity-targeted ops — generated aspect SetTag/UnsetTag delegate to
                // `__entityIndex.SetTag<T>(world)` / `__entityIndex.UnsetTag<T>(world)`.
                public void SetTag<T>(WorldAccessor world) where T : struct, ITag { }
                public void SetTag<T>(in NativeWorldAccessor world) where T : struct, ITag { }
                public void UnsetTag<T>(WorldAccessor world) where T : struct, ITag { }
                public void UnsetTag<T>(in NativeWorldAccessor world) where T : struct, ITag { }
            }

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
                public readonly int UniqueId;
                public readonly int Version;
                public static EntityHandle Null => default;
                public bool IsNull => false;

                // Aspect WorldAccessor+EntityHandle ctor delegates via `entityHandle.ToIndex(world)`.
                public EntityIndex ToIndex(WorldAccessor world) => default;
                public EntityIndex ToIndex(NativeWorldAccessor world) => default;
            }

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

            // InputAttribute mirrors the runtime ctor: (MissingInputBehavior, bool warnOnMissing = false).
            // The parser reads the enum arg via ConstructorArguments[0]; signature has to match.
            [System.AttributeUsage(System.AttributeTargets.Field, AllowMultiple = false)]
            public sealed class InputAttribute : System.Attribute
            {
                public MissingInputBehavior OnMissing { get; }
                public bool WarnOnMissing { get; }

                public InputAttribute(MissingInputBehavior onMissing, bool warnOnMissing = false)
                {
                    OnMissing = onMissing;
                    WarnOnMissing = warnOnMissing;
                }
            }

            // Built-in tag/template chain used to mark the singleton globals entity.
            // TemplateAttributeParser.IsGlobalsTemplate looks up
            // IHasTags<TrecsTags.Globals> by name + containing-type name, so the
            // shapes here just need to satisfy that lookup.
            public static class TrecsTags
            {
                public struct Globals : ITag { }
            }

            public interface IHasTags<T1> where T1 : struct, ITag { }
            public interface IHasTags<T1, T2> where T1 : struct, ITag where T2 : struct, ITag { }
            public interface IHasTags<T1, T2, T3>
                where T1 : struct, ITag where T2 : struct, ITag where T3 : struct, ITag { }
            public interface IHasTags<T1, T2, T3, T4>
                where T1 : struct, ITag where T2 : struct, ITag where T3 : struct, ITag where T4 : struct, ITag { }

            // Iteration / job attributes consumed by ForEach / Job / RunOnce generators.
            // Properties match the real declarations in Packages/com.trecs.core/Scripts/SourceGen/.

            [System.AttributeUsage(System.AttributeTargets.Method, AllowMultiple = false)]
            public sealed class ForEachEntityAttribute : System.Attribute
            {
                public System.Type[]? Tags { get; set; }
                public System.Type? Tag { get; set; }
                public System.Type? Set { get; set; }
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
            // iteration, NativeFactory for cross-entity Burst lookup, and SetTag / UnsetTag /
            // Set surface routed through both WorldAccessor and NativeWorldAccessor.
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

            // SetAccessor<T> — gateway returned by WorldAccessor.Set<T>().
            // Real impl lives at com.trecs.core/Scripts/Sets/SetAccessor.cs.
            public readonly ref struct SetAccessor<T> where T : struct, IEntitySet
            {
                public void DeferredAdd(EntityIndex entityIndex) { }
                public void DeferredAdd(EntityHandle entityHandle) { }
                public void DeferredRemove(EntityIndex entityIndex) { }
                public void DeferredRemove(EntityHandle entityHandle) { }
                public void DeferredClear() { }
            }

            // NativeSetAccessor<T> — Burst-compatible gateway returned by NativeWorldAccessor.Set<T>().
            // Real impl lives at com.trecs.core/Scripts/Sets/NativeSetAccessor.cs.
            public readonly struct NativeSetAccessor<T> where T : struct, IEntitySet
            {
                public void DeferredAdd(EntityIndex entityIndex) { }
                public void DeferredAdd(EntityHandle entityHandle) { }
                public void DeferredRemove(EntityIndex entityIndex) { }
                public void DeferredRemove(EntityHandle entityHandle) { }
                public void DeferredClear() { }
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
            public readonly struct NativeSetWrite<T> where T : struct, IEntitySet { }

            // NativeSetCommandBuffer<T> — thread-safe command buffer for queuing
            // add/remove/clear of entities to/from a set from within parallel jobs.
            // Real type at com.trecs.core/Scripts/Sets/NativeSetCommandBuffer.cs.
            public struct NativeSetCommandBuffer<T> where T : struct, IEntitySet { }

            // NativeEntitySetIndices<T> — read-only, job-friendly view onto the
            // entity indices of a set for one specific group. Real type at
            // com.trecs.core/Scripts/Sets/NativeEntitySetIndices.cs.
            public readonly struct NativeEntitySetIndices<T> where T : struct, IEntitySet { }

            // Main-thread set accessors. ParameterClassifier recognizes these in
            // [WrapAsJob] methods and emits TRECS098. SetRead is read-only; SetWrite is
            // read-write. Real types live in com.trecs.core/Scripts/Sets/.
            // (SetAccessor<T> itself is declared above with its full member surface.)
            public readonly struct SetRead<T> where T : struct, IEntitySet { }
            public readonly struct SetWrite<T> where T : struct, IEntitySet { }

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
                public EntityIndex SingleIndex() => default;
                public bool TrySingleIndex(out EntityIndex entityIndex) { entityIndex = default; return false; }
            }

            // SparseQueryBuilder mirrors most of QueryBuilder's filtering surface — emitted
            // ForEach code chains WithTags/etc. on either kind of builder uniformly.
            public ref struct SparseQueryBuilder
            {
                public WorldAccessor World => default!;
                public bool HasAnyCriteria => false;
                public SparseGroupSliceIterator GroupSlices() => default;
                public int Count() => 0;
                public EntityIndex SingleIndex() => default;
                public bool TrySingleIndex(out EntityIndex entityIndex) { entityIndex = default; return false; }

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
            // `WorldAccessor __world;` field and `WorldAccessor World => __world;` property
            // resolve. Aspect machinery additionally needs Query/ComponentBuffer/SetTag/UnsetTag/Set;
            // job machinery needs the *ForJob accessors that return tuples (the buffer +
            // its current count for jobs that read the count).
            public class WorldAccessor
            {
                public QueryBuilder Query() => default;
                public ComponentBufferAccessor<T> ComponentBuffer<T>(GroupIndex group)
                    where T : unmanaged, IEntityComponent => default;

                // Set gateway: emitted aspect/system code uses `Set<TSet>().DeferredAdd(...)` etc.
                // Real impl lives at com.trecs.core/Scripts/Sets/SetAccessor.cs.
                public SetAccessor<TSet> Set<TSet>() where TSet : struct, IEntitySet => default;

                // Aspect-emitted SetTag/UnsetTag verbs route through EntityIndex-based overloads.
                // Real implementations live as extension methods in
                // com.trecs.core/Scripts/Internal/WorldAccessorInternalExtensions.cs.
                public void SetTag<T>(EntityIndex entityIndex) where T : struct, ITag { }
                public void UnsetTag<T>(EntityIndex entityIndex) where T : struct, ITag { }

                // Per-group EntityHandle buffer fetched by jobs that declare a
                // `NativeEntityHandleBuffer` [FromWorld] field. Real impl lives in
                // com.trecs.core/Scripts/Internal/JobGenSchedulingExtensions.cs.
                public NativeEntityHandleBuffer GetEntityHandleBufferForJob(GroupIndex group) => default;

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

                // Set-related job scheduling helpers. Real impls are extension methods in
                // com.trecs.core/Scripts/Internal/JobGenSchedulingExtensions.cs.
                public NativeSetCommandBuffer<TSet> CreateNativeSetCommandBufferForJob<TSet>()
                    where TSet : struct, IEntitySet => default;
                public Unity.Jobs.JobHandle IncludeNativeSetCommandBufferDepsForJob<TSet>(
                    Unity.Jobs.JobHandle deps
                ) where TSet : struct, IEntitySet => default;
                public void TrackNativeSetCommandBufferDepsForJob<TSet>(
                    Unity.Jobs.JobHandle handle
                ) where TSet : struct, IEntitySet { }

                public NativeEntitySetIndices<TSet> GetSetIndicesForJob<TSet>(GroupIndex group)
                    where TSet : struct, IEntitySet => default;

                public NativeSetRead<TSet> CreateNativeSetReadForJob<TSet>()
                    where TSet : struct, IEntitySet => default;
                public Unity.Jobs.JobHandle IncludeNativeSetReadDepsForJob<TSet>(
                    Unity.Jobs.JobHandle deps
                ) where TSet : struct, IEntitySet => default;
                public void TrackNativeSetReadDepsForJob<TSet>(
                    Unity.Jobs.JobHandle handle
                ) where TSet : struct, IEntitySet { }

                // WorldInfo gives access to world-level group/tag introspection.
                public Trecs.WorldInfo WorldInfo => default!;

                // ToNative() yields a Burst-compatible value-type view onto the same world.
                // AutoJobGenerator's ScheduleParallel shim calls this when forwarding into
                // a Burst job that takes `in NativeWorldAccessor`.
                public NativeWorldAccessor ToNative() => default;
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

            // NativeWorldAccessor — Burst-compatible value-type counterpart. Aspect SetTag/UnsetTag /
            // Set helpers also expose `(in NativeWorldAccessor)` overloads.
            public readonly struct NativeWorldAccessor
            {
                // Burst-side set gateway: emitted job code uses `Set<TSet>().DeferredAdd(...)` etc.
                // Real impl lives at com.trecs.core/Scripts/Native/NativeWorldAccessor.cs.
                public NativeSetAccessor<TSet> Set<TSet>() where TSet : struct, IEntitySet => default;

                public void SetTag<T>(EntityIndex entityIndex) where T : struct, ITag { }
                public void UnsetTag<T>(EntityIndex entityIndex) where T : struct, ITag { }
            }

            // NativeEntityHandleBuffer — per-group handle buffer materialized from
            // GetEntityHandleBufferForJob. Real type lives at
            // com.trecs.core/Scripts/DataStructures/EntityHandleBuffer.cs.
            public readonly struct NativeEntityHandleBuffer
            {
                public EntityHandle this[int index] => default;
                public int Length => 0;
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
                    MissingInputBehavior? inputFrameBehaviour,
                    bool? isConstant,
                    bool? isInterpolated,
                    T? defaultValue
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
                    TagSet[]? dimensions = null,
                    bool isAbstract = false
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

            // Mirrors Packages/com.trecs.core/Scripts/Systems/Attributes.cs.
            public enum MissingInputBehavior { Reset, Retain }

            // Mirrors heap pointer types from
            // Packages/com.trecs.core/Scripts/Heap/{NativeUniquePtr,NativeSharedPtr,
            // UniquePtr,SharedPtr,InputNativeUniquePtr,InputNativeSharedPtr,
            // InputUniquePtr,InputSharedPtr,TrecsList}.cs — only the type symbol
            // (name + namespace + single type-argument) is what TemplateValidator
            // pattern-matches on.
            public readonly struct NativeUniquePtr<T> where T : unmanaged { }
            public readonly struct NativeSharedPtr<T> where T : unmanaged { }
            public readonly struct UniquePtr<T> where T : class { }
            public readonly struct SharedPtr<T> where T : class { }
            public readonly struct InputNativeUniquePtr<T> where T : unmanaged { }
            public readonly struct InputNativeSharedPtr<T> where T : unmanaged { }
            public readonly struct InputUniquePtr<T> where T : class { }
            public readonly struct InputSharedPtr<T> where T : class { }
            public readonly struct TrecsList<T> where T : unmanaged { }

            // NativeSharedRead<T> — safety-checked read view returned by NativeSharedPtr<T>.Read(...).
            // Real type at Packages/com.trecs.core/Scripts/Heap/NativeSharedRead.cs. Stubbed here
            // so NativeSharedPtrImmutabilityAnalyzer tests can reference it.
            public readonly struct NativeSharedRead<T> where T : unmanaged { }

            // Non-generic static helper class hosting the Alloc/Acquire/TryGet/etc factories
            // for the generic NativeSharedPtr<T> struct above. The analyzer recognizes it as
            // a static factory site so a call like NativeSharedPtr.Alloc<Bad>(...) is rejected
            // even before the returned handle is stored anywhere.
            public static class NativeSharedPtr
            {
                public static NativeSharedPtr<T> Alloc<T>(in T value)
                    where T : unmanaged => default;
            }

            // Marker that gates a managed type for use behind SharedPtr<T>.
            // ImmutableAnalyzer (TRECS125/126/127) keys on attribute name + Trecs namespace.
            [System.AttributeUsage(
                System.AttributeTargets.Class | System.AttributeTargets.Interface,
                AllowMultiple = false,
                Inherited = false
            )]
            public sealed class ImmutableAttribute : System.Attribute { }

            // Suppression marker for TRECS127. ImmutableAnalyzer looks up only
            // (name, namespace).
            [System.AttributeUsage(System.AttributeTargets.Method, AllowMultiple = false, Inherited = false)]
            public sealed class AllowMutableReturnAttribute : System.Attribute
            {
            }

            // Non-generic static helper for SharedPtr<T> (mirrors Trecs.NativeSharedPtr).
            // The factories themselves don't matter to the analyzer — only that they exist
            // as generic static methods on a class named SharedPtr in the Trecs namespace.
            public static class SharedPtr
            {
                public static SharedPtr<T> Alloc<T>(T value) where T : class => default;
            }

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
            public enum SystemPhase { Input, Fixed, EarlyPresentation, Presentation, LatePresentation }
            [System.AttributeUsage(System.AttributeTargets.Class)]
            public class ExecuteInAttribute : System.Attribute
            {
                public ExecuteInAttribute(SystemPhase phase) { Phase = phase; }
                public SystemPhase Phase { get; }
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

                // AddTemplate / AddTemplates — call sites the AddAbstractTemplateAnalyzer
                // inspects to fire TRECS039. The analyzer matches by method name + the
                // `Trecs.WorldBuilder` containing type, so both overloads must exist for
                // it to recognize the call.
                public WorldBuilder AddTemplate(Template template) => this;
                public WorldBuilder AddTemplates(System.Collections.Generic.IEnumerable<Template> templates) => this;
            }
        }

        namespace Trecs.Internal
        {
            // Real signature lives at Packages/com.trecs.core/Scripts/Util/UnmanagedUtil.cs.
            // Generated equality / operator overloads in EntityComponentGenerator
            // call BlittableEquals / BlittableHashCode; bodies don't matter for compile-cleanliness tests.
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

            // TrecsDebugAssert.That — generated code (e.g. AutoSystemGenerator's World setter, aspect
            // Query DSLs) uses this for runtime invariants. Real signature lives at
            // com.trecs.core/Scripts/Util/TrecsDebugAssert.cs.
            public static class TrecsDebugAssert
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
            // by WorldAccessor.GetJobSchedulerForJob(). ResourceId / TypeId<T>
            // identify which component-on-which-group a job touches.

            public readonly struct ResourceId
            {
                public static ResourceId Component(TypeId id) => default;
                public static ResourceId Set(SetId id) => default;
            }

            public readonly struct TypeId { }

            public static class TypeId<T> where T : unmanaged, IEntityComponent
            {
                public static TypeId Value => default;
            }

            public class JobScheduler
            {
                public Unity.Jobs.JobHandle IncludeReadDep(
                    Unity.Jobs.JobHandle deps, ResourceId res, GroupIndex group
                ) => default;
                public Unity.Jobs.JobHandle IncludeWriteDep(
                    Unity.Jobs.JobHandle deps, ResourceId res, GroupIndex group
                ) => default;
                public void TrackJobRead(Unity.Jobs.JobHandle handle, ResourceId res, GroupIndex group, string name = null) { }
                public void TrackJobWrite(Unity.Jobs.JobHandle handle, ResourceId res, GroupIndex group, string name = null) { }
                public void TrackJob(Unity.Jobs.JobHandle handle, string name = null) { }

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
