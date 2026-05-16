using System.Runtime.CompilerServices;
using Trecs.Internal;
using Unity.Burst;
using Unity.Collections;
using Unity.Collections.LowLevel.Unsafe;

namespace Trecs
{
    /// <summary>
    /// Lightweight Burst-compatible handle for a single set's deferred add/remove/clear state.
    /// Used by the main-thread <see cref="SetAccessor{T}"/> and the Burst-side
    /// <see cref="NativeSetAccessor{T}"/> for queueing add/remove/clear ops.
    /// <para>
    /// <b>AddQueue / RemoveQueue slot layout:</b> these per-set <see cref="AtomicNativeBags"/>
    /// are written from two paths:
    /// <list type="bullet">
    ///   <item><description>The main-thread <see cref="SetAccessor{T}.DeferredAdd(EntityIndex)"/> /
    ///     <see cref="SetAccessor{T}.DeferredRemove(EntityIndex)"/> path always writes to slot 0.</description></item>
    ///   <item><description>The job-side <see cref="NativeSetAccessor{T}.DeferredAdd(EntityIndex)"/> /
    ///     <see cref="NativeSetAccessor{T}.DeferredRemove(EntityIndex)"/> path writes to
    ///     <c>_threadIndex</c>, which is populated by <c>[NativeSetThreadIndex]</c>
    ///     and ranges over <c>[0, JobsUtility.MaxJobThreadCount)</c>.</description></item>
    /// </list>
    /// Slot 0 is therefore the legitimate main-thread writer slot. Worker threads
    /// servicing <c>Schedule()</c>-ed jobs always receive a non-zero index, so a
    /// worker-side <c>DeferredAdd</c> never aliases slot 0. A job run via <c>.Run()</c>
    /// (or inlined on the main thread when no worker is available) will resolve
    /// <c>_threadIndex == 0</c> and so write to slot 0 — but in that case the main
    /// thread is blocked inside the job, so the writes from the inline job and the
    /// surrounding main-thread <see cref="SetAccessor{T}"/> calls are serialised on a
    /// single thread, not concurrent. The bag at slot 0 therefore never needs
    /// synchronisation.
    /// </para>
    /// <para>
    /// The shared <see cref="AtomicNativeBags"/> is sized <c>MaxJobThreadCount + 1</c>;
    /// the extra slot is vestigial (inherited from Svelto and kept for the
    /// entity-add/remove/move queues' historical layout — see the archived
    /// <c>native_world_accessor_thread_index_offset.md</c> task). Only slots
    /// <c>[0, MaxJobThreadCount)</c> are reached by either writer path for the
    /// set deferred queues.
    /// </para>
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
