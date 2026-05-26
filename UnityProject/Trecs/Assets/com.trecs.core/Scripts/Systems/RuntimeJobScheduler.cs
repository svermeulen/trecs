using System;
using System.Collections.Generic;
using System.ComponentModel;
using System.Diagnostics;
using System.Runtime.CompilerServices;
using Trecs.Collections;
using Unity.Jobs;
#if TRECS_IS_PROFILING
using Unity.Jobs.LowLevel.Unsafe;
using Unity.Profiling.LowLevel.Unsafe;
#endif

namespace Trecs.Internal
{
    /// <summary>
    /// Tracks outstanding jobs and their resource access patterns at runtime.
    /// Computes job input dependencies and syncs main-thread access automatically.
    ///
    /// Tracks per (resource, group) pair for precise dependency resolution.
    /// Resources can be component types or sets.
    /// A job writing CPosition for Fish entities does not block reading CPosition for Meal entities.
    ///
    /// <b>Thread safety:</b> this type is main-thread only. All dictionaries and lists
    /// are unsynchronized; concurrent access from a job thread or worker will corrupt state.
    /// Every mutating method asserts it's running on the main thread in debug builds.
    /// </summary>
    [EditorBrowsable(EditorBrowsableState.Never)]
    public sealed class RuntimeJobScheduler
    {
        // Bundle of (handle, name) stored everywhere we used to store just JobHandle.
        // Name is the logical job identifier used for the per-job "Wait:" profiling
        // span inside CompleteAllOutstanding. May be null; null names skip the span.
        readonly struct JobEntry
        {
            public readonly JobHandle Handle;
            public readonly string Name;

            public JobEntry(JobHandle handle, string name)
            {
                Handle = handle;
                Name = name;
            }
        }

#if TRECS_IS_PROFILING
        // Per-job worker-execution timing buffer attached to a handle by
        // source-generated auto-job code via RegisterJobTimings. Separate from the
        // writer/reader/untracked buckets so that auto-jobs registering the same
        // handle under multiple (resource, group) keys still get a single
        // unambiguous timing entry.
        //
        // Buffer layout (per thread, 3 longs each), values written from inside the
        // Burst-compiled Execute shim using ProfilerUnsafeUtility.Timestamp ticks:
        //     [thread*3 + 0] = first timestamp seen by that worker, 0 if untouched
        //     [thread*3 + 1] = last  timestamp seen by that worker
        //     [thread*3 + 2] = sum of (end-start) deltas across that worker's batches
        // Sized to JobsUtility.MaxJobThreadCount * 3 longs.
        readonly struct JobTimingEntry
        {
            public readonly string Name;
            public readonly NativeArray<long> Timings;

            public JobTimingEntry(string name, NativeArray<long> timings)
            {
                Name = name;
                Timings = timings;
            }
        }

        readonly IterableDictionary<JobHandle, JobTimingEntry> _jobTimingsByHandle = new();
#endif

        // Key: composite of (ResourceId, GroupIndex) packed into a long.
        // Writer: the last outstanding job that writes this (resource, group) pair.
        // Readers: outstanding jobs that read (but don't write) this (resource, group) pair.
        readonly IterableDictionary<long, JobEntry> _writers = new();
        readonly IterableDictionary<long, List<JobEntry>> _readers = new();

        // Jobs without specific component access (e.g. NativeWorldAccessor spawn jobs).
        // Just need to complete at phase boundary.
        readonly List<JobEntry> _untrackedJobs = new();

        // Disposables (typically TempJob-allocated lookups created by source-generated
        // job scheduling code) that must be disposed once their owning jobs have
        // finished. Flushed synchronously at the end of CompleteAllOutstanding.
        // Synchronous disposal sidesteps Unity's "nested native containers are illegal
        // in jobs" restriction that NativeList.Dispose(JobHandle) hits when the list's
        // element type itself contains a native container (e.g. NativeComponentLookupEntry
        // wraps a NativeBuffer).
        readonly List<IDisposable> _pendingDisposes = new();

