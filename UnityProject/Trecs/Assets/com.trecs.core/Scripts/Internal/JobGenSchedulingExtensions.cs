using System.ComponentModel;
using System.Runtime.CompilerServices;
using Trecs.Collections;
using Unity.Collections;
using Unity.Jobs;

namespace Trecs.Internal
{
    /// <summary>
    /// Opaque, read-only view of an array of dense entity indices used by the sparse
    /// iteration shim that <see cref="JobGenerator"/> emits. Wraps a
    /// <see cref="NativeArray{T}"/> internally so source-generated job code can declare
    /// a <c>[FromWorld]</c> equivalent field of this type without needing the user
    /// assembly to reference <c>Unity.Collections</c> directly.
    /// <para>
    /// User code should never construct or read this — only the source-generator
    /// emission for sparse <c>(SparseQueryBuilder)</c> schedule overloads does.
    /// </para>
    /// </summary>
    [EditorBrowsable(EditorBrowsableState.Never)]
    public struct JobSparseIndices
    {
        // Fully qualified to disambiguate from System.ComponentModel.ReadOnlyAttribute
        // (the using-directive at the top of this file pulls that one in alongside
        // Unity.Collections, leading to CS0104 if we just write [ReadOnly]).
        [Unity.Collections.ReadOnly]
        internal NativeArray<int> _array;

        public int Length
        {
            [MethodImpl(MethodImplOptions.AggressiveInlining)]
            get => _array.Length;
        }

        public int this[int i]
        {
            [MethodImpl(MethodImplOptions.AggressiveInlining)]
            get => _array[i];
        }
    }

    /// <summary>
    /// Owns the backing storage for a <see cref="JobSparseIndices"/>. The pre-walk
    /// helper allocates a TempJob <see cref="NativeList{T}"/> and hands its
    /// <see cref="NativeList{T}.AsArray"/> view to the shim's
    /// <see cref="JobSparseIndices"/> field; this lifetime token must be disposed
    /// (chained off the scheduled job's <see cref="JobHandle"/>) so the underlying
    /// memory is released after the job completes.
    /// </summary>
    [EditorBrowsable(EditorBrowsableState.Never)]
    public struct JobSparseIndicesLifetime
    {
        internal NativeList<int> _list;

        public JobHandle Dispose(JobHandle handle) => _list.Dispose(handle);

        public void Dispose() => _list.Dispose();
    }

    /// <summary>
    /// Public extension methods that are intended for use exclusively by source-generated
    /// job code (<see cref="JobGenerator"/>'s emission). They thinly wrap internal Trecs
    /// scheduling APIs so that generated job code can run in user assemblies without
    /// requiring <c>[InternalsVisibleTo]</c> friend access.
    /// <para>
    /// User code should never call these directly — use the generated
    /// <c>ScheduleParallel(WorldAccessor)</c> / <c>ScheduleParallel(QueryBuilder)</c>
    /// member methods on a job struct instead.
    /// </para>
    /// </summary>
    [EditorBrowsable(EditorBrowsableState.Never)]
    public static class JobGenSchedulingExtensions
    {
        [EditorBrowsable(EditorBrowsableState.Never)]
        public static RuntimeJobScheduler GetJobSchedulerForJob(this WorldAccessor world) =>
            world.JobScheduler;

        [EditorBrowsable(EditorBrowsableState.Never)]
        public static (NativeComponentBufferRead<T> buffer, int count) GetBufferReadForJob<T>(
            this WorldAccessor world,
            Group group
        )
            where T : unmanaged, IEntityComponent => world.GetBufferReadForJobScheduling<T>(group);

        [EditorBrowsable(EditorBrowsableState.Never)]
        public static (NativeComponentBufferWrite<T> buffer, int count) GetBufferWriteForJob<T>(
            this WorldAccessor world,
            Group group
        )
            where T : unmanaged, IEntityComponent => world.GetBufferWriteForJobScheduling<T>(group);

        [EditorBrowsable(EditorBrowsableState.Never)]
        public static NativeEntityHandleBuffer GetEntityHandleBufferForJob(
            this WorldAccessor world,
            Group group
        ) => world.GetEntityHandleBufferForJobScheduling(group);

        [EditorBrowsable(EditorBrowsableState.Never)]
        public static NativeComponentRead<T> GetNativeComponentReadForJob<T>(
            this WorldAccessor world,
            EntityIndex entityIndex
        )
            where T : unmanaged, IEntityComponent => world.GetComponentRead<T>(entityIndex);

        [EditorBrowsable(EditorBrowsableState.Never)]
        public static NativeComponentWrite<T> GetNativeComponentWriteForJob<T>(
            this WorldAccessor world,
            EntityIndex entityIndex
        )
            where T : unmanaged, IEntityComponent => world.GetComponentWrite<T>(entityIndex);

        [EditorBrowsable(EditorBrowsableState.Never)]
        public static NativeComponentLookupRead<T> CreateNativeComponentLookupReadForJob<T>(
            this WorldAccessor world,
            ReadOnlyFastList<Group> groups,
            Allocator allocator
        )
            where T : unmanaged, IEntityComponent =>
            world.CreateNativeComponentLookupRead<T>(groups, allocator);

        [EditorBrowsable(EditorBrowsableState.Never)]
        public static NativeComponentLookupWrite<T> CreateNativeComponentLookupWriteForJob<T>(
            this WorldAccessor world,
            ReadOnlyFastList<Group> groups,
            Allocator allocator
        )
            where T : unmanaged, IEntityComponent =>
            world.CreateNativeComponentLookupWrite<T>(groups, allocator);

