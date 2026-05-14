using System;
using System.Collections.Generic;
using Trecs.Collections;
using Trecs.Internal;
using Unity.Collections.LowLevel.Unsafe;

namespace Trecs
{
    /// <summary>
    /// Frame-scoped heap that owns unmanaged memory via <see cref="NativeUniquePtr{T}"/>.
    /// Entries are tagged with the frame they were allocated on and can be bulk-cleared
    /// by frame range. Storage is provided by a shared <see cref="NativeChunkStore"/>
    /// (also used by <see cref="NativeUniqueHeap"/>); this class only manages the
    /// (handle, type, frame) bookkeeping.
    /// </summary>
    public sealed class FrameScopedNativeUniqueHeap
    {
        readonly TrecsLog _log;

        readonly NativeChunkStore _chunkStore;

        // Handle → Type via the shared registry (same Serialize/Deserialize loop as the
        // other chunk-store heaps); the per-entry frame number lives in a parallel
        // dictionary kept in sync. Both use DenseDictionary for deterministic iteration.
        readonly HandleTypeRegistry _registry = new();
        readonly DenseDictionary<uint, int> _framesByHandle = new();
        readonly List<uint> _removeBuffer = new();
        bool _isDisposed;

        internal FrameScopedNativeUniqueHeap(TrecsLog log, NativeChunkStore chunkStore)
        {
            TrecsAssert.IsNotNull(chunkStore);
            _log = log;
            _chunkStore = chunkStore;
        }

        public int NumEntries
        {
            get
            {
                TrecsAssert.That(!_isDisposed);
                return _registry.Count;
            }
        }

        internal NativeUniquePtr<T> Alloc<T>(int frame, in T value)
            where T : unmanaged
        {
            TrecsAssert.That(!_isDisposed);
            TrecsAssert.That(frame >= 0);

            // Plain Alloc — under the unified immediate-write model, new entries are
            // visible to Burst jobs as soon as Alloc returns. Frame-scoped allocations
            // made in the input phase resolve correctly from jobs scheduled later in
            // the same step without needing a flush boundary.
            var handle = _chunkStore.Alloc(
                UnsafeUtility.SizeOf<T>(),
                UnsafeUtility.AlignOf<T>(),
                TypeHash<T>.Value,
                out var address
            );
            unsafe
            {
                UnsafeUtility.WriteArrayElement(address.ToPointer(), 0, value);
            }

            _registry.Add(handle.Value, typeof(T));
            _framesByHandle.Add(handle.Value, frame);

            _log.Trace(
                "Allocated frame-scoped NativeUniquePtr<{0}> with handle {1} for frame {2}",
                typeof(T),
                handle.Value,
                frame
            );

            return new NativeUniquePtr<T>(handle);
        }

        internal NativeUniquePtr<T> Alloc<T>(int frame)
            where T : unmanaged
        {
            return Alloc<T>(frame, default);
        }

        /// <summary>
        /// Takes ownership of an existing native pointer. See
        /// <see cref="NativeUniqueHeap.AllocTakingOwnership{T}"/> for the contract.
        /// </summary>
        internal NativeUniquePtr<T> AllocTakingOwnership<T>(int frame, NativeBlobAllocation alloc)
            where T : unmanaged
        {
            TrecsAssert.That(!_isDisposed);
            TrecsAssert.That(frame >= 0);
            TrecsAssert.That(alloc.Ptr != IntPtr.Zero, "AllocTakingOwnership: null pointer");

            var handle = _chunkStore.AllocExternal(
                alloc.Ptr,
                alloc.AllocSize,
                alloc.Alignment,
                TypeHash<T>.Value
            );
            _registry.Add(handle.Value, typeof(T));
            _framesByHandle.Add(handle.Value, frame);
            return new NativeUniquePtr<T>(handle);
        }

        internal bool ContainsEntry(uint address)
        {
            TrecsAssert.That(!_isDisposed);
            return _registry.ContainsKey(address);
        }

        internal NativeChunkStoreEntry ResolveEntry<T>(uint address, int frame)
            where T : unmanaged
        {
            TrecsAssert.That(!_isDisposed);
            TrecsAssert.That(
                UnityThreadHelper.IsMainThread,
                "ResolveEntry must be called from the main thread; jobs use NativeUniquePtrResolver"
            );
            TrecsAssert.That(address != 0, "Attempted to resolve null address");

            if (!_framesByHandle.TryGetValue(address, out var storedFrame))
            {
                throw TrecsAssert.CreateException(
                    "Attempted to resolve invalid frame-scoped native unique heap address ({0}) for frame {1}",
                    address,
                    frame
                );
            }

            TrecsAssert.IsEqual(
                storedFrame,
                frame,
                "Attempted to get input memory for different frame than it was allocated for"
            );
            // Registry lookup must succeed if the frame lookup did (kept in sync).
            _registry.TryGetType(address, out var storedType);
            TrecsAssert.That(
                storedType == typeof(T),
                "Type mismatch resolving frame-scoped NativeUniquePtr: stored {0}, requested {1}",
                storedType,
                typeof(T)
            );

            return _chunkStore.ResolveEntry(new PtrHandle(address));
        }

