using System;
using System.Collections.Generic;
using Trecs.Collections;
using Trecs.Internal;
using Unity.Collections;
using Unity.Collections.LowLevel.Unsafe;

namespace Trecs
{
    /// <summary>
    /// Heap that tracks refcount handles for input-allocated unmanaged shared
    /// blobs (<see cref="InputNativeSharedPtr{T}"/>). The blob bytes themselves
    /// live in the shared <see cref="BlobCache"/>; this heap owns one
    /// <see cref="PtrHandle"/> per allocation that keeps the blob's refcount
    /// pinned for the lifetime of the input frame. When a frame is trimmed,
    /// the handles for that frame are released in bulk and the cache evicts
    /// the blob if its refcount drops to zero.
    ///
    /// <para>Provides its own <see cref="InputNativeSharedPtrResolver"/> independent
    /// from <see cref="NativeSharedHeap"/>. Input allocations happen during the input
    /// phase (before fixed update); jobs only read during fixed update, so there is
    /// no concurrent read+write and no pending queue is needed.</para>
    /// </summary>
    public sealed class InputNativeSharedHeap
    {
        readonly TrecsLog _log;
        readonly BlobCache _store;
        readonly BlobFactory _factory;

        readonly IterableDictionary<int, List<Entry>> _entriesByFrame = new();
        readonly Stack<List<Entry>> _listPool = new();
        readonly List<int> _frameRemoveBuffer = new();

        NativeHashMap<BlobId, InputNativeSharedHeapEntry> _resolverEntries;
        readonly Dictionary<BlobId, int> _blobIdRefCount = new();
        InputNativeSharedPtrResolver _resolver;

        bool _isDisposed;

        readonly struct Entry
        {
            public readonly BlobId BlobId;
            public readonly PtrHandle CacheHandle;

            // Non-null only for descriptor-acquired input blobs (InputNativeSharedPtr.Acquire<TDesc,T>).
            // Recorded into the input stream so a fresh-process replay can re-register the blob's
            // source and re-derive it; for plain by-reference allocations this is null. The box is
            // shared with the BlobFactory descriptor journal (see GetJournaledDescriptor), so
            // per-frame acquires of the same descriptor don't re-box.
            public readonly object Descriptor;

            public Entry(BlobId blobId, PtrHandle cacheHandle, object descriptor)
            {
                BlobId = blobId;
                CacheHandle = cacheHandle;
                Descriptor = descriptor;
            }
        }

        internal InputNativeSharedHeap(TrecsLog log, BlobCache store, BlobFactory factory)
        {
            _log = log;
            _store = store;
            _factory = factory;
            _resolverEntries = new NativeHashMap<BlobId, InputNativeSharedHeapEntry>(
                16,
                Allocator.Persistent
            );
            _resolver = new InputNativeSharedPtrResolver(_resolverEntries);
        }

        public int NumLiveFrames
        {
            get
            {
                TrecsDebugAssert.That(!_isDisposed);
                return _entriesByFrame.Count;
            }
        }

        public ref InputNativeSharedPtrResolver Resolver
        {
            get
            {
                TrecsDebugAssert.That(!_isDisposed);
                return ref _resolver;
            }
        }

        // Add every BlobId currently retained by a live input frame to <paramref name="output"/>.
        // See InputSharedHeap.AddReferencedBlobIds — same role in a saved recording's blob root set.
        internal void AddReferencedBlobIds(IterableHashSet<BlobId> output)
        {
            TrecsDebugAssert.That(!_isDisposed);
            foreach (var (_, list) in _entriesByFrame)
            {
                for (int i = 0; i < list.Count; i++)
                {
                    output.Add(list[i].BlobId);
                }
            }
        }

