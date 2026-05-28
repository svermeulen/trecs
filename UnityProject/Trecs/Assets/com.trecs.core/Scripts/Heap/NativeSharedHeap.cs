using System;
using System.Collections.Generic;
using System.Linq;
using System.Threading;
using Unity.Collections;
using Unity.Collections.LowLevel.Unsafe;

namespace Trecs.Internal
{
    /// <summary>
    /// Manages reference-counted native (unmanaged) allocations backing <see cref="NativeSharedPtr{T}"/>.
    /// Uses a chunked side-table directory for Burst-compatible pointer resolution, allowing
    /// main-thread allocations concurrent with job reads — no pending queue or deferred flush.
    /// </summary>
    public sealed class NativeSharedHeap
    {
        readonly TrecsLog _log;
        readonly BlobCache _store;

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

        bool _isDisposed;
        int _liveCount;
        NativeSharedPtrResolver _resolver;

        public NativeSharedHeap(TrecsLog log, BlobCache store)
        {
            _log = log;
            _store = store;

            _chunkDirectory = new NativeArray<IntPtr>(
                NativeHeapResolver.MaxChunkCount,
                Allocator.Persistent,
                NativeArrayOptions.ClearMemory
            );

            _densePayloads = new NativeList<NativeSharedHeapPayload>(64, Allocator.Persistent);
            _denseSlotIndices = new NativeList<int>(64, Allocator.Persistent);
            _sparseToDense = new NativeList<int>(1, Allocator.Persistent);

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

        public NativeSharedPtr<T> CreateBlob<T>(BlobId blobId, in T blob)
            where T : unmanaged
        {
            TrecsDebugAssert.That(!_isDisposed);
            var blobCacheResult = _store.AllocNativeBlob(blobId, in blob);
            return AddBlobEntry<T>(blobId, blobCacheResult.Handle);
        }

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

            if (_store.ContainsNativeBlob<T>(blobId, updateAccessTime: true))
            {
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
                    "Attempted to get an unrecognized blob {0}",
                    blobId
                );
            }

            return ptr;
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
        }

        public void ClearAll(bool warnUndisposed)
        {
            TrecsDebugAssert.That(!_isDisposed);

            if (_liveCount > 0 && warnUndisposed)
            {
                if (_log.IsWarningEnabled())
                {
                    var debugTypes = new HashSet<Type>();
                    foreach (var (slotIdx, blobId) in _slotToBlobId)
                    {
                        debugTypes.Add(_store.TryGetNativeBlobType(blobId, updateAccessTime: true));
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

        internal void Dispose()
        {
            TrecsDebugAssert.That(!_isDisposed);
            ClearAll(warnUndisposed: true);

            DisposeAllChunks();
            _chunkDirectory.Dispose();
            _densePayloads.Dispose();
            _denseSlotIndices.Dispose();
            _sparseToDense.Dispose();

            _isDisposed = true;
        }

        public void Serialize(ISerializationWriter writer)
        {
            using var _ = TrecsProfiling.Start("NativeSharedHeap.Serialize");
            TrecsDebugAssert.That(_densePayloads.Length == _liveCount);

            writer.Write<int>("Version", 2);
            writer.Write<int>("LiveCount", _liveCount);
            writer.Write<int>("NextFreshSlot", _nextFreshSlot);

            unsafe
            {
                writer.BlitWriteRawBytes(
                    "SlotIndices",
                    _denseSlotIndices.GetUnsafeReadOnlyPtr(),
                    _liveCount * sizeof(int)
                );
                writer.BlitWriteRawBytes(
                    "Payloads",
                    _densePayloads.GetUnsafeReadOnlyPtr(),
                    _liveCount * UnsafeUtility.SizeOf<NativeSharedHeapPayload>()
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

            using (TrecsProfiling.Start("ResetForDeserialize"))
            {
                ClearAll(warnUndisposed: true);
            }

            var version = reader.Read<int>("Version");
            TrecsAssert.That(
                version == 2,
                "NativeSharedHeap serialization version mismatch: expected 2, got {0}",
                version
            );

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
                reader.BlitReadRawBytes(
                    "SlotIndices",
                    _denseSlotIndices.GetUnsafePtr(),
                    liveCount * sizeof(int)
                );
                reader.BlitReadRawBytes(
                    "Payloads",
                    _densePayloads.GetUnsafePtr(),
                    liveCount * UnsafeUtility.SizeOf<NativeSharedHeapPayload>()
                );
            }

            // Grow sparseToDense
            if (_sparseToDense.Length < _nextFreshSlot)
            {
                var oldLen = _sparseToDense.Length;
                _sparseToDense.Resize(_nextFreshSlot, NativeArrayOptions.UninitializedMemory);
                unsafe
                {
                    UnsafeUtility.MemSet(
                        (int*)_sparseToDense.GetUnsafePtr() + oldLen,
                        0xFF,
                        (long)(_nextFreshSlot - oldLen) * sizeof(int)
                    );
                }
            }

            // Pre-size managed dictionaries so the inserts below don't pay
            // rehash costs on the first Deserialize after world creation.
            // No-op in steady state — Dictionary.Clear() preserves capacity,
            // so subsequent calls find capacity already at the prior high-water
            // mark.
            _slotToBlobId.EnsureCapacity(liveCount);
            _blobIdToSlot.EnsureCapacity(liveCount);
            _blobCacheHandleBySlot.EnsureCapacity(liveCount);
            _blobInfoBySlot.EnsureCapacity(liveCount);
#if DEBUG
            _activeHandleRefCounts.EnsureCapacity(liveCount);
#endif

            using (TrecsProfiling.Start("Reconstructing entries"))
            {
                for (int n = 0; n < liveCount; n++)
                {
                    var slotIdx = _denseSlotIndices[n];
                    var payload = _densePayloads[n];

                    var blobPtr = _store.GetNativeBlobPtr(
                        payload.BlobId,
                        payload.TypeHash,
                        updateAccessTime: true
                    );

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

            _liveCount = liveCount;

            // Restore free-slot stack
            using (TrecsProfiling.Start("Reading free-slot stacks"))
            {
                var freeSlotCount = reader.Read<int>("FreeSlotCount");
                for (int i = 0; i < freeSlotCount; i++)
                {
                    var slotIdx = reader.Read<int>("FreeSlot");
                    var gen = reader.Read<byte>("FreeSlotGen");

                    var entry = GetEntry(slotIdx);
                    entry.Generation = gen;
                    SetEntry(slotIdx, entry);

                    _freeSideTableSlots.Push(slotIdx);
                }
            }

            _log.Debug("Deserialized {0} native blobs", _liveCount);
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
