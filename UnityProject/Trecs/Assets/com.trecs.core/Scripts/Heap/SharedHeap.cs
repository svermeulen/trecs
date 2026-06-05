using System;
using System.Collections.Generic;
using System.Linq;
using Trecs.Collections;
using Trecs.Internal;

namespace Trecs
{
    /// <summary>
    /// Manages reference-counted managed (class) allocations backing <see cref="SharedPtr{T}"/>.
    /// Accessed internally through <see cref="WorldAccessor"/>; not typically used directly.
    /// <para>
    /// Identity is the <see cref="BlobId"/>: a <see cref="SharedPtr{T}"/> stores only its blob id,
    /// and every operation keys off it. The heap keeps one ref-count per active blob (the number of
    /// live <see cref="SharedPtr{T}"/>s pointing at it) and holds exactly one <see cref="BlobCache"/>
    /// pin per active blob — clones bump the ref-count, they do not mint a per-clone handle. This
    /// mirrors <see cref="NativeSharedHeap"/>, where the slot ref-count plays the same role.
    /// </para>
    /// </summary>
    internal sealed class SharedHeap
    {
        readonly TrecsLog _log;

        readonly BlobCache _store;
        readonly BlobFactory _factory;

        // One entry per active blob: the heap-side ref-count (live SharedPtr count) plus the stored
        // type, used to validate typed reads. The keyset is the active-blob set.
        readonly IterableDictionary<BlobId, BlobInfo> _activeBlobs = new();

        // The single BlobCache pin held per active blob. Created on the 0->1 transition (first
        // acquire) and disposed on the 1->0 transition (last dispose). Not per-clone.
        readonly IterableDictionary<BlobId, PtrHandle> _blobCacheHandles = new();

        // Monotonic stamp of _activeBlobs' keyset *membership and iteration order* — bumped on
        // every transition that adds/removes/reorders keys (add, last-handle remove, clear,
        // deserialize), NOT on ref-count-only updates. Lets the snapshot serializer skip
        // re-collecting its referenced-blob set (AddReferencedBlobIds walks this keyset) when
        // nothing changed since the last save — the steady-state rollback case. A missed bump
        // site would silently corrupt the snapshot wire form, so any new mutation of _activeBlobs'
        // keyset MUST bump this; DEBUG builds re-collect and verify on every skipped rebuild.
        long _blobMembershipVersion;

        bool _isDisposed;

        // See _blobMembershipVersion.
        internal long BlobMembershipVersion => _blobMembershipVersion;

        public SharedHeap(TrecsLog log, BlobCache store, BlobFactory factory)
        {
            _log = log;
            _store = store;
            _factory = factory;
        }

        // Exposed so WorldAccessor (which already references SharedHeap) can surface
        // the cache without a separate plumbing path. The same instance is shared
        // with NativeSharedHeap and the frame-scoped heaps.
        internal BlobCache BlobCache => _store;

        public int NumEntries
        {
            get
            {
                TrecsDebugAssert.That(!_isDisposed);
                return _activeBlobs.Count;
            }
        }

        // Add every BlobId this heap currently references (its active-blob keyset) to
        // <paramref name="output"/>. This is the correct source for a snapshot's opaque-blob
        // reference set — the blobs the *serialized state* holds — as opposed to
        // BlobCache.GetAllActiveBlobIds, which returns every blob with any live cache handle
        // (including non-ECS pins such as the rewind buffer's snapshot anchors). Iterates the
        // same _activeBlobs structure the heap serializes, so the order is part of the same
        // deterministic wire form.
        internal void AddReferencedBlobIds(IterableHashSet<BlobId> output)
        {
            TrecsDebugAssert.That(!_isDisposed);
            foreach (var blobId in _activeBlobs.Keys)
            {
                output.Add(blobId);
            }
        }

        // ─── Typed reads (by blob id) ───────────────────────────────────

        internal bool TryGetBlobDirect<T>(BlobId blobId, out T blob)
            where T : class
        {
            if (_activeBlobs.TryGetValue(blobId, out var info))
            {
                TrecsDebugAssert.That(info.TypeHash == TypeId<T>.Value);
                return _store.TryGetManagedBlob<T>(blobId, out blob);
            }

            blob = default;
            return false;
        }

        internal bool ContainsBlobDirect(BlobId blobId)
        {
            return _activeBlobs.ContainsKey(blobId);
        }

        public T GetBlob<T>(BlobId blobId)
            where T : class
        {
            TrecsDebugAssert.That(!_isDisposed);

            if (_activeBlobs.TryGetValue(blobId, out var info))
            {
                TrecsDebugAssert.That(info.TypeHash == TypeId<T>.Value);
                return _store.GetManagedBlob<T>(blobId);
            }

            throw TrecsDebugAssert.CreateException(
                "Attempted to get an inactive or unrecognized blob {0}",
                blobId
            );
        }