        internal InputNativeSharedHeapEntry ResolveEntry<T>(BlobId blobId)
            where T : unmanaged
        {
            TrecsDebugAssert.That(!_isDisposed);
            TrecsDebugAssert.That(!blobId.IsNull, "Attempted to resolve null blob address");

            TrecsAssert.That(
                _resolverEntries.TryGetValue(blobId, out var entry),
                "InputNativeSharedHeap could not resolve blob {0}",
                blobId.Value
            );

            TrecsAssert.That(
                entry.TypeHash == TypeId<T>.Value.Value,
                "Type hash mismatch for blob {0}: stored {1} != requested {2}",
                blobId.Value,
                entry.TypeHash,
                TypeId<T>.Value.Value
            );

            return entry;
        }

        internal InputNativeSharedPtr<T> Alloc<T>(int frame, BlobId blobId, in T value)
            where T : unmanaged
        {
            TrecsDebugAssert.That(!_isDisposed);
            TrecsDebugAssert.That(frame >= 0);
            var handle = _store.AllocNativeBlob<T>(blobId, in value);
            TrackEntry(frame, handle.BlobId, handle.Handle, descriptor: null);
            AddToResolver<T>(handle.BlobId);
            _log.Trace(
                "Allocated input native shared type={0} blobId={1} frame={2}",
                typeof(T),
                handle.BlobId,
                frame
            );
            return new InputNativeSharedPtr<T>(handle.BlobId);
        }

        // Interns the descriptor (registering its native source if first-seen) and acquires a
        // frame-scoped handle. The blob materializes from the registered builder; the descriptor is
        // tracked on the entry so Serialize can record it into the recording's input stream,
        // letting a fresh-process replay re-derive the blob.
        internal InputNativeSharedPtr<T> AcquireFromDescriptor<TDesc, T>(
            int frame,
            in TDesc descriptor
        )
            where T : unmanaged
        {
            TrecsDebugAssert.That(!_isDisposed);
            TrecsDebugAssert.That(frame >= 0);
            // Input is not simulation state, so the registered source is ambient-tagged: invisible
            // to the deterministic by-id resolve until the sim justifies the blob itself (interning
            // the same descriptor, or converting the in-hand pointer via
            // NativeSharedPtr.Acquire(world, inputPtr), both of which promote it). The intern still
            // journals the recipe — snapshot emission filters by the heap-derived referenced set,
            // so nothing leaks unless the sim actually comes to hold the blob.
            var blobId = _factory.Intern(in descriptor, deterministicContext: false);
            // Input-pipeline origin: makes the id eligible for the input-descriptor sweep once no
            // live frame references it (and the sim never promoted it).
            _factory.MarkInputDescriptor(blobId);
            // Share the journal's boxed copy (an Intern postcondition) rather than boxing the
            // struct descriptor again — keeps steady-state per-frame acquires allocation-free.
            // Fetched before EnsureResident, which runs the registered builder (user code that
            // could in principle reentrantly sweep the journal).
            var journaledDescriptor = _factory.GetJournaledDescriptor(blobId);
            _factory.EnsureResident(blobId);
            var handle = _store.CreateHandle(blobId);
            TrackEntry(frame, blobId, handle, journaledDescriptor);
            AddToResolver<T>(blobId);
            _log.Trace(
                "Acquired input native shared from descriptor type={0} blobId={1} frame={2}",
                typeof(T),
                blobId,
                frame
            );
            return new InputNativeSharedPtr<T>(blobId);
        }

        internal bool TryAcquire<T>(int frame, BlobId blobId, out InputNativeSharedPtr<T> ptr)
            where T : unmanaged
        {
            TrecsDebugAssert.That(!_isDisposed);
            if (!_factory.ContainsNativeBlob<T>(blobId))
            {
                ptr = default;
                return false;
            }
            _factory.EnsureResident(blobId);
            var handle = _store.CreateHandle(blobId);
            TrackEntry(frame, blobId, handle, descriptor: null);
            AddToResolver<T>(blobId);
            ptr = new InputNativeSharedPtr<T>(blobId);
            return true;
        }

        internal InputNativeSharedPtr<T> Acquire<T>(int frame, BlobId blobId)
            where T : unmanaged
        {
            if (!TryAcquire<T>(frame, blobId, out var ptr))
            {
                throw TrecsDebugAssert.CreateException(
                    "Acquire: no native blob exists at BlobId {0}",
                    blobId
                );
            }
            return ptr;
        }

