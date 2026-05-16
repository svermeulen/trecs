using System.Runtime.CompilerServices;
using Trecs.Internal;
using Unity.Burst;
using Unity.Collections.LowLevel.Unsafe;

namespace Trecs
{
    /// <summary>
    /// Thread-safe command buffer for queuing add/remove/clear of entities to/from
    /// a set from within parallel jobs. Uses per-thread bags to avoid contention —
    /// each worker thread enqueues to its own bag. Pending writes are flushed to
    /// the actual set by a <see cref="SetFlushJob"/> scheduled immediately after
    /// the writer job completes — so writes from this buffer take effect later in
    /// the same frame, before any reader job that depends on the set.
    /// <para>
    /// A queued <see cref="Clear"/> supersedes any <see cref="Add"/> / <see cref="Remove"/>
    /// from the same writer-job-cycle, and also wipes the set's pre-existing contents,
    /// regardless of call order — analogous to the deferred-clear semantics on
    /// <see cref="SetAccessor{T}.DeferredClear"/> / <see cref="SetWrite{T}.Clear"/>.
    /// </para>
    /// Tracked as a <b>write dependency</b> by the job scheduler.
    /// </summary>
    public unsafe struct NativeSetCommandBuffer<TSet>
        where TSet : struct, IEntitySet
    {
        AtomicNativeBags _addQueue;
        AtomicNativeBags _removeQueue;

        // Race-written from threads in the same parallel job; consumed and reset
        // by the SetFlushJob that runs after this writer.
        [NoAlias]
        [NativeDisableUnsafePtrRestriction]
        readonly int* _clearRequested;

#pragma warning disable 649
        [NativeSetThreadIndex]
        int _threadIndex;
#pragma warning restore 649

        internal NativeSetCommandBuffer(
            AtomicNativeBags addQueue,
            AtomicNativeBags removeQueue,
            int* clearRequested
        )
        {
            _addQueue = addQueue;
            _removeQueue = removeQueue;
            _clearRequested = clearRequested;
            _threadIndex = 0;
        }

        /// <summary>
        /// Queue an entity to be added to the set.
        /// Lock-free — safe to call from any job thread.
        /// Writes are flushed immediately after the writer job completes.
        /// </summary>
        [MethodImpl(MethodImplOptions.AggressiveInlining)]
        public void Add(EntityIndex entityIndex)
        {
            var bag = _addQueue.GetBag(_threadIndex);
            bag.Enqueue(entityIndex);
        }

        /// <summary>
        /// Queue an entity (by stable handle) to be added to the set. Resolves the
        /// handle to an <see cref="EntityIndex"/> via the supplied accessor, then
        /// enqueues. Lock-free.
        /// </summary>
        [MethodImpl(MethodImplOptions.AggressiveInlining)]
        public void Add(EntityHandle entityHandle, in NativeWorldAccessor world)
        {
            Add(entityHandle.ToIndex(world));
        }

        /// <summary>
        /// Queue an entity to be removed from the set.
        /// Lock-free — safe to call from any job thread.
        /// Writes are flushed immediately after the writer job completes.
        /// </summary>
        [MethodImpl(MethodImplOptions.AggressiveInlining)]
        public void Remove(EntityIndex entityIndex)
        {
            var bag = _removeQueue.GetBag(_threadIndex);
            bag.Enqueue(entityIndex);
        }

        /// <summary>
        /// Queue an entity (by stable handle) to be removed from the set. Resolves
        /// the handle to an <see cref="EntityIndex"/> via the supplied accessor, then
        /// enqueues. Lock-free.
        /// </summary>
        [MethodImpl(MethodImplOptions.AggressiveInlining)]
        public void Remove(EntityHandle entityHandle, in NativeWorldAccessor world)
        {
            Remove(entityHandle.ToIndex(world));
        }

        /// <summary>
        /// Queue a clear of the entire set. Multiple writers race-write the same
        /// value (1) so no atomic is required — last writer wins, all writers
        /// want the same outcome. At flush time, supersedes any queued
        /// <see cref="Add"/> / <see cref="Remove"/> from this writer-job-cycle
        /// regardless of call order, and also wipes the set's pre-existing contents.
        /// </summary>
        [MethodImpl(MethodImplOptions.AggressiveInlining)]
        public void Clear()
        {
            *_clearRequested = 1;
        }
    }
}
