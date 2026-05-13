using System;
using System.Collections.Generic;
using System.Runtime.CompilerServices;
using System.Threading;
using Unity.Collections;
using Unity.Collections.LowLevel.Unsafe;

namespace Trecs.Internal
{
    /// <summary>
    /// Paged-slab allocator with bucketed size classes, used as the shared backing for
    /// <c>NativeUniqueHeap</c>, <c>FrameScopedNativeUniqueHeap</c>, and <c>TrecsListHeap</c>.
    ///
    /// <para>
    /// Allocations are bucketed by power-of-two size. Each bucket owns a list of pages,
    /// each page is split into fixed-size slots. Slot reuse is generation-protected via
    /// the upper byte of <see cref="PtrHandle"/>: stale handles to a freed slot are detected
    /// at <see cref="ResolveEntry"/> time. Allocations larger than <see cref="MaxBucketSlotSize"/>
    /// fall through to a dedicated single-slot "huge" page sized for that one allocation.
    /// </para>
    ///
    /// <para>
    /// Allocations and frees are both synchronous. <see cref="Alloc"/> publishes the new
    /// entry to the side table before returning the handle. <see cref="Free"/> calls
    /// <c>AtomicSafetyHandle.CheckDeallocateAndThrow</c> first — if any Burst job is
    /// still using the handle (has it scheduled as a read or write dependency), the call
    /// throws in editor / dev builds; in release without safety checks it's undefined
    /// behaviour, same as <c>NativeList&lt;T&gt;.Dispose()</c>. Callers must complete any
    /// jobs using the handle before freeing it.
    /// </para>
    ///
    /// <para><b>Threading invariant.</b> Every mutating API is main-thread only.
    /// Concurrency with Burst jobs that resolve via <see cref="NativeChunkStoreResolver"/>
    /// splits into two tiers:
    /// </para>
    /// <list type="bullet">
    ///   <item><b>Execute-phase-safe (concurrent with resolving jobs that do not depend
    ///     on the allocation being mutated):</b>
    ///     <see cref="Alloc"/>, <see cref="AllocExternal"/>, <see cref="Free"/>,
    ///     main-thread <see cref="ResolveEntry"/>.
    ///     <para>Alloc writes side-table slot <c>InUse: 0 → 1</c>, but the slot is fresh
    ///     from the freelist — no concurrent reader holds a handle for it at the moment
    ///     of the write, because the handle being returned is the first to encode this
    ///     slot at this generation. Any subsequent <c>Schedule()</c> on a job that uses
    ///     the handle provides the memory ordering needed to publish it.</para>
    ///     <para>Free flips a slot <c>InUse: 1 → 0</c>, but only after
    ///     <c>CheckDeallocateAndThrow</c> verifies no scheduled job holds the safety
    ///     handle, so a concurrent resolver cannot be in the middle of reading the slot
    ///     it's about to clear. Other jobs scheduled in parallel that resolve unrelated
    ///     handles are unaffected.</para>
    ///     <para>New chunk publication uses a memory barrier (<c>AllocateChunk</c>) so
    ///     the chunk's zero-init is visible before the directory pointer.</para>
    ///   </item>
    ///   <item><b>Submit-time only (no jobs may be running):</b>
    ///     <see cref="AllocAtSlot"/>, <see cref="ReclaimEmptyPages"/>,
    ///     <see cref="OnDeserializeComplete"/>, <see cref="Dispose"/>. These free
    ///     backing memory or rebuild internal state in a transient way that would
    ///     tear concurrent reads. Trecs's standard step model satisfies this by
    ///     blocking on outstanding jobs before each <c>SubmitEntities</c> call.
    ///   </item>
    /// </list>
    /// </summary>
    internal sealed class NativeChunkStore : IDisposable
    {
        readonly TrecsLog _log;

        // Slot-size ladder: powers of two from 16 B up to 64 KB. Above this we use the
        // huge-alloc path (one dedicated page per allocation).
        internal const int MinBucketSlotSize = 16;
        internal const int MaxBucketSlotSize = 64 * 1024;
        internal const int TargetSlotsPerPage = 64;
        internal const int MinPageSize = 4 * 1024; // never allocate a page smaller than this

        static readonly int[] BucketSlotSizes =
        {
            16,
            32,
            64,
            128,
            256,
            512,
            1024,
            2048,
            4096,
            8192,
            16384,
            32768,
            65536,
        };

        readonly Bucket[] _buckets;
        readonly List<Page> _pages = new();
        readonly Stack<int> _freePageIds = new();

