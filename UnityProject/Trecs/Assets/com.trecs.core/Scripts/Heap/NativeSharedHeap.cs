using System;
using System.Collections.Generic;
using System.Linq;
using System.Threading;
using Trecs.Collections;
using Unity.Collections;
using Unity.Collections.LowLevel.Unsafe;

namespace Trecs.Internal
{
    /// <summary>
    /// Manages reference-counted native (unmanaged) allocations backing <see cref="NativeSharedPtr{T}"/>.
    /// Uses a chunked side-table directory for Burst-compatible pointer resolution, allowing
    /// main-thread allocations concurrent with job reads — no pending queue or deferred flush.
    /// </summary>
    internal sealed class NativeSharedHeap
    {
        readonly TrecsLog _log;
        readonly BlobCache _store;
        readonly BlobFactory _factory;

        NativeArray<IntPtr> _chunkDirectory;
        int _sideTableLength;
        int _nextFreshSlot = 1;
        readonly Stack<int> _freeSideTableSlots = new();

        readonly Dictionary<int, BlobId> _slotToBlobId = new();
        readonly Dictionary<BlobId, int> _blobIdToSlot = new();
        readonly Dictionary<int, BlobInfo> _blobInfoBySlot = new();
        readonly Dictionary<int, PtrHandle> _blobCacheHandleBySlot = new();

#if DEBUG
        readonly Dictionary<uint, int> _activeHandleRefCounts = new();
#endif

        NativeList<NativeSharedHeapPayload> _densePayloads;
        NativeList<int> _denseSlotIndices;
        NativeList<int> _sparseToDense;

        // Double buffers holding the pre-load dense lists during Deserialize's reconcile
        // (the main lists get overwritten by the wire blit; these are swapped into their
        // place beforehand, so no copy is needed). The slot list drives the stale sweep;
        // the payload list backs the bit-identical fast paths.
        NativeList<int> _prevDenseSlotIndices;
        NativeList<NativeSharedHeapPayload> _prevDensePayloads;

        // Monotonic stamp of the dense lists' *blob membership and order* — bumped on every
        // transition that adds/removes/reorders dense entries (AddBlobEntry, FreeSlot's
        // swap-remove, ClearAll, a non-bit-identical Deserialize), NOT on ref-count-only payload
        // updates. Lets the snapshot serializer skip re-collecting its referenced-blob set
        // (AddReferencedBlobIds walks _densePayloads in order) when nothing changed since the
        // last save — the steady-state rollback case. A missed bump site would silently corrupt
        // the snapshot wire form, so any new mutation of dense membership/order MUST bump this;
        // DEBUG builds re-collect and verify on every skipped rebuild.
        long _blobMembershipVersion;

        bool _isDisposed;
        int _liveCount;
        NativeSharedPtrResolver _resolver;

        // See _blobMembershipVersion.
        internal long BlobMembershipVersion => _blobMembershipVersion;

        public NativeSharedHeap(TrecsLog log, BlobCache store, BlobFactory factory)
        {
            _log = log;
            _store = store;
            _factory = factory;

            _chunkDirectory = new NativeArray<IntPtr>(
                NativeHeapResolver.MaxChunkCount,
                Allocator.Persistent,
                NativeArrayOptions.ClearMemory
            );

            _densePayloads = new NativeList<NativeSharedHeapPayload>(64, Allocator.Persistent);
            _denseSlotIndices = new NativeList<int>(64, Allocator.Persistent);
            _sparseToDense = new NativeList<int>(1, Allocator.Persistent);
            _prevDenseSlotIndices = new NativeList<int>(64, Allocator.Persistent);
            _prevDensePayloads = new NativeList<NativeSharedHeapPayload>(64, Allocator.Persistent);

            EnsureSideTableLength(1);

            _resolver = new NativeSharedPtrResolver(_chunkDirectory);
        }

        public int NumEntries
        {
            get
            {
                TrecsDebugAssert.That(!_isDisposed);
                return _liveCount;
            }
        }

        public ref NativeSharedPtrResolver Resolver
        {
            get
            {
                TrecsDebugAssert.That(!_isDisposed);
                return ref _resolver;
            }
        }

        public unsafe NativeSharedRead<T> Read<T>(in NativeSharedPtr<T> ptr)
            where T : unmanaged
        {
            TrecsDebugAssert.That(!_isDisposed);
            TrecsDebugAssert.That(
                UnityThreadHelper.IsMainThread,
                "NativeSharedHeap.Read is main-thread only."
            );

            var entry = _resolver.ResolveEntryWithSlotPtr<T>(ptr.Handle, out var slotPtr);
#if ENABLE_UNITY_COLLECTIONS_CHECKS
            return new NativeSharedRead<T>(
                entry.Address.ToPointer(),
                slotPtr,
                entry.Generation,
                entry.Safety
            );
#else
            return new NativeSharedRead<T>(entry.Address.ToPointer(), slotPtr, entry.Generation);
#endif
        }

