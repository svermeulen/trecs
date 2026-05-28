using System;
using System.Diagnostics;
using System.Runtime.CompilerServices;
using Trecs.Internal;
using Unity.Collections;
using Unity.Collections.LowLevel.Unsafe;

namespace Trecs
{
    [Flags]
    internal enum NativeWorldAccessorFlags : byte
    {
        None = 0,

        // Permission to mutate deterministic simulation state — structural changes
        // (Add/Remove/Move entity, set ops) and heap mutation (Alloc/Write/Set/
        // Clone/Acquire/Dispose). Set on Fixed-role and Unrestricted-role accessors;
        // cleared on Variable-role (which includes input-system) accessors. Both
        // gates collapse into one flag because they share the same role check —
        // there is no role that can do one but not the other.
        AllowSimulationMutation = 1 << 0,
    }

    // Bundles the four world-init-built handles the AddEntity fast path needs.
    // Constructor-only passthrough — NativeWorldAccessor splits these back into
    // individual fields so Unity's job-safety attributes ([ReadOnly] /
    // [NativeDisableUnsafePtrRestriction]) attach where they need to.
    internal readonly unsafe struct FastAddNativeInfo
    {
        public readonly PerGroupAddBags Bags;
        public readonly NativeHashMap<int, GroupIndex> TagSetToGroup;
        public readonly NativeTemplateLayoutHeader* LayoutHeadersPtr;
        public readonly NativeComponentLayoutEntry* LayoutEntriesPtr;
        public readonly UnsafeHashMap<long, int> TypeIdToCi;

        public FastAddNativeInfo(
            PerGroupAddBags bags,
            NativeHashMap<int, GroupIndex> tagSetToGroup,
            NativeTemplateLayoutHeader* layoutHeadersPtr,
            NativeComponentLayoutEntry* layoutEntriesPtr,
            UnsafeHashMap<long, int> typeIdToCi
        )
        {
            Bags = bags;
            TagSetToGroup = tagSetToGroup;
            LayoutHeadersPtr = layoutHeadersPtr;
            LayoutEntriesPtr = layoutEntriesPtr;
            TypeIdToCi = typeIdToCi;
        }
    }

    /// <summary>
    /// Burst-compatible struct providing all non-generic ECS operations for use in parallel jobs.
    /// This is the native counterpart to <see cref="WorldAccessor"/> — obtain one via
    /// <see cref="WorldAccessor.ToNative"/>.
    ///
    /// Bundles entity lifecycle operations (add/remove/move), set mutations, entity
    /// reference resolution, and shared pointer resolution into a single struct.
    /// The thread index required for parallel-safe enqueueing is managed internally
    /// via [NativeSetThreadIndex].
    /// <para>
    /// <b>Thread Safety:</b> NativeWorldAccessor is <b>job-safe</b>. All methods can be called
    /// from parallel jobs. Operations are buffered with sort keys to ensure deterministic
    /// ordering when multiple jobs write concurrently. Buffered operations are applied during
    /// the next <c>Submit()</c> call on the main thread.
    /// </para>
    /// </summary>
    public struct NativeWorldAccessor
    {
        readonly AtomicNativeBags _moveQueue;
        readonly AtomicNativeBags _removeQueue;
        readonly int _accessorId;

        readonly EntityHandleMap _entityIds;
        readonly NativeWorldAccessorFlags _flags;
        readonly NativeSharedPtrResolver _sharedPtrResolver;
        readonly InputNativeSharedPtrResolver _inputSharedPtrResolver;
        readonly NativeHeapResolver _chunkStoreResolver;

        [NativeDisableContainerSafetyRestriction]
        readonly NativeHashMap<SetId, NativeSetDeferredQueues> _deferredQueues;

