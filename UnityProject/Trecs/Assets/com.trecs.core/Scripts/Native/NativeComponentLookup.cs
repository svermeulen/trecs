using System;
using System.Runtime.CompilerServices;
using Trecs.Internal;
using Unity.Burst;
using Unity.Collections;
using Unity.Collections.LowLevel.Unsafe;

namespace Trecs
{
    /// <summary>
    /// Internal storage for one (group, buffer pointer, count) entry inside a
    /// <see cref="NativeComponentLookupRead{T}"/> or <see cref="NativeComponentLookupWrite{T}"/>.
    /// </summary>
    internal unsafe struct NativeComponentLookupEntry
    {
        public GroupIndex GroupIndex;
        public void* DataPtr; // raw pointer into the underlying ComponentArray's NativeList<T>
        public int Count;
    }

    /// <summary>
    /// Burst-compatible read-only struct for looking up entity components across multiple
    /// groups by <see cref="EntityIndex"/>. Carries its own <see cref="AtomicSafetyHandle"/>
    /// so Unity's job-reflection walker can detect use-after-dispose and accidental access
    /// outside the owning job — without conflating the lookup with iteration jobs that
    /// touch the same underlying component buffers.
    /// <para>
    /// Cross-job ordering for the underlying <c>(component, group)</c> data is enforced by
    /// Trecs's <see cref="RuntimeJobScheduler"/> via per-<c>(component, group)</c>
    /// <c>IncludeReadDep</c> / <c>TrackJobRead</c> calls emitted by source-gen at schedule
    /// time. The lookup's safety handle is intentionally per-lookup-instance (not pooled)
    /// so multiple lookups for the same component type don't appear as a global conflict to
    /// Unity's walker.
    /// </para>
    /// <para>
    /// Constructed by source-generated job code (declare a
    /// <c>[FromWorld] NativeComponentLookupRead&lt;T&gt;</c> field on a Trecs job) and
    /// disposed automatically once the owning job completes. Lifetime is the same as the
    /// previous implementation; only the internal storage and safety wiring have changed.
    /// </para>
    /// </summary>
    [NativeContainer]
    [NativeContainerIsReadOnly]
    public readonly unsafe struct NativeComponentLookupRead<T> : IDisposable
        where T : unmanaged, IEntityComponent
    {
        [NativeDisableUnsafePtrRestriction]
        readonly NativeComponentLookupEntry* _entries;
        readonly int _entryCount;
        readonly Allocator _allocator;

#if ENABLE_UNITY_COLLECTIONS_CHECKS
        readonly AtomicSafetyHandle m_Safety;

        // Static safety id labels this container's handle so Unity's job debugger reports
        // errors as e.g. "NativeComponentLookupRead<CPosition>" instead of "AtomicSafetyHandle".
        // SharedStatic<int> ensures one int per generic instantiation across the AppDomain.
        static readonly SharedStatic<int> s_staticSafetyId = SharedStatic<int>.GetOrCreate<
            NativeComponentLookupRead<T>
        >();
#endif

        internal NativeComponentLookupRead(
            NativeComponentLookupEntry* entries,
            int entryCount,
            Allocator allocator
        )
        {
            _entries = entries;
            _entryCount = entryCount;
            _allocator = allocator;
#if ENABLE_UNITY_COLLECTIONS_CHECKS
            m_Safety = AtomicSafetyHandle.Create();
            AtomicSafetyHandle.SetBumpSecondaryVersionOnScheduleWrite(m_Safety, true);
            CollectionHelper.SetStaticSafetyId<NativeComponentLookupRead<T>>(
                ref m_Safety,
                ref s_staticSafetyId.Data
            );
#endif
        }

        public bool IsCreated
        {
            [MethodImpl(MethodImplOptions.AggressiveInlining)]
            get => _entries != null;
        }

        public ref readonly T this[EntityIndex entityIndex]
        {
            [MethodImpl(MethodImplOptions.AggressiveInlining)]
            get
            {
#if ENABLE_UNITY_COLLECTIONS_CHECKS
                AtomicSafetyHandle.CheckReadAndThrow(m_Safety);
#endif
                ref var entry = ref ResolveEntry(entityIndex.GroupIndex);
                Require.That(
                    entityIndex.Index < entry.Count,
                    "NativeComponentLookupRead: Entity index {} out of range (count={}) in group {}",
                    entityIndex.Index,
                    entry.Count,
                    entityIndex.GroupIndex
                );
                return ref UnsafeUtility.ArrayElementAsRef<T>(entry.DataPtr, entityIndex.Index);
            }
        }

        [MethodImpl(MethodImplOptions.AggressiveInlining)]
        internal NativeComponentBufferRead<T> GetBufferForGroupInternal(GroupIndex group)
        {
#if ENABLE_UNITY_COLLECTIONS_CHECKS
            AtomicSafetyHandle.CheckReadAndThrow(m_Safety);
#endif
            ref var entry = ref ResolveEntry(group);
            var nb = new NativeBuffer<T>((T*)entry.DataPtr, entry.Count);
#if ENABLE_UNITY_COLLECTIONS_CHECKS
            return new NativeComponentBufferRead<T>(nb, m_Safety);
#else
            return new NativeComponentBufferRead<T>(nb);
#endif
        }

        [MethodImpl(MethodImplOptions.AggressiveInlining)]
        public bool TryGet(EntityIndex entityIndex, out T value)
        {
#if ENABLE_UNITY_COLLECTIONS_CHECKS
            AtomicSafetyHandle.CheckReadAndThrow(m_Safety);
#endif
            if (TryFindEntry(entityIndex.GroupIndex, out var entryIdx))
            {
                ref var entry = ref _entries[entryIdx];
                if (entityIndex.Index < entry.Count)
                {
                    value = UnsafeUtility.ArrayElementAsRef<T>(entry.DataPtr, entityIndex.Index);
                    return true;
                }
            }

            value = default;
            return false;
        }

        [MethodImpl(MethodImplOptions.AggressiveInlining)]
        public bool Exists(EntityIndex entityIndex)
        {
#if ENABLE_UNITY_COLLECTIONS_CHECKS
            AtomicSafetyHandle.CheckReadAndThrow(m_Safety);
#endif
            return TryFindEntry(entityIndex.GroupIndex, out var entryIdx)
                && entityIndex.Index < _entries[entryIdx].Count;
        }

        [MethodImpl(MethodImplOptions.AggressiveInlining)]
        ref NativeComponentLookupEntry ResolveEntry(GroupIndex group)
        {
            if (TryFindEntry(group, out var entryIdx))
                return ref _entries[entryIdx];
            return ref ThrowGroupNotInLookup(group);
        }

        // Linear scan over entries. Acceptable because lookups in practice span a
        // handful of groups (typical 1-4) and Burst inlines this loop into the calling
        // job's hot path. If profiling ever shows this on a wide lookup
        // (~10+ groups), upgrade to a hashmap: add a NativeHashMap<int, int>
        // (groupId → entryIdx) field, populate it in BuildNativeComponentLookupEntries,
        // and dispose it alongside _entries. The change is local — no API impact.
        [MethodImpl(MethodImplOptions.AggressiveInlining)]
        bool TryFindEntry(GroupIndex group, out int entryIdx)
        {
            for (int i = 0; i < _entryCount; i++)
            {
                if (_entries[i].GroupIndex == group)
                {
                    entryIdx = i;
                    return true;
                }
            }
            entryIdx = -1;
            return false;
        }

        // Slow throw path lives in its own non-inlined helper so the inlined fast path
        // in ResolveEntry doesn't bloat every call site with the string formatting.
        // Returns a `ref` to satisfy the compiler — the body always throws so the ref
        // is never actually produced.
        //
        // Burst note: NoInlining alone doesn't stop Burst from compiling this helper,
        // and string interpolation / typeof(T).Name aren't Burst-compatible. We split
        // the rich managed exception into a [BurstDiscard] sub-helper (no-op under
        // Burst) and follow it with a literal-string InvalidOperationException that
        // Burst can compile. Managed callers throw the rich exception and never reach
        // the fallback; Burst callers throw the simpler one.
        [MethodImpl(MethodImplOptions.NoInlining)]
        static ref NativeComponentLookupEntry ThrowGroupNotInLookup(GroupIndex group)
        {
            ThrowGroupNotInLookupManaged(group);
            throw new InvalidOperationException(
                "NativeComponentLookupRead: entity's group is not in this lookup's permitted "
                    + "group set. Add the group to the lookup's [ForEachEntity] or schedule-time TagSet."
            );
        }

        [BurstDiscard]
        static void ThrowGroupNotInLookupManaged(GroupIndex group)
        {
            var containerName = $"NativeComponentLookupRead<{typeof(T).Name}>";
            throw new GroupNotInContainerException(
                containerName,
                group.GetHashCode(),
                $"{containerName}: entity belongs to {group}, "
                    + "which is not in this lookup's permitted group set. Add the group to the "
                    + "lookup's [ForEachEntity] or schedule-time TagSet."
            );
        }

        public void Dispose()
        {
            if (_entries == null)
                return;

            // EXPERIMENT: Struct is now `readonly` so we can't clear _entries after free.
            // Double-Dispose protection comes from the safety handle's CheckDeallocateAndThrow
            // (in checks-enabled builds). In release builds with no safety checks, double
            // Dispose would double-free — but the source-gen disposal pattern guarantees
            // a single Dispose per lookup so this is acceptable.
#if ENABLE_UNITY_COLLECTIONS_CHECKS
            AtomicSafetyHandle.CheckDeallocateAndThrow(m_Safety);
            AtomicSafetyHandle.Release(m_Safety);
#endif
            UnsafeUtility.FreeTracked(_entries, _allocator);
        }
    }