        void AddToResolver<T>(BlobId blobId)
            where T : unmanaged
        {
            if (_blobIdRefCount.TryGetValue(blobId, out var count))
            {
                _blobIdRefCount[blobId] = count + 1;
                return;
            }

            _blobIdRefCount[blobId] = 1;

            var ptr = _store.GetNativeBlobPtr(blobId, TypeId<T>.Value.Value);
            var burstTypeHash = TypeId<T>.Value.Value;

            EnsureResolverCapacity(_resolverEntries.Count + 1);

#if ENABLE_UNITY_COLLECTIONS_CHECKS
            var safety = AtomicSafetyHandle.Create();
            AtomicSafetyHandle.SetBumpSecondaryVersionOnScheduleWrite(safety, true);
            _resolverEntries.Add(
                blobId,
                new InputNativeSharedHeapEntry(burstTypeHash, ptr, safety)
            );
#else
            _resolverEntries.Add(blobId, new InputNativeSharedHeapEntry(burstTypeHash, ptr));
#endif
        }

        void RemoveFromResolver(BlobId blobId)
        {
            if (!_blobIdRefCount.TryGetValue(blobId, out var count))
                return;

            count -= 1;
            if (count > 0)
            {
                _blobIdRefCount[blobId] = count;
                return;
            }

            _blobIdRefCount.Remove(blobId);

#if ENABLE_UNITY_COLLECTIONS_CHECKS
            if (_resolverEntries.TryGetValue(blobId, out var entry))
            {
                AtomicSafetyHandle.CheckDeallocateAndThrow(entry.Safety);
                AtomicSafetyHandle.Release(entry.Safety);
            }
#endif
            _resolverEntries.Remove(blobId);
        }

        void EnsureResolverCapacity(int needed)
        {
            if (_resolverEntries.Capacity >= needed)
                return;
            var newCapacity = Math.Max(needed, _resolverEntries.Capacity * 2);
            _resolverEntries.Capacity = newCapacity;
        }

        void TrackEntry(int frame, BlobId blobId, PtrHandle cacheHandle, object descriptor)
        {
            if (!_entriesByFrame.TryGetValue(frame, out var list))
            {
                list = _listPool.Count > 0 ? _listPool.Pop() : new List<Entry>();
                TrecsDebugAssert.That(list.Count == 0);
                _entriesByFrame.Add(frame, list);
            }
            list.Add(new Entry(blobId, cacheHandle, descriptor));
        }

        internal void ClearAtOrAfterFrame(int frame)
        {
            TrecsDebugAssert.That(!_isDisposed);
            TrecsDebugAssert.That(_frameRemoveBuffer.IsEmpty());

            foreach (var (f, _) in _entriesByFrame)
            {
                if (f >= frame)
                {
                    _frameRemoveBuffer.Add(f);
                }
            }
            foreach (var f in _frameRemoveBuffer)
            {
                ReleaseFrame(f);
            }
            _frameRemoveBuffer.Clear();
        }

        internal void ClearAtOrBeforeFrame(int frame)
        {
            TrecsDebugAssert.That(!_isDisposed);
            TrecsDebugAssert.That(_frameRemoveBuffer.IsEmpty());

            foreach (var (f, _) in _entriesByFrame)
            {
                if (f <= frame)
                {
                    _frameRemoveBuffer.Add(f);
                }
            }
            foreach (var f in _frameRemoveBuffer)
            {
                ReleaseFrame(f);
            }
            _frameRemoveBuffer.Clear();
        }

        internal void ClearAll()
        {
            TrecsDebugAssert.That(!_isDisposed);
            foreach (var (_, list) in _entriesByFrame)
            {
                for (int i = 0; i < list.Count; i++)
                {
                    // No per-entry RemoveFromResolver here — the resolver is bulk-cleared below.
                    ReleaseEntry(list[i]);
                }
                list.Clear();
                _listPool.Push(list);
            }
            _entriesByFrame.Clear();

#if ENABLE_UNITY_COLLECTIONS_CHECKS
            foreach (var kvp in _resolverEntries)
            {
                AtomicSafetyHandle.CheckDeallocateAndThrow(kvp.Value.Safety);
                AtomicSafetyHandle.Release(kvp.Value.Safety);
            }
#endif
            _resolverEntries.Clear();
            _blobIdRefCount.Clear();
        }

