using System;
using System.Diagnostics;
using Trecs.Collections;
using Trecs.Internal;

namespace Trecs
{
    /// <summary>
    /// Provides heap allocation and pointer resolution operations for the ECS world.
    /// Access via <see cref="WorldAccessor.Heap"/>.
    /// </summary>
    public class HeapAccessor
    {
        readonly EcsHeapAllocator _heapAllocator;
        readonly SystemRunner _systemRunner;
        readonly bool _isFixedSystem;
        readonly bool _isInputSystem;
        readonly Rng _fixedRng;
        readonly string _debugName;

        internal HeapAccessor(
            EcsHeapAllocator heapAllocator,
            SystemRunner systemRunner,
            bool isFixedSystem,
            bool isInputSystem,
            Rng fixedRng,
            string debugName
        )
        {
            _heapAllocator = heapAllocator;
            _systemRunner = systemRunner;
            _isFixedSystem = isFixedSystem;
            _isInputSystem = isInputSystem;
            _fixedRng = fixedRng;
            _debugName = debugName;
        }

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

        public ref NativeSharedPtrResolver NativeSharedPtrResolver
        {
            get { return ref _heapAllocator.NativeSharedHeap.Resolver; }
        }

        public ref NativeUniquePtrResolver NativeUniquePtrResolver
        {
            get { return ref _heapAllocator.NativeUniquePtrResolver; }
        }

        public unsafe void* ResolveUnsafePtr<T>(BlobId address)
            where T : unmanaged
        {
            return NativeSharedHeap.ResolveUnsafePtr<T>(address);
        }

        // ── NativeShared ────────────────────────────────────────────

        public NativeSharedPtr<T> AllocNativeShared<T>(in T blob)
            where T : unmanaged
        {
            AssertNotInputSystem();

            if (_isFixedSystem)
            {
                var fixedRngValue = _fixedRng.NextLong();
                if (fixedRngValue == 0)
                    fixedRngValue = 1;
                var blobId = new BlobId(fixedRngValue);
                return _heapAllocator.AllocNativeShared<T>(blobId, in blob);
            }

            return _heapAllocator.AllocNativeShared<T>(in blob);
        }

        public bool TryAllocNativeShared<T>(BlobId blobId, out NativeSharedPtr<T> ptr)
            where T : unmanaged
        {
            AssertNotInputSystem();
            return _heapAllocator.TryAllocNativeShared<T>(blobId, out ptr);
        }

        public NativeSharedPtr<T> AllocNativeShared<T>(BlobId blobId)
            where T : unmanaged
        {
            AssertNotInputSystem();
            return _heapAllocator.AllocNativeShared<T>(blobId);
        }

        public NativeSharedPtr<T> AllocNativeShared<T>(BlobId blobId, in T value)
            where T : unmanaged
        {
            AssertNotInputSystem();
            return _heapAllocator.AllocNativeShared<T>(blobId, in value);
        }

        /// <summary>
        /// Takes ownership of an existing native pointer without copying.
        /// The caller must provide the exact allocation size and alignment
        /// so the memory can be freed correctly on disposal.
        /// See <see cref="AllocNativeUniqueTakingOwnership{T}"/> for the ownership contract.
        /// </summary>
        public NativeSharedPtr<T> AllocNativeSharedTakingOwnership<T>(
            IntPtr ptr,
            int allocSize,
            int allocAlignment
        )
            where T : unmanaged
        {
            AssertNotInputSystem();

            if (_isFixedSystem)
            {
                var fixedRngValue = _fixedRng.NextLong();
                if (fixedRngValue == 0)
                    fixedRngValue = 1;
                var blobId = new BlobId(fixedRngValue);
                return _heapAllocator.AllocNativeSharedTakingOwnership<T>(
                    blobId,
                    ptr,
                    allocSize,
                    allocAlignment
                );
            }

            return _heapAllocator.AllocNativeSharedTakingOwnership<T>(
                ptr,
                allocSize,
                allocAlignment
            );
        }

        /// <summary>
        /// Takes ownership of an existing native pointer with an explicit BlobId.
        /// The caller must provide the exact allocation size and alignment
        /// so the memory can be freed correctly on disposal.
        /// See <see cref="AllocNativeUniqueTakingOwnership{T}"/> for the ownership contract.
        /// </summary>
        public NativeSharedPtr<T> AllocNativeSharedTakingOwnership<T>(
            BlobId blobId,
            IntPtr ptr,
            int allocSize,
            int allocAlignment
        )
            where T : unmanaged
        {
            AssertNotInputSystem();
            return _heapAllocator.AllocNativeSharedTakingOwnership<T>(
                blobId,
                ptr,
                allocSize,
                allocAlignment
            );
        }

        // ── Shared ──────────────────────────────────────────────────