        // Side-table slot recycling. Slot 0 is reserved as the "null" slot — never returned.
        readonly Stack<int> _freeSideTableSlots = new();
        int _nextFreshSideTableSlot = 1;

#if ENABLE_UNITY_COLLECTIONS_CHECKS
        // Tracks addresses currently registered via AllocExternal so we can catch a caller
        // that takes ownership of the same pointer twice (which would leave the chunk store
        // with two slots aliasing the same memory; freeing either leaves the other dangling).
        // Removed when the slot is released. Debug-only — release builds skip the check.
        readonly HashSet<IntPtr> _externalAddresses = new();
#endif

        // Chunked side table: a fixed-capacity directory of pointers to fixed-size
        // chunks of NativeChunkStoreEntry. Appending a new chunk does not move existing
        // chunks, so jobs reading via the resolver can keep resolving handles concurrent
        // with main-thread Allocs that materialise additional slots.
        NativeArray<IntPtr> _chunkDirectory;
        int _sideTableLength;
        NativeChunkStoreResolver _resolver;
        bool _isDisposed;
        int _liveCount;

        public unsafe NativeChunkStore(TrecsLog log)
        {
            _log = log;
            _buckets = new Bucket[BucketSlotSizes.Length];
            for (int i = 0; i < BucketSlotSizes.Length; i++)
            {
                var slotSize = BucketSlotSizes[i];
                var pageSize = Math.Max(MinPageSize, slotSize * TargetSlotsPerPage);
                var slotsPerPage = pageSize / slotSize;
                _buckets[i] = new Bucket
                {
                    SlotSize = slotSize,
                    SlotsPerPage = slotsPerPage,
                    PageSizeBytes = pageSize,
                };
            }

            // Fixed-capacity directory: 16K pointers = 128 KB upfront, covering the
            // entire 24-bit index space. The directory itself never moves; chunks
            // are allocated lazily as slot indices cross chunk boundaries.
            _chunkDirectory = new NativeArray<IntPtr>(
                NativeChunkStoreResolver.MaxChunkCount,
                Allocator.Persistent,
                NativeArrayOptions.ClearMemory
            );

            // Materialise slot 0 as the reserved "null" slot. ClearMemory + the
            // chunk allocator's MemClear guarantee _sideTable[0] reads as default.
            EnsureSideTableLength(1);

            _resolver = new NativeChunkStoreResolver(_chunkDirectory);
        }

        public int NumLiveAllocations
        {
            get
            {
                Assert.That(!_isDisposed);
                return _liveCount;
            }
        }

        public int NumPages
        {
            get
            {
                Assert.That(!_isDisposed);
                return _pages.Count - _freePageIds.Count;
            }
        }

        public ref NativeChunkStoreResolver Resolver
        {
            get
            {
                Assert.That(!_isDisposed);
                return ref _resolver;
            }
        }

        /// <summary>
        /// Allocate a slot at least <paramref name="size"/> bytes wide, aligned to at least
        /// <paramref name="alignment"/>. Returns a handle whose validity is checked against
        /// the side table's generation/in-use bits on every resolve.
        ///
        /// <para>The new entry is written directly to the side table before the handle is
        /// returned, so any Burst job scheduled afterwards with this handle can resolve it
        /// immediately — no flush boundary required.</para>
        /// </summary>
        [MethodImpl(MethodImplOptions.AggressiveInlining)]
        public PtrHandle Alloc(int size, int alignment, int typeHash)
        {
            return Alloc(size, alignment, typeHash, out _);
        }

