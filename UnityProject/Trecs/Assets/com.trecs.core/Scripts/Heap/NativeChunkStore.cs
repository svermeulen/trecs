using System;
using System.Collections.Generic;
using System.ComponentModel;
using System.Runtime.CompilerServices;
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
    /// The pending-flush model mirrors the heap layer above: <see cref="Alloc"/> and
    /// <see cref="Free"/> stage their side-table mutations in managed collections, and
    /// <see cref="FlushPendingOperations"/> promotes them to the Burst-visible
    /// <see cref="NativeList{T}"/> at a known sync point so concurrent jobs see a
    /// consistent snapshot.
    /// </para>
    ///
    /// <para><b>Threading invariant.</b> Every mutating API on this class
    /// (<see cref="Alloc"/>, <see cref="AllocImmediate"/>, <see cref="AllocExternal"/>,
    /// <see cref="AllocAtSlot"/>, <see cref="Free"/>, <see cref="FlushPendingOperations"/>,
    /// <see cref="ReclaimEmptyPages"/>, <see cref="OnDeserializeComplete"/>) is main-thread
    /// only AND must not run concurrent with any Burst job that holds a
    /// <see cref="NativeChunkStoreResolver"/>. The side-table is a <see cref="NativeList{T}"/>
    /// accessed by jobs via <c>[NativeDisableContainerSafetyRestriction]</c>; main-thread
    /// growth of the list can move the backing buffer, and a concurrent job reading
    /// through the resolver's cached struct would dereference torn state. Trecs's standard
    /// step model (main thread blocks waiting for jobs to complete before the next phase)
    /// satisfies this naturally; bespoke async code that does not block must arrange its
    /// own synchronisation.
    /// </para>
    /// </summary>
    [EditorBrowsable(EditorBrowsableState.Never)]
    public sealed class NativeChunkStore : IDisposable
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

        // Pending-flush coordination, same model as the existing native heaps.
        // _pendingAdds keeps newly-allocated entries main-thread-visible before they
        // reach the Burst-readable side table; _pendingFrees defers slot release until
        // the next flush so jobs that still hold the freed handle drain cleanly.
        readonly Dictionary<uint, NativeChunkStoreEntry> _pendingAdds = new();
        readonly List<PendingFree> _pendingFrees = new();

#if ENABLE_UNITY_COLLECTIONS_CHECKS
        // Tracks addresses currently registered via AllocExternal so we can catch a caller
        // that takes ownership of the same pointer twice (which would leave the chunk store
        // with two slots aliasing the same memory; freeing either leaves the other dangling).
        // Removed when the slot is released. Debug-only — release builds skip the check.
        readonly HashSet<IntPtr> _externalAddresses = new();