        /// <summary>
        /// Eagerly creates a native shared blob, taking ownership of <paramref name="alloc"/>.
        /// Used by <see cref="BlobBuilder"/>, whose bytes are produced up front and are not
        /// re-creatable from a source. The blob behaves like the former in-memory store entries:
        /// it lingers until LRU-evicted and is then forgotten.
        /// </summary>
        public NativeSharedPtr<T> CreateBlobTakingOwnership<T>(
            BlobId blobId,
            NativeBlobAllocation alloc
        )
            where T : unmanaged
        {
            TrecsDebugAssert.That(!_isDisposed);
            var blobCacheResult = _store.AllocNativeBlobTakingOwnership<T>(blobId, alloc);
            return AddBlobEntry<T>(blobId, blobCacheResult.Handle);
        }

        public void RegisterBlob<T>(BlobId blobId, Func<T> factory)
            where T : unmanaged
        {
            TrecsDebugAssert.That(!_isDisposed);
            _factory.RegisterNativeBlob(blobId, factory);
        }

        public void RegisterBlob<T>(BlobId blobId, in T value)
            where T : unmanaged
        {
            TrecsDebugAssert.That(!_isDisposed);
            _factory.RegisterNativeBlob(blobId, in value);
        }

        public void RegisterBlobTakingOwnership<T>(
            BlobId blobId,
            Func<NativeBlobAllocation> factory
        )
            where T : unmanaged
        {
            TrecsDebugAssert.That(!_isDisposed);
            _factory.RegisterNativeBlobTakingOwnership<T>(blobId, factory);
        }

        public bool TryGetBlob<T>(BlobId blobId, out NativeSharedPtr<T> ptr)
            where T : unmanaged
        {
            TrecsDebugAssert.That(!_isDisposed);

            _log.Trace("Looking up native blob with id {0}", blobId);

            if (_blobIdToSlot.TryGetValue(blobId, out var slotIdx))
            {
                ptr = IncrementRef<T>(slotIdx);
                return true;
            }

            // Deterministic resolve: held by this heap (above) or backed by a deterministically
            // registered source — never raw cache residency. See SharedHeap.TryGetBlobById for the
            // full rationale (the cache is not simulation state).
            if (_factory.ContainsNativeBlobDeterministic<T>(blobId))
            {
                _factory.EnsureResident(blobId);
                var blobCacheHandleId = _store.CreateHandle(blobId);
                ptr = AddBlobEntry<T>(blobId, blobCacheHandleId);
                return true;
            }

            ptr = default;
            return false;
        }

        public NativeSharedPtr<T> GetBlob<T>(BlobId blobId)
            where T : unmanaged
        {
            if (!TryGetBlob<T>(blobId, out var ptr))
            {
                throw TrecsDebugAssert.CreateException(
                    "Blob {0} is not deterministically reachable from simulation: it is neither "
                        + "held by the native shared heap nor backed by a deterministically "
                        + "registered source. Hold a NativeSharedPtr to keep it alive, register a "
                        + "source at setup (NativeSharedPtr.Register / NativeSharedAnchor.Register), "
                        + "or pass the data through the input stream and convert the in-hand "
                        + "payload with NativeSharedPtr.Acquire(world, inputPtr). Cache residency "
                        + "alone is non-deterministic state and is deliberately not consulted.",
                    blobId
                );
            }

            return ptr;
        }

        /// <summary>
        /// Pins <paramref name="blobId"/> on the strength of a justification the caller holds
        /// in hand: content it just allocated (content-addressed
        /// <see cref="NativeSharedPtr.Alloc{T}(WorldAccessor, in T)"/>) or an input payload
        /// delivered for the current frame
        /// (<see cref="NativeSharedPtr.Acquire{T}(WorldAccessor, InputNativeSharedPtr{T})"/>).
        /// Trusts residency directly instead of consulting the source registry — see
        /// <c>SharedHeap.PinResident</c>. The blob must be resident.
        /// </summary>
        internal NativeSharedPtr<T> PinResident<T>(BlobId blobId)
            where T : unmanaged
        {
            TrecsDebugAssert.That(!_isDisposed);

            if (_blobIdToSlot.TryGetValue(blobId, out var slotIdx))
            {
                return IncrementRef<T>(slotIdx);
            }

            TrecsAssert.That(
                _store.IsResident(blobId),
                "PinResident: blob {0} is not resident. The caller's in-hand justification "
                    + "(just-allocated content, or an input payload for the current frame) should "
                    + "guarantee residency — convert input payloads in the frame that delivers them.",
                blobId
            );
            // AddBlobEntry resolves the typed pointer through the cache, which asserts the resident
            // blob's type hash matches T.
            return AddBlobEntry<T>(blobId, _store.CreateHandle(blobId));
        }

        public BlobId GetBlobId(uint handle)
        {
            TrecsDebugAssert.That(!_isDisposed);
            NativeHeapResolver.DecodeHandle(new PtrHandle(handle), out var index, out _);

            if (_slotToBlobId.TryGetValue((int)index, out var blobId))
            {
                return blobId;
            }

            throw TrecsDebugAssert.CreateException("No BlobId found for handle {0}", handle);
        }

