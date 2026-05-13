using System;
using System.Diagnostics;
using Trecs.Internal;

namespace Trecs
{
    /// <summary>
    /// Provides heap allocation and pointer resolution operations for the ECS world.
    /// Access via <see cref="WorldAccessor.Heap"/>.
    /// </summary>
    public sealed class HeapAccessor
    {
        readonly EcsHeapAllocator _heapAllocator;
        readonly SystemRunner _systemRunner;
        readonly AccessorRole _role;
        readonly bool _isInput;
        readonly string _debugName;

        internal HeapAccessor(
            EcsHeapAllocator heapAllocator,
            SystemRunner systemRunner,
            AccessorRole role,
            bool isInput,
            string debugName
        )
        {
            _heapAllocator = heapAllocator;
            _systemRunner = systemRunner;
            _role = role;
            _isInput = isInput;
            _debugName = debugName;
        }

        bool IsFixed => _role == AccessorRole.Fixed;
        bool IsUnrestricted => _role == AccessorRole.Unrestricted;
        bool IsInput => _isInput;

        internal int FixedFrame => _systemRunner.FixedFrame;

        internal UniqueHeap UniqueHeap => _heapAllocator.UniqueHeap;

        internal SharedHeap SharedHeap => _heapAllocator.SharedHeap;

        internal FrameScopedSharedHeap FrameScopedSharedHeap =>
            _heapAllocator.FrameScopedSharedHeap;

        internal NativeSharedHeap NativeSharedHeap => _heapAllocator.NativeSharedHeap;

        internal FrameScopedNativeSharedHeap FrameScopedNativeSharedHeap =>
            _heapAllocator.FrameScopedNativeSharedHeap;

        internal NativeUniqueHeap NativeUniqueHeap => _heapAllocator.NativeUniqueHeap;

        internal FrameScopedNativeUniqueHeap FrameScopedNativeUniqueHeap =>
            _heapAllocator.FrameScopedNativeUniqueHeap;

        internal FrameScopedUniqueHeap FrameScopedUniqueHeap =>
            _heapAllocator.FrameScopedUniqueHeap;

        internal TrecsListHeap TrecsListHeap => _heapAllocator.TrecsListHeap;

        public ref NativeSharedPtrResolver NativeSharedPtrResolver
        {
            get { return ref _heapAllocator.NativeSharedHeap.Resolver; }
        }

        public ref NativeUniquePtrResolver NativeUniquePtrResolver
        {
            get { return ref _heapAllocator.NativeUniquePtrResolver; }
        }

        public ref NativeTrecsListResolver NativeTrecsListResolver
        {
            get { return ref _heapAllocator.NativeTrecsListResolver; }
        }

        // ── NativeShared ────────────────────────────────────────────

        /// <summary>
        /// Opens a safety-checked read view over the given <see cref="NativeSharedPtr{T}"/>.
        /// Main-thread only; jobs use <see cref="NativeSharedPtrResolver.Read{T}"/>. Shared
        /// blobs are immutable by convention — there is no <c>Write</c> counterpart.
        /// </summary>
        public NativeSharedRead<T> Read<T>(in NativeSharedPtr<T> ptr)
            where T : unmanaged
        {
            return NativeSharedHeap.Read(in ptr);
        }

        public bool TryAllocNativeShared<T>(BlobId blobId, out NativeSharedPtr<T> ptr)
            where T : unmanaged
        {
            AssertCanAllocatePersistent();
            return _heapAllocator.TryAllocNativeShared<T>(blobId, out ptr);
        }

        public NativeSharedPtr<T> AllocNativeShared<T>(BlobId blobId)
            where T : unmanaged
        {
            AssertCanAllocatePersistent();
            return _heapAllocator.AllocNativeShared<T>(blobId);
        }

        public NativeSharedPtr<T> AllocNativeShared<T>(BlobId blobId, in T value)
            where T : unmanaged
        {
            AssertCanAllocatePersistent();
            return _heapAllocator.AllocNativeShared<T>(blobId, in value);
        }