        // Pool of reader lists to avoid allocation
        readonly Stack<List<JobEntry>> _listPool = new();

#if TRECS_IS_PROFILING
        // Pool of per-job timing buffers. Each buffer is a NativeArray<long> sized
        // JobsUtility.MaxJobThreadCount * 3 (see JobTimingEntry doc for layout).
        // Rent zero-fills before returning to the caller; Return parks the array
        // for reuse. Buffers are Persistent-allocated so they survive across
        // schedule/complete cycles.
        readonly Stack<NativeArray<long>> _timingBufferPool = new();
#endif

        // Reused across CompleteAllOutstanding calls to dedupe the same JobHandle
        // appearing under multiple (resource, group) keys, so each unique job's wait
        // is profiled and Complete()-d exactly once. A single scheduled job is
        // tracked once per (resource, group) pair its aspect touches — e.g. a job
        // that reads CPosition and writes CVelocity lands in both _readers and
        // _writers; one writing CPosition across Fish and Meal groups lands under
        // two writer keys — so the same handle naturally shows up in multiple
        // buckets here.
        readonly HashSet<JobHandle> _completedThisCall = new();

        int _outstandingJobCount;

        public bool HasOutstandingJobs
        {
            [MethodImpl(MethodImplOptions.AggressiveInlining)]
            get => _outstandingJobCount > 0;
        }

        [MethodImpl(MethodImplOptions.AggressiveInlining)]
        static long MakeKey(ResourceId resourceType, GroupIndex group)
        {
            return ((long)resourceType.Value << 32) | (uint)group.GetHashCode();
        }

        [Conditional("DEBUG")]
        static void AssertMainThread()
        {
            TrecsDebugAssert.That(
                UnityThreadHelper.IsMainThread,
                "RuntimeJobScheduler is main-thread only"
            );
        }

        /// <summary>
        /// Include the dependency for reading a (resource, group) pair in a job.
        /// A job that reads resource R in group G must wait for the outstanding writer of (R, G).
        /// </summary>
        [MethodImpl(MethodImplOptions.AggressiveInlining)]
        public JobHandle IncludeReadDep(
            JobHandle baseDeps,
            ResourceId resourceType,
            GroupIndex group
        )
        {
            var key = MakeKey(resourceType, group);

            if (_writers.TryGetValue(key, out var writer))
            {
                return JobHandle.CombineDependencies(baseDeps, writer.Handle);
            }

            return baseDeps;
        }

        /// <summary>
        /// Include the dependency for writing a (resource, group) pair in a job.
        /// A job that writes resource R in group G must wait for the outstanding writer AND all readers of (R, G).
        /// </summary>
        public JobHandle IncludeWriteDep(
            JobHandle baseDeps,
            ResourceId resourceType,
            GroupIndex group
        )
        {
            var key = MakeKey(resourceType, group);

            if (_writers.TryGetValue(key, out var writer))
            {
                baseDeps = JobHandle.CombineDependencies(baseDeps, writer.Handle);
            }

            if (_readers.TryGetValue(key, out var readerList))
            {
                for (int i = 0; i < readerList.Count; i++)
                {
                    baseDeps = JobHandle.CombineDependencies(baseDeps, readerList[i].Handle);
                }
            }

            return baseDeps;
        }

        /// <summary>
        /// Register a scheduled job as a reader of a (resource, group) pair. Main thread only.
        /// <paramref name="name"/> is a short identifier (e.g. <c>"IdleBobSystem.BobAspectJob"</c>)
        /// used as the per-job profiling span label inside <see cref="CompleteAllOutstanding"/>.
        /// May be null; null names skip the per-job span.
        /// </summary>
        [MethodImpl(MethodImplOptions.AggressiveInlining)]
        public void TrackJobRead(
            JobHandle handle,
            ResourceId resourceType,
            GroupIndex group,
            string name = null
        )
        {
            AssertMainThread();
            var key = MakeKey(resourceType, group);

            if (!_readers.TryGetValue(key, out var readerList))
            {
                readerList = RentList();
                _readers[key] = readerList;
            }

            readerList.Add(new JobEntry(handle, name));
            _outstandingJobCount++;
        }

