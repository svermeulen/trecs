using System;
using System.Collections.Generic;
using System.Linq;
using Trecs.Internal;
using Unity.Burst;
using Unity.Collections.LowLevel.Unsafe;

namespace Trecs
{
    /// <summary>
    /// Manages exclusive-ownership native (unmanaged) allocations backing <see cref="NativeUniquePtr{T}"/>.
    /// Storage is provided by a shared <see cref="NativeChunkStore"/> (also used by
    /// <see cref="FrameScopedNativeUniqueHeap"/>) — this heap is responsible only for the managed-side
    /// bookkeeping (type tracking for warnings and serialization) and lifecycle of persistent entries.
    /// Provides a <see cref="NativeUniquePtrResolver"/> for Burst-compatible pointer resolution in jobs.
    /// </summary>
    public sealed class NativeUniqueHeap
    {
        readonly TrecsLog _log;

        readonly NativeChunkStore _chunkStore;
        readonly Dictionary<uint, Type> _typesByHandle = new();
        bool _isDisposed;

        NativeUniquePtrResolver _resolver;

        public NativeUniqueHeap(TrecsLog log, NativeChunkStore chunkStore)
        {
            Assert.IsNotNull(chunkStore);
            _log = log;
            _chunkStore = chunkStore;
            _resolver = new NativeUniquePtrResolver(_chunkStore.Resolver);
        }

