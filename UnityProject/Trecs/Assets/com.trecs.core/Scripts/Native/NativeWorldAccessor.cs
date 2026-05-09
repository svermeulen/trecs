using System;
using System.Diagnostics;
using System.Runtime.CompilerServices;
using Trecs.Internal;
using Unity.Collections.LowLevel.Unsafe;

namespace Trecs
{
    [Flags]
    internal enum NativeWorldAccessorFlags : byte
    {
        None = 0,
        AllowStructuralChanges = 1 << 0,
        RequireDeterministicIds = 1 << 1,
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
    /// the next <c>SubmitEntities()</c> call on the main thread.
    /// </para>
    /// </summary>
    public struct NativeWorldAccessor
    {
        readonly AtomicNativeBags _addQueue;
        readonly AtomicNativeBags _moveQueue;
        readonly AtomicNativeBags _removeQueue;
        readonly int _accessorId;

        readonly EntityHandleMap _entityIds;
        readonly NativeWorldAccessorFlags _flags;
        readonly NativeSharedPtrResolver _sharedPtrResolver;
        readonly NativeUniquePtrResolver _uniquePtrResolver;
        readonly NativeDenseDictionary<SetId, NativeSetDeferredQueues> _deferredQueues;

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
        public NativeSharedPtrResolver SharedPtrResolver => _sharedPtrResolver;

        /// <summary>
        /// Provides native unique pointer resolution for use in Burst jobs.
        /// Only resolves entries flushed to the native heap (not pending adds from the current frame).
        /// </summary>
        public NativeUniquePtrResolver UniquePtrResolver => _uniquePtrResolver;

        internal NativeWorldAccessor(
            AtomicNativeBags addQueue,
            AtomicNativeBags moveQueue,
            AtomicNativeBags removeQueue,
            int accessorId,
            EntityHandleMap entityIds,
            NativeWorldAccessorFlags flags,
            NativeSharedPtrResolver sharedPtrResolver,
            NativeUniquePtrResolver uniquePtrResolver,
            NativeDenseDictionary<SetId, NativeSetDeferredQueues> deferredQueues,
            float deltaTime,
            float elapsedTime
        )
        {
            _addQueue = addQueue;
            _moveQueue = moveQueue;
            _removeQueue = removeQueue;
            _accessorId = accessorId;
            _entityIds = entityIds;
            _flags = flags;
            _sharedPtrResolver = sharedPtrResolver;
            _uniquePtrResolver = uniquePtrResolver;
            _deferredQueues = deferredQueues;
            _threadIndex = 0;
            DeltaTime = deltaTime;
            ElapsedTime = elapsedTime;
        }

        // ── Entity Add ──────────────────────────────────────────────

        /// <summary>
        /// Schedule an entity add with a pre-built TagSet.
        /// </summary>
        public NativeEntityInitializer AddEntity(TagSet tags, uint sortKey)
        {
            AssertStructuralChangesAllowed();
            AssertNonDeterministicAddAllowed();
            return AddEntity(tags, sortKey, _entityIds.ClaimId());
        }

        /// <summary>
        /// Schedule an entity add with a pre-built TagSet using a pre-reserved EntityHandle.
        /// </summary>
        public readonly NativeEntityInitializer AddEntity(
            TagSet tags,
            uint sortKey,
            EntityHandle reservedRef
        )
        {
            AssertStructuralChangesAllowed();
            NativeBag bag = _addQueue.GetBag(_threadIndex + 1);

            bag.Enqueue(_accessorId);
            bag.Enqueue((int)-1); // sentinel: TagSet ID follows
            bag.Enqueue(tags.Id);
            bag.Enqueue(reservedRef);
            bag.Enqueue(sortKey);
            bag.ReserveEnqueue<uint>(out var index) = 0;

            return new NativeEntityInitializer(bag, index, reservedRef);
        }

        [MethodImpl(MethodImplOptions.AggressiveInlining)]
        public readonly NativeEntityInitializer AddEntity<T1>(uint sortKey)
            where T1 : struct, ITag
        {
            AssertStructuralChangesAllowed();
            AssertNonDeterministicAddAllowed();
            return AddEntity<T1>(sortKey, _entityIds.ClaimId());
        }

        [MethodImpl(MethodImplOptions.AggressiveInlining)]
        public readonly NativeEntityInitializer AddEntity<T1>(
            uint sortKey,
            EntityHandle reservedRef
        )
            where T1 : struct, ITag
        {
            AssertStructuralChangesAllowed();
            NativeBag bag = _addQueue.GetBag(_threadIndex + 1);
            bag.Enqueue(_accessorId);
            bag.Enqueue((int)1); // tag count
            bag.Enqueue(Tag<T1>.NativeGuid);
            bag.Enqueue(reservedRef);
            bag.Enqueue(sortKey);
            bag.ReserveEnqueue<uint>(out var index) = 0;
            return new NativeEntityInitializer(bag, index, reservedRef);
        }