        /// <summary>
        /// Register a scheduled job as a writer of a (resource, group) pair.
        /// Replaces any previous writer and clears readers for this pair. Main thread only.
        /// See <see cref="TrackJobRead"/> for the <paramref name="name"/> contract.
        /// </summary>
        public void TrackJobWrite(
            JobHandle handle,
            ResourceId resourceType,
            GroupIndex group,
            string name = null
        )
        {
            AssertMainThread();
            var key = MakeKey(resourceType, group);

            if (_readers.TryGetValue(key, out var readerList))
            {
                _outstandingJobCount -= readerList.Count;
                readerList.Clear();
                ReturnList(readerList);
                _readers.TryRemove(key);
            }

            if (!_writers.ContainsKey(key))
            {
                _outstandingJobCount++;
            }

            _writers[key] = new JobEntry(handle, name);
        }

        /// <summary>
        /// Ensure it is safe to READ a (resource, group) pair on the main thread.
        /// Only completes outstanding writers for this pair — concurrent readers are safe.
        /// Returns true if any jobs were actually completed (a sync point occurred). Main thread only.
        /// </summary>
        public bool SyncMainThreadForRead(ResourceId resourceType, GroupIndex group)
        {
            AssertMainThread();
            var key = MakeKey(resourceType, group);

            if (_writers.TryGetValue(key, out var writer))
            {
                CompleteWithSpan(writer);
                _writers.TryRemove(key);
                _outstandingJobCount--;
                return true;
            }

            return false;
        }

        /// <summary>
        /// Ensure it is safe to WRITE a (resource, group) pair on the main thread.
        /// Completes outstanding writer and all readers for this pair.
        /// Returns true if any jobs were actually completed (a sync point occurred). Main thread only.
        /// </summary>
        public bool SyncMainThread(ResourceId resourceType, GroupIndex group)
        {
            AssertMainThread();
            var key = MakeKey(resourceType, group);
            bool synced = false;

            if (_writers.TryGetValue(key, out var writer))
            {
                CompleteWithSpan(writer);
                _writers.TryRemove(key);
                _outstandingJobCount--;
                synced = true;
            }

            if (_readers.TryGetValue(key, out var readerList))
            {
                for (int i = 0; i < readerList.Count; i++)
                {
                    CompleteWithSpan(readerList[i]);
                }

                _outstandingJobCount -= readerList.Count;
                readerList.Clear();
                ReturnList(readerList);
                _readers.TryRemove(key);
                synced = true;
            }

            return synced;
        }

        /// <summary>
        /// Track a job handle that doesn't have specific component access patterns.
        /// Used for jobs that perform structural operations (e.g. NativeWorldAccessor)
        /// or other work that just needs to complete at the next phase boundary. Main thread only.
        /// See <see cref="TrackJobRead"/> for the <paramref name="name"/> contract.
        /// </summary>
        [MethodImpl(MethodImplOptions.AggressiveInlining)]
        public void TrackJob(JobHandle handle, string name = null)
        {
            AssertMainThread();
            _untrackedJobs.Add(new JobEntry(handle, name));
            _outstandingJobCount++;
        }

#if TRECS_IS_PROFILING
        /// <summary>
        /// Attach a per-worker timing buffer to a scheduled job handle. The buffer
        /// is decoded by <see cref="CompleteAllOutstanding"/> after the handle
        /// completes; the resulting wall-clock and total-CPU worker-execution
        /// timings are forwarded to the profiler via
        /// <see cref="TrecsProfiling.RecordWorkerJob"/>. The buffer is returned
        /// to the internal pool by the same call.
        /// <para>
        /// Source-generated auto-job scheduling code calls this exactly once per
        /// scheduled handle, in addition to its <c>TrackJobRead/Write/Job</c>
        /// dependency registrations. Calling more than once for the same handle
        /// overwrites the prior entry (and leaks the prior buffer). Main thread only.
        /// </para>
        /// </summary>
        [EditorBrowsable(EditorBrowsableState.Never)]
        [MethodImpl(MethodImplOptions.AggressiveInlining)]
        public void RegisterJobTimings(JobHandle handle, string name, NativeArray<long> timings)
        {
            AssertMainThread();
            // Collision = caller is reusing the same handle for two timing buffers
            // in one phase; the prior buffer would be silently overwritten and leak.
            // Source-gen emits exactly one call per scheduled handle, so any
            // collision is a bug.
            TrecsDebugAssert.That(
                !_jobTimingsByHandle.ContainsKey(handle),
                "RegisterJobTimings called twice for the same JobHandle; the prior "
                    + "timing buffer would leak"
            );
            _jobTimingsByHandle[handle] = new JobTimingEntry(name, timings);
        }
#endif

