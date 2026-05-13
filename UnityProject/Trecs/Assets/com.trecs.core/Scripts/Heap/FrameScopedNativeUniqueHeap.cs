using System;
using System.Collections.Generic;
using Trecs.Collections;
using Trecs.Internal;
using Unity.Burst;
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
        readonly DenseDictionary<uint, FrameEntry> _activeEntries = new();
        readonly List<uint> _removeBuffer = new();
        bool _isDisposed;

        public FrameScopedNativeUniqueHeap(TrecsLog log, NativeChunkStore chunkStore)
        {
            Assert.IsNotNull(chunkStore);
            _log = log;
            _chunkStore = chunkStore;
        }

        public int NumEntries
        {
            get
            {
                Assert.That(!_isDisposed);
                return _activeEntries.Count;
            }
        }

        internal NativeUniquePtr<T> Alloc<T>(int frame, in T value)
            where T : unmanaged
        {
            Assert.That(!_isDisposed);
            Assert.That(frame >= 0);

            // AllocImmediate (not the deferred-pending Alloc) — frame-scoped entries are
            // allocated during the input phase and must be resolvable from Burst jobs scheduled
            // in the same step, before the world's next FlushPendingOperations.
            var handle = _chunkStore.AllocImmediate(
                UnsafeUtility.SizeOf<T>(),
                UnsafeUtility.AlignOf<T>(),
                TypeHash<T>.Value,
                out var address
            );
            unsafe
            {
                UnsafeUtility.WriteArrayElement(address.ToPointer(), 0, value);
            }

            _activeEntries.Add(handle.Value, new FrameEntry(typeof(T), frame));

            _log.Trace(
                "Allocated frame-scoped NativeUniquePtr<{}> with handle {} for frame {}",
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
            Assert.That(!_isDisposed);
            Assert.That(frame >= 0);
            Assert.That(alloc.Ptr != IntPtr.Zero, "AllocTakingOwnership: null pointer");

            var handle = _chunkStore.AllocExternalImmediate(
                alloc.Ptr,
                alloc.AllocSize,
                alloc.Alignment,
                TypeHash<T>.Value
            );
            _activeEntries.Add(handle.Value, new FrameEntry(typeof(T), frame));
            return new NativeUniquePtr<T>(handle);
        }

        internal bool ContainsEntry(uint address)
        {
            Assert.That(!_isDisposed);
            return _activeEntries.ContainsKey(address);
        }

        internal NativeChunkStoreEntry ResolveEntry<T>(uint address, int frame)
            where T : unmanaged
        {
            Assert.That(!_isDisposed);
            Assert.That(
                UnityThreadHelper.IsMainThread,
                "ResolveEntry must be called from the main thread; jobs use NativeUniquePtrResolver"
            );
            Assert.That(address != 0, "Attempted to resolve null address");

            if (!_activeEntries.TryGetValue(address, out var frameEntry))
            {
                throw Assert.CreateException(
                    "Attempted to resolve invalid frame-scoped native unique heap address ({}) for frame {}",
                    address,
                    frame
                );
            }

            Assert.IsEqual(
                frameEntry.Frame,
                frame,
                "Attempted to get input memory for different frame than it was allocated for"
            );
            Assert.That(
                frameEntry.Type == typeof(T),
                "Type mismatch resolving frame-scoped NativeUniquePtr: stored {}, requested {}",
                frameEntry.Type,
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
            Assert.That(!_isDisposed);

            Assert.That(_removeBuffer.IsEmpty());
            foreach (var (key, entry) in _activeEntries)
            {
                if (entry.Frame >= frame)
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
            Assert.That(!_isDisposed);

            Assert.That(_removeBuffer.IsEmpty());
            foreach (var (key, entry) in _activeEntries)
            {
                if (entry.Frame <= frame)
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
            Assert.That(!_isDisposed);
            if (!_activeEntries.TryRemove(address, out _))
            {
                throw Assert.CreateException(
                    "Attempted to dispose invalid frame-scoped native unique heap address ({})",
                    address
                );
            }
            _chunkStore.Free(new PtrHandle(address));
        }

        /// <summary>
        /// Applies any deferred chunk-store operations. Called by the world's flush pipeline.
        /// </summary>
        internal void FlushPendingOperations()
        {
            Assert.That(!_isDisposed);
            // The shared chunk store flushes once (via NativeUniqueHeap.FlushPendingOperations);
            // we don't double-flush here, but we keep the method for symmetry / callers that
            // bypass NativeUniqueHeap.
            _chunkStore.FlushPendingOperations();
        }

        internal void ClearAll()
        {
            Assert.That(!_isDisposed);
            foreach (var (address, _) in _activeEntries)
            {
                _chunkStore.Free(new PtrHandle(address));
            }
            _activeEntries.Clear();
            _chunkStore.FlushPendingOperations();
        }

        internal void Dispose()
        {
            Assert.That(!_isDisposed);
            ClearAll();
            _isDisposed = true;
        }

        internal unsafe void Serialize(ITrecsSerializationWriter writer)
        {
            Assert.That(!_isDisposed);

            writer.Write<int>("NumEntries", _activeEntries.Count);

            foreach (var (address, frameEntry) in _activeEntries)
            {
                var entry = _chunkStore.ResolveEntry(new PtrHandle(address));
                var size = UnsafeUtility.SizeOf(frameEntry.Type);

                writer.Write<uint>("Address", address);
                writer.Write<int>("Frame", frameEntry.Frame);
                writer.Write<int>("InnerTypeId", TypeIdProvider.GetTypeId(frameEntry.Type));
                writer.Write<int>("DataSize", size);
                writer.Write<int>("Alignment", 16);
                writer.BlitWriteRawBytes("Data", entry.Address.ToPointer(), size);
            }
        }

        internal unsafe void Deserialize(ITrecsSerializationReader reader)
        {
            Assert.That(!_isDisposed);
            Assert.That(_activeEntries.Count == 0);

            var numEntries = reader.Read<int>("NumEntries");
            _activeEntries.EnsureCapacity(numEntries);

            for (int i = 0; i < numEntries; i++)
            {
                var savedAddress = reader.Read<uint>("Address");
                var frame = reader.Read<int>("Frame");
                var innerTypeId = reader.Read<int>("InnerTypeId");
                var dataSize = reader.Read<int>("DataSize");
                var alignment = reader.Read<int>("Alignment");
                var innerType = TypeIdProvider.GetTypeFromId(innerTypeId);

                // Preserve the saved handle value so components storing it still resolve.
                var handle = _chunkStore.AllocAtSlot(
                    savedAddress,
                    dataSize,
                    alignment,
                    BurstRuntime.GetHashCode32(innerType),
                    out var address
                );
                reader.BlitReadRawBytes("Data", address.ToPointer(), dataSize);
                _activeEntries.Add(handle.Value, new FrameEntry(innerType, frame));
            }

            _chunkStore.OnDeserializeComplete();
            _log.Debug("Deserialized {} frame-scoped native unique entries", _activeEntries.Count);
        }

        readonly struct FrameEntry
        {
            public readonly Type Type;
            public readonly int Frame;

            public FrameEntry(Type type, int frame)
            {
                Type = type;
                Frame = frame;
            }
        }
    }
}