        /// <summary>
        /// Same as <see cref="Alloc(int, int, int)"/>, but additionally returns the slot's
        /// address in <paramref name="address"/>. Callers that need to write the initial
        /// value into the allocation should use this overload to avoid an immediate
        /// follow-up <see cref="ResolveEntry"/>.
        /// </summary>
        public PtrHandle Alloc(int size, int alignment, int typeHash, out IntPtr address)
        {
            Assert.That(!_isDisposed);
            Assert.That(size > 0, "Allocation size must be positive");
            Assert.That(
                alignment > 0 && (alignment & (alignment - 1)) == 0,
                "Alignment {} must be a positive power of two",
                alignment
            );
            Assert.That(
                UnityThreadHelper.IsMainThread,
                "NativeChunkStore.Alloc must be called from the main thread"
            );

            int pageId;
            int slotIdx;
            int bucketIdx;
            bool isHuge;

            var neededSlotSize = Math.Max(size, alignment);
            if (neededSlotSize > MaxBucketSlotSize)
            {
                AllocHugePage(neededSlotSize, alignment, out pageId, out slotIdx, out address);
                bucketIdx = -1;
                isHuge = true;
            }
            else
            {
                bucketIdx = SelectBucket(neededSlotSize);
                AllocFromBucket(bucketIdx, out pageId, out slotIdx, out address);
                isHuge = false;
            }

            var sideTableIdx = AcquireSideTableSlot();

            // The slot is in InUse=0 from the freelist; we own it until we write
            // InUse=1 below. AcquireSideTableSlot guarantees the slot exists, so the
            // generation read here is the slot's true last-used generation. Skip 0 on
            // wrap since 0 means "never allocated."
            var prior = GetEntry(sideTableIdx);
            var nextGen = (byte)(prior.Generation + 1);
            if (nextGen == 0)
                nextGen = 1;

#if ENABLE_UNITY_COLLECTIONS_CHECKS
            var safety = AtomicSafetyHandle.Create();
            AtomicSafetyHandle.SetBumpSecondaryVersionOnScheduleWrite(safety, true);
#endif

            var newEntry = new NativeChunkStoreEntry
            {
                Address = address,
                TypeHash = typeHash,
                Generation = nextGen,
                InUse = 1,
                IsHuge = (byte)(isHuge ? 1 : 0),
                PageId = pageId,
                SlotIndex = slotIdx,
#if ENABLE_UNITY_COLLECTIONS_CHECKS
                Safety = safety,
#endif
            };

            var handleValue = NativeChunkStoreResolver.EncodeHandleValue(
                nextGen,
                (uint)sideTableIdx
            );
            // Write the entry to the side table directly. Safe concurrent with running
            // jobs because the slot was free (InUse=0) and no other reader holds a handle
            // for it: the handle we're about to return is the first one to exist for
            // this slot at this generation. Schedule() on any subsequent job provides
            // the ordering needed to publish the entry to that job.
            SetEntry(sideTableIdx, newEntry);
            _liveCount++;

            _log.Trace(
                "Alloc: handle={0} bucket={1} page={2} slot={3} addr={4}",
                handleValue,
                bucketIdx,
                pageId,
                slotIdx,
                address.ToInt64()
            );

            return new PtrHandle(handleValue);
        }

        /// <summary>
        /// Releases a previously-allocated handle, synchronously. The side-table slot is
        /// marked <c>InUse=0</c>, the bucket slot (or huge page) is returned, and the
        /// per-allocation safety handle is released — all before <see cref="Free"/> returns.
        ///
        /// <para>If any Burst job currently holds this allocation's safety handle (i.e. has
        /// it scheduled as a read or write dependency),
        /// <c>AtomicSafetyHandle.CheckDeallocateAndThrow</c> throws in editor / dev builds.
        /// Callers must arrange for no job to be using the handle when <c>Free</c> is called
        /// — typically by completing the relevant jobs first, or by calling <c>Free</c> at a
        /// phase-boundary where the framework has already drained outstanding jobs.</para>
        ///
        /// <para>In release builds with <c>ENABLE_UNITY_COLLECTIONS_CHECKS</c> off the
        /// dependency check is compiled out and freeing while a job uses the handle is
        /// undefined behaviour — same contract as <c>NativeList&lt;T&gt;.Dispose()</c>.</para>
        ///
        /// <para>If <paramref name="onDrained"/> is non-null, it runs after the safety check
        /// passes but before the chunk-store slot is released. Use this for any caller-owned
        /// memory tied to this allocation (e.g. the <c>TrecsList</c> data buffer) — the entry
        /// passed in is still pointing at valid slot memory; the chunk-store releases the slot
        /// after the callback returns. Prefer a static method assigned to a static readonly
        /// field to avoid per-call GC allocation from closure capture.</para>
        /// </summary>
        public void Free(PtrHandle handle, Action<NativeChunkStoreEntry> onDrained = null)
        {
            Assert.That(!_isDisposed);
            Assert.That(!handle.IsNull, "Attempted to free null PtrHandle");
            Assert.That(
                UnityThreadHelper.IsMainThread,
                "NativeChunkStore.Free must be called from the main thread"
            );

            NativeChunkStoreResolver.DecodeHandle(handle, out var idxU, out var gen);
            var idx = (int)idxU;
            Assert.That(
                idx < _sideTableLength,
                "Free: handle {} side-table index {} out of range",
                handle.Value,
                idx
            );
            var entry = GetEntry(idx);
            Assert.That(entry.InUse == 1, "Free: handle {} already freed", handle.Value);
            Assert.That(
                entry.Generation == gen,
                "Free: stale handle {} (slot gen {})",
                handle.Value,
                entry.Generation
            );

#if ENABLE_UNITY_COLLECTIONS_CHECKS
            // Throws InvalidOperationException if any job has this allocation scheduled
            // as a dependency. Caller is responsible for completing those jobs before
            // calling Free. Mirrors NativeList<T>.Dispose() semantics.
            AtomicSafetyHandle.CheckDeallocateAndThrow(entry.Safety);
            AtomicSafetyHandle.Release(entry.Safety);
#endif

            // Run caller cleanup while the entry's Address still points at live memory
            // (chunk store hasn't called AllocatorManager.Free on huge/external pages yet).
            onDrained?.Invoke(entry);

            entry.InUse = 0;
            entry.Address = IntPtr.Zero;
            SetEntry(idx, entry);

            ReturnSlot(entry.PageId, entry.SlotIndex, entry.IsHuge != 0);
            _freeSideTableSlots.Push(idx);
            _liveCount--;
            _log.Trace("Free: handle={0}", handle.Value);
        }

