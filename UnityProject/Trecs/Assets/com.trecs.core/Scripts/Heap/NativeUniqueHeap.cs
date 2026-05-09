using System;
using System.Collections.Generic;
using System.Linq;
using Trecs.Collections;
using Trecs.Internal;
using Unity.Burst;
using Unity.Collections;

namespace Trecs
{
    /// <summary>
    /// Manages exclusive-ownership native (unmanaged) allocations backing <see cref="NativeUniquePtr{T}"/>.
    /// Provides a <see cref="NativeUniquePtrResolver"/> for Burst-compatible pointer resolution in jobs.
    /// Accessed internally through <see cref="HeapAccessor"/>; not typically used directly.
    /// </summary>
    public sealed class NativeUniqueHeap
    {
        static readonly TrecsLog _log = new(nameof(NativeUniqueHeap));

        readonly NativeDenseDictionary<uint, NativeUniqueHeapEntry> _allEntries;
        readonly Dictionary<uint, NativeBlobBox> _pendingAdds = new();
        readonly List<(uint Address, NativeBlobBox Box)> _pendingRemoves = new();
        readonly DenseDictionary<uint, NativeBlobBox> _activeBoxes = new();

        readonly HeapIdCounter _idCounter = new(1, 2);
        bool _isDisposed;

        // Resolver is rebuilt when frame-scoped heap reference changes
        NativeUniquePtrResolver _resolver;
        NativeDenseDictionary<uint, NativeUniqueHeapEntry> _frameScopedEntries;

        public NativeUniqueHeap()
        {
            _allEntries = new NativeDenseDictionary<uint, NativeUniqueHeapEntry>(
                1,
                Allocator.Persistent
            );
        }

        public int NumEntries
        {
            get
            {
                Assert.That(!_isDisposed);
                return _activeBoxes.Count;
            }
        }

        internal void SetFrameScopedEntries(
            NativeDenseDictionary<uint, NativeUniqueHeapEntry> frameScopedEntries
        )
        {
            _frameScopedEntries = frameScopedEntries;
            _resolver = new NativeUniquePtrResolver(_allEntries, _frameScopedEntries);
        }

        public ref NativeUniquePtrResolver Resolver
        {
            get
            {
                Assert.That(!_isDisposed);
                return ref _resolver;
            }
        }

        /// <summary>
        /// Resolves a pointer from managed (main-thread) code.
        /// Unlike NativeUniquePtrResolver, this can resolve entries that were just created
        /// in the current frame (before FlushPendingOperations).
        /// </summary>
        internal unsafe void* ResolveUnsafePtr<T>(uint address)
            where T : unmanaged
        {
            Assert.That(!_isDisposed);
            Assert.That(
                UnityThreadHelper.IsMainThread,
                "ResolveUnsafePtr must be called from the main thread; jobs use NativeUniquePtrResolver"
            );
            Assert.That(address != 0, "Attempted to resolve null address");

            if (_pendingAdds.TryGetValue(address, out var box))
            {
                AssertTypeMatches<T>(box);
                return box.Ptr.ToPointer();
            }

            return _resolver.ResolveUnsafePtr<T>(address);
        }

        public NativeUniquePtr<T> Alloc<T>(in T value)
            where T : unmanaged
        {
            Assert.That(!_isDisposed);
            return AllocFromBox<T>(NativeBlobBox.AllocFromValue(in value));
        }

        public NativeUniquePtr<T> Alloc<T>()
            where T : unmanaged
        {
            return Alloc<T>(default(T));
        }

        /// <summary>
        /// Takes ownership of an existing native pointer and stores it in the heap without copying.
        ///
        /// <para><b>Caller responsibilities (failure to satisfy these is undefined behavior):</b></para>
        /// <list type="number">
        ///   <item>The pointer must have been allocated via
        ///     <c>AllocatorManager.Allocate(Allocator.Persistent, sizeof(T), alignof(T), 1)</c>
        ///     or an equivalent path that produces a pointer compatible with
        ///     <c>AllocatorManager.Free(Allocator.Persistent, ptr, sizeof(T), alignof(T), 1)</c>.</item>
        ///   <item>No other code may free this pointer — the heap takes exclusive ownership.</item>
        ///   <item>No other code may hold a reference to this pointer after this call;
        ///     the heap may move/free it during dispose, deferred flush, or shutdown.</item>
        /// </list>
        /// <para>Unity does NOT validate the pointer at the allocator level. Misuse will
        /// typically corrupt the heap or crash on a later allocation. Use only when you
        /// have control over the original allocation.</para>
        /// </summary>
        public NativeUniquePtr<T> AllocTakingOwnership<T>(
            IntPtr ptr,
            int allocSize,
            int allocAlignment
        )
            where T : unmanaged
        {
            Assert.That(!_isDisposed);
            var box = NativeBlobBox.FromExistingPointer(ptr, allocSize, allocAlignment, typeof(T));
            return AllocFromBox<T>(box);
        }

        internal NativeUniquePtr<T> AllocFromBox<T>(NativeBlobBox box)
            where T : unmanaged
        {
            Assert.That(!_isDisposed);
            Assert.That(
                box.InnerType == typeof(T),
                "NativeBlobBox inner type {} does not match requested type {}",
                box.InnerType,
                typeof(T)
            );

            var address = _idCounter.Alloc();
            Assert.That(address != 0);

            _activeBoxes.Add(address, box);
            _pendingAdds.Add(address, box);

            _log.Trace(
                "Allocated new native unique ptr with address {} and type {}",
                address,
                typeof(T)
            );

            return new NativeUniquePtr<T>(new PtrHandle(address));
        }

