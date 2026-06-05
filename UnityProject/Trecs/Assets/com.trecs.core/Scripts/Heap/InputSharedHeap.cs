using System.Collections.Generic;
using Trecs.Collections;
using Trecs.Internal;

namespace Trecs
{
    /// <summary>
    /// How an input-heap entry's blob is recovered on recording replay — written as a per-entry tag
    /// in the input stream. Shared by <see cref="InputSharedHeap"/> and
    /// <see cref="InputNativeSharedHeap"/>.
    /// </summary>
    internal enum InputBlobKind : byte
    {
        // Expected to already be resident on replay: a reference to a blob persisted elsewhere (a sim
        // blob restored from the snapshot, or another input blob alloc'd earlier in the stream).
        Reference = 0,

        // Acquired from a descriptor: the descriptor is recorded so the source can be re-registered
        // and the bytes re-derived (the input-side analog of the snapshot descriptor journal).
        Descriptor = 1,

        // An opaque (eager) blob with no descriptor/source: its bytes are persisted to the
        // IOpaqueBlobStore and restored before the frame's handle is re-minted.
        Opaque = 2,
    }

    /// <summary>
    /// Managed-side analog of <see cref="InputNativeSharedHeap"/>. Tracks
    /// refcount handles into the shared <see cref="BlobCache"/> for managed
    /// blobs allocated through the input pipeline
    /// (<see cref="InputSharedPtr{T}"/>). Releases handles in bulk when a
    /// frame is trimmed; per-frame <see cref="List{PtrHandle}"/>s are pooled.
    /// </summary>
    public sealed class InputSharedHeap
    {
        readonly TrecsLog _log;
        readonly BlobCache _store;
        readonly BlobFactory _factory;

        // (frame -> list of (BlobId, refcount handle, descriptor?)). BlobId is needed to recreate
        // the refcount handle on Deserialize; the handle itself is used only for Release on frame
        // trim; the descriptor is non-null only for descriptor-acquired blobs (see Entry).
        readonly IterableDictionary<int, List<Entry>> _entriesByFrame = new();
        readonly Stack<List<Entry>> _listPool = new();
        readonly List<int> _frameRemoveBuffer = new();

        bool _isDisposed;

