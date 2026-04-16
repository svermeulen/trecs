using System;
using System.Collections.Generic;
using Trecs.Collections;
using Trecs.Internal;
using Unity.Collections;

namespace Trecs
{
    /// <summary>
    /// Frame-scoped heap that owns unmanaged memory via <see cref="NativeUniquePtr{T}"/>.
    /// Entries are tagged with the frame they were allocated on and can be bulk-cleared
    /// by frame range. Removals are deferred until <c>FlushPendingOperations</c> to avoid
    /// racing with Burst jobs that may still be reading through the native resolver.
    /// </summary>
    public class FrameScopedNativeUniqueHeap
    {
        static readonly TrecsLog _log = new(nameof(FrameScopedNativeUniqueHeap));

        readonly NativeDenseDictionary<uint, NativeUniqueHeapEntry> _allEntries;
        readonly DenseDictionary<uint, FrameEntry> _activeEntries = new();
        readonly List<uint> _removeBuffer = new();

        // Deferred-removal list (mirrors NativeUniqueHeap pattern). Frame cleanup
        // happens between fixed updates, but jobs may still be reading via the
        // resolver until FlushPendingOperations is called at submission time.
        readonly List<(uint Address, NativeBlobBox Box)> _pendingRemoves = new();
        readonly HeapIdCounter _idCounter = new(2, 2);

        bool _isDisposed;

        public FrameScopedNativeUniqueHeap()
        {
            _allEntries = new NativeDenseDictionary<uint, NativeUniqueHeapEntry>(
                1,
                Allocator.Persistent
            );
        }

        internal NativeDenseDictionary<uint, NativeUniqueHeapEntry> AllEntries => _allEntries;

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
            return AllocFromBox<T>(frame, NativeBlobBox.AllocFromValue(in value));
        }

        internal NativeUniquePtr<T> Alloc<T>(int frame)
            where T : unmanaged
        {
            return Alloc<T>(frame, default(T));
        }

        /// <summary>
        /// Takes ownership of an existing native pointer. See
        /// <see cref="NativeUniqueHeap.AllocTakingOwnership{T}"/> for the contract.
        /// </summary>
        internal NativeUniquePtr<T> AllocTakingOwnership<T>(
            int frame,
            IntPtr ptr,
            int allocSize,
            int allocAlignment
        )
            where T : unmanaged
        {
            Assert.That(!_isDisposed);
            var box = NativeBlobBox.FromExistingPointer(ptr, allocSize, allocAlignment, typeof(T));
            return AllocFromBox<T>(frame, box);
        }

        internal NativeUniquePtr<T> AllocFromBox<T>(int frame, NativeBlobBox box)
            where T : unmanaged
        {
            Assert.That(!_isDisposed);
            Assert.That(
                box.InnerType == typeof(T),
                "NativeBlobBox inner type {} does not match requested type {}",
                box.InnerType,
                typeof(T)
            );
            Assert.That(frame >= 0);

            var address = _idCounter.Alloc();
            Assert.That(address != 0);

            _activeEntries.Add(address, new FrameEntry(box, frame));
            _allEntries.Add(
                address,
                new NativeUniqueHeapEntry(BurstHashFromType(box.InnerType), box.Ptr)
            );

            _log.Trace(
                "Allocated frame-scoped native unique ptr with address {} and type {} for frame {}",
                address,
                typeof(T),
                frame
            );

            return new NativeUniquePtr<T>(new PtrHandle(address));
        }

        internal bool ContainsEntry(uint address)
        {
            Assert.That(!_isDisposed);
            return _activeEntries.ContainsKey(address);
        }

        internal unsafe void* ResolveUnsafePtr<T>(uint address, int frame)
            where T : unmanaged
        {
            Assert.That(!_isDisposed);
            Assert.That(
                UnityThreadUtil.IsMainThread,
                "ResolveUnsafePtr must be called from the main thread; jobs use NativeUniquePtrResolver"
            );
            Assert.That(address != 0, "Attempted to resolve null address");

            if (!_activeEntries.TryGetValue(address, out var entry))
            {
                throw Assert.CreateException(
                    "Attempted to resolve invalid frame-scoped native unique heap address ({}) for frame {}",
                    address,
                    frame
                );
            }

            Assert.IsEqual(
                entry.Frame,
                frame,
                "Attempted to get input memory for different frame than it was allocated for"
            );
            Assert.That(
                entry.Box.InnerType == typeof(T),
                "Type mismatch resolving frame-scoped NativeUniquePtr: stored {}, requested {}",
                entry.Box.InnerType,
                typeof(T)
            );

            return entry.Box.Ptr.ToPointer();
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

            if (!_activeEntries.TryRemove(address, out var entry))
            {
                throw Assert.CreateException(
                    "Attempted to dispose invalid frame-scoped native unique heap address ({})",
                    address
                );
            }

            // Defer free until FlushPendingOperations — jobs may still be reading via the resolver.
            _pendingRemoves.Add((address, entry.Box));
            _log.Trace(
                "Disposed frame-scoped native unique ptr with address {} (deferred)",
                address
            );
        }