        public void DisposeEntry(uint address)
        {
            Assert.That(!_isDisposed);
            Assert.That(address != 0);

            if (!_activeBoxes.TryRemove(address, out var box))
            {
                throw Assert.CreateException(
                    "Attempted to dispose invalid native unique heap address ({})",
                    address
                );
            }

            if (_pendingAdds.Remove(address))
            {
                // Entry was allocated but never flushed to _allEntries (never visible to jobs),
                // so we can free immediately.
                box.Dispose();
                _log.Trace("Disposed native unique ptr with address {} (pre-flush)", address);
            }
            else
            {
                // Entry is in _allEntries — defer free until FlushPendingOperations
                // since jobs may still be reading via the resolver.
                _pendingRemoves.Add((address, box));
                _log.Trace("Disposed native unique ptr with address {} (deferred)", address);
            }
        }

        /// <summary>
        /// Applies deferred _allEntries additions and removals.
        /// Must be called when no jobs are reading from _allEntries (e.g. at submission time).
        /// </summary>
        internal void FlushPendingOperations()
        {
            Assert.That(!_isDisposed);
            Assert.That(UnityThreadHelper.IsMainThread);

            if (_pendingAdds.Count > 0)
            {
                _allEntries.EnsureCapacity(_allEntries.Count + _pendingAdds.Count);
                foreach (var (address, box) in _pendingAdds)
                {
                    _allEntries.Add(
                        address,
                        new NativeUniqueHeapEntry(BurstHashFromType(box.InnerType), box.Ptr)
                    );
                }
                _pendingAdds.Clear();
            }

            foreach (var (address, box) in _pendingRemoves)
            {
                _allEntries.Remove(address);
                box.Dispose();
            }
            _pendingRemoves.Clear();
        }

        public void ClearAll(bool warnUndisposed)
        {
            Assert.That(!_isDisposed);

            if (warnUndisposed && _activeBoxes.Count > 0 && _log.IsWarningEnabled())
            {
                var typeNames = _activeBoxes
                    .Select(kv => kv.Value.InnerType.GetPrettyName())
                    .Distinct()
                    .Join(", ");
                _log.Warning(
                    "Found {} undisposed entries in NativeUniqueHeap with types: {}",
                    _activeBoxes.Count,
                    typeNames
                );
            }

            // Free all pending-removes (already removed from _activeBoxes)
            foreach (var (_, box) in _pendingRemoves)
            {
                box.Dispose();
            }
            _pendingRemoves.Clear();

            // Free everything still tracked in _activeBoxes (covers _pendingAdds + flushed entries)
            foreach (var (_, box) in _activeBoxes)
            {
                box.Dispose();
            }
            _activeBoxes.Clear();
            _pendingAdds.Clear();
            _allEntries.Clear();

            Assert.That(_activeBoxes.Count == 0);
            Assert.That(_allEntries.Count == 0);
        }

        internal void Dispose()
        {
            Assert.That(!_isDisposed);
            ClearAll(warnUndisposed: true);

            _allEntries.Dispose();

            _isDisposed = true;
        }

        public unsafe void Serialize(ITrecsSerializationWriter writer)
        {
            FlushPendingOperations();
            Assert.That(_allEntries.Count == _activeBoxes.Count);

            writer.Write<uint>("IdCounter", _idCounter.Value);
            writer.Write<int>("NumEntries", _activeBoxes.Count);

            foreach (var (address, box) in _activeBoxes)
            {
                writer.Write<uint>("Address", address);
                writer.Write<int>("InnerTypeId", TypeIdProvider.GetTypeId(box.InnerType));
                writer.Write<int>("DataSize", box.Size);
                writer.Write<int>("Alignment", box.Alignment);
                writer.BlitWriteRawBytes("Data", box.Ptr.ToPointer(), box.Size);
            }

            _log.Trace("Serialized {} native unique entries", _activeBoxes.Count);
        }

        public unsafe void Deserialize(ITrecsSerializationReader reader)
        {
            Assert.That(_allEntries.Count == 0);
            Assert.That(_pendingAdds.Count == 0);
            Assert.That(_pendingRemoves.Count == 0);
            Assert.That(_activeBoxes.Count == 0);

            _idCounter.Value = reader.Read<uint>("IdCounter");
            var numEntries = reader.Read<int>("NumEntries");

            _allEntries.EnsureCapacity(numEntries);

            for (int i = 0; i < numEntries; i++)
            {
                var address = reader.Read<uint>("Address");
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

                _activeBoxes.Add(address, box);
                _allEntries.Add(
                    address,
                    new NativeUniqueHeapEntry(BurstHashFromType(innerType), box.Ptr)
                );
            }

            _log.Debug("Deserialized {} native unique entries", numEntries);
        }

        // ─── Helpers ──────────────────────────────────────────────

        static void AssertTypeMatches<T>(NativeBlobBox box)
            where T : unmanaged
        {
            Assert.That(
                box.InnerType == typeof(T),
                "Type mismatch resolving NativeUniquePtr: stored {}, requested {}",
                box.InnerType,
                typeof(T)
            );
        }

        // The resolver compares against TypeHash<T>.Value which is BurstRuntime.GetHashCode32<T>().
        // BurstRuntime.GetHashCode32(Type) returns the same hash from a managed Type, so this
        // is interchangeable across managed and Burst contexts.
        static int BurstHashFromType(Type t)
        {
            return BurstRuntime.GetHashCode32(t);
        }
    }
}