        // ─── Registration / acquire ─────────────────────────────────────

        public void RegisterBlob<T>(BlobId blobId, Func<T> factory)
            where T : class
        {
            TrecsDebugAssert.That(!_isDisposed);
            _factory.RegisterManagedBlob(blobId, factory);
        }

        public void RegisterBlob<T>(BlobId blobId, T value)
            where T : class
        {
            TrecsDebugAssert.That(!_isDisposed);
            _factory.RegisterManagedBlob(blobId, value);
        }

        public bool TryGetBlobById<T>(BlobId blobId, out SharedPtr<T> ptr)
            where T : class
        {
            TrecsDebugAssert.That(!_isDisposed);

            if (_activeBlobs.ContainsKey(blobId))
            {
                ptr = IncrementRefExisting<T>(blobId);
                return true;
            }

            // Deterministic resolve: succeeds iff held by this heap (above) or backed by a
            // deterministically registered source — never raw cache residency. The cache is not
            // simulation state, so "resident right now" (ambient anchor pins, eviction timing) must
            // not influence a gated resolve: a sourceless or ambient-only id answers false here in
            // every run of the same timeline, which makes false a stable, replayable result. See
            // docs/maintainers/maintainer-docs/blob-determinism-domains.md.
            if (_factory.ContainsManagedBlobDeterministic<T>(blobId))
            {
                _factory.EnsureResident(blobId);
                var blobCacheHandleId = _store.CreateHandle(blobId);
                ptr = AddBlobEntry<T>(blobId, blobCacheHandleId);
                return true;
            }

            ptr = default;
            return false;
        }

        public SharedPtr<T> GetBlobById<T>(BlobId blobId)
            where T : class
        {
            if (!TryGetBlobById<T>(blobId, out var ptr))
            {
                throw TrecsDebugAssert.CreateException(
                    "Blob {0} is not deterministically reachable from simulation: it is neither "
                        + "held by the shared heap nor backed by a deterministically registered "
                        + "source. Hold a SharedPtr to keep it alive, register a source at setup "
                        + "(SharedPtr.Register / SharedAnchor.Register), or pass the data through "
                        + "the input stream and convert the in-hand payload with "
                        + "SharedPtr.Acquire(world, inputPtr). Cache residency alone is "
                        + "non-deterministic state and is deliberately not consulted.",
                    blobId
                );
            }

            return ptr;
        }

        /// <summary>
        /// Pins <paramref name="blobId"/> into the heap on the strength of a justification the
        /// caller holds <i>in hand</i>: content it just allocated (content-addressed
        /// <see cref="SharedPtr.Alloc{T}(WorldAccessor, T)"/>) or an input payload delivered for the
        /// current frame (<see cref="SharedPtr.Acquire{T}(WorldAccessor, InputSharedPtr{T})"/>).
        /// Trusts residency directly instead of consulting the source registry — the justification
        /// is the caller's value, not the cache state, so this stays deterministic where a general
        /// by-id resolve of a sourceless blob would not. The blob must be resident.
        /// </summary>
        internal SharedPtr<T> PinResident<T>(BlobId blobId)
            where T : class
        {
            TrecsDebugAssert.That(!_isDisposed);

            if (_activeBlobs.ContainsKey(blobId))
            {
                return IncrementRefExisting<T>(blobId);
            }

            TrecsAssert.That(
                _store.IsResident(blobId),
                "PinResident: blob {0} is not resident. The caller's in-hand justification "
                    + "(just-allocated content, or an input payload for the current frame) should "
                    + "guarantee residency — convert input payloads in the frame that delivers them.",
                blobId
            );
#if DEBUG
            if (_store.TryGetResidentTypeInfo(blobId, out var typeId, out var isNative))
            {
                TrecsDebugAssert.That(
                    !isNative && typeof(T).IsAssignableFrom(TypeId.ToType(typeId)),
                    "PinResident: resident blob {0} is not a managed blob assignable to {1}",
                    blobId,
                    typeof(T)
                );
            }
#endif
            return AddBlobEntry<T>(blobId, _store.CreateHandle(blobId));
        }

        SharedPtr<T> AddBlobEntry<T>(BlobId blobId, PtrHandle blobCacheHandleId)
            where T : class
        {
            _activeBlobs.Add(blobId, new BlobInfo { RefCount = 1, TypeHash = TypeId<T>.Value });
            _blobMembershipVersion++;
            _blobCacheHandles.Add(blobId, blobCacheHandleId);
            _log.Trace("Added new blob {0}", blobId);
            return new SharedPtr<T>(blobId);
        }