        /// <summary>
        /// Main-thread entry resolution. Reads directly from the side table; jobs use the
        /// equivalent <see cref="Resolver"/>. With the immediate-Alloc model there is no
        /// pending-adds layer to bridge, so this is now identical in behavior to a Burst-job
        /// resolve — kept as a managed-friendly entry point that doesn't require capturing
        /// the resolver struct.
        /// </summary>
        public NativeChunkStoreEntry ResolveEntry(PtrHandle handle)
        {
            Assert.That(!_isDisposed);
            Assert.That(!handle.IsNull, "Attempted to resolve null PtrHandle");
            Assert.That(
                UnityThreadHelper.IsMainThread,
                "NativeChunkStore.ResolveEntry is main-thread only; jobs use NativeChunkStoreResolver"
            );

            return _resolver.ResolveEntry(handle);
        }

        /// <summary>
        /// Walks the page list and frees any page whose every slot is unallocated. Returns
        /// the number of pages reclaimed. Safe to call at any flush boundary.
        /// </summary>
        internal int ReclaimEmptyPages()
        {
            Assert.That(!_isDisposed);
            Assert.That(UnityThreadHelper.IsMainThread);

            int reclaimed = 0;
            for (int pageId = 0; pageId < _pages.Count; pageId++)
            {
                var page = _pages[pageId];
                if (page == null || page.FreeCount != page.SlotCount)
                    continue;

                // Strip this page out of its bucket's freelist before freeing it.
                if (page.BucketIdx >= 0)
                {
                    var bucket = _buckets[page.BucketIdx];
                    bucket.PageIds.Remove(pageId);
                    RemovePageFromFreeSlots(bucket, pageId);
                }

                FreePage(page);
                _pages[pageId] = null;
                _freePageIds.Push(pageId);
                reclaimed++;
            }
            return reclaimed;
        }

        public void Dispose()
        {
            Assert.That(!_isDisposed);

            // Any side-table slots still in use indicate leaked allocations. Release
            // their safety handles so the safety system's slot table doesn't leak,
            // even though we can't return their bucket slots cleanly without knowing
            // who still references them.
#if ENABLE_UNITY_COLLECTIONS_CHECKS
            for (int i = 1; i < _sideTableLength; i++)
            {
                var entry = GetEntry(i);
                if (entry.InUse == 1)
                {
                    AtomicSafetyHandle.EnforceAllBufferJobsHaveCompletedAndRelease(entry.Safety);
                }
            }
#endif

            // Free every page we still own.
            foreach (var page in _pages)
            {
                if (page != null)
                {
                    FreePage(page);
                }
            }
            _pages.Clear();
            _freePageIds.Clear();
            _freeSideTableSlots.Clear();
#if ENABLE_UNITY_COLLECTIONS_CHECKS
            _externalAddresses.Clear();
#endif

            DisposeAllChunks();
            _chunkDirectory.Dispose();
            _sideTableLength = 0;
            _isDisposed = true;
        }

        // ─── Internal mechanics ──────────────────────────────────

        static int SelectBucket(int slotSize)
        {
            // Tiny linear scan over 13 entries — branch-predictor friendly, no log2 instruction
            // needed. Replace if profiling shows the cost.
            for (int i = 0; i < BucketSlotSizes.Length; i++)
            {
                if (BucketSlotSizes[i] >= slotSize)
                    return i;
            }
            throw Assert.CreateException(
                "Allocation size {} exceeds max bucket size {}",
                slotSize,
                MaxBucketSlotSize
            );
        }

        unsafe void AllocFromBucket(
            int bucketIdx,
            out int pageId,
            out int slotIdx,
            out IntPtr address
        )
        {
            var bucket = _buckets[bucketIdx];

            if (bucket.FreeSlots.Count == 0)
            {
                AllocateNewPageForBucket(bucketIdx);
            }

            var (pId, sIdx) = bucket.FreeSlots.Pop();
            var page = _pages[pId];
            Assert.That(page != null, "Free slot pointed at reclaimed page {}", pId);
            Assert.That(page.FreeCount > 0);
            page.FreeCount--;

            pageId = pId;
            slotIdx = sIdx;
            address = new IntPtr((byte*)page.Address.ToPointer() + sIdx * page.SlotSize);
        }