        /// <summary>
        /// Takes ownership of an existing native allocation with an explicit BlobId.
        /// See <see cref="AllocNativeUniqueTakingOwnership{T}"/> for the ownership contract.
        /// </summary>
        public NativeSharedPtr<T> AllocNativeSharedTakingOwnership<T>(
            BlobId blobId,
            NativeBlobAllocation alloc
        )
            where T : unmanaged
        {
            AssertCanAllocatePersistent();
            return _heapAllocator.AllocNativeSharedTakingOwnership<T>(blobId, alloc);
        }

        /// <summary>
        /// Returns the existing native-shared blob at <paramref name="blobId"/> if cached,
        /// otherwise calls <paramref name="factory"/> and stores the result. The factory
        /// is only invoked on cache miss.
        /// <para>
        /// To stay allocation-free, pass <paramref name="factory"/> as either a
        /// <c>static</c> method group (<c>BuildIt</c>), a <c>static</c> lambda
        /// (<c>static () =&gt; …</c>, C# 9+), or a cached <c>static readonly Func&lt;T&gt;</c>
        /// field. Plain lambdas that capture local state allocate a closure on every call.
        /// </para>
        /// </summary>
        public NativeSharedPtr<T> GetOrAllocNativeShared<T>(BlobId blobId, Func<T> factory)
            where T : unmanaged
        {
            AssertCanAllocatePersistent();
            if (_heapAllocator.TryAllocNativeShared<T>(blobId, out var ptr))
            {
                return ptr;
            }
            return _heapAllocator.AllocNativeShared<T>(blobId, factory());
        }

        /// <summary>
        /// Returns the existing native-shared blob at <paramref name="blobId"/> if cached,
        /// otherwise calls <paramref name="factory"/> to obtain a native allocation and
        /// takes ownership of it. The factory is only invoked on cache miss.
        /// See <see cref="AllocNativeSharedTakingOwnership{T}"/> for the ownership contract
        /// and <see cref="GetOrAllocNativeShared{T}"/> for how to keep <paramref name="factory"/>
        /// allocation-free.
        /// </summary>
        public NativeSharedPtr<T> GetOrAllocNativeSharedTakingOwnership<T>(
            BlobId blobId,
            Func<NativeBlobAllocation> factory
        )
            where T : unmanaged
        {
            AssertCanAllocatePersistent();
            if (_heapAllocator.TryAllocNativeShared<T>(blobId, out var ptr))
            {
                return ptr;
            }
            return _heapAllocator.AllocNativeSharedTakingOwnership<T>(blobId, factory());
        }

        // ── Shared ──────────────────────────────────────────────────

        /// <summary>
        /// Allocates a shared blob under an explicit, caller-supplied
        /// <see cref="BlobId"/>. Use a <see cref="BlobId"/> factory
        /// (<see cref="BlobIdGenerator.FromKey"/>, <see cref="BlobId.FromGuid"/>,
        /// <see cref="BlobId.FromBytes"/>, or the content-hash extension in
        /// <c>Trecs.Serialization</c>) to obtain one — persistent allocations
        /// always carry caller-chosen identity.
        /// </summary>
        public SharedPtr<T> AllocShared<T>(BlobId blobId, T blob)
            where T : class
        {
            AssertCanAllocatePersistent();
            return _heapAllocator.AllocShared<T>(blobId, blob);
        }

        public SharedPtr<T> AllocShared<T>(BlobId blobId)
            where T : class
        {
            AssertCanAllocatePersistent();
            return _heapAllocator.AllocShared<T>(blobId);
        }

        public bool TryAllocShared<T>(BlobId blobId, out SharedPtr<T> ptr)
            where T : class
        {
            AssertCanAllocatePersistent();
            return _heapAllocator.TryAllocShared<T>(blobId, out ptr);
        }

