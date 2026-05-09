using System.Runtime.CompilerServices;
using Trecs.Internal;
using Unity.Burst;
using Unity.Collections;
using Unity.Collections.LowLevel.Unsafe;

namespace Trecs
{
    /// <summary>
    /// Lightweight Burst-compatible handle for a single set's deferred add/remove/clear state.
    /// Used by <see cref="NativeWorldAccessor"/> for <see cref="NativeWorldAccessor.SetAdd{TSet}"/>,
    /// <see cref="NativeWorldAccessor.SetRemove{TSet}"/>, and <see cref="NativeWorldAccessor.SetClear{TSet}"/>.
    /// </summary>
    internal readonly unsafe struct NativeSetDeferredQueues
    {
        internal readonly AtomicNativeBags AddQueue;
        internal readonly AtomicNativeBags RemoveQueue;

        [NoAlias]
        [NativeDisableUnsafePtrRestriction]
        readonly int* _clearRequested;

        readonly AllocatorManager.AllocatorHandle _allocator;

        internal NativeSetDeferredQueues(
            AtomicNativeBags addQueue,
            AtomicNativeBags removeQueue,
            AllocatorManager.AllocatorHandle allocator
        )
        {
            AddQueue = addQueue;
            RemoveQueue = removeQueue;
            _allocator = allocator;
            _clearRequested = (int*)UnsafeUtility.Malloc(sizeof(int), 4, allocator.ToAllocator);
            *_clearRequested = 0;
        }

        /// <summary>
        /// Mark this set's deferred clear flag. Multiple writers race-write the
        /// same value (1) so no atomic is required — last writer wins, all
        /// writers want the same outcome.
        /// </summary>
        [MethodImpl(MethodImplOptions.AggressiveInlining)]
        internal readonly void RequestClear()
        {
            *_clearRequested = 1;
        }

        /// <summary>
        /// Read and reset the clear flag. Called from the main thread during
        /// submission, after all jobs have completed.
        /// </summary>
        [MethodImpl(MethodImplOptions.AggressiveInlining)]
        internal readonly bool ConsumeClearRequest()
        {
            if (*_clearRequested == 0)
                return false;
            *_clearRequested = 0;
            return true;
        }

        internal readonly void Dispose()
        {
            AddQueue.Dispose();
            RemoveQueue.Dispose();
            UnsafeUtility.Free(_clearRequested, _allocator.ToAllocator);
        }
    }
}