        [MethodImpl(MethodImplOptions.AggressiveInlining)]
        public readonly NativeEntityInitializer AddEntity<T1, T2>(uint sortKey)
            where T1 : struct, ITag
            where T2 : struct, ITag
        {
            AssertStructuralChangesAllowed();
            AssertNonDeterministicAddAllowed();
            return AddEntity<T1, T2>(sortKey, _entityIds.ClaimId());
        }

        [MethodImpl(MethodImplOptions.AggressiveInlining)]
        public readonly NativeEntityInitializer AddEntity<T1, T2>(
            uint sortKey,
            EntityHandle reservedRef
        )
            where T1 : struct, ITag
            where T2 : struct, ITag
        {
            AssertStructuralChangesAllowed();
            NativeBag bag = _addQueue.GetBag(_threadIndex + 1);
            bag.Enqueue(_accessorId);
            bag.Enqueue((int)2); // tag count
            bag.Enqueue(Tag<T1>.NativeGuid);
            bag.Enqueue(Tag<T2>.NativeGuid);
            bag.Enqueue(reservedRef);
            bag.Enqueue(sortKey);
            bag.ReserveEnqueue<uint>(out var index) = 0;
            return new NativeEntityInitializer(bag, index, reservedRef);
        }

        [MethodImpl(MethodImplOptions.AggressiveInlining)]
        public readonly NativeEntityInitializer AddEntity<T1, T2, T3>(uint sortKey)
            where T1 : struct, ITag
            where T2 : struct, ITag
            where T3 : struct, ITag
        {
            AssertStructuralChangesAllowed();
            AssertNonDeterministicAddAllowed();
            return AddEntity<T1, T2, T3>(sortKey, _entityIds.ClaimId());
        }

        [MethodImpl(MethodImplOptions.AggressiveInlining)]
        public readonly NativeEntityInitializer AddEntity<T1, T2, T3>(
            uint sortKey,
            EntityHandle reservedRef
        )
            where T1 : struct, ITag
            where T2 : struct, ITag
            where T3 : struct, ITag
        {
            AssertStructuralChangesAllowed();
            NativeBag bag = _addQueue.GetBag(_threadIndex + 1);
            bag.Enqueue(_accessorId);
            bag.Enqueue((int)3); // tag count
            bag.Enqueue(Tag<T1>.NativeGuid);
            bag.Enqueue(Tag<T2>.NativeGuid);
            bag.Enqueue(Tag<T3>.NativeGuid);
            bag.Enqueue(reservedRef);
            bag.Enqueue(sortKey);
            bag.ReserveEnqueue<uint>(out var index) = 0;
            return new NativeEntityInitializer(bag, index, reservedRef);
        }

        [MethodImpl(MethodImplOptions.AggressiveInlining)]
        public readonly NativeEntityInitializer AddEntity<T1, T2, T3, T4>(uint sortKey)
            where T1 : struct, ITag
            where T2 : struct, ITag
            where T3 : struct, ITag
            where T4 : struct, ITag
        {
            AssertStructuralChangesAllowed();
            AssertNonDeterministicAddAllowed();
            return AddEntity<T1, T2, T3, T4>(sortKey, _entityIds.ClaimId());
        }

        [MethodImpl(MethodImplOptions.AggressiveInlining)]
        public readonly NativeEntityInitializer AddEntity<T1, T2, T3, T4>(
            uint sortKey,
            EntityHandle reservedRef
        )
            where T1 : struct, ITag
            where T2 : struct, ITag
            where T3 : struct, ITag
            where T4 : struct, ITag
        {
            AssertStructuralChangesAllowed();
            NativeBag bag = _addQueue.GetBag(_threadIndex + 1);
            bag.Enqueue(_accessorId);
            bag.Enqueue((int)4); // tag count
            bag.Enqueue(Tag<T1>.NativeGuid);
            bag.Enqueue(Tag<T2>.NativeGuid);
            bag.Enqueue(Tag<T3>.NativeGuid);
            bag.Enqueue(Tag<T4>.NativeGuid);
            bag.Enqueue(reservedRef);
            bag.Enqueue(sortKey);
            bag.ReserveEnqueue<uint>(out var index) = 0;
            return new NativeEntityInitializer(bag, index, reservedRef);
        }