        unsafe NativeSharedPtr<T> AddBlobEntry<T>(BlobId blobId, PtrHandle blobCacheHandleId)
            where T : unmanaged
        {
            var burstTypeHash = TypeId<T>.Value.Value;
            var slotIdx = AcquireSideTableSlot();

            var ptr = _store.GetNativeBlobPtr(blobId, TypeId<T>.Value.Value);

            var prior = GetEntry(slotIdx);
            var nextGen = (byte)(prior.Generation + 1);
            if (nextGen == 0)
                nextGen = 1;

            var newEntry = new NativeSharedHeapSideTableEntry
            {
                Address = ptr,
                TypeHash = burstTypeHash,
                Generation = nextGen,
                InUse = 1,
#if ENABLE_UNITY_COLLECTIONS_CHECKS
                Safety = CreateSafetyHandle(),
#endif
            };

            SetEntry(slotIdx, newEntry);

            _slotToBlobId[slotIdx] = blobId;
            _blobIdToSlot[blobId] = slotIdx;
            _blobCacheHandleBySlot[slotIdx] = blobCacheHandleId;
            _blobInfoBySlot[slotIdx] = new BlobInfo
            {
                RefCount = 1,
                InnerTypeId = TypeId<T>.Value,
                BurstTypeHash = burstTypeHash,
            };

            _liveCount++;
            _blobMembershipVersion++;

            _sparseToDense[slotIdx] = _densePayloads.Length;
            _denseSlotIndices.Add(slotIdx);
            _densePayloads.Add(
                new NativeSharedHeapPayload
                {
                    BlobId = blobId,
                    TypeHash = burstTypeHash,
                    Generation = nextGen,
                    InUse = 1,
                    RefCount = 1,
                }
            );

            var handleValue = NativeSharedPtrResolver.EncodeHandle(nextGen, (uint)slotIdx);

#if DEBUG
            _activeHandleRefCounts[handleValue] = 1;
#endif

            _log.Trace(
                "AddBlobEntry: handle={0} slot={1} blobId={2}",
                handleValue,
                slotIdx,
                blobId
            );

            return new NativeSharedPtr<T>(handleValue);
        }

        NativeSharedPtr<T> IncrementRef<T>(int slotIdx)
            where T : unmanaged
        {
            var info = _blobInfoBySlot[slotIdx];
            TrecsDebugAssert.That(info.InnerTypeId == TypeId<T>.Value);
            info.RefCount += 1;
            _blobInfoBySlot[slotIdx] = info;

            var denseIdx = _sparseToDense[slotIdx];
            var payload = _densePayloads[denseIdx];
            payload.RefCount = info.RefCount;
            _densePayloads[denseIdx] = payload;

            var entry = GetEntry(slotIdx);
            var handleValue = NativeSharedPtrResolver.EncodeHandle(entry.Generation, (uint)slotIdx);

#if DEBUG
            _activeHandleRefCounts.TryGetValue(handleValue, out var debugCount);
            _activeHandleRefCounts[handleValue] = debugCount + 1;
#endif

            _log.Trace(
                "IncrementRef: handle={0} slot={1} refCount={2}",
                handleValue,
                slotIdx,
                info.RefCount
            );

            return new NativeSharedPtr<T>(handleValue);
        }

        public bool TryClone<T>(uint handle, out NativeSharedPtr<T> result)
            where T : unmanaged
        {
            TrecsDebugAssert.That(!_isDisposed);

            if (handle == 0)
            {
                result = default;
                return false;
            }

            NativeHeapResolver.DecodeHandle(
                new PtrHandle(handle),
                out var index,
                out var generation
            );
            var slotIdx = (int)index;

            var entry = GetEntry(slotIdx);
            if (entry.InUse != 1 || entry.Generation != generation)
            {
                result = default;
                return false;
            }

            result = IncrementRef<T>(slotIdx);
            return true;
        }

        public void DecrementRef(uint handle)
        {
            TrecsDebugAssert.That(!_isDisposed);
            TrecsDebugAssert.That(handle != 0, "Attempted to dispose null handle");

            NativeHeapResolver.DecodeHandle(new PtrHandle(handle), out var index, out _);
            var slotIdx = (int)index;

            TrecsAssert.That(
                _resolver.TryResolveEntry(handle, out _),
                "DecrementRef on invalid or stale handle {0}",
                handle
            );

#if DEBUG
            TrecsDebugAssert.That(
                _activeHandleRefCounts.TryGetValue(handle, out var debugRefCount)
                    && debugRefCount > 0,
                "Attempted to dispose unrecognized handle {0} (double-dispose?)",
                handle
            );
            if (debugRefCount <= 1)
                _activeHandleRefCounts.Remove(handle);
            else
                _activeHandleRefCounts[handle] = debugRefCount - 1;
#endif

            if (!_blobInfoBySlot.TryGetValue(slotIdx, out var info))
            {
                throw TrecsDebugAssert.CreateException(
                    "Attempted to dispose handle {0} for unknown slot {1}",
                    handle,
                    slotIdx
                );
            }

            info.RefCount -= 1;
            TrecsDebugAssert.That(info.RefCount >= 0);

            if (info.RefCount == 0)
            {
                FreeSlot(slotIdx);
            }
            else
            {
                _blobInfoBySlot[slotIdx] = info;

                var denseIdx = _sparseToDense[slotIdx];
                var payload = _densePayloads[denseIdx];
                payload.RefCount = info.RefCount;
                _densePayloads[denseIdx] = payload;
            }

            _log.Trace(
                "DecrementRef: handle={0} slot={1} refCount={2}",
                handle,
                slotIdx,
                info.RefCount
            );
        }