        /// <summary>
        /// Returns the existing shared blob at <paramref name="blobId"/> if cached,
        /// otherwise calls <paramref name="factory"/> and stores the result. The factory
        /// is only invoked on cache miss.
        /// See <see cref="GetOrAllocNativeShared{T}"/> for how to keep
        /// <paramref name="factory"/> allocation-free.
        /// </summary>
        public SharedPtr<T> GetOrAllocShared<T>(BlobId blobId, Func<T> factory)
            where T : class
        {
            AssertCanAllocatePersistent();
            if (_heapAllocator.TryAllocShared<T>(blobId, out var ptr))
            {
                return ptr;
            }
            return _heapAllocator.AllocShared<T>(blobId, factory());
        }

        // ── Unique ──────────────────────────────────────────────────

        public UniquePtr<T> AllocUnique<T>()
            where T : class
        {
            AssertCanAllocatePersistent();
            return _heapAllocator.AllocUnique<T>();
        }

        public UniquePtr<T> AllocUnique<T>(T value)
            where T : class
        {
            AssertCanAllocatePersistent();
            return _heapAllocator.AllocUnique<T>(value);
        }

        // ── TrecsList ───────────────────────────────────────────────

        /// <summary>
        /// Allocates a new <see cref="TrecsList{T}"/> with the given initial capacity (0 by
        /// default). The backing buffer grows automatically on <c>Add</c> when the count
        /// reaches capacity. Owner must call <see cref="TrecsList{T}.Dispose(HeapAccessor)"/>.
        /// </summary>
        public TrecsList<T> AllocTrecsList<T>(int initialCapacity = 0)
            where T : unmanaged
        {
            AssertCanAllocatePersistent();
            return _heapAllocator.AllocTrecsList<T>(initialCapacity);
        }

        /// <summary>
        /// Opens a safety-checked read view over the given <see cref="TrecsList{T}"/>.
        /// Main-thread only; jobs use <see cref="NativeTrecsListResolver.Read{T}"/>.
        /// </summary>
        public TrecsListRead<T> Read<T>(in TrecsList<T> list)
            where T : unmanaged
        {
            return TrecsListHeap.Read(in list);
        }

        /// <summary>
        /// Opens a safety-checked write view over the given <see cref="TrecsList{T}"/>.
        /// Main-thread only; jobs use <see cref="NativeTrecsListResolver.Write{T}"/>.
        /// </summary>
        public TrecsListWrite<T> Write<T>(in TrecsList<T> list)
            where T : unmanaged
        {
            return TrecsListHeap.Write(in list);
        }

        // ── NativeUnique ────────────────────────────────────────────

        /// <summary>
        /// Opens a safety-checked read view over the given <see cref="NativeUniquePtr{T}"/>.
        /// Main-thread only; jobs use <see cref="NativeUniquePtrResolver.Read{T}"/>.
        /// Checks both persistent and frame-scoped storage.
        /// </summary>
        public NativeUniqueRead<T> Read<T>(in NativeUniquePtr<T> ptr)
            where T : unmanaged
        {
            return FrameScopedNativeUniqueHeap.ContainsEntry(ptr.Handle.Value)
                ? OpenFrameScopedRead(ptr)
                : NativeUniqueHeap.Read(in ptr);
        }

        /// <summary>
        /// Opens a safety-checked write view over the given <see cref="NativeUniquePtr{T}"/>.
        /// Main-thread only; jobs use <see cref="NativeUniquePtrResolver.Write{T}"/>.
        /// Checks both persistent and frame-scoped storage.
        /// </summary>
        public NativeUniqueWrite<T> Write<T>(in NativeUniquePtr<T> ptr)
            where T : unmanaged
        {
            return FrameScopedNativeUniqueHeap.ContainsEntry(ptr.Handle.Value)
                ? OpenFrameScopedWrite(ptr)
                : NativeUniqueHeap.Write(in ptr);
        }