        void ReleaseFrame(int frame)
        {
            var list = _entriesByFrame.RemoveAndGet(frame);
            for (int i = 0; i < list.Count; i++)
            {
                ReleaseEntry(list[i]);
                RemoveFromResolver(list[i].BlobId);
            }
            list.Clear();
            _listPool.Push(list);
        }

        void ReleaseEntry(in Entry entry)
        {
            _store.DisposeHandle(entry.CacheHandle);
            if (entry.Descriptor != null)
            {
                // Counts toward the input-descriptor sweep trigger — releases are what turn
                // tracked descriptor ids into sweepable garbage.
                _factory.NoteInputDescriptorEntryReleased();
            }
        }

        /// <summary>
        /// Writes (frame -> [(BlobId, kind, payload?), ...]) pairs, tagged per
        /// <see cref="InputBlobKind"/>: descriptor-acquired blobs record their descriptor; opaque
        /// (eager) blobs record their type and persist their native bytes to
        /// <paramref name="opaqueBlobStore"/> (content-addressed; <paramref name="opaqueBaker"/> bakes
        /// them); plain references write nothing extra. See <see cref="InputSharedHeap.Serialize"/>.
        /// </summary>
        internal void Serialize(
            ISerializationWriter writer,
            IOpaqueBlobStore opaqueBlobStore = null,
            OpaqueBlobBaker opaqueBaker = null
        )
        {
            TrecsDebugAssert.That(!_isDisposed);

            writer.Write<int>("NumFrames", _entriesByFrame.Count);
            foreach (var (frame, list) in _entriesByFrame)
            {
                writer.Write<int>("Frame", frame);
                writer.Write<int>("NumEntries", list.Count);
                for (int i = 0; i < list.Count; i++)
                {
                    var entry = list[i];
                    writer.Write<BlobId>("BlobId", entry.BlobId);

                    if (entry.Descriptor != null)
                    {
                        writer.Write<byte>("Kind", (byte)InputBlobKind.Descriptor);
                        writer.WriteObject("Descriptor", entry.Descriptor);
                        continue;
                    }

                    var metadata = _store.GetBlobMetadata(entry.BlobId);
                    if (!metadata.IsEager)
                    {
                        writer.Write<byte>("Kind", (byte)InputBlobKind.Reference);
                        continue;
                    }

                    writer.Write<byte>("Kind", (byte)InputBlobKind.Opaque);
                    writer.Write<TypeId>("TypeId", metadata.TypeId);
                    if (opaqueBlobStore != null && !opaqueBlobStore.Contains(entry.BlobId))
                    {
                        TrecsAssert.That(
                            opaqueBaker != null,
                            "Persisting opaque input blob {0} requires an opaque-blob baker",
                            entry.BlobId
                        );
                        var blobId = entry.BlobId;
                        opaqueBlobStore.Write(
                            blobId,
                            stream =>
                                opaqueBaker.SerializeResidentBlob(
                                    _store,
                                    blobId,
                                    stream,
                                    OpaqueBlobBaker.CurrentFormatVersion
                                )
                        );
                    }
                }
            }
        }