        unsafe void AllocateNewPageForBucket(int bucketIdx)
        {
            var bucket = _buckets[bucketIdx];
            var pagePtr = AllocatorManager.Allocate(
                Allocator.Persistent,
                bucket.PageSizeBytes,
                bucket.SlotSize,
                items: 1
            );
            Assert.That(
                pagePtr != null,
                "AllocatorManager returned null page (size {}, align {})",
                bucket.PageSizeBytes,
                bucket.SlotSize
            );

            var page = new Page
            {
                Address = new IntPtr(pagePtr),
                BucketIdx = bucketIdx,
                SlotSize = bucket.SlotSize,
                SlotCount = bucket.SlotsPerPage,
                FreeCount = bucket.SlotsPerPage,
                Alignment = bucket.SlotSize,
            };

            int pageId = AcquirePageId(page);
            bucket.PageIds.Add(pageId);

            // Seed the bucket freelist with every slot in the new page. Push in reverse so
            // pops come out in ascending slot order — gives newly-grown buckets a more
            // predictable layout for the next batch of allocations.
            for (int s = bucket.SlotsPerPage - 1; s >= 0; s--)
            {
                bucket.FreeSlots.Push((pageId, s));
            }

            _log.Trace(
                "Allocated bucket page: bucket={0} slotSize={1} slots={2} pageId={3}",
                bucketIdx,
                bucket.SlotSize,
                bucket.SlotsPerPage,
                pageId
            );
        }

        unsafe void AllocHugePage(
            int size,
            int alignment,
            out int pageId,
            out int slotIdx,
            out IntPtr address
        )
        {
            var pagePtr = AllocatorManager.Allocate(
                Allocator.Persistent,
                size,
                alignment,
                items: 1
            );
            Assert.That(
                pagePtr != null,
                "AllocatorManager returned null huge page (size {})",
                size
            );

            var page = new Page
            {
                Address = new IntPtr(pagePtr),
                BucketIdx = -1,
                SlotSize = size,
                SlotCount = 1,
                FreeCount = 0, // immediately occupied
                Alignment = alignment,
            };

            pageId = AcquirePageId(page);
            slotIdx = 0;
            address = page.Address;

            _log.Trace("Allocated huge page: size={0} pageId={1}", size, pageId);
        }

        /// <summary>
        /// Restores an entry at the exact slot/generation encoded in <paramref name="savedHandle"/>.
        /// Used by heap deserializers so that handles serialised at save-time still resolve at
        /// load-time — meaning component arrays that store handles can be blit through save/load
        /// without per-handle remapping.
        ///
        /// <para>Call <see cref="OnDeserializeComplete"/> after the last <see cref="AllocAtSlot"/>
        /// so the chunk store's free-slot accounting catches up with the now-populated side table.</para>
        /// </summary>
        [MethodImpl(MethodImplOptions.AggressiveInlining)]
        internal PtrHandle AllocAtSlot(uint savedHandle, int size, int alignment, int typeHash)
        {
            return AllocAtSlot(savedHandle, size, alignment, typeHash, out _);
        }

        /// <summary>
        /// Same as <see cref="AllocAtSlot(uint, int, int, int)"/> but additionally returns
        /// the slot's address so the deserializer can blit data straight into it without
        /// a follow-up <see cref="ResolveEntry"/>.
        /// </summary>
        internal PtrHandle AllocAtSlot(
            uint savedHandle,
            int size,
            int alignment,
            int typeHash,
            out IntPtr address
        )
        {
            Assert.That(!_isDisposed);
            Assert.That(savedHandle != 0, "AllocAtSlot: savedHandle is null");
            Assert.That(size > 0, "AllocAtSlot: size must be positive");
            Assert.That(
                alignment > 0 && (alignment & (alignment - 1)) == 0,
                "AllocAtSlot: alignment {} must be a positive power of two",
                alignment
            );
            Assert.That(UnityThreadHelper.IsMainThread);

            NativeChunkStoreResolver.DecodeHandle(
                new PtrHandle(savedHandle),
                out var idxU,
                out var gen
            );
            var idx = (int)idxU;
            Assert.That(idx >= 1, "AllocAtSlot: slot 0 is reserved as null");
            Assert.That(gen >= 1, "AllocAtSlot: generation 0 is reserved");

            EnsureSideTableLength(idx + 1);
            Assert.That(
                GetEntry(idx).InUse == 0,
                "AllocAtSlot: slot {} already in use (handle collision during restore?)",
                idx
            );

            int pageId;
            int slotIdx;
            bool isHuge;
            var neededSlotSize = Math.Max(size, alignment);
            if (neededSlotSize > MaxBucketSlotSize)
            {
                AllocHugePage(neededSlotSize, alignment, out pageId, out slotIdx, out address);
                isHuge = true;
            }
            else
            {
                var bucketIdx = SelectBucket(neededSlotSize);
                AllocFromBucket(bucketIdx, out pageId, out slotIdx, out address);
                isHuge = false;
            }

#if ENABLE_UNITY_COLLECTIONS_CHECKS
            var safety = AtomicSafetyHandle.Create();
            AtomicSafetyHandle.SetBumpSecondaryVersionOnScheduleWrite(safety, true);
#endif

            SetEntry(
                idx,
                new NativeChunkStoreEntry
                {
                    Address = address,
                    TypeHash = typeHash,
                    Generation = gen,
                    InUse = 1,
                    IsHuge = (byte)(isHuge ? 1 : 0),
                    PageId = pageId,
                    SlotIndex = slotIdx,
#if ENABLE_UNITY_COLLECTIONS_CHECKS
                    Safety = safety,
#endif
                }
            );
            _liveCount++;

            return new PtrHandle(savedHandle);
        }