        unsafe NativeUniqueRead<T> OpenFrameScopedRead<T>(in NativeUniquePtr<T> ptr)
            where T : unmanaged
        {
            var entry = FrameScopedNativeUniqueHeap.ResolveEntry<T>(ptr.Handle.Value, FixedFrame);
#if ENABLE_UNITY_COLLECTIONS_CHECKS
            return new NativeUniqueRead<T>(entry.Address.ToPointer(), entry.Safety);
#else
            return new NativeUniqueRead<T>(entry.Address.ToPointer());
#endif
        }

        unsafe NativeUniqueWrite<T> OpenFrameScopedWrite<T>(in NativeUniquePtr<T> ptr)
            where T : unmanaged
        {
            var entry = FrameScopedNativeUniqueHeap.ResolveEntry<T>(ptr.Handle.Value, FixedFrame);
#if ENABLE_UNITY_COLLECTIONS_CHECKS
            return new NativeUniqueWrite<T>(entry.Address.ToPointer(), entry.Safety);
#else
            return new NativeUniqueWrite<T>(entry.Address.ToPointer());
#endif
        }

        public NativeUniquePtr<T> AllocNativeUnique<T>(in T value)
            where T : unmanaged
        {
            AssertCanAllocatePersistent();
            return _heapAllocator.AllocNativeUnique<T>(in value);
        }

        public NativeUniquePtr<T> AllocNativeUnique<T>()
            where T : unmanaged
        {
            AssertCanAllocatePersistent();
            return _heapAllocator.AllocNativeUnique<T>();
        }

        /// <summary>
        /// Takes ownership of an existing native allocation without copying.
        ///
        /// <para><b>Caller responsibilities (failure is undefined behavior):</b></para>
        /// <list type="number">
        ///   <item>The pointer in <paramref name="alloc"/> must have been allocated via
        ///     <c>AllocatorManager.Allocate(Allocator.Persistent, size, alignment, 1)</c>
        ///     with matching size and alignment.</item>
        ///   <item>No other code may free this pointer — Trecs takes exclusive ownership.</item>
        ///   <item>No other code may hold a reference to this pointer after this call.</item>
        /// </list>
        /// <para>Unity does NOT validate the pointer at the allocator level. Misuse typically
        /// corrupts the heap or crashes on a later allocation. Use only when you have full
        /// control over the original allocation.</para>
        /// </summary>
        public NativeUniquePtr<T> AllocNativeUniqueTakingOwnership<T>(NativeBlobAllocation alloc)
            where T : unmanaged
        {
            AssertCanAllocatePersistent();
            return _heapAllocator.AllocNativeUniqueTakingOwnership<T>(alloc);
        }

        // ── Frame-Scoped Unique ─────────────────────────────────────

        public UniquePtr<T> AllocUniqueFrameScoped<T>()
            where T : class
        {
            AssertCanAddInputsSystem();
            return _heapAllocator.AllocUniqueFrameScoped<T>(_systemRunner.FixedFrame);
        }

        public UniquePtr<T> AllocUniqueFrameScoped<T>(T value)
            where T : class
        {
            AssertCanAddInputsSystem();
            return _heapAllocator.AllocUniqueFrameScoped<T>(_systemRunner.FixedFrame, value);
        }

        // ── Frame-Scoped NativeShared ───────────────────────────────

        public NativeSharedPtr<T> AllocNativeSharedFrameScoped<T>(BlobId blobId, in T value)
            where T : unmanaged
        {
            AssertCanAddInputsSystem();
            return _heapAllocator.AllocNativeSharedFrameScoped<T>(
                _systemRunner.FixedFrame,
                blobId,
                in value
            );
        }

        public NativeSharedPtr<T> AllocNativeSharedFrameScoped<T>(BlobId blobId)
            where T : unmanaged
        {
            AssertCanAddInputsSystem();
            return _heapAllocator.AllocNativeSharedFrameScoped<T>(_systemRunner.FixedFrame, blobId);
        }