        [Conditional("DEBUG")]
        readonly void AssertStructuralChangesAllowed()
        {
            Assert.That(
                (_flags & NativeWorldAccessorFlags.AllowStructuralChanges) != 0,
                "Attempted structural change (add/remove/move) from a non-fixed context. "
                    + "Structural changes are only allowed from fixed systems."
            );
        }

        [Conditional("DEBUG")]
        readonly void AssertNonDeterministicAddAllowed()
        {
            Assert.That(
                (_flags & NativeWorldAccessorFlags.RequireDeterministicIds) == 0,
                "In deterministic mode, use the AddEntity overload with a pre-reserved EntityHandle. "
                    + "Call ReserveEntityHandles() on the main thread before the job."
            );
        }

        // ── Entity Remove ───────────────────────────────────────────

        [MethodImpl(MethodImplOptions.AggressiveInlining)]
        public readonly void RemoveEntity(EntityIndex entityIndex)
        {
            AssertStructuralChangesAllowed();
            Assert.That(entityIndex != EntityIndex.Null);

            var simpleNativeBag = _removeQueue.GetBag(_threadIndex);
            simpleNativeBag.Enqueue(_accessorId);
            simpleNativeBag.Enqueue(entityIndex);
        }

        [MethodImpl(MethodImplOptions.AggressiveInlining)]
        public readonly void RemoveEntity(EntityHandle entityHandle)
        {
            RemoveEntity(GetEntityIndex(entityHandle));
        }

        // ── Entity Tag Changes ──────────────────────────────────────

        /// <summary>
        /// Schedule a tag change with a pre-built TagSet.
        /// </summary>
        public readonly void MoveTo(EntityIndex entityIndex, TagSet tags)
        {
            AssertStructuralChangesAllowed();
            var bag = _moveQueue.GetBag(_threadIndex);
            bag.Enqueue(_accessorId);
            bag.Enqueue(entityIndex);
            bag.Enqueue((int)-1); // sentinel: TagSet ID follows
            bag.Enqueue(tags.Id);
        }

        [MethodImpl(MethodImplOptions.AggressiveInlining)]
        public readonly void MoveTo<T1>(EntityIndex entityIndex)
            where T1 : struct, ITag
        {
            AssertStructuralChangesAllowed();
            var bag = _moveQueue.GetBag(_threadIndex);
            bag.Enqueue(_accessorId);
            bag.Enqueue(entityIndex);
            bag.Enqueue((int)1); // tag count
            bag.Enqueue(Tag<T1>.NativeGuid);
        }

        [MethodImpl(MethodImplOptions.AggressiveInlining)]
        public readonly void MoveTo<T1, T2>(EntityIndex entityIndex)
            where T1 : struct, ITag
            where T2 : struct, ITag
        {
            AssertStructuralChangesAllowed();
            var bag = _moveQueue.GetBag(_threadIndex);
            bag.Enqueue(_accessorId);
            bag.Enqueue(entityIndex);
            bag.Enqueue((int)2); // tag count
            bag.Enqueue(Tag<T1>.NativeGuid);
            bag.Enqueue(Tag<T2>.NativeGuid);
        }

        [MethodImpl(MethodImplOptions.AggressiveInlining)]
        public readonly void MoveTo<T1, T2, T3>(EntityIndex entityIndex)
            where T1 : struct, ITag
            where T2 : struct, ITag
            where T3 : struct, ITag
        {
            AssertStructuralChangesAllowed();
            var bag = _moveQueue.GetBag(_threadIndex);
            bag.Enqueue(_accessorId);
            bag.Enqueue(entityIndex);
            bag.Enqueue((int)3); // tag count
            bag.Enqueue(Tag<T1>.NativeGuid);
            bag.Enqueue(Tag<T2>.NativeGuid);
            bag.Enqueue(Tag<T3>.NativeGuid);
        }

        [MethodImpl(MethodImplOptions.AggressiveInlining)]
        public readonly void MoveTo<T1, T2, T3, T4>(EntityIndex entityIndex)
            where T1 : struct, ITag
            where T2 : struct, ITag
            where T3 : struct, ITag
            where T4 : struct, ITag
        {
            AssertStructuralChangesAllowed();
            var bag = _moveQueue.GetBag(_threadIndex);
            bag.Enqueue(_accessorId);
            bag.Enqueue(entityIndex);
            bag.Enqueue((int)4); // tag count
            bag.Enqueue(Tag<T1>.NativeGuid);
            bag.Enqueue(Tag<T2>.NativeGuid);
            bag.Enqueue(Tag<T3>.NativeGuid);
            bag.Enqueue(Tag<T4>.NativeGuid);
        }