        unsafe void FreeSlot(int slotIdx)
        {
            var entry = GetEntry(slotIdx);

#if ENABLE_UNITY_COLLECTIONS_CHECKS
            AtomicSafetyHandle.CheckDeallocateAndThrow(entry.Safety);
            AtomicSafetyHandle.Release(entry.Safety);
#endif

            entry.InUse = 0;
            entry.Address = IntPtr.Zero;
            SetEntry(slotIdx, entry);

            if (_blobCacheHandleBySlot.Remove(slotIdx, out var cacheHandle))
            {
                _store.DisposeHandle(cacheHandle);
            }

            if (_slotToBlobId.Remove(slotIdx, out var blobId))
            {
                _blobIdToSlot.Remove(blobId);
            }

            _blobInfoBySlot.Remove(slotIdx);

            // Dense list swap-remove
            var denseIdx = _sparseToDense[slotIdx];
            var lastDense = _densePayloads.Length - 1;
            if (denseIdx != lastDense)
            {
                _densePayloads[denseIdx] = _densePayloads[lastDense];
                _denseSlotIndices[denseIdx] = _denseSlotIndices[lastDense];
                _sparseToDense[_denseSlotIndices[denseIdx]] = denseIdx;
            }
            _densePayloads.Length--;
            _denseSlotIndices.Length--;
            _sparseToDense[slotIdx] = -1;

            _freeSideTableSlots.Push(slotIdx);
            _liveCount--;
            _blobMembershipVersion++;
        }

        public void ClearAll(bool warnUndisposed)
        {
            TrecsDebugAssert.That(!_isDisposed);

            if (_liveCount > 0)
            {
                _blobMembershipVersion++;
            }

            if (_liveCount > 0 && warnUndisposed)
            {
                if (_log.IsWarningEnabled())
                {
                    var debugTypes = new HashSet<Type>();
                    foreach (var (slotIdx, blobId) in _slotToBlobId)
                    {
                        debugTypes.Add(_store.TryGetNativeBlobType(blobId));
                    }

                    _log.Warning(
                        "Found {0} native blobs that were not disposed, with types: {1}",
                        _liveCount,
                        debugTypes.Select(x => x.GetPrettyName()).Join(", ")
                    );
                }
            }

            // Release all blob cache handles and safety handles
            foreach (var (slotIdx, cacheHandle) in _blobCacheHandleBySlot)
            {
                _store.DisposeHandle(cacheHandle);
#if ENABLE_UNITY_COLLECTIONS_CHECKS
                var entry = GetEntry(slotIdx);
                if (entry.InUse == 1)
                {
                    AtomicSafetyHandle.CheckDeallocateAndThrow(entry.Safety);
                    AtomicSafetyHandle.Release(entry.Safety);
                }
#endif
            }

#if DEBUG
            _activeHandleRefCounts.Clear();
#endif

            _blobCacheHandleBySlot.Clear();
            _slotToBlobId.Clear();
            _blobIdToSlot.Clear();
            _blobInfoBySlot.Clear();

            _densePayloads.Clear();
            _denseSlotIndices.Clear();

            // Reset side table entries to InUse=0
            for (int i = 1; i < _sideTableLength; i++)
            {
                var entry = GetEntry(i);
                if (entry.InUse == 1)
                {
                    entry.InUse = 0;
                    entry.Address = IntPtr.Zero;
                    SetEntry(i, entry);
                }
            }

            _freeSideTableSlots.Clear();
            _nextFreshSlot = 1;
            _sideTableLength = 0;
            EnsureSideTableLength(1);
            _liveCount = 0;

            if (_sparseToDense.Length > 0)
            {
                unsafe
                {
                    UnsafeUtility.MemSet(
                        _sparseToDense.GetUnsafePtr(),
                        0xFF,
                        (long)_sparseToDense.Length * sizeof(int)
                    );
                }
            }
        }

        internal bool SuppressDisposeWarnings { get; set; }

        internal void Dispose()
        {
            TrecsDebugAssert.That(!_isDisposed);
            ClearAll(warnUndisposed: !SuppressDisposeWarnings);

            DisposeAllChunks();
            _chunkDirectory.Dispose();
            _densePayloads.Dispose();
            _denseSlotIndices.Dispose();
            _sparseToDense.Dispose();
            _prevDenseSlotIndices.Dispose();
            _prevDensePayloads.Dispose();

            _isDisposed = true;
        }

        // Add every BlobId this heap currently references to <paramref name="output"/>. Walks the
        // dense payload list — the same insertion-ordered structure Serialize blits — NOT the
        // _blobIdToSlot dictionary, whose iteration order is non-deterministic; the order must be
        // stable across capture and replay for the snapshot checksum. See
        // SharedHeap.AddReferencedBlobIds for why this is the right source for a snapshot's blob set.
        internal void AddReferencedBlobIds(IterableHashSet<BlobId> output)
        {
            TrecsDebugAssert.That(!_isDisposed);
            for (int i = 0; i < _densePayloads.Length; i++)
            {
                output.Add(_densePayloads[i].BlobId);
            }
        }