        public SharedPtr<T> AllocShared<T>(T blob)
            where T : class
        {
            AssertNotInputSystem();

            if (_isFixedSystem)
            {
                var fixedRngValue = _fixedRng.NextLong();
                if (fixedRngValue == 0)
                    fixedRngValue = 1;
                var blobId = new BlobId(fixedRngValue);
                return _heapAllocator.AllocShared<T>(blobId, blob);
            }

            return _heapAllocator.AllocShared<T>(blob);
        }

        public SharedPtr<T> AllocShared<T>(BlobId blobId)
            where T : class
        {
            AssertNotInputSystem();
            return _heapAllocator.AllocShared<T>(blobId);
        }

        public bool TryAllocShared<T>(BlobId blobId, out SharedPtr<T> ptr)
            where T : class
        {
            AssertNotInputSystem();
            return _heapAllocator.TryAllocShared<T>(blobId, out ptr);
        }

        // ── Unique ──────────────────────────────────────────────────

        public UniquePtr<T> AllocUnique<T>()
            where T : class
        {
            AssertNotInputSystem();
            return _heapAllocator.AllocUnique<T>();
        }

        public UniquePtr<T> AllocUnique<T>(T value)
            where T : class
        {
            AssertNotInputSystem();
            return _heapAllocator.AllocUnique<T>(value);
        }

        // ── NativeUnique ────────────────────────────────────────────

        public NativeUniquePtr<T> AllocNativeUnique<T>(in T value)
            where T : unmanaged
        {
            AssertNotInputSystem();
            return _heapAllocator.AllocNativeUnique<T>(in value);
        }

        public NativeUniquePtr<T> AllocNativeUnique<T>()
            where T : unmanaged
        {
            AssertNotInputSystem();
            return _heapAllocator.AllocNativeUnique<T>();
        }

        /// <summary>
        /// Takes ownership of an existing native pointer without copying.
        ///
        /// <para><b>Caller responsibilities (failure is undefined behavior):</b></para>
        /// <list type="number">
        ///   <item>The pointer must have been allocated via
        ///     <c>AllocatorManager.Allocate(Allocator.Persistent, sizeof(T), alignof(T), 1)</c>.</item>
        ///   <item>No other code may free this pointer — Trecs takes exclusive ownership.</item>
        ///   <item>No other code may hold a reference to this pointer after this call.</item>
        /// </list>
        /// <para>Unity does NOT validate the pointer at the allocator level. Misuse typically
        /// corrupts the heap or crashes on a later allocation. Use only when you have full
        /// control over the original allocation.</para>
        /// </summary>
        public NativeUniquePtr<T> AllocNativeUniqueTakingOwnership<T>(
            IntPtr ptr,
            int allocSize,
            int allocAlignment
        )
            where T : unmanaged
        {
            AssertNotInputSystem();
            return _heapAllocator.AllocNativeUniqueTakingOwnership<T>(
                ptr,
                allocSize,
                allocAlignment
            );
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

        public NativeSharedPtr<T> AllocNativeSharedFrameScoped<T>(in T value)
            where T : unmanaged
        {
            AssertCanAddInputsSystem();
            return _heapAllocator.AllocNativeSharedFrameScoped<T>(
                _systemRunner.FixedFrame,
                in value
            );
        }

        /// <summary>
        /// Frame-scoped variant of <see cref="AllocNativeSharedTakingOwnership{T}"/>.
        /// </summary>
        public NativeSharedPtr<T> AllocNativeSharedFrameScopedTakingOwnership<T>(
            IntPtr ptr,
            int allocSize,
            int allocAlignment
        )
            where T : unmanaged
        {
            AssertCanAddInputsSystem();
            return _heapAllocator.AllocNativeSharedFrameScopedTakingOwnership<T>(
                _systemRunner.FixedFrame,
                ptr,
                allocSize,
                allocAlignment
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

        public SharedPtr<T> AllocSharedFrameScoped<T>(T value)
            where T : class
        {
            AssertCanAddInputsSystem();
            return _heapAllocator.AllocSharedFrameScoped<T>(_systemRunner.FixedFrame, value);
        }

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
            IntPtr ptr,
            int allocSize,
            int allocAlignment
        )
            where T : unmanaged
        {
            AssertCanAddInputsSystem();
            return _heapAllocator.AllocNativeUniqueFrameScopedTakingOwnership<T>(
                _systemRunner.FixedFrame,
                ptr,
                allocSize,
                allocAlignment
            );
        }

        // ── Assertions ──────────────────────────────────────────────

        [Conditional("DEBUG")]
        void AssertCanAddInputsSystem()
        {
            Assert.That(
                !_systemRunner.IsExecutingSystems || _isInputSystem,
                "Attempted to use input system only functionality from a non-input system {}",
                _debugName
            );
        }

        [Conditional("DEBUG")]
        void AssertNotInputSystem()
        {
            Assert.That(
                !_systemRunner.IsExecutingSystems || !_isInputSystem,
                "Cannot allocate persistent heap pointers from input system {}. Use the FrameScoped variant (e.g. AllocSharedFrameScoped) instead so that the pointer is frame scoped",
                _debugName
            );
        }
    }
}