        public bool TryAllocNativeSharedFrameScoped<T>(BlobId blobId, out NativeSharedPtr<T> ptr)
            where T : unmanaged
        {
            AssertCanAddInputsSystem();
            return _heapAllocator.TryAllocNativeSharedFrameScoped<T>(
                _systemRunner.FixedFrame,
                blobId,
                out ptr
            );
        }

        // ── Frame-Scoped Shared ─────────────────────────────────────

        public SharedPtr<T> AllocSharedFrameScoped<T>(BlobId blobId, T value)
            where T : class
        {
            AssertCanAddInputsSystem();
            return _heapAllocator.AllocSharedFrameScoped<T>(
                _systemRunner.FixedFrame,
                blobId,
                value
            );
        }

        public SharedPtr<T> AllocSharedFrameScoped<T>(BlobId blobId)
            where T : class
        {
            AssertCanAddInputsSystem();
            return _heapAllocator.AllocSharedFrameScoped<T>(_systemRunner.FixedFrame, blobId);
        }

        public bool TryAllocSharedFrameScoped<T>(BlobId blobId, out SharedPtr<T> ptr)
            where T : class
        {
            AssertCanAddInputsSystem();
            return _heapAllocator.TryAllocSharedFrameScoped<T>(
                _systemRunner.FixedFrame,
                blobId,
                out ptr
            );
        }

        // ── Frame-Scoped NativeUnique ───────────────────────────────

        public NativeUniquePtr<T> AllocNativeUniqueFrameScoped<T>(in T value)
            where T : unmanaged
        {
            AssertCanAddInputsSystem();
            return _heapAllocator.AllocNativeUniqueFrameScoped<T>(
                _systemRunner.FixedFrame,
                in value
            );
        }

        public NativeUniquePtr<T> AllocNativeUniqueFrameScoped<T>()
            where T : unmanaged
        {
            AssertCanAddInputsSystem();
            return _heapAllocator.AllocNativeUniqueFrameScoped<T>(_systemRunner.FixedFrame);
        }

        /// <summary>
        /// Frame-scoped variant of <see cref="AllocNativeUniqueTakingOwnership{T}"/>.
        /// Same caller responsibilities apply.
        /// </summary>
        public NativeUniquePtr<T> AllocNativeUniqueFrameScopedTakingOwnership<T>(
            NativeBlobAllocation alloc
        )
            where T : unmanaged
        {
            AssertCanAddInputsSystem();
            return _heapAllocator.AllocNativeUniqueFrameScopedTakingOwnership<T>(
                _systemRunner.FixedFrame,
                alloc
            );
        }

        // ── Assertions ──────────────────────────────────────────────

        [Conditional("DEBUG")]
        void AssertCanAddInputsSystem()
        {
            Assert.That(
                IsUnrestricted || IsInput,
                "Attempted to use input-only functionality from a non-Input accessor {}",
                _debugName
            );
        }

        // Persistent heap allocations are only allowed from Fixed-role and
        // Unrestricted-role accessors. Input-system and Variable-role accessors are
        // rejected regardless of whether systems are currently executing —
        // see docs/advanced/heap-allocation-rules.md.
        [Conditional("DEBUG")]
        void AssertCanAllocatePersistent()
        {
            if (IsUnrestricted || IsFixed)
            {
                return;
            }

            if (IsInput)
            {
                Assert.That(
                    false,
                    "Cannot allocate persistent heap pointers from input-system accessor {}. Use the FrameScoped variant (e.g. AllocSharedFrameScoped) instead so that the pointer is frame scoped.",
                    _debugName
                );
            }
            else
            {
                Assert.That(
                    false,
                    "Cannot allocate heap pointers from Variable-role accessor {}. Heap allocation is only allowed from Fixed-role and Unrestricted-role accessors. See Heap Allocation Rules in the docs.",
                    _debugName
                );
            }
        }
    }
}