        public void Serialize(ISerializationWriter writer)
        {
            using var _ = TrecsProfiling.Start("NativeSharedHeap.Serialize");
            TrecsDebugAssert.That(_densePayloads.Length == _liveCount);
            TrecsDebugAssert.That(_denseSlotIndices.Length == _liveCount);

            writer.Write<int>("LiveCount", _liveCount);
            writer.Write<int>("NextFreshSlot", _nextFreshSlot);

            unsafe
            {
                writer.BlitWriteArrayPtr(
                    "SlotIndices",
                    _denseSlotIndices.GetUnsafeReadOnlyPtr(),
                    _liveCount
                );
                writer.BlitWriteArrayPtr(
                    "Payloads",
                    _densePayloads.GetUnsafeReadOnlyPtr(),
                    _liveCount
                );
            }

            // Write free-slot stack with generations for correct slot reuse after deserialize
            var freeSlotCount = _freeSideTableSlots.Count;
            writer.Write<int>("FreeSlotCount", freeSlotCount);
            if (freeSlotCount > 0)
            {
                var freeSlots = _freeSideTableSlots.ToArray();
                for (int i = 0; i < freeSlotCount; i++)
                {
                    var entry = GetEntry(freeSlots[i]);
                    writer.Write<int>("FreeSlot", freeSlots[i]);
                    writer.Write<byte>("FreeSlotGen", entry.Generation);
                }
            }

            _log.Trace("Serialized {0} native blobs", _liveCount);
        }