        /// <summary>
        /// Applies deferred _allEntries removals.
        /// Must be called when no jobs are reading from _allEntries (e.g. at submission time).
        /// </summary>
        internal void FlushPendingOperations()
        {
            Assert.That(!_isDisposed);
            Assert.That(UnityThreadUtil.IsMainThread);

            foreach (var (address, box) in _pendingRemoves)
            {
                _allEntries.Remove(address);
                box.Dispose();
            }
            _pendingRemoves.Clear();
        }

        internal void ClearAll()
        {
            Assert.That(!_isDisposed);

            // Free all pending-removes (already removed from _activeEntries)
            foreach (var (_, box) in _pendingRemoves)
            {
                box.Dispose();
            }
            _pendingRemoves.Clear();

            // Free all entries still in _activeEntries
            foreach (var (_, entry) in _activeEntries)
            {
                entry.Box.Dispose();
            }
            _activeEntries.Clear();
            _allEntries.Clear();
        }

        internal void Dispose()
        {
            Assert.That(!_isDisposed);
            ClearAll();
            _allEntries.Dispose();
            _isDisposed = true;
        }

        internal unsafe void Serialize(ITrecsSerializationWriter writer)
        {
            Assert.That(!_isDisposed);

            writer.Write<uint>("IdCounter", _idCounter.Value);
            writer.Write<int>("NumEntries", _activeEntries.Count);

            foreach (var (address, entry) in _activeEntries)
            {
                writer.Write<uint>("Address", address);
                writer.Write<int>("Frame", entry.Frame);
                writer.Write<int>("InnerTypeId", TypeIdProvider.GetTypeId(entry.Box.InnerType));
                writer.Write<int>("DataSize", entry.Box.Size);
                writer.Write<int>("Alignment", entry.Box.Alignment);
                writer.BlitWriteRawBytes("Data", entry.Box.Ptr.ToPointer(), entry.Box.Size);
            }
        }

        internal unsafe void Deserialize(ITrecsSerializationReader reader)
        {
            Assert.That(!_isDisposed);
            Assert.That(_activeEntries.Count == 0);

            // See FrameScopedSharedHeap.Deserialize for the rationale behind EnsureAtLeast.
            _idCounter.EnsureAtLeast(reader.Read<uint>("IdCounter"));
            var numEntries = reader.Read<int>("NumEntries");

            _activeEntries.EnsureCapacity(numEntries);

            uint maxAddress = 0;

            for (int i = 0; i < numEntries; i++)
            {
                var address = reader.Read<uint>("Address");
                if (address > maxAddress)
                {
                    maxAddress = address;
                }

                var frame = reader.Read<int>("Frame");
                var innerTypeId = reader.Read<int>("InnerTypeId");
                var dataSize = reader.Read<int>("DataSize");
                var alignment = reader.Read<int>("Alignment");

                var innerType = TypeIdProvider.GetTypeFromId(innerTypeId);

                NativeBlobBox box = null;
                try
                {
                    box = NativeBlobBox.AllocUninitialized(dataSize, alignment, innerType);
                    reader.BlitReadRawBytes("Data", box.Ptr.ToPointer(), dataSize);
                }
                catch
                {
                    box?.Dispose();
                    throw;
                }

                _activeEntries.Add(address, new FrameEntry(box, frame));
                _allEntries.Add(
                    address,
                    new NativeUniqueHeapEntry(BurstHashFromType(innerType), box.Ptr)
                );

                _log.Trace(
                    "Deserialized frame-scoped native unique ptr with address {} for frame {}",
                    address,
                    frame
                );
            }

            if (maxAddress > 0)
            {
                _idCounter.AdvancePast(maxAddress);
            }

            _log.Debug("Deserialized {} frame-scoped native unique entries", _activeEntries.Count);
        }

        internal void RemapFrameOffsets(int frameOffset)
        {
            Assert.That(!_isDisposed);

            if (frameOffset == 0)
            {
                return;
            }

            // FrameEntry is a value type with a readonly Frame field, so we can't update in place.
            // Collect into a temp list, then re-add with the new frame.
            var entriesToUpdate = new List<(uint address, FrameEntry entry)>();
            foreach (var (address, entry) in _activeEntries)
            {
                entriesToUpdate.Add((address, entry));
            }

            _activeEntries.Clear();

            foreach (var (address, oldEntry) in entriesToUpdate)
            {
                _activeEntries.Add(
                    address,
                    new FrameEntry(oldEntry.Box, oldEntry.Frame + frameOffset)
                );
            }

            _log.Debug(
                "Remapped {} frame-scoped native unique entries by {} frames",
                entriesToUpdate.Count,
                frameOffset
            );
        }

        static int BurstHashFromType(Type t)
        {
            return Unity.Burst.BurstRuntime.GetHashCode32(t);
        }

        readonly struct FrameEntry
        {
            public readonly NativeBlobBox Box;
            public readonly int Frame;

            public FrameEntry(NativeBlobBox box, int frame)
            {
                Box = box;
                Frame = frame;
            }
        }
    }
}