        /// <summary>
        /// Reconciles slot accounting after one or more deserializers have called
        /// <see cref="AllocAtSlot"/>. Pushes every InUse=0 slot in the side table onto the
        /// free-slot stack and advances <c>_nextFreshSideTableSlot</c> past the populated
        /// range. Safe to call multiple times — each call sees the latest state. Without this
        /// reconciliation a fresh <see cref="Alloc"/> after deserialize could pick a slot
        /// index that's already in use by a restored entry.
        /// </summary>
        internal void OnDeserializeComplete()
        {
            Assert.That(!_isDisposed);
            Assert.That(UnityThreadHelper.IsMainThread);

            _freeSideTableSlots.Clear();
            for (int i = 1; i < _sideTableLength; i++)
            {
                if (GetEntry(i).InUse == 0)
                {
                    _freeSideTableSlots.Push(i);
                }
            }
            _nextFreshSideTableSlot = _sideTableLength;
        }

        /// <summary>
        /// Registers an externally-allocated pointer as a single-slot page owned by this
        /// chunk store. The pointer must have been allocated via <c>AllocatorManager.Allocate</c>
        /// with the supplied <paramref name="size"/> and <paramref name="alignment"/> — those
        /// values are stored verbatim and used to call <c>AllocatorManager.Free</c> when the
        /// returned handle is freed. After this call the chunk store takes exclusive ownership.
        /// </summary>
        public PtrHandle AllocExternal(IntPtr address, int size, int alignment, int typeHash)
        {
            Assert.That(!_isDisposed);
            Assert.That(address != IntPtr.Zero, "AllocExternal: null address");
            Assert.That(size > 0, "AllocExternal: size must be positive");
            Assert.That(
                alignment > 0 && (alignment & (alignment - 1)) == 0,
                "AllocExternal: alignment {} must be a positive power of two",
                alignment
            );
            Assert.That(
                UnityThreadHelper.IsMainThread,
                "NativeChunkStore.AllocExternal must be called from the main thread"
            );

#if ENABLE_UNITY_COLLECTIONS_CHECKS
            Assert.That(
                _externalAddresses.Add(address),
                "AllocExternal: pointer 0x{} is already registered in this chunk store "
                    + "(double-take-ownership leaves two aliased slots; freeing either "
                    + "dangles the other)",
                address.ToInt64()
            );
#endif

            var page = new Page
            {
                Address = address,
                BucketIdx = -1,
                SlotSize = size,
                SlotCount = 1,
                FreeCount = 0,
                Alignment = alignment,
            };
            var pageId = AcquirePageId(page);

            var sideTableIdx = AcquireSideTableSlot();
            var prior = GetEntry(sideTableIdx);
            var nextGen = (byte)(prior.Generation + 1);
            if (nextGen == 0)
                nextGen = 1;

#if ENABLE_UNITY_COLLECTIONS_CHECKS
            var safety = AtomicSafetyHandle.Create();
            AtomicSafetyHandle.SetBumpSecondaryVersionOnScheduleWrite(safety, true);
#endif

            var entry = new NativeChunkStoreEntry
            {
                Address = address,
                TypeHash = typeHash,
                Generation = nextGen,
                InUse = 1,
                IsHuge = 1, // single-slot page; the IsExternal bit on the Page handles the Free path.
                PageId = pageId,
                SlotIndex = 0,
#if ENABLE_UNITY_COLLECTIONS_CHECKS
                Safety = safety,
#endif
            };

            var handleValue = NativeChunkStoreResolver.EncodeHandleValue(
                nextGen,
                (uint)sideTableIdx
            );
            // See the Alloc method for the rationale; the slot was free and no
            // reader has a handle for it yet.
            SetEntry(sideTableIdx, entry);
            _liveCount++;

            return new PtrHandle(handleValue);
        }

