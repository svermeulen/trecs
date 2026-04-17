using System;
using System.Collections.Generic;
using System.ComponentModel;
using System.Runtime.CompilerServices;
using Unity.Collections.LowLevel.Unsafe;

namespace Trecs.Internal
{
    /// <summary>
    /// Owns the pool of <see cref="AtomicSafetyHandle"/> instances Trecs hands out to its custom
    /// <c>[NativeContainer]</c> wrapper structs (such as <c>NativeComponentBufferRead/Write&lt;T&gt;</c>,
    /// <c>NativeComponentLookupRead/Write&lt;T&gt;</c>).
    /// <para>
    /// Mirrors the design of Unity ECS's <c>EntityDataAccess.DependencyManager.Safety</c> pool.
    /// One read handle and one write handle are kept per <c>(ResourceId, Group)</c> pair, lazily
    /// created the first time a job touches that pair. All wrapper structs that target the same
    /// <c>(resource, group)</c> share the same handle so that Unity's safety walker can detect
    /// cross-job conflicts at schedule time.
    /// </para>
    /// <para>
    /// The Read/Write split mirrors the same split <see cref="RuntimeJobScheduler"/> uses
    /// internally and allows concurrent readers without false-positive conflicts on writers.
    /// </para>
    /// <para>
    /// The entire class is a no-op outside of <c>ENABLE_UNITY_COLLECTIONS_CHECKS</c> — handles
    /// returned in release player builds are <c>default</c> and any safety check using them is
    /// stripped at the call site via <c>[Conditional]</c>.
    /// </para>
    /// </summary>
    [EditorBrowsable(EditorBrowsableState.Never)]
    public sealed class WorldSafetyManager : IDisposable
    {
#if ENABLE_UNITY_COLLECTIONS_CHECKS
        readonly Dictionary<long, AtomicSafetyHandle> _handles = new();
#endif
        bool _disposed;

#if ENABLE_UNITY_COLLECTIONS_CHECKS
        /// <summary>
        /// Get (or lazily create) the safety handle for the given <c>(resource, group)</c>.
        /// A single handle is shared by all containers (read and write) targeting the same
        /// pair, matching Unity's native-collection design. This allows Unity's job walker
        /// to detect cross-job conflicts (concurrent read+write or write+write) on the
        /// same data.
        /// </summary>
        [MethodImpl(MethodImplOptions.AggressiveInlining)]
        public AtomicSafetyHandle GetReadHandle(ResourceId resource, Group group)
        {
            return GetHandle(resource, group);
        }

        /// <inheritdoc cref="GetReadHandle"/>
        [MethodImpl(MethodImplOptions.AggressiveInlining)]
        public AtomicSafetyHandle GetWriteHandle(ResourceId resource, Group group)
        {
            return GetHandle(resource, group);
        }

        [MethodImpl(MethodImplOptions.AggressiveInlining)]
        AtomicSafetyHandle GetHandle(ResourceId resource, Group group)
        {
            Assert.That(!_disposed, "WorldSafetyManager is disposed");
            var key = MakeKey(resource, group);
            if (!_handles.TryGetValue(key, out var handle))
            {
                handle = AtomicSafetyHandle.Create();
                AtomicSafetyHandle.SetBumpSecondaryVersionOnScheduleWrite(handle, true);
                _handles[key] = handle;
            }
            return handle;
        }
#endif

        /// <summary>
        /// Drain all outstanding jobs that hold any of the pooled handles, then release the
        /// handles. Must be called before world teardown.
        /// </summary>
        public void Dispose()
        {
            if (_disposed)
                return;
            _disposed = true;

#if ENABLE_UNITY_COLLECTIONS_CHECKS
            Exception firstFailure = null;

            try
            {
                foreach (var handle in _handles.Values)
                {
                    try
                    {
                        AtomicSafetyHandle.EnforceAllBufferJobsHaveCompletedAndRelease(handle);
                    }
                    catch (Exception e)
                    {
                        firstFailure ??= e;
                    }
                }
            }
            finally
            {
                _handles.Clear();
            }

            if (firstFailure != null)
                throw firstFailure;
#endif
        }

        // Same packing scheme as RuntimeJobScheduler.MakeKey so that the two stay in lockstep.
        [MethodImpl(MethodImplOptions.AggressiveInlining)]
        static long MakeKey(ResourceId resource, Group group)
        {
            return ((long)resource.Value << 32) | (uint)group.Id;
        }
    }
}