    /// <summary>
    /// Burst-compatible writable struct for looking up entity components across multiple
    /// groups by <see cref="EntityIndex"/>. See <see cref="NativeComponentLookupRead{T}"/> for
    /// the safety story; the only difference is that this variant uses
    /// <c>CheckWriteAndThrow</c> at access sites and exposes a writable indexer.
    /// </summary>
    [NativeContainer]
    public readonly unsafe struct NativeComponentLookupWrite<T> : IDisposable
        where T : unmanaged, IEntityComponent
    {
        [NativeDisableUnsafePtrRestriction]
        readonly NativeComponentLookupEntry* _entries;
        readonly int _entryCount;
        readonly Allocator _allocator;

#if ENABLE_UNITY_COLLECTIONS_CHECKS
        readonly AtomicSafetyHandle m_Safety;

        // See NativeComponentLookupRead.s_staticSafetyId for the rationale.
        static readonly SharedStatic<int> s_staticSafetyId = SharedStatic<int>.GetOrCreate<
            NativeComponentLookupWrite<T>
        >();
#endif

        internal NativeComponentLookupWrite(
            NativeComponentLookupEntry* entries,
            int entryCount,
            Allocator allocator
        )
        {
            _entries = entries;
            _entryCount = entryCount;
            _allocator = allocator;
#if ENABLE_UNITY_COLLECTIONS_CHECKS
            m_Safety = AtomicSafetyHandle.Create();
            AtomicSafetyHandle.SetBumpSecondaryVersionOnScheduleWrite(m_Safety, true);
            CollectionHelper.SetStaticSafetyId<NativeComponentLookupWrite<T>>(
                ref m_Safety,
                ref s_staticSafetyId.Data
            );
#endif
        }

        public bool IsCreated
        {
            [MethodImpl(MethodImplOptions.AggressiveInlining)]
            get => _entries != null;
        }

        public ref T this[EntityIndex entityIndex]
        {
            [MethodImpl(MethodImplOptions.AggressiveInlining)]
            get
            {
#if ENABLE_UNITY_COLLECTIONS_CHECKS
                AtomicSafetyHandle.CheckWriteAndThrow(m_Safety);
#endif
                ref var entry = ref ResolveEntry(entityIndex.GroupIndex);
                Require.That(
                    entityIndex.Index < entry.Count,
                    "NativeComponentLookupWrite: Entity index {} out of range (count={}) in group {}",
                    entityIndex.Index,
                    entry.Count,
                    entityIndex.GroupIndex
                );
                return ref UnsafeUtility.ArrayElementAsRef<T>(entry.DataPtr, entityIndex.Index);
            }
        }

        [MethodImpl(MethodImplOptions.AggressiveInlining)]
        internal NativeComponentBufferWrite<T> GetBufferForGroupInternal(GroupIndex group)
        {
#if ENABLE_UNITY_COLLECTIONS_CHECKS
            AtomicSafetyHandle.CheckWriteAndThrow(m_Safety);
#endif
            ref var entry = ref ResolveEntry(group);
            var nb = new NativeBuffer<T>((T*)entry.DataPtr, entry.Count);
#if ENABLE_UNITY_COLLECTIONS_CHECKS
            return new NativeComponentBufferWrite<T>(nb, m_Safety);
#else
            return new NativeComponentBufferWrite<T>(nb);
#endif
        }

        [MethodImpl(MethodImplOptions.AggressiveInlining)]
        public bool TryGet(EntityIndex entityIndex, out T value)
        {
#if ENABLE_UNITY_COLLECTIONS_CHECKS
            AtomicSafetyHandle.CheckWriteAndThrow(m_Safety);
#endif
            if (TryFindEntry(entityIndex.GroupIndex, out var entryIdx))
            {
                ref var entry = ref _entries[entryIdx];
                if (entityIndex.Index < entry.Count)
                {
                    value = UnsafeUtility.ArrayElementAsRef<T>(entry.DataPtr, entityIndex.Index);
                    return true;
                }
            }

            value = default;
            return false;
        }

        [MethodImpl(MethodImplOptions.AggressiveInlining)]
        public bool Exists(EntityIndex entityIndex)
        {
#if ENABLE_UNITY_COLLECTIONS_CHECKS
            AtomicSafetyHandle.CheckWriteAndThrow(m_Safety);
#endif
            return TryFindEntry(entityIndex.GroupIndex, out var entryIdx)
                && entityIndex.Index < _entries[entryIdx].Count;
        }

        [MethodImpl(MethodImplOptions.AggressiveInlining)]
        ref NativeComponentLookupEntry ResolveEntry(GroupIndex group)
        {
            if (TryFindEntry(group, out var entryIdx))
                return ref _entries[entryIdx];
            return ref ThrowGroupNotInLookup(group);
        }

        // See NativeComponentLookupRead.TryFindEntry for the linear-scan rationale and
        // upgrade-to-hashmap path.
        [MethodImpl(MethodImplOptions.AggressiveInlining)]
        bool TryFindEntry(GroupIndex group, out int entryIdx)
        {
            for (int i = 0; i < _entryCount; i++)
            {
                if (_entries[i].GroupIndex == group)
                {
                    entryIdx = i;
                    return true;
                }
            }
            entryIdx = -1;
            return false;
        }

        // See NativeComponentLookupRead.ThrowGroupNotInLookup for the rationale on the
        // ref-returning throw helper pattern and the [BurstDiscard] split.
        [MethodImpl(MethodImplOptions.NoInlining)]
        static ref NativeComponentLookupEntry ThrowGroupNotInLookup(GroupIndex group)
        {
            ThrowGroupNotInLookupManaged(group);
            throw new InvalidOperationException(
                "NativeComponentLookupWrite: entity's group is not in this lookup's permitted "
                    + "group set. Add the group to the lookup's [ForEachEntity] or schedule-time TagSet."
            );
        }

        [BurstDiscard]
        static void ThrowGroupNotInLookupManaged(GroupIndex group)
        {
            var containerName = $"NativeComponentLookupWrite<{typeof(T).Name}>";
            throw new GroupNotInContainerException(
                containerName,
                group.GetHashCode(),
                $"{containerName}: entity belongs to {group}, "
                    + "which is not in this lookup's permitted group set. Add the group to the "
                    + "lookup's [ForEachEntity] or schedule-time TagSet."
            );
        }

        public void Dispose()
        {
            if (_entries == null)
                return;

            // See NativeComponentLookupRead.Dispose for the rationale.
#if ENABLE_UNITY_COLLECTIONS_CHECKS
            AtomicSafetyHandle.CheckDeallocateAndThrow(m_Safety);
            AtomicSafetyHandle.Release(m_Safety);
#endif
            UnsafeUtility.FreeTracked(_entries, _allocator);
        }
    }
}