        int AcquirePageId(Page page)
        {
            if (_freePageIds.Count > 0)
            {
                var pageId = _freePageIds.Pop();
                _pages[pageId] = page;
                return pageId;
            }
            _pages.Add(page);
            return _pages.Count - 1;
        }

        void ReturnSlot(int pageId, int slotIdx, bool isHuge)
        {
            var page = _pages[pageId];
            Assert.That(page != null, "ReturnSlot: page {} already reclaimed", pageId);

            if (isHuge)
            {
#if ENABLE_UNITY_COLLECTIONS_CHECKS
                // If this was an external page (registered via AllocExternal), drop it
                // from the tracking set so the address can be re-registered later. The
                // Remove is silent on miss — most huge pages aren't external.
                _externalAddresses.Remove(page.Address);
#endif
                FreePage(page);
                _pages[pageId] = null;
                _freePageIds.Push(pageId);
                return;
            }

            page.FreeCount++;
            Assert.That(
                page.FreeCount <= page.SlotCount,
                "Page {} free-count overflow ({} > {})",
                pageId,
                page.FreeCount,
                page.SlotCount
            );

            var bucket = _buckets[page.BucketIdx];
            bucket.FreeSlots.Push((pageId, slotIdx));
        }

        unsafe void FreePage(Page page)
        {
            // Total bytes to free differs between bucket pages (slotSize × slotCount) and
            // single-slot huge/external pages (the slot is the page).
            var sizeOf = page.BucketIdx >= 0 ? page.SlotSize * page.SlotCount : page.SlotSize;
            AllocatorManager.Free(
                Allocator.Persistent,
                page.Address.ToPointer(),
                sizeOf,
                page.Alignment,
                items: 1
            );
        }

        int AcquireSideTableSlot()
        {
            // Pop slots until we find one that isn't claimed. The free-list can contain
            // stale entries after a multi-heap Deserialize: each heap's Deserialize calls
            // OnDeserializeComplete which rebuilds the list from the current side table,
            // but a later heap's AllocAtSlot can then write InUse=1 into a slot that's
            // still recorded as free. Filtering here keeps the free-list eventually-
            // consistent without requiring the deserialize coordinators to know about it.
            while (_freeSideTableSlots.Count > 0)
            {
                var candidate = _freeSideTableSlots.Pop();
                if (candidate < _sideTableLength && GetEntry(candidate).InUse == 1)
                {
                    // Stale entry — a restored AllocAtSlot has claimed this slot since it
                    // was pushed. Discard and try the next.
                    continue;
                }
                // Materialise the side-table slot so Alloc can read its current generation
                // (the in-table Generation byte is the source of truth for slot reuse).
                EnsureSideTableLength(candidate + 1);
                return candidate;
            }

            var idx = _nextFreshSideTableSlot++;
            Assert.That(
                (uint)idx <= NativeChunkStoreResolver.MaxIndex,
                "NativeChunkStore exhausted side-table index space ({} max)",
                NativeChunkStoreResolver.MaxIndex
            );
            EnsureSideTableLength(idx + 1);
            return idx;
        }

        unsafe void EnsureSideTableLength(int requiredLength)
        {
            // Grow the chunked side table to cover at least `requiredLength` slots.
            // Materialise any chunks not yet allocated, zero-initialised so unused
            // slots read as default (InUse=0, Generation=0).
            while (_sideTableLength < requiredLength)
            {
                var chunkIdx = _sideTableLength >> NativeChunkStoreResolver.ChunkSizeBits;
                if (_chunkDirectory[chunkIdx] == IntPtr.Zero)
                {
                    AllocateChunk(chunkIdx);
                }
                _sideTableLength++;
            }
        }