        public void Deserialize(ISerializationReader reader)
        {
            using var _ = TrecsProfiling.Start("NativeSharedHeap.Deserialize");
            TrecsDebugAssert.That(!_isDisposed);

            // Reconciling deserialize: rather than tearing every live entry down and
            // rebuilding from the wire (per-entry BlobCache handle dispose/create,
            // safety-handle churn, inactive-totals and eviction bookkeeping), diff the
            // incoming entries against the live ones. In the rollback path — restoring a
            // snapshot taken moments ago into the same world, every frame — almost every
            // slot still holds the same blob at the same generation, so the existing
            // cache handle (which kept the blob resident at a stable address the whole
            // time) is simply kept. Cost then scales with the churn between capture and
            // now instead of with the live entry count.

            // Swap the pre-load dense lists into the prev buffers (no copy) — the main
            // lists are about to be overwritten by the wire blit, and the reconcile
            // below diffs against the pre-load state.
            var prevLiveCount = _liveCount;
            // Pre-load fresh-slot high-water mark, captured before the wire overwrites
            // it — the generation reset below covers the region between the snapshot's
            // mark and this one.
            var prevNextFreshSlot = _nextFreshSlot;
            (_denseSlotIndices, _prevDenseSlotIndices) = (_prevDenseSlotIndices, _denseSlotIndices);
            (_densePayloads, _prevDensePayloads) = (_prevDensePayloads, _densePayloads);

            var liveCount = reader.Read<int>("LiveCount");
            _nextFreshSlot = reader.Read<int>("NextFreshSlot");

            using (TrecsProfiling.Start("EnsureSideTableLength"))
            {
                EnsureSideTableLength(_nextFreshSlot);
            }

            _denseSlotIndices.ResizeUninitialized(liveCount);
            _densePayloads.ResizeUninitialized(liveCount);

            unsafe
            {
                reader.BlitReadArrayPtr("SlotIndices", _denseSlotIndices.GetUnsafePtr(), liveCount);
                reader.BlitReadArrayPtr("Payloads", _densePayloads.GetUnsafePtr(), liveCount);
            }

            // _sparseToDense growth (with -1 fill) is handled by the
            // EnsureSideTableLength call above; the fast path below depends on every
            // slot of it being well-formed.
            TrecsDebugAssert.That(_sparseToDense.Length >= _nextFreshSlot);

            // Whole-state fast path: in rollback steady state the incoming dense lists
            // are bit-identical to the pre-load ones (same slots, payloads, ref counts),
            // so every side-table entry, dictionary, and cache handle — and the
            // _sparseToDense map — is already exactly right. Two memcmps instead of any
            // per-entry work. (Bit equality is exact: payload padding is deterministic —
            // struct initializers zero it and the lists round-trip through the wire.)
            bool bitIdentical = liveCount == prevLiveCount;
            if (bitIdentical && liveCount > 0)
            {
                unsafe
                {
                    bitIdentical =
                        UnsafeUtility.MemCmp(
                            _denseSlotIndices.GetUnsafeReadOnlyPtr(),
                            _prevDenseSlotIndices.GetUnsafeReadOnlyPtr(),
                            (long)liveCount * sizeof(int)
                        ) == 0
                        && UnsafeUtility.MemCmp(
                            _densePayloads.GetUnsafeReadOnlyPtr(),
                            _prevDensePayloads.GetUnsafeReadOnlyPtr(),
                            (long)liveCount * UnsafeUtility.SizeOf<NativeSharedHeapPayload>()
                        ) == 0;
                }
            }

            int unchangedCount = bitIdentical
                ? liveCount
                : ReconcileEntries(liveCount, prevLiveCount);

            if (!bitIdentical)
            {
                // The wire replaced dense membership/order wholesale (bypassing
                // AddBlobEntry/FreeSlot). Bit-identical loads skip the bump on purpose: the
                // dense lists are byte-equal to the pre-load state, so a referenced-blob set
                // collected before the load is still exact — which is what keeps the snapshot
                // serializer's rebuild-skip alive across steady-state rollback loads.
                _blobMembershipVersion++;
            }

            _liveCount = liveCount;

            // Determinism: re-fresh the generations of slots this timeline touched beyond the
            // snapshot's high-water mark. Generations are minted from the side table
            // (prior.Generation + 1) and are serialized — both in the dense payloads and baked
            // into every NativeSharedPtr handle stored in component data. A slot first claimed
            // after the snapshot keeps its bumped generation through a load (it is in no wire
            // section: not live, not in the wire free stack), so a rolled-back-and-replayed
            // world would re-mint gen N+1 where a straight run mints gen N — divergent snapshot
            // bytes / checksums for identical logical state (a false desync under per-frame
            // checksum comparison). Slots below the wire mark are fully wire-determined (live
            // slots take payload.Generation, free slots take the free-stack generations); slots
            // in [wire mark, pre-load mark) were provably never used as of the snapshot, so
            // generation 0 — "never minted" — is exactly the straight-run state. Runs on the
            // bit-identical fast path too: an alloc-then-free since the capture leaves the dense
            // lists byte-equal (no reconcile, no sweep) yet still deposits generation residue.
            // Empty range in steady state.
            using (TrecsProfiling.Start("Resetting fresh-region generations"))
            {
                for (int slotIdx = _nextFreshSlot; slotIdx < prevNextFreshSlot; slotIdx++)
                {
                    var entry = GetEntry(slotIdx);
                    // Anything at or beyond the wire's fresh mark cannot be live in the
                    // incoming state (wire slots are all below it), and the reconcile/sweep
                    // above already released any pre-load occupant.
                    TrecsDebugAssert.That(entry.InUse == 0);
                    if (entry.Generation != 0)
                    {
                        entry.Generation = 0;
                        SetEntry(slotIdx, entry);
                    }
                }
            }

            // Restore free-slot stack
            using (TrecsProfiling.Start("Reading free-slot stacks"))
            {
                _freeSideTableSlots.Clear();
                var freeSlotCount = reader.Read<int>("FreeSlotCount");
                var slotIdxs = new NativeArray<int>(freeSlotCount, Allocator.Temp);
                var gens = new NativeArray<byte>(freeSlotCount, Allocator.Temp);
                for (int i = 0; i < freeSlotCount; i++)
                {
                    slotIdxs[i] = reader.Read<int>("FreeSlot");
                    gens[i] = reader.Read<byte>("FreeSlotGen");
                }

                // Data is top-to-bottom (Stack.ToArray order in Serialize); push in
                // reverse so the original top ends up back on top. Free slots must
                // be reused in the same order as a never-reloaded run — the slot
                // index is baked into allocation handles, so a reversed stack makes
                // post-reload allocations land on different slots and the timeline
                // (and snapshot checksums) diverge from an unreloaded world.
                for (int i = freeSlotCount - 1; i >= 0; i--)
                {
                    var slotIdx = slotIdxs[i];
                    var entry = GetEntry(slotIdx);
                    entry.Generation = gens[i];
                    SetEntry(slotIdx, entry);

                    _freeSideTableSlots.Push(slotIdx);
                }
                slotIdxs.Dispose();
                gens.Dispose();
            }

            _log.Debug(
                "Deserialized {0} native blobs ({1} unchanged in place)",
                _liveCount,
                unchangedCount
            );
        }