        // Fast-path AddEntity infrastructure. Build-once-at-world-init, read-only on the
        // hot path. _fastAddBags holds the (thread, group) staging slots that the
        // returned NativeEntityInitializer writes into. The native pointers into the
        // layout arrays are taken once on the main thread in WorldAccessor.ToNative and
        // captured here so Burst code can read offsets without going through NativeArray's
        // safety machinery.
        readonly PerGroupAddBags _fastAddBags;

        [ReadOnly]
        readonly NativeHashMap<int, GroupIndex> _tagSetToGroup;

        [NativeDisableUnsafePtrRestriction]
        readonly unsafe NativeTemplateLayoutHeader* _layoutHeadersPtr;

        [NativeDisableUnsafePtrRestriction]
        readonly unsafe NativeComponentLayoutEntry* _layoutEntriesPtr;

        // Composite-keyed (groupIndex << 32 | typeIdValue -> ci) map for the
        // Burst-friendly AddEntity Set<T> hot path. Lets each .Set call do a
        // single hashmap lookup instead of linear-scanning the group's
        // _layoutEntriesPtr slice, which matters when callers .Set many (or
        // every) component on wide templates.
        [ReadOnly]
        readonly UnsafeHashMap<long, int> _typeIdToCi;

        /// <summary>
        /// The time step for the current phase (fixed or variable), in seconds.
        /// Populated by <see cref="WorldAccessor.ToNative"/>.
        /// Will be <see cref="float.NaN"/> in fixed-phase jobs when
        /// <see cref="WorldSettings.AssertNoTimeInFixedPhase"/> is enabled.
        /// </summary>
        public readonly float DeltaTime;

        /// <summary>
        /// The total elapsed simulation time for the current phase, in seconds.
        /// Populated by <see cref="WorldAccessor.ToNative"/>.
        /// Will be <see cref="float.NaN"/> in fixed-phase jobs when
        /// <see cref="WorldSettings.AssertNoTimeInFixedPhase"/> is enabled.
        /// </summary>
        public readonly float ElapsedTime;

#pragma warning disable 649
        [NativeSetThreadIndex]
        int _threadIndex;
#pragma warning restore 649

        /// <summary>
        /// Provides read-only shared pointer resolution for use in Burst jobs.
        /// Only resolves entries flushed to the native heap (not pending adds from the current frame).
        /// </summary>
        internal NativeSharedPtrResolver SharedPtrResolver => _sharedPtrResolver;

        internal InputNativeSharedPtrResolver InputSharedPtrResolver => _inputSharedPtrResolver;

        /// <summary>
        /// Job-safe resolver for both <see cref="NativeUniquePtr{T}"/> and
        /// <see cref="TrecsList{T}"/> dereferences. The per-allocation TypeId tag on the
        /// shared <see cref="NativeHeap"/> distinguishes which heap owns each slot,
        /// so a single resolver covers every native-heap pointer type.
        /// </summary>
        public NativeHeapResolver ChunkStoreResolver => _chunkStoreResolver;

        internal unsafe NativeWorldAccessor(
            AtomicNativeBags moveQueue,
            AtomicNativeBags removeQueue,
            int accessorId,
            EntityHandleMap entityIds,
            NativeWorldAccessorFlags flags,
            NativeSharedPtrResolver sharedPtrResolver,
            InputNativeSharedPtrResolver inputSharedPtrResolver,
            NativeHeapResolver chunkStoreResolver,
            NativeHashMap<SetId, NativeSetDeferredQueues> deferredQueues,
            FastAddNativeInfo fastAdd,
            float deltaTime,
            float elapsedTime
        )
        {
            _moveQueue = moveQueue;
            _removeQueue = removeQueue;
            _accessorId = accessorId;
            _entityIds = entityIds;
            _flags = flags;
            _sharedPtrResolver = sharedPtrResolver;
            _inputSharedPtrResolver = inputSharedPtrResolver;
            _chunkStoreResolver = chunkStoreResolver;
            _deferredQueues = deferredQueues;
            _fastAddBags = fastAdd.Bags;
            _tagSetToGroup = fastAdd.TagSetToGroup;
            _layoutHeadersPtr = fastAdd.LayoutHeadersPtr;
            _layoutEntriesPtr = fastAdd.LayoutEntriesPtr;
            _typeIdToCi = fastAdd.TypeIdToCi;
            _threadIndex = 0;
            DeltaTime = deltaTime;
            ElapsedTime = elapsedTime;
        }