#endif

        NativeList<NativeChunkStoreEntry> _sideTable;
        NativeChunkStoreResolver _resolver;
        bool _isDisposed;
        int _liveCount;

        public NativeChunkStore(TrecsLog log)
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

            _sideTable = new NativeList<NativeChunkStoreEntry>(16, Allocator.Persistent);
            // Reserve slot 0 as the null slot. Pre-populate one inert entry so
            // _sideTable[0] is always safe to read.
            _sideTable.Add(default);

            _resolver = new NativeChunkStoreResolver(_sideTable);
        }

        public int NumLiveAllocations
        {
            get
            {
                Assert.That(!_isDisposed);
                // Tracked separately from any side-table/pending-set counts since live
                // allocations span both pending-adds (not yet promoted) and the side table
                // (post-flush), and exclude pending-frees (already detached on the user's
                // accounting but not yet physically released).
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
        /// <para>The new entry is staged in a managed pending-list and is only visible to the
        /// Burst-side resolver after <see cref="FlushPendingOperations"/>. Use
        /// <see cref="AllocImmediate"/> when the caller needs Burst-visibility before the next
        /// flush (e.g. input-phase frame-scoped allocations that get resolved by a job within
        /// the same step).</para>
        /// </summary>
        [MethodImpl(MethodImplOptions.AggressiveInlining)]
        public PtrHandle Alloc(int size, int alignment, int typeHash)
        {
            return Alloc(size, alignment, typeHash, out _);
        }

        /// <summary>
        /// Same as <see cref="Alloc(int, int, int)"/>, but additionally returns the slot's
        /// address in <paramref name="address"/>. Callers that need to write the initial
        /// value into the allocation should use this overload to skip the extra
        /// <see cref="ResolveEntry"/> dictionary lookup that would otherwise be required.
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

            // AcquireSideTableSlot guarantees _sideTable[sideTableIdx] exists, so the
            // generation we bump from is the slot's true last-used generation — even if
            // the prior cycle's release was a pre-flush fast-path that never went through
            // FlushPendingOperations. Skip 0 on wrap since 0 means "never allocated."
            var prior = _sideTable[sideTableIdx];
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
            _pendingAdds[handleValue] = newEntry;
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
        /// Releases a previously-allocated handle. The physical slot is returned to its bucket
        /// (or its dedicated huge page is freed) immediately if the alloc was still pending;
        /// otherwise the release is deferred to the next <see cref="FlushPendingOperations"/>
        /// so any jobs still holding the handle can drain.
        ///
        /// <para>If <paramref name="onDrained"/> is non-null, it runs after the safety handle
        /// has been drained but before the chunk-store slot is released. Use this for any
        /// caller-owned memory tied to this allocation (e.g. the <c>TrecsList</c> data buffer)
        /// — the callback runs at a point where no Burst job can still be referencing the
        /// allocation, so it's safe to <c>AllocatorManager.Free</c> auxiliary memory. The
        /// entry passed in is still pointing at valid slot memory; the chunk-store releases
        /// the slot after the callback returns. Prefer a static method assigned to a static
        /// readonly field to avoid per-call GC allocation from closure capture.</para>
        /// </summary>
        public void Free(PtrHandle handle, Action<NativeChunkStoreEntry> onDrained = null)
        {
            Assert.That(!_isDisposed);
            Assert.That(!handle.IsNull, "Attempted to free null PtrHandle");
            Assert.That(
                UnityThreadHelper.IsMainThread,
                "NativeChunkStore.Free must be called from the main thread"
            );

            // Fast path: still in pending-adds, the side table never saw this handle, so no
            // Burst job could have scheduled against it via the resolver. A main-thread-issued
            // wrapper could still be sitting in a scheduled job, though — EnforceAll drains
            // it. Critical ordering: drain BEFORE ReturnSlot, because for huge/external pages
            // ReturnSlot frees the page memory and a drain-blocked job would otherwise read
            // freed memory during EnforceAll's blocking window.
            if (_pendingAdds.Remove(handle.Value, out var pendingEntry))
            {
#if ENABLE_UNITY_COLLECTIONS_CHECKS
                AtomicSafetyHandle.EnforceAllBufferJobsHaveCompletedAndRelease(pendingEntry.Safety);
#endif
                onDrained?.Invoke(pendingEntry);
                ReturnSlot(pendingEntry.PageId, pendingEntry.SlotIndex, pendingEntry.IsHuge != 0);
                ReleasePreFlushSideTableSlot(handle, pendingEntry);
                _liveCount--;
                _log.Trace("Free (pre-flush): handle={0}", handle.Value);
                return;
            }

            // The handle has been promoted to the side table; defer the actual release so
            // any scheduled jobs that resolved through the side table can complete.
            NativeChunkStoreResolver.DecodeHandle(handle, out var idxU, out var gen);
            var idx = (int)idxU;
            Assert.That(
                idx < _sideTable.Length,
                "Free: handle {} side-table index {} out of range",
                handle.Value,
                idx
            );
            var entry = _sideTable[idx];
            Assert.That(entry.InUse == 1, "Free: handle {} already freed", handle.Value);
            Assert.That(
                entry.Generation == gen,
                "Free: stale handle {} (slot gen {})",
                handle.Value,
                entry.Generation
            );

            _pendingFrees.Add(
                new PendingFree
                {
                    SideTableIdx = idx,
                    PageId = entry.PageId,
                    SlotIndex = entry.SlotIndex,
                    IsHuge = entry.IsHuge != 0,
                    OnDrained = onDrained,
#if ENABLE_UNITY_COLLECTIONS_CHECKS
                    Safety = entry.Safety,
#endif
                }
            );
            _liveCount--;
            _log.Trace("Free (deferred): handle={0}", handle.Value);
        }

        /// <summary>
        /// Main-thread entry resolution that bridges <see cref="_pendingAdds"/> and the
        /// Burst-visible side table. Use this for main-thread access between <see cref="Alloc"/>
        /// and the next <see cref="FlushPendingOperations"/>; jobs use the <see cref="Resolver"/>.
        /// </summary>
        public NativeChunkStoreEntry ResolveEntry(PtrHandle handle)
        {
            Assert.That(!_isDisposed);
            Assert.That(!handle.IsNull, "Attempted to resolve null PtrHandle");
            Assert.That(
                UnityThreadHelper.IsMainThread,
                "NativeChunkStore.ResolveEntry is main-thread only; jobs use NativeChunkStoreResolver"
            );

            if (_pendingAdds.TryGetValue(handle.Value, out var pending))
            {
                return pending;
            }

            return _resolver.ResolveEntry(handle);
        }

        /// <summary>
        /// Promotes pending allocations into the Burst-visible side table and finalises any
        /// deferred frees. Must be called when no Burst jobs are reading the side table.
        /// </summary>
        public void FlushPendingOperations()
        {
            Assert.That(!_isDisposed);
            Assert.That(UnityThreadHelper.IsMainThread);

            if (_pendingAdds.Count > 0)
            {
                foreach (var (handleValue, entry) in _pendingAdds)
                {
                    NativeChunkStoreResolver.DecodeHandle(
                        new PtrHandle(handleValue),
                        out var idxU,
                        out _
                    );
                    // AcquireSideTableSlot already materialised this slot.
                    _sideTable[(int)idxU] = entry;
                }
                _pendingAdds.Clear();
            }

            if (_pendingFrees.Count > 0)
            {
                foreach (var pf in _pendingFrees)
                {
                    // Order matches the pre-flush fast-path: drain safety handle BEFORE
                    // releasing any memory, then run caller-supplied cleanup, then release.
                    // The drain is mostly a no-op here (flush is documented as "no jobs
                    // running"), but for huge/external pages it would otherwise be a UAF
                    // window if the caller broke that contract.
                    var entry = _sideTable[pf.SideTableIdx];
#if ENABLE_UNITY_COLLECTIONS_CHECKS
                    AtomicSafetyHandle.EnforceAllBufferJobsHaveCompletedAndRelease(pf.Safety);
#endif
                    pf.OnDrained?.Invoke(entry);

                    entry.InUse = 0;
                    entry.Address = IntPtr.Zero;
                    _sideTable[pf.SideTableIdx] = entry;

                    ReturnSlot(pf.PageId, pf.SlotIndex, pf.IsHuge);
                    _freeSideTableSlots.Push(pf.SideTableIdx);
                }
                _pendingFrees.Clear();
            }
        }

        /// <summary>
        /// Walks the page list and frees any page whose every slot is unallocated. Returns
        /// the number of pages reclaimed. Safe to call at any flush boundary.
        /// </summary>
        public int ReclaimEmptyPages()
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

            // Drain any pending state — releases their safety handles too.
            FlushPendingOperations();

            // Any side-table slots still in use indicate leaked allocations. Release
            // their safety handles so the safety system's slot table doesn't leak,
            // even though we can't return their bucket slots cleanly without knowing
            // who still references them.
#if ENABLE_UNITY_COLLECTIONS_CHECKS
            for (int i = 1; i < _sideTable.Length; i++)
            {
                var entry = _sideTable[i];
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

            _sideTable.Dispose();
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
        /// Bucketed allocation that bypasses the pending-add staging — the new entry lands in
        /// the side table immediately and is visible to Burst resolvers on the next read. Use
        /// from contexts that are guaranteed not to run concurrent with Burst jobs (the input
        /// phase, between submission and tick start, etc.) and need the alloc to be resolvable
        /// from a Burst job scheduled before the next <see cref="FlushPendingOperations"/>.
        /// </summary>
        [MethodImpl(MethodImplOptions.AggressiveInlining)]
        public PtrHandle AllocImmediate(int size, int alignment, int typeHash)
        {
            return AllocImmediate(size, alignment, typeHash, out _);
        }

        /// <summary>
        /// Same as <see cref="AllocImmediate(int, int, int)"/> but additionally returns the
        /// slot's address so the caller can write the initial value without a follow-up
        /// <see cref="ResolveEntry"/>.
        /// </summary>
        public PtrHandle AllocImmediate(int size, int alignment, int typeHash, out IntPtr address)
        {
            var handle = Alloc(size, alignment, typeHash, out address);
            PromotePendingEntryToSideTable(handle);
            return handle;
        }

        /// <summary>
        /// Like <see cref="AllocExternal"/> but writes the entry to the side table immediately
        /// — see <see cref="AllocImmediate"/> for the visibility contract.
        /// </summary>
        public PtrHandle AllocExternalImmediate(
            IntPtr address,
            int size,
            int alignment,
            int typeHash
        )
        {
            var handle = AllocExternal(address, size, alignment, typeHash);
            PromotePendingEntryToSideTable(handle);
            return handle;
        }

        void PromotePendingEntryToSideTable(PtrHandle handle)
        {
            if (_pendingAdds.Remove(handle.Value, out var entry))
            {
                NativeChunkStoreResolver.DecodeHandle(handle, out var idxU, out _);
                _sideTable[(int)idxU] = entry;
            }
        }

        /// <summary>
        /// Restores an entry at the exact slot/generation encoded in <paramref name="savedHandle"/>.
        /// Used by heap deserializers so that handles serialised at save-time still resolve at
        /// load-time — meaning component arrays that store handles can be blit through save/load
        /// without per-handle remapping.
        ///
        /// <para>The new entry is written directly to the side table (no pending-add staging).
        /// Call <see cref="OnDeserializeComplete"/> after the last <see cref="AllocAtSlot"/> so
        /// the chunk store's free-slot accounting catches up with the now-populated side table.</para>
        /// </summary>
        [MethodImpl(MethodImplOptions.AggressiveInlining)]
        public PtrHandle AllocAtSlot(uint savedHandle, int size, int alignment, int typeHash)
        {
            return AllocAtSlot(savedHandle, size, alignment, typeHash, out _);
        }

        /// <summary>
        /// Same as <see cref="AllocAtSlot(uint, int, int, int)"/> but additionally returns
        /// the slot's address so the deserializer can blit data straight into it without
        /// a follow-up <see cref="ResolveEntry"/>.
        /// </summary>
        public PtrHandle AllocAtSlot(
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
                _sideTable[idx].InUse == 0,
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

            _sideTable[idx] = new NativeChunkStoreEntry
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
            };
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
        public void OnDeserializeComplete()
        {
            Assert.That(!_isDisposed);
            Assert.That(UnityThreadHelper.IsMainThread);

            _freeSideTableSlots.Clear();
            for (int i = 1; i < _sideTable.Length; i++)
            {
                if (_sideTable[i].InUse == 0)
                {
                    _freeSideTableSlots.Push(i);
                }
            }
            _nextFreshSideTableSlot = _sideTable.Length;
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
            var prior = _sideTable[sideTableIdx];
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
            _pendingAdds[handleValue] = entry;
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
                if (candidate < _sideTable.Length && _sideTable[candidate].InUse == 1)
                {
                    // Stale entry — a restored AllocAtSlot has claimed this slot since it
                    // was pushed. Discard and try the next.
                    continue;
                }
                // Materialise the side-table slot eagerly so Alloc can always read its
                // current generation, and so pre-flush Free can persist a bumped
                // generation against any stale handle still floating around.
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

        void EnsureSideTableLength(int requiredLength)
        {
            while (_sideTable.Length < requiredLength)
            {
                _sideTable.Add(default);
            }
        }

        void ReleasePreFlushSideTableSlot(PtrHandle handle, NativeChunkStoreEntry entry)
        {
            // The entry never reached the side table via FlushPendingOperations, but we
            // still need to persist the just-used generation so a later Alloc that reuses
            // this slot bumps to a fresh value (otherwise two Alloc/Free/Alloc cycles
            // within a single flush window would hand out colliding handle values).
            NativeChunkStoreResolver.DecodeHandle(handle, out var idxU, out _);
            var idx = (int)idxU;
            // AcquireSideTableSlot guaranteed the slot exists.
            var existing = _sideTable[idx];
            existing.InUse = 0;
            existing.Address = IntPtr.Zero;
            existing.Generation = entry.Generation;
            _sideTable[idx] = existing;
            _freeSideTableSlots.Push(idx);
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

        struct PendingFree
        {
            public int SideTableIdx;
            public int PageId;
            public int SlotIndex;
            public bool IsHuge;
            public Action<NativeChunkStoreEntry> OnDrained;
#if ENABLE_UNITY_COLLECTIONS_CHECKS
            public AtomicSafetyHandle Safety;
#endif
        }
    }
}
