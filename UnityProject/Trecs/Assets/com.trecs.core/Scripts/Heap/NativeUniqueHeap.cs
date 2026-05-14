using System;
using Trecs.Internal;
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
        readonly HandleTypeRegistry _registry = new();
        bool _isDisposed;

        NativeUniquePtrResolver _resolver;

        internal NativeUniqueHeap(TrecsLog log, NativeChunkStore chunkStore)
        {
            TrecsAssert.IsNotNull(chunkStore);
            _log = log;
            _chunkStore = chunkStore;
            _resolver = new NativeUniquePtrResolver(_chunkStore.Resolver);
        }

        public int NumEntries
        {
            get
            {
                TrecsAssert.That(!_isDisposed);
                return _registry.Count;
            }
        }

        public ref NativeUniquePtrResolver Resolver
        {
            get
            {
                TrecsAssert.That(!_isDisposed);
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
            TrecsAssert.That(!_isDisposed);
            return _registry.ContainsKey(address);
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
            TrecsAssert.That(!_isDisposed);
            TrecsAssert.That(
                UnityThreadHelper.IsMainThread,
                "NativeUniqueHeap.ResolveEntry must be called from the main thread; jobs use NativeUniquePtrResolver"
            );
            TrecsAssert.That(address != 0, "Attempted to resolve null address");

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
            TrecsAssert.That(!_isDisposed);
            var handle = _chunkStore.Alloc(
                UnsafeUtility.SizeOf<T>(),
                UnsafeUtility.AlignOf<T>(),
                TypeHash<T>.Value,
                out var address
            );
            UnsafeUtility.WriteArrayElement(address.ToPointer(), 0, value);
            _registry.Add(handle.Value, typeof(T));
            _log.Trace("Allocated NativeUniquePtr<{0}> with handle {1}", typeof(T), handle.Value);
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
            TrecsAssert.That(!_isDisposed);
            TrecsAssert.That(alloc.Ptr != IntPtr.Zero, "AllocTakingOwnership: null pointer");
            var handle = _chunkStore.AllocExternal(
                alloc.Ptr,
                alloc.AllocSize,
                alloc.Alignment,
                TypeHash<T>.Value
            );
            _registry.Add(handle.Value, typeof(T));
            _log.Trace(
                "Allocated external NativeUniquePtr<{0}> with handle {1}",
                typeof(T),
                handle.Value
            );
            return new NativeUniquePtr<T>(handle);
        }

        public void DisposeEntry(uint address)
        {
            TrecsAssert.That(!_isDisposed);
            TrecsAssert.That(address != 0);
            if (!_registry.ContainsKey(address))
            {
                throw TrecsAssert.CreateException(
                    "Attempted to dispose invalid native unique heap address ({0}) — handle is not owned by NativeUniqueHeap",
                    address
                );
            }
            // Call chunk-store Free first; it may throw via CheckDeallocateAndThrow
            // if a Burst job is still using the handle. If we removed from the
            // registry first, that throw would leave the heap in an inconsistent
            // state with a leaked side-table slot.
            _chunkStore.Free(new PtrHandle(address));
            _registry.TryRemove(address);
            _log.Trace("Disposed NativeUniquePtr with handle {0}", address);
        }

        public void ClearAll(bool warnUndisposed)
        {
            TrecsAssert.That(!_isDisposed);

            if (warnUndisposed && _registry.Count > 0 && _log.IsWarningEnabled())
            {
                _log.Warning(
                    "Found {0} undisposed entries in NativeUniqueHeap with types: {1}",
                    _registry.Count,
                    _registry.DescribeRegisteredTypes()
                );
            }

            // _chunkStore.Free doesn't touch the registry; safe to iterate directly.
            foreach (var address in _registry.Handles)
            {
                _chunkStore.Free(new PtrHandle(address));
            }
            _registry.Clear();
        }

        internal void Dispose()
        {
            TrecsAssert.That(!_isDisposed);
            ClearAll(warnUndisposed: true);
            _isDisposed = true;
        }

        /// <summary>
        /// Writes only the managed-side (handle → type) bookkeeping. The actual entry
        /// memory + side-table state are dumped in bulk by <c>NativeChunkStore.Serialize</c>
        /// which must run before this.
        /// </summary>
        public void Serialize(ISerializationWriter writer)
        {
            _registry.Serialize(writer);
            _log.Trace("Serialized {0} native unique entries", _registry.Count);
        }

        /// <summary>
        /// Restores only the managed-side (handle → type) bookkeeping. Assumes
        /// <c>NativeChunkStore.Deserialize</c> already ran so every restored handle's
        /// data and safety handle is live.
        /// </summary>
        public void Deserialize(ISerializationReader reader)
        {
            _registry.Deserialize(reader);
            _log.Debug("Deserialized {0} native unique entries", _registry.Count);
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
    }
}