        // ── Entity Add ──────────────────────────────────────────────
        //
        // Resolves TagSet → GroupIndex natively (no managed bounce), reserves a
        // fixed-size slot in a per-(thread, group) staging buffer, writes the
        // slot header (entity handle, sortKey, accessorId, setMask=0), and
        // returns a NativeEntityInitializer whose .Set<T> writes each component
        // at its template-defined offset within the slot's component-bytes
        // region. Drained by the parallel-fill pipeline in EntitySubmitter.

        /// <summary>
        /// Schedule an entity add with a pre-built TagSet using a pre-reserved EntityHandle.
        /// Obtain handles via <see cref="WorldAccessor.ReserveEntityHandles"/> on the main thread
        /// before scheduling the job.
        /// </summary>
        public readonly unsafe NativeEntityInitializer AddEntity(
            TagSet tags,
            uint sortKey,
            EntityHandle reservedRef
        )
        {
            AssertStructuralChangesAllowed();

            if (!_tagSetToGroup.TryGetValue(tags.Id, out var groupIndex))
            {
                throw new TrecsException(
                    "AddEntity: TagSet does not resolve to a unique registered group"
                );
            }

            int gIdx = groupIndex.Index;
            var layoutHeader = _layoutHeadersPtr[gIdx];

            byte* slotPtr = _fastAddBags.AppendSlot(_threadIndex, gIdx);

            var hdr = (FastAddSlotHeader*)slotPtr;
            hdr->ReservedRef = reservedRef;
            hdr->SortKey = sortKey;
            hdr->AccessorId = _accessorId;
            hdr->SetMask = default;

            byte* componentBytes = slotPtr + sizeof(FastAddSlotHeader);
            return new NativeEntityInitializer(
                componentBytes,
                &hdr->SetMask,
                _layoutEntriesPtr,
                layoutHeader.FirstComponentIndex,
                _typeIdToCi,
                (long)gIdx << 32,
                reservedRef
            );
        }

        // Generic-tag AddEntity overloads. Route through TagSet.BurstableFromTags
        // rather than TagSet<T...>.Value — the latter's `static readonly` initializer
        // gets AOT-eval'd by Burst and fails BC0101 when it walks into the assert in
        // Tag<T>.Value's getter before managed Init has populated _value. See
        // TagSet.BurstableFromTags for the contract.

        [MethodImpl(MethodImplOptions.AggressiveInlining)]
        public readonly NativeEntityInitializer AddEntity<T1>(
            uint sortKey,
            EntityHandle reservedRef
        )
            where T1 : struct, ITag =>
            AddEntity(TagSet.BurstableFromTags<T1>(), sortKey, reservedRef);

        [MethodImpl(MethodImplOptions.AggressiveInlining)]
        public readonly NativeEntityInitializer AddEntity<T1, T2>(
            uint sortKey,
            EntityHandle reservedRef
        )
            where T1 : struct, ITag
            where T2 : struct, ITag =>
            AddEntity(TagSet.BurstableFromTags<T1, T2>(), sortKey, reservedRef);

        [MethodImpl(MethodImplOptions.AggressiveInlining)]
        public readonly NativeEntityInitializer AddEntity<T1, T2, T3>(
            uint sortKey,
            EntityHandle reservedRef
        )
            where T1 : struct, ITag
            where T2 : struct, ITag
            where T3 : struct, ITag =>
            AddEntity(TagSet.BurstableFromTags<T1, T2, T3>(), sortKey, reservedRef);

