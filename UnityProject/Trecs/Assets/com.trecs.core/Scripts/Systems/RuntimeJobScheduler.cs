using System;
using System.Collections.Generic;
using System.ComponentModel;
using System.Diagnostics;
using System.Runtime.CompilerServices;
using Unity.Jobs;

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
    public class RuntimeJobScheduler
    {
        // Key: composite of (ResourceId, GroupIndex) packed into a long.
        // Writer: the last outstanding job that writes this (resource, group) pair.
        // Readers: outstanding jobs that read (but don't write) this (resource, group) pair.
        readonly Dictionary<long, JobHandle> _writers = new();
        readonly Dictionary<long, List<JobHandle>> _readers = new();

        // Jobs without specific component access (e.g. NativeWorldAccessor spawn jobs).
        // Just need to complete at phase boundary.
        readonly List<JobHandle> _untrackedJobs = new();

        // Disposables (typically TempJob-allocated lookups created by source-generated
        // job scheduling code) that must be disposed once their owning jobs have
        // finished. Flushed synchronously at the end of CompleteAllOutstanding.
        // Synchronous disposal sidesteps Unity's "nested native containers are illegal
        // in jobs" restriction that NativeList.Dispose(JobHandle) hits when the list's
        // element type itself contains a native container (e.g. NativeComponentLookupEntry
        // wraps a NativeBuffer).
        readonly List<IDisposable> _pendingDisposes = new();

        // Pool of reader lists to avoid allocation
        readonly Stack<List<JobHandle>> _listPool = new();

        int _outstandingJobCount;

        public bool HasOutstandingJobs
        {
            [MethodImpl(MethodImplOptions.AggressiveInlining)]
            get => _outstandingJobCount > 0;
        }

        [MethodImpl(MethodImplOptions.AggressiveInlining)]
        static long MakeKey(ResourceId resourceType, GroupIndex group)
        {
            return ((long)resourceType.Value << 32) | (uint)group.Value;
        }

        [Conditional("DEBUG")]
        static void AssertMainThread()
        {
            Assert.That(UnityThreadHelper.IsMainThread, "RuntimeJobScheduler is main-thread only");
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

            if (_writers.TryGetValue(key, out var writerHandle))
            {
                return JobHandle.CombineDependencies(baseDeps, writerHandle);
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

            if (_writers.TryGetValue(key, out var writerHandle))
            {
                baseDeps = JobHandle.CombineDependencies(baseDeps, writerHandle);
            }

            if (_readers.TryGetValue(key, out var readerList))
            {
                for (int i = 0; i < readerList.Count; i++)
                {
                    baseDeps = JobHandle.CombineDependencies(baseDeps, readerList[i]);
                }
            }

            return baseDeps;
        }

        /// <summary>
        /// Register a scheduled job as a reader of a (resource, group) pair. Main thread only.
        /// </summary>
        [MethodImpl(MethodImplOptions.AggressiveInlining)]
        public void TrackJobRead(JobHandle handle, ResourceId resourceType, GroupIndex group)
        {
            AssertMainThread();
            var key = MakeKey(resourceType, group);

            if (!_readers.TryGetValue(key, out var readerList))
            {
                readerList = RentList();
                _readers[key] = readerList;
            }

            readerList.Add(handle);
            _outstandingJobCount++;
        }

        /// <summary>
        /// Register a scheduled job as a writer of a (resource, group) pair.
        /// Replaces any previous writer and clears readers for this pair. Main thread only.
        /// </summary>
        public void TrackJobWrite(JobHandle handle, ResourceId resourceType, GroupIndex group)
        {
            AssertMainThread();
            var key = MakeKey(resourceType, group);

            if (_readers.TryGetValue(key, out var readerList))
            {
                _outstandingJobCount -= readerList.Count;
                readerList.Clear();
                ReturnList(readerList);
                _readers.Remove(key);
            }

            if (!_writers.ContainsKey(key))
            {
                _outstandingJobCount++;
            }

            _writers[key] = handle;
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

            if (_writers.TryGetValue(key, out var writerHandle))
            {
                writerHandle.Complete();
                _writers.Remove(key);
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

            if (_writers.TryGetValue(key, out var writerHandle))
            {
                writerHandle.Complete();
                _writers.Remove(key);
                _outstandingJobCount--;
                synced = true;
            }

            if (_readers.TryGetValue(key, out var readerList))
            {
                for (int i = 0; i < readerList.Count; i++)
                {
                    readerList[i].Complete();
                }

                _outstandingJobCount -= readerList.Count;
                readerList.Clear();
                ReturnList(readerList);
                _readers.Remove(key);
                synced = true;
            }

            return synced;
        }

        /// <summary>
        /// Track a job handle that doesn't have specific component access patterns.
        /// Used for jobs that perform structural operations (e.g. NativeWorldAccessor)
        /// or other work that just needs to complete at the next phase boundary. Main thread only.
        /// </summary>
        [MethodImpl(MethodImplOptions.AggressiveInlining)]
        public void TrackJob(JobHandle handle)
        {
            AssertMainThread();
            _untrackedJobs.Add(handle);
            _outstandingJobCount++;
        }

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
        /// </summary>
        public void CompleteAllOutstanding()
        {
            AssertMainThread();
            using (TrecsProfiling.Start("Complete Writers"))
            {
                foreach (var kvp in _writers)
                {
                    kvp.Value.Complete();
                }

                _writers.Clear();
            }

            using (TrecsProfiling.Start("Complete Readers"))
            {
                foreach (var kvp in _readers)
                {
                    for (int i = 0; i < kvp.Value.Count; i++)
                    {
                        kvp.Value[i].Complete();
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
                    _untrackedJobs[i].Complete();
                }

                _untrackedJobs.Clear();
            }

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

        List<JobHandle> RentList()
        {
            if (_listPool.Count > 0)
            {
                return _listPool.Pop();
            }

            return new List<JobHandle>(4);
        }

        void ReturnList(List<JobHandle> list)
        {
            _listPool.Push(list);
        }
    }
}