        readonly struct Entry
        {
            public readonly BlobId BlobId;
            public readonly PtrHandle CacheHandle;

            // Non-null only for descriptor-acquired input blobs (InputSharedPtr.Acquire<TDesc,T>).
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

        internal InputSharedHeap(TrecsLog log, BlobCache store, BlobFactory factory)
        {
            _log = log;
            _store = store;
            _factory = factory;
        }

        public int NumLiveFrames
        {
            get
            {
                TrecsDebugAssert.That(!_isDisposed);
                return _entriesByFrame.Count;
            }
        }

        // Add every BlobId currently retained by a live input frame to <paramref name="output"/>.
        // Together with the sim heaps' AddReferencedBlobIds, this forms a saved recording's
        // blob root set — the ids the input-stream window references — as opposed to
        // BlobCache.GetAllActiveBlobIds, which would also sweep in unrelated ambient pins.
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

        // Content-addressed: derives the BlobId by serializing and hashing the value (so identical
        // content dedups and the id is stable across machines/runs), makes it resident, then pins a
        // frame-scoped handle. The managed mirror of the simulation-side SharedPtr.Alloc; the caller
        // never names the blob. The blob has no descriptor/source, so it serializes into a recording
        // as an opaque (eager) entry whose bytes are persisted.
        internal InputSharedPtr<T> Alloc<T>(int frame, T value)
            where T : class
        {
            TrecsDebugAssert.That(!_isDisposed);
            TrecsDebugAssert.That(frame >= 0);
            var blobId = _factory.AllocManagedContentAddressed(value);
            var handle = _store.CreateHandle(blobId);
            TrackEntry(frame, blobId, handle, descriptor: null);
            _log.Trace(
                "Allocated input managed shared (content-addressed) type={0} blobId={1} frame={2}",
                typeof(T),
                blobId,
                frame
            );
            return new InputSharedPtr<T>(blobId);
        }

        // Interns the descriptor (registering its source if first-seen) and acquires a frame-scoped
        // handle. The blob materializes from the registered builder on first access — so unlike
        // Alloc, no value is supplied. The descriptor is tracked on the entry so Serialize can
        // record it into the recording's input stream, letting a fresh-process replay re-derive
        // the blob.
        internal InputSharedPtr<T> AcquireFromDescriptor<TDesc, T>(int frame, in TDesc descriptor)
            where T : class
        {
            TrecsDebugAssert.That(!_isDisposed);
            TrecsDebugAssert.That(frame >= 0);
            // Input is not simulation state, so the registered source is ambient-tagged: invisible
            // to the deterministic by-id resolve until the sim justifies the blob itself (interning
            // the same descriptor, or converting the in-hand pointer via
            // SharedPtr.Acquire(world, inputPtr), both of which promote it). The intern still
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
            _log.Trace(
                "Acquired input managed shared from descriptor type={0} blobId={1} frame={2}",
                typeof(T),
                blobId,
                frame
            );
            return new InputSharedPtr<T>(blobId);
        }

        internal bool TryAcquire<T>(int frame, BlobId blobId, out InputSharedPtr<T> ptr)
            where T : class
        {
            TrecsDebugAssert.That(!_isDisposed);
            if (!_factory.ContainsManagedBlob<T>(blobId))
            {
                ptr = default;
                return false;
            }
            _factory.EnsureResident(blobId);
            var handle = _store.CreateHandle(blobId);
            TrackEntry(frame, blobId, handle, descriptor: null);
            ptr = new InputSharedPtr<T>(blobId);
            return true;
        }

        internal InputSharedPtr<T> Acquire<T>(int frame, BlobId blobId)
            where T : class
        {
            if (!TryAcquire<T>(frame, blobId, out var ptr))
            {
                throw TrecsDebugAssert.CreateException(
                    "Acquire: no managed blob exists at BlobId {0}",
                    blobId
                );
            }
            return ptr;
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
                ReleaseEntries(list);
                _listPool.Push(list);
            }
            _entriesByFrame.Clear();
        }

        void ReleaseFrame(int frame)
        {
            var list = _entriesByFrame.RemoveAndGet(frame);
            ReleaseEntries(list);
            _listPool.Push(list);
        }

        void ReleaseEntries(List<Entry> list)
        {
            for (int i = 0; i < list.Count; i++)
            {
                _store.DisposeHandle(list[i].CacheHandle);
                if (list[i].Descriptor != null)
                {
                    // Counts toward the input-descriptor sweep trigger — releases are what turn
                    // tracked descriptor ids into sweepable garbage.
                    _factory.NoteInputDescriptorEntryReleased();
                }
            }
            list.Clear();
        }

        /// <summary>
        /// Writes (frame -> [(BlobId, kind, payload?), ...]) pairs. The refcount handle isn't
        /// serialized — it's re-minted from the BlobId on Deserialize via
        /// <see cref="BlobCache.CreateHandle"/>. Each entry is tagged (<see cref="InputBlobKind"/>):
        /// descriptor-acquired blobs write their descriptor so the source can be re-registered;
        /// opaque (eager) blobs write their type and persist their bytes to
        /// <paramref name="opaqueBlobStore"/> (content-addressed, so an id already present is skipped),
        /// using <paramref name="opaqueBaker"/> to bake them; plain references write nothing extra and
        /// rely on the blob being resident on replay. Persisting opaque blobs requires both a store
        /// and a baker; without a store the refs are still tagged (load then fails loudly).
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

                    // No descriptor: an opaque (eager) blob must persist its bytes; anything else is a
                    // reference to a blob persisted elsewhere (sim snapshot, or an earlier input blob).
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
            // Defensive: callers contract is ClearAll() before Deserialize.
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
                }
            }
            _log.Debug("Deserialized {0} frames into InputSharedHeap", _entriesByFrame.Count);
        }

        // Restores an opaque (eager) managed input blob from the store before its handle is re-minted.
        // No-op if already resident (also a sim blob, or restored earlier this load).
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
                    isNative: false,
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
            _isDisposed = true;
        }
    }
}