        /// <summary>
        /// Register a disposable (typically a TempJob-allocated NativeComponentLookup
        /// produced by source-generated job code) for synchronous disposal at the next
        /// <see cref="CompleteAllOutstanding"/> phase boundary, AFTER all outstanding
        /// jobs have completed. Use this for native containers whose layout makes
        /// <c>NativeList.Dispose(JobHandle)</c> illegal due to nested-container rules.
        /// Main thread only.
        /// </summary>
        [MethodImpl(MethodImplOptions.AggressiveInlining)]
        public void RegisterPendingDispose(IDisposable disposable)
        {
            AssertMainThread();
            _pendingDisposes.Add(disposable);
        }

        /// <summary>
        /// Complete all outstanding jobs and clear tracking state.
        /// Called at phase boundaries. Main thread only.
        /// <para>
        /// Per-job profiling: each unique JobHandle is timed via a <c>"Wait: {name}"</c>
        /// child span. The same handle can be tracked under multiple (resource, group)
        /// keys for one Schedule call, so we dedupe via <see cref="_completedThisCall"/>;
        /// only the first completion in this call gets the wait span (the rest are no-op
        /// completes on already-completed handles).
        /// </para>
        /// <para>
        /// <b>Attribution caveat:</b> <c>JobHandle.Complete()</c> waits for the handle's
        /// transitive dependencies too. Whichever handle in a dependency chain we
        /// complete first absorbs the upstream wait — its <c>Wait:</c> span will look
        /// inflated relative to its own on-thread work. Spans on later-completed
        /// handles in the same chain show clean wait times.
        /// </para>
        /// </summary>
        public void CompleteAllOutstanding()
        {
            AssertMainThread();
            _completedThisCall.Clear();

            using (TrecsProfiling.Start("Complete Writers"))
            {
                foreach (var kvp in _writers)
                {
                    CompleteOnceWithSpan(kvp.Value);
                }

                _writers.Clear();
            }

            using (TrecsProfiling.Start("Complete Readers"))
            {
                foreach (var kvp in _readers)
                {
                    for (int i = 0; i < kvp.Value.Count; i++)
                    {
                        CompleteOnceWithSpan(kvp.Value[i]);
                    }

                    kvp.Value.Clear();
                    ReturnList(kvp.Value);
                }

                _readers.Clear();
            }

            using (TrecsProfiling.Start("Complete Untracked"))
            {
                for (int i = 0; i < _untrackedJobs.Count; i++)
                {
                    CompleteOnceWithSpan(_untrackedJobs[i]);
                }

                _untrackedJobs.Clear();
            }

#if TRECS_IS_PROFILING
            // Per-job worker-execution timings: decode each timing buffer, publish
            // wall-clock + total-CPU ms to the profiler, return buffer to the pool.
            if (_jobTimingsByHandle.Count > 0)
            {
                PublishWorkerTimings();
            }
#endif

            // Synchronous dispose runs AFTER all jobs have completed above so that
            // disposing the underlying native containers is safe (no in-flight readers
            // or writers).
            using (TrecsProfiling.Start("Pending Disposes"))
            {
                for (int i = 0; i < _pendingDisposes.Count; i++)
                {
                    _pendingDisposes[i].Dispose();
                }

                _pendingDisposes.Clear();
            }

            _outstandingJobCount = 0;
        }

#if TRECS_IS_PROFILING
        // Drain _jobTimingsByHandle, decoding each per-worker timing buffer into
        // (wallClockMs, totalCpuMs) and forwarding to the profiler. Returns each
        // buffer to the pool. Called once near the end of CompleteAllOutstanding,
        // after all handles have been Complete()-d (so worker writes are visible).
        void PublishWorkerTimings()
        {
            var ratio = ProfilerUnsafeUtility.TimestampToNanosecondsConversionRatio;
            int threadCount = JobsUtility.MaxJobThreadCount;

            foreach (var kvp in _jobTimingsByHandle)
            {
                var entry = kvp.Value;
                var timings = entry.Timings;
                if (!timings.IsCreated)
                {
                    continue;
                }

                long minStart = long.MaxValue;
                long maxEnd = long.MinValue;
                long totalCpuTicks = 0;
                bool anyTouched = false;
                for (int t = 0; t < threadCount; t++)
                {
                    long first = timings[t * 3];
                    if (first == 0)
                    {
                        continue;
                    }
                    long last = timings[t * 3 + 1];
                    long cpu = timings[t * 3 + 2];
                    if (first < minStart)
                        minStart = first;
                    if (last > maxEnd)
                        maxEnd = last;
                    totalCpuTicks += cpu;
                    anyTouched = true;
                }

                if (anyTouched)
                {
                    // ticks → nanoseconds → milliseconds. Matches Unity DOTS's own
                    // StructuralChangesProfiler.GetElapsedNanoseconds: multiply by
                    // Numerator then divide by Denominator. The 64-bit headroom is
                    // ample for Trecs job durations — at typical timestamp rates an
                    // overflow would require a single job to occupy workers for
                    // many seconds, which a per-fixed-tick job will never do.
                    long wallNs = (maxEnd - minStart) * ratio.Numerator / ratio.Denominator;
                    long cpuNs = totalCpuTicks * ratio.Numerator / ratio.Denominator;
                    float wallMs = wallNs / 1_000_000f;
                    float cpuMs = cpuNs / 1_000_000f;
                    TrecsProfiling.RecordWorkerJob(entry.Name, wallMs, cpuMs);
                }

                ReturnTimingBuffer(timings);
            }

            _jobTimingsByHandle.Clear();
        }
#endif

