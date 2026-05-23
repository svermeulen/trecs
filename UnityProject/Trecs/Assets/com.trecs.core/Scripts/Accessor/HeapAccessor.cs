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

        // Per-role-aware copy of the chunk store's resolver: same backing
        // directory, but with the CanMutateHeap bit stamped from this accessor's
        // role. Stored as a field (rather than computed per-getter-call) so the
        // public getter can keep returning by-ref, matching the previous shape.
        NativeChunkStoreResolver _chunkStoreResolver;

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
            _chunkStoreResolver = new NativeChunkStoreResolver(
                in heapAllocator.NativeChunkStoreResolver,
                canMutateHeap: role == AccessorRole.Unrestricted || role == AccessorRole.Fixed
            );
        }

        bool IsFixed => _role == AccessorRole.Fixed;
        bool IsUnrestricted => _role == AccessorRole.Unrestricted;
        bool IsInput => _isInput;

        // Non-Conditional sibling of AssertCanMutateHeap. Exposed for callers that
        // need to query the role without throwing — e.g. a NativeWorldAccessor
        // constructor stamping the role onto its embedded chunk-store resolver so
        // Burst-job Write paths can assert without re-reading any role state.
        internal bool CanMutateHeap => IsUnrestricted || IsFixed;

        internal int FixedFrame => _systemRunner.FixedFrame;

        internal UniqueHeap UniqueHeap => _heapAllocator.UniqueHeap;

        internal SharedHeap SharedHeap => _heapAllocator.SharedHeap;

        /// <summary>
        /// The world's shared <see cref="Trecs.BlobCache"/>. Most game code reaches the
        /// cache through <see cref="SharedPtr{T}"/> / <see cref="NativeSharedPtr{T}"/>;
        /// this accessor is for code that needs to talk to the cache directly — async
        /// preload, non-ECS anchoring.
        /// </summary>
        public BlobCache BlobCache => _heapAllocator.SharedHeap.BlobCache;

        internal NativeSharedHeap NativeSharedHeap => _heapAllocator.NativeSharedHeap;

        internal InputNativeUniqueHeap InputNativeUniqueHeap =>
            _heapAllocator.InputNativeUniqueHeap;

        internal InputNativeSharedHeap InputNativeSharedHeap =>
            _heapAllocator.InputNativeSharedHeap;

        internal InputSharedHeap InputSharedHeap => _heapAllocator.InputSharedHeap;

        internal InputUniqueHeap InputUniqueHeap => _heapAllocator.InputUniqueHeap;

        internal NativeChunkStore NativeUniqueChunkStore => _heapAllocator.NativeUniqueChunkStore;

        /// <summary>
        /// Job-safe resolver for <see cref="NativeSharedPtr{T}"/> dereferences inside
        /// Burst-compiled job structs. Copy by-value into the job's fields; the resolver
        /// stays valid for the duration of the job.
        /// </summary>
        public ref NativeSharedPtrResolver NativeSharedPtrResolver
        {
            get { return ref _heapAllocator.NativeSharedHeap.Resolver; }
        }

        /// <summary>
        /// Job-safe resolver for <see cref="NativeUniquePtr{T}"/> and
        /// <see cref="TrecsList{T}"/> dereferences inside Burst-compiled job structs.
        /// Backed by the shared <see cref="NativeChunkStore"/>; the per-allocation
        /// TypeId tag carries which heap owns the slot, so a single resolver covers
        /// every native-heap pointer type.
        ///
        /// <para>Carries this accessor's role: a Variable-role resolver fails fast
        /// at Write-open time inside Burst jobs (Read access is unaffected).</para>
        /// </summary>
        public ref NativeChunkStoreResolver NativeChunkStoreResolver
        {
            get { return ref _chunkStoreResolver; }
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

        // Centralized main-thread gate for managed heap operations reached through
        // HeapAccessor. Every Alloc / Write-open path on a pointer type
        // (SharedPtr, UniquePtr, NativeSharedPtr, NativeUniquePtr, TrecsList,
        // InputSharedPtr, InputUniquePtr, InputNativeSharedPtr, InputNativeUniquePtr)
        // routes through AssertCanAddInputsSystem or AssertCanMutateHeap below,
        // both of which call into this — so adding the main-thread invariant once
        // here covers every pointer-type entry point uniformly. Heap lifecycle
        // methods that don't go through HeapAccessor (Dispose / Serialize /
        // Deserialize / ClearAll / ClearAtOr*Frame, reached via EcsHeapAllocator
        // and EntityInputQueue) still keep their own main-thread asserts as
        // defense-in-depth. The Burst-job side (NativeWorldAccessor /
        // NativeChunkStoreResolver) deliberately does NOT call this — Burst jobs
        // are off-thread by construction.
        [Conditional("DEBUG")]
        internal void AssertMainThread()
        {
            TrecsDebugAssert.That(
                UnityThreadHelper.IsMainThread,
                "HeapAccessor entry points must be called from the main thread; "
                    + "Burst jobs use the resolver types (NativeChunkStoreResolver, "
                    + "NativeSharedPtrResolver, InputNativeUniqueResolver). "
                    + "Accessor {0}",
                _debugName
            );
        }

        [Conditional("DEBUG")]
        internal void AssertCanAddInputsSystem()
        {
            AssertMainThread();
            TrecsDebugAssert.That(
                IsUnrestricted || IsInput,
                "Attempted to use input-only functionality from a non-Input accessor {0}",
                _debugName
            );
        }

        // The Trecs heap is simulation state — its contents (and reference counts /
        // capacities / live-slot sets) are walked by the snapshot, recording, and
        // checksum serializers. Mutating it from a non-deterministic phase produces
        // desyncs, so every heap-mutating entry point (Alloc, Write open, Set,
        // Clone, Acquire, Dispose, EnsureCapacity, ...) routes through this gate.
        // Only Fixed-role and Unrestricted-role accessors pass; Variable-role and
        // input-system accessors are rejected. See docs/advanced/accessor-roles.md.
        [Conditional("DEBUG")]
        internal void AssertCanMutateHeap()
        {
            AssertMainThread();

            if (IsUnrestricted || IsFixed)
            {
                return;
            }

            if (IsInput)
            {
                TrecsDebugAssert.That(
                    false,
                    "Cannot mutate the persistent heap from input-system accessor {0}. Use an Input pointer type (InputSharedPtr.Alloc, InputNativeSharedPtr.Alloc, InputUniquePtr.Alloc, InputNativeUniquePtr.Alloc) for input-side allocations; mutating persistent heap entries from an input system would land in the deterministic snapshot but not in the input replay log, producing desyncs.",
                    _debugName
                );
            }
            else
            {
                TrecsDebugAssert.That(
                    false,
                    "Cannot mutate the heap from Variable-role accessor {0}. The Trecs heap is simulation state — Alloc, Write, Set, Clone, Acquire, Dispose, and EnsureCapacity are only allowed from Fixed-role or Unrestricted-role accessors. Read access is always allowed. See Accessor Roles in the docs.",
                    _debugName
                );
            }
        }
    }
}
