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

        // All allocation and Read/Write methods now live on the pointer types as static
        // factories and instance methods. See:
        //   - NativeSharedPtr.cs   (NativeSharedPtr<T>)
        //   - NativeUniquePtr.cs   (NativeUniquePtr<T>)
        //   - SharedPtr.cs         (SharedPtr<T>)
        //   - UniquePtr.cs         (UniquePtr<T>)
        //   - TrecsList.cs         (TrecsList<T>)
        // HeapAccessor only exposes role/lifecycle gates and the typed heap accessors
        // they need.

        // ── Assertions ──────────────────────────────────────────────

        [Conditional("DEBUG")]
        internal void AssertCanAddInputsSystem()
        {
            TrecsAssert.That(
                IsUnrestricted || IsInput,
                "Attempted to use input-only functionality from a non-Input accessor {0}",
                _debugName
            );
        }

        // Persistent heap allocations are only allowed from Fixed-role and
        // Unrestricted-role accessors. Input-system and Variable-role accessors are
        // rejected regardless of whether systems are currently executing —
        // see docs/advanced/heap-allocation-rules.md.
        [Conditional("DEBUG")]
        internal void AssertCanAllocatePersistent()
        {
            if (IsUnrestricted || IsFixed)
            {
                return;
            }

            if (IsInput)
            {
                TrecsAssert.That(
                    false,
                    "Cannot allocate persistent heap pointers from input-system accessor {0}. Use the FrameScoped variant (e.g. SharedPtr.AllocFrameScoped) instead so that the pointer is frame scoped.",
                    _debugName
                );
            }
            else
            {
                TrecsAssert.That(
                    false,
                    "Cannot allocate heap pointers from Variable-role accessor {0}. Heap allocation is only allowed from Fixed-role and Unrestricted-role accessors. See Heap Allocation Rules in the docs.",
                    _debugName
                );
            }
        }
    }
}