        SharedPtr<T> IncrementRefExisting<T>(BlobId blobId)
            where T : class
        {
            ref var info = ref _activeBlobs.GetValueByRef(blobId);
            TrecsDebugAssert.That(info.TypeHash == TypeId<T>.Value);
            info.RefCount += 1;
            return new SharedPtr<T>(blobId);
        }

        // Clone: a known-active blob gains another live SharedPtr. Type-checked against the stored
        // blob type. Callers (SharedPtr.Clone) hold a non-null ptr, so the blob must be active.
        public bool TryClone<T>(BlobId blobId, out SharedPtr<T> result)
            where T : class
        {
            TrecsDebugAssert.That(!_isDisposed);

            if (!_activeBlobs.ContainsKey(blobId))
            {
                result = default;
                return false;
            }

            result = IncrementRefExisting<T>(blobId);
            return true;
        }

        public void ClearAll(bool warnUndisposed)
        {
            TrecsDebugAssert.That(!_isDisposed);

            if (_activeBlobs.Count > 0)
            {
                if (warnUndisposed && _log.IsWarningEnabled())
                {
                    var debugStrings = new HashSet<Type>();

                    foreach (var (blobId, _) in _activeBlobs)
                    {
                        debugStrings.Add(_store.GetManagedBlobType(blobId));
                    }

                    _log.Warning(
                        "Found {0} managed shared blobs that were not disposed, with types: {1}",
                        _activeBlobs.Count,
                        debugStrings.Select(x => x.GetPrettyName()).Join(", ")
                    );
                }

                // Every active blob holds exactly one cache handle, so disposing
                // _blobCacheHandles' values and clearing both maps releases
                // everything. Iterate the handles map directly (read-only) and
                // defer the removal to the bulk Clear below.
                TrecsDebugAssert.That(_blobCacheHandles.Count == _activeBlobs.Count);

                foreach (var (_, blobHandle) in _blobCacheHandles)
                {
                    _store.DisposeHandle(blobHandle);
                }

                _blobCacheHandles.Clear();
                _activeBlobs.Clear();
                _blobMembershipVersion++;
            }

            TrecsDebugAssert.That(_activeBlobs.Count == 0);
            TrecsDebugAssert.That(_blobCacheHandles.Count == 0);
        }

        public void Dispose()
        {
            TrecsDebugAssert.That(!_isDisposed);
            ClearAll(warnUndisposed: true);
            _isDisposed = true;
        }

        public void DecrementRef(BlobId blobId)
        {
            TrecsDebugAssert.That(!_isDisposed);

            if (!_activeBlobs.TryGetIndex(blobId, out var index))
            {
                throw TrecsDebugAssert.CreateException(
                    "Attempted to dispose an inactive or unrecognized blob {0} (double-dispose?)",
                    blobId
                );
            }

            ref var info = ref _activeBlobs.GetValueAtIndexByRef(index);
            info.RefCount -= 1;
            TrecsDebugAssert.That(info.RefCount >= 0);

            if (info.RefCount == 0)
            {
                _activeBlobs.RemoveMustExist(blobId);
                _blobMembershipVersion++;

                var blobHandle = _blobCacheHandles.RemoveAndGet(blobId);
                _store.DisposeHandle(blobHandle);
                _log.Trace("Disposed last handle for blob {0}", blobId);
            }
        }

        public void Deserialize(ISerializationReader reader)
        {
            TrecsDebugAssert.That(!_isDisposed);
            // Defensive: callers contract is ClearAll() before Deserialize, but
            // a wrong-order call would silently corrupt state — warn-then-clean
            // so the contract violation is observable in dev builds while still
            // recoverable in release.
            ClearAll(warnUndisposed: true);

            reader.ReadInPlace<IterableDictionary<BlobId, BlobInfo>>("_activeBlobs", _activeBlobs);
            // The wire repopulated the keyset wholesale (bypassing AddBlobEntry), so the
            // ClearAll above may not have bumped (empty heap) and the adds never will.
            _blobMembershipVersion++;

            foreach (var (blobId, _) in _activeBlobs)
            {
                _factory.EnsureResident(blobId);
                _blobCacheHandles.Add(blobId, _store.CreateHandle(blobId));
            }
        }

        public void Serialize(ISerializationWriter writer)
        {
            writer.Write<IterableDictionary<BlobId, BlobInfo>>("_activeBlobs", _activeBlobs);
        }

        public struct BlobInfo
        {
            public int RefCount;
            public TypeId TypeHash;
        }
    }
}