        internal void Deserialize(
            ISerializationReader reader,
            IOpaqueBlobStore opaqueBlobStore = null,
            OpaqueBlobBaker opaqueBaker = null
        )
        {
            TrecsDebugAssert.That(!_isDisposed);
            ClearAll();

            var numFrames = reader.Read<int>("NumFrames");
            for (int i = 0; i < numFrames; i++)
            {
                var frame = reader.Read<int>("Frame");
                var numEntries = reader.Read<int>("NumEntries");
                for (int k = 0; k < numEntries; k++)
                {
                    var blobId = reader.Read<BlobId>("BlobId");
                    var kind = (InputBlobKind)reader.Read<byte>("Kind");
                    object descriptor = null;

                    switch (kind)
                    {
                        case InputBlobKind.Descriptor:
                            reader.ReadObject("Descriptor", ref descriptor);
                            // Re-register the source before CreateHandle materializes it — on a fresh
                            // process the descriptor blob isn't otherwise known (input blobs are absent
                            // from snapshots, so the world-state load didn't register it).
                            _factory.RestoreInputDescriptor(blobId, descriptor);
                            break;
                        case InputBlobKind.Opaque:
                            RestoreOpaqueInputBlob(
                                blobId,
                                reader.Read<TypeId>("TypeId"),
                                opaqueBlobStore,
                                opaqueBaker
                            );
                            break;
                        case InputBlobKind.Reference:
                            break;
                    }

                    _factory.EnsureResident(blobId);
                    var handle = _store.CreateHandle(blobId);
                    TrackEntry(frame, blobId, handle, descriptor);

                    // Rebuild resolver entries from BlobCache
                    if (!_blobIdRefCount.ContainsKey(blobId))
                    {
                        _blobIdRefCount[blobId] = 1;
                        var metadata = _store.GetBlobMetadata(blobId);
                        var ptr = _store.GetNativeBlobPtr(blobId, metadata.TypeId.Value);

                        EnsureResolverCapacity(_resolverEntries.Count + 1);

#if ENABLE_UNITY_COLLECTIONS_CHECKS
                        var safety = AtomicSafetyHandle.Create();
                        AtomicSafetyHandle.SetBumpSecondaryVersionOnScheduleWrite(safety, true);
                        _resolverEntries.Add(
                            blobId,
                            new InputNativeSharedHeapEntry(metadata.TypeId.Value, ptr, safety)
                        );
#else
                        _resolverEntries.Add(
                            blobId,
                            new InputNativeSharedHeapEntry(metadata.TypeId.Value, ptr)
                        );
#endif
                    }
                    else
                    {
                        _blobIdRefCount[blobId] += 1;
                    }
                }
            }
            _log.Debug("Deserialized {0} frames into InputNativeSharedHeap", _entriesByFrame.Count);
        }

        // Restores an opaque (eager) native input blob from the store before its handle is re-minted
        // and its resolver entry rebuilt. No-op if already resident.
        void RestoreOpaqueInputBlob(
            BlobId id,
            TypeId typeId,
            IOpaqueBlobStore opaqueBlobStore,
            OpaqueBlobBaker opaqueBaker
        )
        {
            if (_store.IsResident(id))
            {
                return;
            }
            TrecsAssert.That(
                opaqueBlobStore != null,
                "Replaying opaque input blob {0} requires an IOpaqueBlobStore",
                id
            );
            TrecsAssert.That(
                opaqueBaker != null,
                "Replaying opaque input blob {0} requires an opaque-blob baker",
                id
            );
            TrecsAssert.That(
                opaqueBlobStore.TryOpenRead(id, out var blobStream),
                "IOpaqueBlobStore has no bytes for opaque input blob {0}",
                id
            );
            TrecsAssert.That(
                TypeId.TryToType(typeId, out var blobType),
                "Could not resolve type id {0} for opaque input blob {1}",
                typeId,
                id
            );
            // One-shot restore: deserialize and seed the cache directly as an eager blob — the
            // read-side mirror of the bake. No lazy source needed (the input log re-supplies it).
            object blob;
            using (blobStream)
            {
                blob = opaqueBaker.Deserialize(
                    blobStream,
                    blobType,
                    isNative: true,
                    OpaqueBlobBaker.CurrentFormatVersion,
                    _store.NativeBlobBoxPool
                );
            }
            _store.InsertEagerBlob(id, blob);
        }

        internal void Dispose()
        {
            TrecsDebugAssert.That(!_isDisposed);
            ClearAll();
            _resolverEntries.Dispose();
            _isDisposed = true;
        }
    }
}