        [MethodImpl(MethodImplOptions.AggressiveInlining)]
        public readonly NativeEntityInitializer AddEntity<T1, T2, T3, T4>(
            uint sortKey,
            EntityHandle reservedRef
        )
            where T1 : struct, ITag
            where T2 : struct, ITag
            where T3 : struct, ITag
            where T4 : struct, ITag =>
            AddEntity(TagSet.BurstableFromTags<T1, T2, T3, T4>(), sortKey, reservedRef);

        /// <summary>
        /// Fast-path fire-and-forget add. No pre-reserved EntityHandle — the
        /// submitter claims one on the main thread after the sort runs, so the
        /// assigned id follows deterministic sort-key order rather than bag-
        /// thread arrival. The returned initializer has no .Handle property
        /// since the handle isn't known yet at the call site.
        /// </summary>
        public readonly unsafe NativeAnonymousEntityInitializer AddEntity(TagSet tags, uint sortKey)
        {
            AssertStructuralChangesAllowed();

            if (!_tagSetToGroup.TryGetValue(tags.Id, out var groupIndex))
            {
                throw new TrecsException(
                    "AddEntity: TagSet does not resolve to a unique registered group"
                );
            }

            int gIdx = groupIndex.Index;
            var layoutHeader = _layoutHeadersPtr[gIdx];

            byte* slotPtr = _fastAddBags.AppendSlot(_threadIndex, gIdx);

            var hdr = (FastAddSlotHeader*)slotPtr;
            hdr->ReservedRef = EntityHandle.Null;
            hdr->SortKey = sortKey;
            hdr->AccessorId = _accessorId;
            hdr->SetMask = default;

            byte* componentBytes = slotPtr + sizeof(FastAddSlotHeader);
            return new NativeAnonymousEntityInitializer(
                componentBytes,
                &hdr->SetMask,
                _layoutEntriesPtr,
                layoutHeader.FirstComponentIndex,
                _typeIdToCi,
                (long)gIdx << 32
            );
        }

        // Generic-tag fire-and-forget overloads. Same Burst-safety reasoning as
        // the pre-reserved variants above.

        [MethodImpl(MethodImplOptions.AggressiveInlining)]
        public readonly NativeAnonymousEntityInitializer AddEntity<T1>(uint sortKey)
            where T1 : struct, ITag => AddEntity(TagSet.BurstableFromTags<T1>(), sortKey);

        [MethodImpl(MethodImplOptions.AggressiveInlining)]
        public readonly NativeAnonymousEntityInitializer AddEntity<T1, T2>(uint sortKey)
            where T1 : struct, ITag
            where T2 : struct, ITag => AddEntity(TagSet.BurstableFromTags<T1, T2>(), sortKey);

        [MethodImpl(MethodImplOptions.AggressiveInlining)]
        public readonly NativeAnonymousEntityInitializer AddEntity<T1, T2, T3>(uint sortKey)
            where T1 : struct, ITag
            where T2 : struct, ITag
            where T3 : struct, ITag => AddEntity(TagSet.BurstableFromTags<T1, T2, T3>(), sortKey);

        [MethodImpl(MethodImplOptions.AggressiveInlining)]
        public readonly NativeAnonymousEntityInitializer AddEntity<T1, T2, T3, T4>(uint sortKey)
            where T1 : struct, ITag
            where T2 : struct, ITag
            where T3 : struct, ITag
            where T4 : struct, ITag =>
            AddEntity(TagSet.BurstableFromTags<T1, T2, T3, T4>(), sortKey);

        [Conditional("DEBUG")]
        readonly void AssertStructuralChangesAllowed()
        {
            TrecsDebugAssert.That(
                (_flags & NativeWorldAccessorFlags.AllowSimulationMutation) != 0,
                "Attempted structural change (add/remove/move) from a non-fixed context. "
                    + "Structural changes are only allowed from Fixed-role and "
                    + "Unrestricted-role accessors."
            );
        }