        /// <summary>
        /// Schedule a tag change with a pre-built TagSet, resolving the entity by handle.
        /// </summary>
        public readonly void MoveTo(EntityHandle entityHandle, TagSet tags) =>
            MoveTo(GetEntityIndex(entityHandle), tags);

        [MethodImpl(MethodImplOptions.AggressiveInlining)]
        public readonly void MoveTo<T1>(EntityHandle entityHandle)
            where T1 : struct, ITag => MoveTo<T1>(GetEntityIndex(entityHandle));

        [MethodImpl(MethodImplOptions.AggressiveInlining)]
        public readonly void MoveTo<T1, T2>(EntityHandle entityHandle)
            where T1 : struct, ITag
            where T2 : struct, ITag => MoveTo<T1, T2>(GetEntityIndex(entityHandle));

        [MethodImpl(MethodImplOptions.AggressiveInlining)]
        public readonly void MoveTo<T1, T2, T3>(EntityHandle entityHandle)
            where T1 : struct, ITag
            where T2 : struct, ITag
            where T3 : struct, ITag => MoveTo<T1, T2, T3>(GetEntityIndex(entityHandle));

        [MethodImpl(MethodImplOptions.AggressiveInlining)]
        public readonly void MoveTo<T1, T2, T3, T4>(EntityHandle entityHandle)
            where T1 : struct, ITag
            where T2 : struct, ITag
            where T3 : struct, ITag
            where T4 : struct, ITag => MoveTo<T1, T2, T3, T4>(GetEntityIndex(entityHandle));

        // ── Entity Reference Resolution ─────────────────────────────

        [MethodImpl(MethodImplOptions.AggressiveInlining)]
        public readonly bool TryGetEntityIndex(
            EntityHandle entityHandle,
            out EntityIndex entityIndex
        ) => _entityIds.TryGetEntityIndex(entityHandle, out entityIndex);

        [MethodImpl(MethodImplOptions.AggressiveInlining)]
        public readonly EntityIndex GetEntityIndex(EntityHandle entityHandle) =>
            _entityIds.GetEntityIndex(entityHandle);

        [MethodImpl(MethodImplOptions.AggressiveInlining)]
        public readonly EntityHandle GetEntityHandle(EntityIndex entityIndex) =>
            _entityIds.GetEntityHandle(entityIndex);

        [MethodImpl(MethodImplOptions.AggressiveInlining)]
        public readonly bool EntityExists(EntityHandle entityHandle) =>
            _entityIds.TryGetEntityIndex(entityHandle, out _);

        // ── Deferred Set Operations ─────────────────────────────────

        [MethodImpl(MethodImplOptions.AggressiveInlining)]
        public void SetAdd<TSet>(EntityIndex entityIndex)
            where TSet : struct, IEntitySet
        {
            AssertStructuralChangesAllowed();
            _deferredQueues
                .GetValueByRef(EntitySetId<TSet>.Value)
                .AddQueue.GetBag(_threadIndex)
                .Enqueue(entityIndex);
        }

        [MethodImpl(MethodImplOptions.AggressiveInlining)]
        public void SetAdd<TSet>(EntityHandle entityHandle)
            where TSet : struct, IEntitySet
        {
            SetAdd<TSet>(GetEntityIndex(entityHandle));
        }

        [MethodImpl(MethodImplOptions.AggressiveInlining)]
        public void SetRemove<TSet>(EntityIndex entityIndex)
            where TSet : struct, IEntitySet
        {
            AssertStructuralChangesAllowed();
            _deferredQueues
                .GetValueByRef(EntitySetId<TSet>.Value)
                .RemoveQueue.GetBag(_threadIndex)
                .Enqueue(entityIndex);
        }

        [MethodImpl(MethodImplOptions.AggressiveInlining)]
        public void SetRemove<TSet>(EntityHandle entityHandle)
            where TSet : struct, IEntitySet
        {
            SetRemove<TSet>(GetEntityIndex(entityHandle));
        }

        /// <summary>
        /// Defer a clear of an entity set until the next submission. The clear
        /// supersedes any pending <see cref="SetAdd{TSet}"/> /
        /// <see cref="SetRemove{TSet}"/> for the same set, regardless of call
        /// order or originating thread.
        /// </summary>
        [MethodImpl(MethodImplOptions.AggressiveInlining)]
        public void SetClear<TSet>()
            where TSet : struct, IEntitySet
        {
            AssertStructuralChangesAllowed();
            _deferredQueues.GetValueByRef(EntitySetId<TSet>.Value).RequestClear();
        }
    }
}