        // Complete a single entry on a non-CompleteAllOutstanding sync path (no
        // cross-bucket dedup needed). Adds the per-job span if name is non-null.
        [MethodImpl(MethodImplOptions.AggressiveInlining)]
        static void CompleteWithSpan(JobEntry entry)
        {
            if (entry.Name == null)
            {
                entry.Handle.Complete();
                return;
            }

            using (TrecsProfiling.Start("Wait: {0}", entry.Name))
            {
                entry.Handle.Complete();
            }
        }

        // Complete an entry during CompleteAllOutstanding, deduping across the
        // writer/reader/untracked buckets via _completedThisCall. Already-completed
        // handles skip both the span and the (no-op) Complete() call.
        [MethodImpl(MethodImplOptions.AggressiveInlining)]
        void CompleteOnceWithSpan(JobEntry entry)
        {
            if (!_completedThisCall.Add(entry.Handle))
            {
                return;
            }

            CompleteWithSpan(entry);
        }

        List<JobEntry> RentList()
        {
            if (_listPool.Count > 0)
            {
                return _listPool.Pop();
            }

            return new List<JobEntry>(4);
        }

        void ReturnList(List<JobEntry> list)
        {
            _listPool.Push(list);
        }

#if TRECS_IS_PROFILING
        /// <summary>
        /// Rent a zero-filled per-job timing buffer. Source-generated auto-job
        /// scheduling code assigns this to the job struct's timing field before
        /// scheduling, then passes the same array to the matching
        /// <c>TrackJob*</c> overload. Returned to the pool by
        /// <c>CompleteAllOutstanding</c> after the job's worker timings have been
        /// published. Main thread only.
        /// </summary>
        [EditorBrowsable(EditorBrowsableState.Never)]
        public NativeArray<long> RentTimingBuffer()
        {
            AssertMainThread();

            if (_timingBufferPool.Count > 0)
            {
                var buf = _timingBufferPool.Pop();
                // Zero so the per-worker "first start, 0 if untouched" sentinel is honest.
                for (int i = 0; i < buf.Length; i++)
                {
                    buf[i] = 0;
                }
                return buf;
            }

            return new NativeArray<long>(
                JobsUtility.MaxJobThreadCount * 3,
                Allocator.Persistent,
                NativeArrayOptions.ClearMemory
            );
        }

        void ReturnTimingBuffer(NativeArray<long> buffer)
        {
            if (!buffer.IsCreated)
            {
                return;
            }
            _timingBufferPool.Push(buffer);
        }

        /// <summary>
        /// Dispose all pooled native timing buffers. Called when the world is torn
        /// down. Main thread only.
        /// </summary>
        public void DisposeTimingBuffers()
        {
            AssertMainThread();
            while (_timingBufferPool.Count > 0)
            {
                var buf = _timingBufferPool.Pop();
                if (buf.IsCreated)
                {
                    buf.Dispose();
                }
            }
        }
#endif
    }
}