        // The non-bit-identical reconcile: diff each incoming entry against the live
        // slot state, rebuilding only what changed, then sweep slots that are live but
        // absent from the incoming state. Returns how many entries were kept in place.
        int ReconcileEntries(int liveCount, int prevLiveCount)
        {
            int unchangedCount = 0;

            // _sparseToDense doubles as the reconcile marker: reset every slot to -1,
            // then each incoming entry stamps its slot with its dense index. Pre-load
            // slots still at -1 afterwards are stale and get swept.
            unsafe
            {
                UnsafeUtility.MemSet(
                    _sparseToDense.GetUnsafePtr(),
                    0xFF,
                    (long)_sparseToDense.Length * sizeof(int)
                );
            }

            // Pre-size managed dictionaries so the inserts below don't pay
            // rehash costs on the first Deserialize after world creation.
            // No-op in steady state — capacity stays at the prior high-water
            // mark.
            _slotToBlobId.EnsureCapacity(liveCount);
            _blobIdToSlot.EnsureCapacity(liveCount);
            _blobCacheHandleBySlot.EnsureCapacity(liveCount);
            _blobInfoBySlot.EnsureCapacity(liveCount);
#if DEBUG
            _activeHandleRefCounts.EnsureCapacity(liveCount);
#endif

            using (TrecsProfiling.Start("Reconciling entries"))
            {
                for (int n = 0; n < liveCount; n++)
                {
                    var slotIdx = _denseSlotIndices[n];
                    var payload = _densePayloads[n];

                    // Identical to the pre-load entry at the same dense position (in a
                    // small-churn rollback, everything before the first changed dense
                    // index): all slot-keyed state is already exactly right — only the
                    // reconcile marker needs stamping.
                    if (n < prevLiveCount && _prevDenseSlotIndices[n] == slotIdx)
                    {
                        var prevPayload = _prevDensePayloads[n];
                        if (
                            prevPayload.BlobId == payload.BlobId
                            && prevPayload.TypeHash == payload.TypeHash
                            && prevPayload.Generation == payload.Generation
                            && prevPayload.RefCount == payload.RefCount
                        )
                        {
                            _sparseToDense[slotIdx] = n;
                            unchangedCount++;
                            continue;
                        }
                    }

                    if (_slotToBlobId.TryGetValue(slotIdx, out var oldBlobId))
                    {
                        var existing = GetEntry(slotIdx);
                        if (
                            oldBlobId == payload.BlobId
                            && existing.InUse == 1
                            && existing.Generation == payload.Generation
                            && existing.TypeHash == payload.TypeHash
                        )
                        {
                            // Unchanged: same blob, same slot, same generation. The held
                            // cache handle kept the blob resident at a stable address, so
                            // the side-table entry (and its safety handle) is already
                            // exactly right — only the ref count and the dense index
                            // (which shuffles via swap-remove) can differ.
                            var info = _blobInfoBySlot[slotIdx];
                            if (info.RefCount != payload.RefCount)
                            {
                                info.RefCount = payload.RefCount;
                                _blobInfoBySlot[slotIdx] = info;
                            }
                            _sparseToDense[slotIdx] = n;
                            unchangedCount++;
#if DEBUG
                            _activeHandleRefCounts[
                                NativeSharedPtrResolver.EncodeHandle(
                                    payload.Generation,
                                    (uint)slotIdx
                                )
                            ] = payload.RefCount;
#endif
                            continue;
                        }

                        // The slot is live but holds something else (different blob, or
                        // a generation mismatch from slot reuse between capture and
                        // now): release its handles, then rebuild below.
                        ReleaseReconciledSlot(slotIdx, in existing, oldBlobId);
                    }

                    _factory.EnsureResident(payload.BlobId);
                    var blobPtr = _store.GetNativeBlobPtr(payload.BlobId, payload.TypeHash);

                    var entry = new NativeSharedHeapSideTableEntry
                    {
                        Address = blobPtr,
                        TypeHash = payload.TypeHash,
                        Generation = payload.Generation,
                        InUse = 1,
#if ENABLE_UNITY_COLLECTIONS_CHECKS
                        Safety = CreateSafetyHandle(),
#endif
                    };
                    SetEntry(slotIdx, entry);

                    _slotToBlobId[slotIdx] = payload.BlobId;
                    _blobIdToSlot[payload.BlobId] = slotIdx;
                    _blobCacheHandleBySlot[slotIdx] = _store.CreateHandle(payload.BlobId);
                    _blobInfoBySlot[slotIdx] = new BlobInfo
                    {
                        RefCount = payload.RefCount,
                        InnerTypeId = new TypeId(payload.TypeHash),
                        BurstTypeHash = payload.TypeHash,
                    };

                    _sparseToDense[slotIdx] = n;

#if DEBUG
                    var handleValue = NativeSharedPtrResolver.EncodeHandle(
                        payload.Generation,
                        (uint)slotIdx
                    );
                    _activeHandleRefCounts[handleValue] = payload.RefCount;
#endif
                }
            }

            // Sweep slots that were live before the load but aren't in the incoming
            // state. Runs after the pass above so a blob that merely moved slots
            // overlaps its new cache handle with its old one and never dips through
            // refcount 0 (no spurious inactive-totals / eviction churn).
            using (TrecsProfiling.Start("Sweeping stale entries"))
            {
                for (int i = 0; i < prevLiveCount; i++)
                {
                    var slotIdx = _prevDenseSlotIndices[i];
                    if (_sparseToDense[slotIdx] >= 0)
                    {
                        continue; // matched by an incoming entry above
                    }

                    var entry = GetEntry(slotIdx);
                    ReleaseReconciledSlot(slotIdx, in entry, _slotToBlobId[slotIdx]);

                    _slotToBlobId.Remove(slotIdx);
                    _blobInfoBySlot.Remove(slotIdx);

                    entry.InUse = 0;
                    entry.Address = IntPtr.Zero;
                    SetEntry(slotIdx, entry);
                }
            }

            return unchangedCount;
        }