        internal unsafe void* ResolveUnsafePtr<T>(uint address, int frame)
            where T : unmanaged
        {
            return ResolveEntry<T>(address, frame).Address.ToPointer();
        }

        internal void ClearAtOrAfterFrame(int frame)
        {
            TrecsAssert.That(!_isDisposed);

            TrecsAssert.That(_removeBuffer.IsEmpty());
            foreach (var (key, entryFrame) in _framesByHandle)
            {
                if (entryFrame >= frame)
                {
                    _removeBuffer.Add(key);
                }
            }

            foreach (var key in _removeBuffer)
            {
                DisposeEntry(key);
            }

            _removeBuffer.Clear();
        }

        internal void ClearAtOrBeforeFrame(int frame)
        {
            TrecsAssert.That(!_isDisposed);

            TrecsAssert.That(_removeBuffer.IsEmpty());
            foreach (var (key, entryFrame) in _framesByHandle)
            {
                if (entryFrame <= frame)
                {
                    _removeBuffer.Add(key);
                }
            }

            foreach (var key in _removeBuffer)
            {
                DisposeEntry(key);
            }

            _removeBuffer.Clear();
        }

        void DisposeEntry(uint address)
        {
            TrecsAssert.That(!_isDisposed);
            if (!_registry.ContainsKey(address))
            {
                throw TrecsAssert.CreateException(
                    "Attempted to dispose invalid frame-scoped native unique heap address ({0})",
                    address
                );
            }
            // Chunk-store Free first; if it throws (job still using the handle), the
            // managed-side bookkeeping stays intact so the caller can retry after
            // completing the job.
            _chunkStore.Free(new PtrHandle(address));
            _registry.TryRemove(address);
            _framesByHandle.TryRemove(address);
        }

        internal void ClearAll()
        {
            TrecsAssert.That(!_isDisposed);
            foreach (var address in _registry.Handles)
            {
                _chunkStore.Free(new PtrHandle(address));
            }
            _registry.Clear();
            _framesByHandle.Clear();
        }

        internal void Dispose()
        {
            TrecsAssert.That(!_isDisposed);
            ClearAll();
            _isDisposed = true;
        }

        /// <summary>
        /// Writes the managed-side (handle → type, frame) bookkeeping. Per-entry layout:
        /// <c>Address: uint, Frame: int, InnerTypeId: int</c>. Slot memory and side-table
        /// state are dumped in bulk by <c>NativeChunkStore.Serialize</c> which must run
        /// before this.
        /// </summary>
        internal void Serialize(ISerializationWriter writer)
        {
            TrecsAssert.That(!_isDisposed);

            writer.Write<int>("NumEntries", _registry.Count);

            // Custom layout (with Frame) — can't delegate to _registry.Serialize since
            // that doesn't know about the per-entry frame metadata. Iteration order
            // matches the registry's DenseDictionary, so it's deterministic.
            foreach (var (address, type) in _registry.All)
            {
                writer.Write<uint>("Address", address);
                writer.Write<int>("Frame", _framesByHandle[address]);
                writer.Write<int>("InnerTypeId", TypeIdProvider.GetTypeId(type));
            }
        }

        /// <summary>
        /// Restores managed-side bookkeeping. Assumes <c>NativeChunkStore.Deserialize</c>
        /// already ran so every restored handle is live.
        /// </summary>
        internal void Deserialize(ISerializationReader reader)
        {
            TrecsAssert.That(!_isDisposed);
            TrecsAssert.That(_registry.Count == 0);
            TrecsAssert.That(_framesByHandle.Count == 0);

            var numEntries = reader.Read<int>("NumEntries");
            _framesByHandle.EnsureCapacity(numEntries);

            for (int i = 0; i < numEntries; i++)
            {
                var address = reader.Read<uint>("Address");
                var frame = reader.Read<int>("Frame");
                var innerTypeId = reader.Read<int>("InnerTypeId");
                _registry.Add(address, TypeIdProvider.GetTypeFromId(innerTypeId));
                _framesByHandle.Add(address, frame);
            }

            _log.Debug("Deserialized {0} frame-scoped native unique entries", _registry.Count);
        }
    }
}
