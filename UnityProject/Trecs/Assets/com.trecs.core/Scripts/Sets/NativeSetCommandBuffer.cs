using System.Runtime.CompilerServices;
using Trecs.Internal;
using Unity.Collections.LowLevel.Unsafe;

namespace Trecs
{
    /// <summary>
    /// Thread-safe command buffer for queuing add/remove of entities to/from a set
    /// from within parallel jobs. Uses per-thread bags to avoid contention — each
    /// worker thread enqueues to its own bag. Pending writes are flushed to the
    /// actual set by a <see cref="SetFlushJob"/> scheduled immediately after the
    /// writer job completes — so writes from this buffer take effect later in the
    /// same frame, before any reader job that depends on the set.
    ///
    /// Tracked as a <b>write dependency</b> by the job scheduler.
    /// </summary>
    public struct NativeSetCommandBuffer<TSet>
        where TSet : struct, IEntitySet
    {
        AtomicNativeBags _addQueue;
        AtomicNativeBags _removeQueue;

#pragma warning disable 649
        [NativeSetThreadIndex]
        int _threadIndex;
#pragma warning restore 649

        internal NativeSetCommandBuffer(AtomicNativeBags addQueue, AtomicNativeBags removeQueue)
        {
            _addQueue = addQueue;
            _removeQueue = removeQueue;
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
    }
}