        // Releases a live slot's externally-held resources — its blob-cache handle, its
        // safety handle, and its DEBUG handle bookkeeping — plus the blobId→slot mapping
        // (only when it still points at this slot: the reconcile pass may already have
        // re-assigned the blob to a different slot, overwriting the mapping). The caller
        // either overwrites the remaining slot-keyed state (changed slot) or removes it
        // (stale sweep).
        void ReleaseReconciledSlot(
            int slotIdx,
            in NativeSharedHeapSideTableEntry entry,
            BlobId oldBlobId
        )
        {
#if ENABLE_UNITY_COLLECTIONS_CHECKS
            AtomicSafetyHandle.CheckDeallocateAndThrow(entry.Safety);
            AtomicSafetyHandle.Release(entry.Safety);
#endif
#if DEBUG
            _activeHandleRefCounts.Remove(
                NativeSharedPtrResolver.EncodeHandle(entry.Generation, (uint)slotIdx)
            );
#endif

            if (_blobCacheHandleBySlot.Remove(slotIdx, out var cacheHandle))
            {
                _store.DisposeHandle(cacheHandle);
            }

            if (_blobIdToSlot.TryGetValue(oldBlobId, out var mappedSlot) && mappedSlot == slotIdx)
            {
                _blobIdToSlot.Remove(oldBlobId);
            }
        }

#if ENABLE_UNITY_COLLECTIONS_CHECKS
        static AtomicSafetyHandle CreateSafetyHandle()
        {
            var safety = AtomicSafetyHandle.Create();
            AtomicSafetyHandle.SetBumpSecondaryVersionOnScheduleWrite(safety, true);
            return safety;
        }
#endif

        // ─── Chunked side-table infrastructure ───────────────────────

        int AcquireSideTableSlot()
        {
            if (_freeSideTableSlots.Count > 0)
            {
                var candidate = _freeSideTableSlots.Pop();
                EnsureSideTableLength(candidate + 1);
                return candidate;
            }

            var idx = _nextFreshSlot++;
            TrecsDebugAssert.That(
                (uint)idx <= NativeHeapResolver.MaxIndex,
                "NativeSharedHeap exhausted side-table index space ({0} max)",
                NativeHeapResolver.MaxIndex
            );
            EnsureSideTableLength(idx + 1);
            return idx;
        }

        unsafe void EnsureSideTableLength(int requiredLength)
        {
            while (_sideTableLength < requiredLength)
            {
                var chunkIdx = _sideTableLength >> NativeHeapResolver.ChunkSizeBits;
                if (_chunkDirectory[chunkIdx] == IntPtr.Zero)
                {
                    AllocateChunk(chunkIdx);
                }
                _sideTableLength++;
            }

            if (_sparseToDense.Length < requiredLength)
            {
                var oldLen = _sparseToDense.Length;
                _sparseToDense.Resize(requiredLength, NativeArrayOptions.UninitializedMemory);
                var ptr = (int*)_sparseToDense.GetUnsafePtr() + oldLen;
                UnsafeUtility.MemSet(ptr, 0xFF, (long)(requiredLength - oldLen) * sizeof(int));
            }
        }

        unsafe void DisposeAllChunks()
        {
            var entrySize = UnsafeUtility.SizeOf<NativeSharedHeapSideTableEntry>();
            var entryAlign = UnsafeUtility.AlignOf<NativeSharedHeapSideTableEntry>();
            for (int i = 0; i < _chunkDirectory.Length; i++)
            {
                var ptr = _chunkDirectory[i];
                if (ptr == IntPtr.Zero)
                    continue;
                AllocatorManager.Free(
                    Allocator.Persistent,
                    ptr.ToPointer(),
                    entrySize,
                    entryAlign,
                    items: NativeHeapResolver.ChunkSize
                );
                _chunkDirectory[i] = IntPtr.Zero;
            }
        }

        unsafe void AllocateChunk(int chunkIdx)
        {
            var entrySize = UnsafeUtility.SizeOf<NativeSharedHeapSideTableEntry>();
            var entryAlign = UnsafeUtility.AlignOf<NativeSharedHeapSideTableEntry>();
            var ptr = AllocatorManager.Allocate(
                Allocator.Persistent,
                entrySize,
                entryAlign,
                items: NativeHeapResolver.ChunkSize
            );

            UnsafeUtility.MemClear(ptr, (long)entrySize * NativeHeapResolver.ChunkSize);

            Thread.MemoryBarrier();

            _chunkDirectory[chunkIdx] = new IntPtr(ptr);
        }

        unsafe NativeSharedHeapSideTableEntry GetEntry(int idx)
        {
            var chunkIdx = idx >> NativeHeapResolver.ChunkSizeBits;
            var chunkPtr = (NativeSharedHeapSideTableEntry*)_chunkDirectory[chunkIdx].ToPointer();
            return chunkPtr[idx & NativeHeapResolver.ChunkIndexMask];
        }

        unsafe void SetEntry(int idx, in NativeSharedHeapSideTableEntry entry)
        {
            var chunkIdx = idx >> NativeHeapResolver.ChunkSizeBits;
            var chunkPtr = (NativeSharedHeapSideTableEntry*)_chunkDirectory[chunkIdx].ToPointer();
            chunkPtr[idx & NativeHeapResolver.ChunkIndexMask] = entry;
        }

        internal struct BlobInfo
        {
            public int RefCount;
            public TypeId InnerTypeId;
            public int BurstTypeHash;
        }
    }
}