        /// <summary>
        /// Burst-job-side gate for heap mutation (TrecsList Write, NativeUniquePtr
        /// Write, etc. through a resolver). Shares the same role check as
        /// <c>AssertStructuralChangesAllowed</c> — both are restricted to
        /// Fixed-role and Unrestricted-role accessors.
        /// </summary>
        [Conditional("DEBUG")]
        internal readonly void AssertCanMutateHeap()
        {
            TrecsDebugAssert.That(
                (_flags & NativeWorldAccessorFlags.AllowSimulationMutation) != 0,
                "Attempted heap mutation (Write / Set / Clone / Dispose) on a heap "
                    + "pointer from a non-fixed Burst job. Heap mutation is only "
                    + "allowed from Fixed-role and Unrestricted-role accessors."
            );
        }

        // ── Entity Remove ───────────────────────────────────────────

        [MethodImpl(MethodImplOptions.AggressiveInlining)]
        internal readonly void RemoveEntity(EntityIndex entityIndex)
        {
            AssertStructuralChangesAllowed();
            TrecsDebugAssert.That(entityIndex != EntityIndex.Null);

            var simpleNativeBag = _removeQueue.GetBag(_threadIndex);
            simpleNativeBag.Enqueue(_accessorId);
            simpleNativeBag.Enqueue(entityIndex);
        }

        [MethodImpl(MethodImplOptions.AggressiveInlining)]
        internal readonly void RemoveEntity(EntityHandle entityHandle)
        {
            RemoveEntity(GetEntityIndex(entityHandle));
        }

        // ── Entity Tag Changes ──────────────────────────────────────

        /// <summary>
        /// Burst-side equivalent of <see cref="EntityIndex.SetTag{T}(in NativeWorldAccessor)"/>.
        /// Enqueues a single NativeTagOp; the submitter resolves the destination
        /// partition (using the source group's template dimensions) on the main thread.
        /// </summary>
        [MethodImpl(MethodImplOptions.AggressiveInlining)]
        internal readonly void SetTag<T>(EntityIndex entityIndex)
            where T : struct, ITag
        {
            AssertStructuralChangesAllowed();
            _moveQueue
                .GetBag(_threadIndex)
                .Enqueue(
                    new NativeTagOp
                    {
                        AccessorId = _accessorId,
                        EntityIndex = entityIndex,
                        TagId = TypeId<T>.Value.Value,
                        IsSet = true,
                    }
                );
        }

        [MethodImpl(MethodImplOptions.AggressiveInlining)]
        internal readonly void SetTag<T>(EntityHandle entityHandle)
            where T : struct, ITag => SetTag<T>(GetEntityIndex(entityHandle));

        /// <summary>
        /// Burst-side equivalent of <see cref="EntityIndex.UnsetTag{T}(in NativeWorldAccessor)"/>.
        /// </summary>
        [MethodImpl(MethodImplOptions.AggressiveInlining)]
        internal readonly void UnsetTag<T>(EntityIndex entityIndex)
            where T : struct, ITag
        {
            AssertStructuralChangesAllowed();
            _moveQueue
                .GetBag(_threadIndex)
                .Enqueue(
                    new NativeTagOp
                    {
                        AccessorId = _accessorId,
                        EntityIndex = entityIndex,
                        TagId = TypeId<T>.Value.Value,
                        IsSet = false,
                    }
                );
        }

        [MethodImpl(MethodImplOptions.AggressiveInlining)]
        internal readonly void UnsetTag<T>(EntityHandle entityHandle)
            where T : struct, ITag => UnsetTag<T>(GetEntityIndex(entityHandle));

        // ── Entity Reference Resolution ─────────────────────────────