        public int NumEntries
        {
            get
            {
                Assert.That(!_isDisposed);
                return _typesByHandle.Count;
            }
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
        /// True if <paramref name="address"/> is a handle this persistent heap owns. Used by
        /// <see cref="HeapAccessor"/> to disambiguate persistent vs frame-scoped handles before
        /// dispatching Read / Write to the right heap (the chunk store itself doesn't track
        /// which heap an entry belongs to).
        /// </summary>
        internal bool ContainsEntry(uint address)
        {
            Assert.That(!_isDisposed);
            return _typesByHandle.ContainsKey(address);
        }

        public unsafe NativeUniqueRead<T> Read<T>(in NativeUniquePtr<T> ptr)
            where T : unmanaged
        {
            var entry = ResolveEntry<T>(ptr.Handle.Value);
#if ENABLE_UNITY_COLLECTIONS_CHECKS
            return new NativeUniqueRead<T>(entry.Address.ToPointer(), entry.Safety);
#else
            return new NativeUniqueRead<T>(entry.Address.ToPointer());
#endif
        }

        public unsafe NativeUniqueWrite<T> Write<T>(in NativeUniquePtr<T> ptr)
            where T : unmanaged
        {
            var entry = ResolveEntry<T>(ptr.Handle.Value);
#if ENABLE_UNITY_COLLECTIONS_CHECKS
            return new NativeUniqueWrite<T>(entry.Address.ToPointer(), entry.Safety);
#else
            return new NativeUniqueWrite<T>(entry.Address.ToPointer());
#endif
        }

        /// <summary>
        /// Resolves a handle through the chunk store with a managed-side type-hash check.
        /// Main-thread only; Burst jobs use <see cref="NativeUniquePtrResolver"/>.
        /// </summary>
        internal NativeChunkStoreEntry ResolveEntry<T>(uint address)
            where T : unmanaged
        {
            Assert.That(!_isDisposed);
            Assert.That(
                UnityThreadHelper.IsMainThread,
                "NativeUniqueHeap.ResolveEntry must be called from the main thread; jobs use NativeUniquePtrResolver"
            );
            Assert.That(address != 0, "Attempted to resolve null address");

            var entry = _chunkStore.ResolveEntry(new PtrHandle(address));
            AssertTypeHashMatches<T>(entry.TypeHash);
            return entry;
        }

        internal unsafe void* ResolveUnsafePtr<T>(uint address)
            where T : unmanaged
        {
            return ResolveEntry<T>(address).Address.ToPointer();
        }

        public unsafe NativeUniquePtr<T> Alloc<T>(in T value)
            where T : unmanaged
        {
            Assert.That(!_isDisposed);
            var handle = _chunkStore.Alloc(
                UnsafeUtility.SizeOf<T>(),
                UnsafeUtility.AlignOf<T>(),
                TypeHash<T>.Value,
                out var address
            );
            UnsafeUtility.WriteArrayElement(address.ToPointer(), 0, value);
            _typesByHandle.Add(handle.Value, typeof(T));
            _log.Trace("Allocated NativeUniquePtr<{}> with handle {}", typeof(T), handle.Value);
            return new NativeUniquePtr<T>(handle);
        }

        public NativeUniquePtr<T> Alloc<T>()
            where T : unmanaged
        {
            return Alloc<T>(default);
        }

        /// <summary>
        /// Takes ownership of an existing native pointer and stores it in the heap without copying.
        ///
        /// <para><b>Caller responsibilities (failure to satisfy these is undefined behavior):</b></para>
        /// <list type="number">
        ///   <item>The pointer must have been allocated via
        ///     <c>AllocatorManager.Allocate(Allocator.Persistent, sizeof(T), alignof(T), 1)</c>
        ///     or an equivalent path. The chunk store stores the supplied size/alignment and
        ///     calls <c>AllocatorManager.Free</c> with them at dispose time.</item>
        ///   <item>No other code may free this pointer — the heap takes exclusive ownership.</item>
        ///   <item>No other code may hold a reference to this pointer after this call.</item>
        /// </list>
        /// </summary>
        public NativeUniquePtr<T> AllocTakingOwnership<T>(NativeBlobAllocation alloc)
            where T : unmanaged
        {
            Assert.That(!_isDisposed);
            Assert.That(alloc.Ptr != IntPtr.Zero, "AllocTakingOwnership: null pointer");
            var handle = _chunkStore.AllocExternal(
                alloc.Ptr,
                alloc.AllocSize,
                alloc.Alignment,
                TypeHash<T>.Value
            );
            _typesByHandle.Add(handle.Value, typeof(T));
            _log.Trace(
                "Allocated external NativeUniquePtr<{}> with handle {}",
                typeof(T),
                handle.Value
            );
            return new NativeUniquePtr<T>(handle);
        }

        public void DisposeEntry(uint address)
        {
            Assert.That(!_isDisposed);
            Assert.That(address != 0);
            if (!_typesByHandle.Remove(address))
            {
                throw Assert.CreateException(
                    "Attempted to dispose invalid native unique heap address ({}) — handle is not owned by NativeUniqueHeap",
                    address
                );
            }
            _chunkStore.Free(new PtrHandle(address));
            _log.Trace("Disposed NativeUniquePtr with handle {}", address);
        }

        /// <summary>
        /// Applies any deferred chunk-store operations.
        /// Must be called when no jobs are reading via the resolver (e.g. at submission time).
        /// </summary>
        internal void FlushPendingOperations()
        {
            Assert.That(!_isDisposed);
            _chunkStore.FlushPendingOperations();
        }

        public void ClearAll(bool warnUndisposed)
        {
            Assert.That(!_isDisposed);

            if (warnUndisposed && _typesByHandle.Count > 0 && _log.IsWarningEnabled())
            {
                var typeNames = _typesByHandle
                    .Values.Select(t => t.GetPrettyName())
                    .Distinct()
                    .Join(", ");
                _log.Warning(
                    "Found {} undisposed entries in NativeUniqueHeap with types: {}",
                    _typesByHandle.Count,
                    typeNames
                );
            }

            foreach (var address in _typesByHandle.Keys.ToArray())
            {
                _chunkStore.Free(new PtrHandle(address));
            }
            _typesByHandle.Clear();
            // Drain pending-frees through the chunk store so subsequent restore-time
            // AllocAtSlot calls don't see those slots as still-in-use.
            _chunkStore.FlushPendingOperations();
        }

        internal void Dispose()
        {
            Assert.That(!_isDisposed);
            ClearAll(warnUndisposed: true);
            _isDisposed = true;
        }

        public unsafe void Serialize(ITrecsSerializationWriter writer)
        {
            // Force a flush so every live handle is fully reflected in the chunk store before
            // we serialise (otherwise pending-add entries would only be visible via main-thread
            // resolution, which is fine for us — both go through chunk_store.ResolveEntry —
            // but the flush also drains pending-frees that may free slots we'd otherwise scan).
            FlushPendingOperations();

            writer.Write<int>("NumEntries", _typesByHandle.Count);

            foreach (var (address, type) in _typesByHandle)
            {
                var entry = _chunkStore.ResolveEntry(new PtrHandle(address));
                var size = UnsafeSizeOfRuntimeType(type);
                var alignment = UnsafeAlignOfRuntimeType(type);

                writer.Write<uint>("Address", address);
                writer.Write<int>("InnerTypeId", TypeIdProvider.GetTypeId(type));
                writer.Write<int>("DataSize", size);
                writer.Write<int>("Alignment", alignment);
                writer.BlitWriteRawBytes("Data", entry.Address.ToPointer(), size);
            }

            _log.Trace("Serialized {} native unique entries", _typesByHandle.Count);
        }

        public unsafe void Deserialize(ITrecsSerializationReader reader)
        {
            Assert.That(_typesByHandle.Count == 0);

            var numEntries = reader.Read<int>("NumEntries");

            for (int i = 0; i < numEntries; i++)
            {
                var savedAddress = reader.Read<uint>("Address");
                var innerTypeId = reader.Read<int>("InnerTypeId");
                var dataSize = reader.Read<int>("DataSize");
                var alignment = reader.Read<int>("Alignment");
                var innerType = TypeIdProvider.GetTypeFromId(innerTypeId);

                // Restore at the exact slot/generation encoded in the saved handle so that
                // components storing this handle can be blit through save/load without remap.
                var handle = _chunkStore.AllocAtSlot(
                    savedAddress,
                    dataSize,
                    alignment,
                    BurstRuntime.GetHashCode32(innerType),
                    out var address
                );
                reader.BlitReadRawBytes("Data", address.ToPointer(), dataSize);
                _typesByHandle.Add(handle.Value, innerType);
            }

            _chunkStore.OnDeserializeComplete();
            _log.Debug("Deserialized {} native unique entries", numEntries);
        }

        // ─── Helpers ──────────────────────────────────────────────

        static void AssertTypeHashMatches<T>(int storedHash)
            where T : unmanaged
        {
            if (storedHash != TypeHash<T>.Value)
            {
                throw new TrecsException(
                    $"Type hash mismatch resolving NativeUniquePtr<{typeof(T).Name}>: "
                        + $"stored {storedHash}, requested {TypeHash<T>.Value}"
                );
            }
        }

        // UnsafeUtility.SizeOf<T> and AlignOf<T> are generic; for serialization the type is
        // only known at runtime. We bounce through reflection to get the equivalent values.
        static int UnsafeSizeOfRuntimeType(Type t)
        {
            return UnsafeUtility.SizeOf(t);
        }

        static int UnsafeAlignOfRuntimeType(Type t)
        {
            // Unity's UnsafeUtility doesn't expose AlignOf(Type) directly, but
            // Marshal.SizeOf is structurally equivalent for blittable structs. For simplicity
            // we fall back to the platform's max useful alignment (16) — chunk store rounds up
            // to a power of two ≥ alignment anyway. Replace with a proper Type-aware AlignOf
            // if it becomes important.
            return 16;
        }
    }
}