        // ── NativeFactory helpers (buffer extraction from lookups) ──

        [EditorBrowsable(EditorBrowsableState.Never)]
        public static NativeComponentBufferRead<T> GetBufferForGroupForJob<T>(
            this NativeComponentLookupRead<T> lookup,
            Group group
        )
            where T : unmanaged, IEntityComponent => lookup.GetBufferForGroupInternal(group);

        [EditorBrowsable(EditorBrowsableState.Never)]
        public static NativeComponentBufferWrite<T> GetBufferForGroupForJob<T>(
            this NativeComponentLookupWrite<T> lookup,
            Group group
        )
            where T : unmanaged, IEntityComponent => lookup.GetBufferForGroupInternal(group);

        // ── NativeSetWrite (immediate set writes, flushed by SetFlushJob) ──

        [EditorBrowsable(EditorBrowsableState.Never)]
        public static NativeSetWrite<TSet> CreateNativeSetWriteForJob<TSet>(
            this WorldAccessor world
        )
            where TSet : struct, IEntitySet =>
            world.GetSetForJobScheduling<TSet>().CreateWriter<TSet>();

        [EditorBrowsable(EditorBrowsableState.Never)]
        public static JobHandle IncludeNativeSetWriteDepsForJob<TSet>(
            this WorldAccessor world,
            JobHandle deps
        )
            where TSet : struct, IEntitySet
        {
            var rid = ResourceId.Set(EntitySet<TSet>.Value.Id);
            var scheduler = world.JobScheduler;
            ref var collection = ref world.GetSetForJobScheduling<TSet>();
            foreach (var entry in collection._entriesPerGroup)
                deps = scheduler.IncludeWriteDep(deps, rid, entry.Key);
            return deps;
        }

        [EditorBrowsable(EditorBrowsableState.Never)]
        public static void TrackNativeSetWriteDepsForJob<TSet>(
            this WorldAccessor world,
            JobHandle handle
        )
            where TSet : struct, IEntitySet
        {
            var rid = ResourceId.Set(EntitySet<TSet>.Value.Id);
            var scheduler = world.JobScheduler;
            ref var collection = ref world.GetSetForJobScheduling<TSet>();

            var flushJob = new SetFlushJob
            {
                AddQueue = collection._jobAddQueue,
                RemoveQueue = collection._jobRemoveQueue,
                EntriesPerGroup = collection._entriesPerGroup,
                RequireDeterministic = world.RequireDeterministicSubmission,
            };
            var flushHandle = flushJob.Schedule(handle);

            foreach (var entry in collection._entriesPerGroup)
                scheduler.TrackJobWrite(flushHandle, rid, entry.Key);
        }

        // ── NativeEntitySetIndices (read-only set indices for one group) ──

        [EditorBrowsable(EditorBrowsableState.Never)]
        public static NativeEntitySetIndices<TSet> GetSetIndicesForJob<TSet>(
            this WorldAccessor world,
            Group group
        )
            where TSet : struct, IEntitySet
        {
            ref var collection = ref world.GetSetForJobScheduling<TSet>();
            if (collection._entriesPerGroup.TryGetValue(group, out var groupEntry))
            {
                var indices = groupEntry.Indices;
                return new NativeEntitySetIndices<TSet>(indices.Buffer, indices.Count);
            }
            return default;
        }

        // ── NativeSetRead (read + deferred set ops) ──

        [EditorBrowsable(EditorBrowsableState.Never)]
        public static NativeSetRead<TSet> CreateNativeSetReadForJob<TSet>(this WorldAccessor world)
            where TSet : struct, IEntitySet
        {
            ref var collection = ref world.GetSetForJobScheduling<TSet>();
            return new NativeSetRead<TSet>(collection);
        }

        [EditorBrowsable(EditorBrowsableState.Never)]
        public static JobHandle IncludeNativeSetReadDepsForJob<TSet>(
            this WorldAccessor world,
            JobHandle deps
        )
            where TSet : struct, IEntitySet
        {
            var setId = EntitySet<TSet>.Value.Id;
            var rid = ResourceId.Set(setId);
            var scheduler = world.JobScheduler;
            ref var collection = ref world.GetSetForJobScheduling(setId);
            foreach (var entry in collection._entriesPerGroup)
                deps = scheduler.IncludeReadDep(deps, rid, entry.Key);
            return deps;
        }

        [EditorBrowsable(EditorBrowsableState.Never)]
        public static void TrackNativeSetReadDepsForJob<TSet>(
            this WorldAccessor world,
            JobHandle handle
        )
            where TSet : struct, IEntitySet
        {
            var setId = EntitySet<TSet>.Value.Id;
            var rid = ResourceId.Set(setId);
            var scheduler = world.JobScheduler;
            ref var collection = ref world.GetSetForJobScheduling(setId);
            foreach (var entry in collection._entriesPerGroup)
                scheduler.TrackJobRead(handle, rid, entry.Key);
        }

        // ── Sparse iteration helpers ──

        [EditorBrowsable(EditorBrowsableState.Never)]
        public static (
            JobSparseIndices indices,
            JobSparseIndicesLifetime lifetime,
            int count
        ) AllocateSparseIndicesForJob(this WorldAccessor world, SparseGroupSlice slice)
        {
            var list = new NativeList<int>(64, Allocator.TempJob);
            foreach (var idx in slice.Indices)
                list.Add(idx);
            var indices = new JobSparseIndices { _array = list.AsArray() };
            var lifetime = new JobSparseIndicesLifetime { _list = list };
            return (indices, lifetime, list.Length);
        }
    }
}