        [MethodImpl(MethodImplOptions.AggressiveInlining)]
        internal readonly bool TryGetEntityIndex(
            EntityHandle entityHandle,
            out EntityIndex entityIndex
        ) => _entityIds.TryGetEntityIndex(entityHandle, out entityIndex);

        [MethodImpl(MethodImplOptions.AggressiveInlining)]
        internal readonly EntityIndex GetEntityIndex(EntityHandle entityHandle) =>
            _entityIds.GetEntityIndex(entityHandle);

        [MethodImpl(MethodImplOptions.AggressiveInlining)]
        internal readonly EntityHandle GetEntityHandle(EntityIndex entityIndex) =>
            _entityIds.GetEntityHandle(entityIndex);

        [MethodImpl(MethodImplOptions.AggressiveInlining)]
        internal readonly bool EntityExists(EntityHandle entityHandle) =>
            _entityIds.TryGetEntityIndex(entityHandle, out _);

        // ── Deferred Set Operations ─────────────────────────────────

        /// <summary>
        /// Returns a Burst-compatible gateway for an entity set. Only
        /// <c>DeferredAdd</c> / <c>DeferredRemove</c> / <c>DeferredClear</c>
        /// are exposed — Burst can't sync, so there's no <c>.Read</c> /
        /// <c>.Write</c> counterpart to <see cref="SetAccessor{T}"/>.
        /// Operations are buffered and applied at the next
        /// <c>Submit()</c> call on the main thread.
        /// </summary>
        [MethodImpl(MethodImplOptions.AggressiveInlining)]
        public NativeSetAccessor<TSet> Set<TSet>()
            where TSet : struct, IEntitySet
        {
            return new NativeSetAccessor<TSet>(this);
        }

        [MethodImpl(MethodImplOptions.AggressiveInlining)]
        internal void DeferredSetAdd<TSet>(EntityIndex entityIndex)
            where TSet : struct, IEntitySet
        {
            AssertStructuralChangesAllowed();
            // Writes to slot `_threadIndex`; for worker threads this is always
            // non-zero, so it never aliases the main-thread slot 0 used by
            // SetAccessor<T>.DeferredAdd. When this runs via .Run() or inlined
            // on the main thread, `_threadIndex == 0` — but then the main
            // thread is blocked inside the job, so the write is sequenced with
            // surrounding main-thread DeferredAdd calls. See
            // NativeSetDeferredQueues for the full slot-layout invariant.
            _deferredQueues[SetId<TSet>.Value]
                .AddQueue.GetBag(_threadIndex)
                .Enqueue(entityIndex);
        }

        [MethodImpl(MethodImplOptions.AggressiveInlining)]
        internal void DeferredSetAdd<TSet>(EntityHandle entityHandle)
            where TSet : struct, IEntitySet
        {
            DeferredSetAdd<TSet>(GetEntityIndex(entityHandle));
        }

        [MethodImpl(MethodImplOptions.AggressiveInlining)]
        internal void DeferredSetRemove<TSet>(EntityIndex entityIndex)
            where TSet : struct, IEntitySet
        {
            AssertStructuralChangesAllowed();
            // See DeferredSetAdd above and NativeSetDeferredQueues for the
            // slot-layout invariant that makes shared use of slot 0 safe.
            _deferredQueues[SetId<TSet>.Value]
                .RemoveQueue.GetBag(_threadIndex)
                .Enqueue(entityIndex);
        }

        [MethodImpl(MethodImplOptions.AggressiveInlining)]
        internal void DeferredSetRemove<TSet>(EntityHandle entityHandle)
            where TSet : struct, IEntitySet
        {
            DeferredSetRemove<TSet>(GetEntityIndex(entityHandle));
        }

        [MethodImpl(MethodImplOptions.AggressiveInlining)]
        internal void DeferredSetClear<TSet>()
            where TSet : struct, IEntitySet
        {
            AssertStructuralChangesAllowed();
            _deferredQueues[SetId<TSet>.Value].RequestClear();
        }
    }
}