        unsafe void AllocateChunk(int chunkIdx)
        {
            Assert.That(
                chunkIdx < NativeChunkStoreResolver.MaxChunkCount,
                "AllocateChunk: chunkIdx {} exceeds max chunk count {}",
                chunkIdx,
                NativeChunkStoreResolver.MaxChunkCount
            );
            Assert.That(
                _chunkDirectory[chunkIdx] == IntPtr.Zero,
                "AllocateChunk: chunk {} already allocated",
                chunkIdx
            );

            var entrySize = UnsafeUtility.SizeOf<NativeChunkStoreEntry>();
            var entryAlign = UnsafeUtility.AlignOf<NativeChunkStoreEntry>();
            var ptr = AllocatorManager.Allocate(
                Allocator.Persistent,
                entrySize,
                entryAlign,
                items: NativeChunkStoreResolver.ChunkSize
            );
            Assert.That(
                ptr != null,
                "AllocateChunk: AllocatorManager returned null for chunk {}",
                chunkIdx
            );
            UnsafeUtility.MemClear(ptr, (long)entrySize * NativeChunkStoreResolver.ChunkSize);
            // Defense-in-depth: any handle obtained via a normal Alloc → Schedule path
            // already sees the zero-init because Unity's job-system Schedule() fences
            // main-thread writes before the worker runs. The barrier here covers the
            // resolver's contract that ANY PtrHandle (garbage, stale, synthesised) must
            // safely report "not live" — without it, a handle whose index lands in a
            // mid-materialised chunk could, on weakly-ordered hardware (ARM), see the
            // new directory pointer but read uninitialised slot bytes that happen to
            // pass InUse=1/Generation checks. Also serves as a compiler fence on x64.
            Thread.MemoryBarrier();
            _chunkDirectory[chunkIdx] = new IntPtr(ptr);
        }

        [MethodImpl(MethodImplOptions.AggressiveInlining)]
        unsafe NativeChunkStoreEntry GetEntry(int idx)
        {
            Assert.That(
                idx >= 0 && idx < _sideTableLength,
                "GetEntry: idx {} out of range (length {})",
                idx,
                _sideTableLength
            );
            var chunkIdx = idx >> NativeChunkStoreResolver.ChunkSizeBits;
            var chunkPtr = (NativeChunkStoreEntry*)_chunkDirectory[chunkIdx].ToPointer();
            return chunkPtr[idx & NativeChunkStoreResolver.ChunkIndexMask];
        }

        [MethodImpl(MethodImplOptions.AggressiveInlining)]
        unsafe void SetEntry(int idx, in NativeChunkStoreEntry entry)
        {
            Assert.That(
                idx >= 0 && idx < _sideTableLength,
                "SetEntry: idx {} out of range (length {})",
                idx,
                _sideTableLength
            );
            var chunkIdx = idx >> NativeChunkStoreResolver.ChunkSizeBits;
            var chunkPtr = (NativeChunkStoreEntry*)_chunkDirectory[chunkIdx].ToPointer();
            chunkPtr[idx & NativeChunkStoreResolver.ChunkIndexMask] = entry;
        }

        unsafe void DisposeAllChunks()
        {
            var entrySize = UnsafeUtility.SizeOf<NativeChunkStoreEntry>();
            var entryAlign = UnsafeUtility.AlignOf<NativeChunkStoreEntry>();
            for (int i = 0; i < _chunkDirectory.Length; i++)
            {
                var ptr = _chunkDirectory[i];
                if (ptr == IntPtr.Zero)
                {
                    continue;
                }
                AllocatorManager.Free(
                    Allocator.Persistent,
                    ptr.ToPointer(),
                    entrySize,
                    entryAlign,
                    items: NativeChunkStoreResolver.ChunkSize
                );
                _chunkDirectory[i] = IntPtr.Zero;
            }
        }

        static void RemovePageFromFreeSlots(Bucket bucket, int pageId)
        {
            // Rebuild the freelist without entries for the reclaimed page. Stack ordering
            // is preserved relative to the survivors.
            if (bucket.FreeSlots.Count == 0)
                return;
            var survivors = new List<(int PageId, int SlotIdx)>(bucket.FreeSlots.Count);
            foreach (var entry in bucket.FreeSlots)
            {
                if (entry.PageId != pageId)
                {
                    survivors.Add(entry);
                }
            }
            bucket.FreeSlots.Clear();
            // Push in reverse so iteration order over survivors is preserved.
            for (int i = survivors.Count - 1; i >= 0; i--)
            {
                bucket.FreeSlots.Push(survivors[i]);
            }
        }

        // ─── Inner types ─────────────────────────────────────────

        sealed class Bucket
        {
            public int SlotSize;
            public int SlotsPerPage;
            public int PageSizeBytes;
            public readonly List<int> PageIds = new();
            public readonly Stack<(int PageId, int SlotIdx)> FreeSlots = new();
        }

        sealed class Page
        {
            public IntPtr Address;
            public int BucketIdx; // -1 for huge or external single-slot pages
            public int SlotSize;
            public int SlotCount;
            public int FreeCount;

            // Alignment passed to AllocatorManager.Allocate (or supplied by AllocExternal).
            // Stored so the matching AllocatorManager.Free call uses the same value — Unity's
            // allocator requires symmetric alloc/free parameters.
            public int Alignment;
        }
    }
}
