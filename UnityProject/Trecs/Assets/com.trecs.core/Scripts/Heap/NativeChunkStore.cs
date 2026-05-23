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
    /// persistent <see cref="NativeUniquePtr{T}"/> and <see cref="TrecsList{T}"/>.
    /// Per-frame input-allocated unmanaged data lives in
    /// <see cref="InputNativeUniqueHeap"/>, not in this chunk store.
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
    ///     <see cref="ReclaimEmptyPages"/>, <see cref="Serialize"/>,
    ///     <see cref="Deserialize"/>, <see cref="Dispose"/>. These free backing memory
    ///     or rebuild internal state in a transient way that would tear concurrent
    ///     reads. Trecs's standard step model satisfies this by blocking on outstanding
    ///     jobs before each <c>Submit</c> call.
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

        // (handle.Value → call site that allocated it). Populated by Alloc / AllocExternal
        // via [CallerFilePath] / [CallerLineNumber] attributes baked into the IL at user
        // call sites; consumed by the Dispose-time leak warning to give actionable
        // attribution. Debug-only — release builds skip both the tracking and the warning.
        readonly Dictionary<uint, (string File, int Line)> _callerInfoByHandle = new();
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
                TrecsDebugAssert.That(!_isDisposed);
                return _liveCount;
            }
        }

        public int NumPages
        {
            get
            {
                TrecsDebugAssert.That(!_isDisposed);
                return _pages.Count - _freePageIds.Count;
            }
        }

        /// <summary>
        /// Snapshot of internal allocator state — per-bucket page/slot counts, side-table
        /// density, total reserved bytes. Intended for benchmarking and diagnostics only;
        /// kept <c>internal</c> so it isn't part of the public API surface yet.
        /// </summary>
        internal HeapStatistics GetStatistics()
        {
            TrecsDebugAssert.That(!_isDisposed);
            TrecsDebugAssert.That(
                UnityThreadHelper.IsMainThread,
                "NativeChunkStore.GetStatistics must be called from the main thread"
            );

            var bucketStats = new BucketStatistics[_buckets.Length];
            long totalReserved = 0;

            for (int b = 0; b < _buckets.Length; b++)
            {
                var bucket = _buckets[b];
                int pageCount = 0;
                int liveSlotCount = 0;
                int freeSlotCount = 0;
                long reservedBytes = 0;
                foreach (var pageId in bucket.PageIds)
                {
                    var page = _pages[pageId];
                    if (page == null)
                    {
                        continue;
                    }
                    pageCount++;
                    liveSlotCount += page.SlotCount - page.FreeCount;
                    freeSlotCount += page.FreeCount;
                    reservedBytes += (long)page.SlotSize * page.SlotCount;
                }
                bucketStats[b] = new BucketStatistics
                {
                    SlotSize = bucket.SlotSize,
                    SlotsPerPage = bucket.SlotsPerPage,
                    PageCount = pageCount,
                    LiveSlotCount = liveSlotCount,
                    FreeSlotCount = freeSlotCount,
                    ReservedBytes = reservedBytes,
                };
                totalReserved += reservedBytes;
            }

            int numHuge = 0;
            long hugeReserved = 0;
            for (int pageId = 0; pageId < _pages.Count; pageId++)
            {
                var page = _pages[pageId];
                if (page == null || page.BucketIdx >= 0)
                {
                    continue;
                }
                numHuge++;
                hugeReserved += (long)page.SlotSize * page.SlotCount;
            }
            totalReserved += hugeReserved;

            return new HeapStatistics
            {
                LiveAllocations = _liveCount,
                NumPages = _pages.Count - _freePageIds.Count,
                NumHugePages = numHuge,
                SideTableLength = _sideTableLength,
                FreeSideTableSlots = _freeSideTableSlots.Count,
                NextFreshSideTableSlot = _nextFreshSideTableSlot,
                HugePagesReservedBytes = hugeReserved,
                TotalReservedBytes = totalReserved,
                Buckets = bucketStats,
            };
        }

        /// <summary>
        /// Per-page occupancy snapshot — one <see cref="PageOccupancyHeader"/> per
        /// currently-allocated page, plus a flat bool span recording per-slot in-use
        /// state. Intended for the heap-stress HUD's fragmentation visualisation;
        /// not part of the public Trecs API surface.
        ///
        /// <para>Caller owns the output lists and is expected to reuse them across
        /// calls. Each call clears them and refills. Walking the side table to mark
        /// live slots is O(sideTableLength), so prefer to call this at the same low
        /// cadence as <see cref="GetStatistics"/> rather than per frame.</para>
        /// </summary>
        internal void SnapshotPageOccupancy(
            List<PageOccupancyHeader> outPages,
            List<bool> outLiveSlots
        )
        {
            TrecsDebugAssert.That(!_isDisposed);
            TrecsDebugAssert.That(
                UnityThreadHelper.IsMainThread,
                "NativeChunkStore.SnapshotPageOccupancy must be called from the main thread"
            );
            TrecsDebugAssert.That(outPages != null && outLiveSlots != null);

            outPages.Clear();
            outLiveSlots.Clear();

            // Pass 1: reserve a header + per-slot bool block for each live page.
            // pageIdToHeaderIdx is keyed by raw pageId (which may have gaps due to
            // reclaimed pages) and maps into the dense outPages list.
            var pageIdToHeaderIdx = new int[_pages.Count];
            for (int pageId = 0; pageId < _pages.Count; pageId++)
            {
                var page = _pages[pageId];
                if (page == null)
                {
                    pageIdToHeaderIdx[pageId] = -1;
                    continue;
                }
                pageIdToHeaderIdx[pageId] = outPages.Count;
                outPages.Add(
                    new PageOccupancyHeader
                    {
                        PageId = pageId,
                        BucketIdx = page.BucketIdx,
                        SlotSize = page.SlotSize,
                        SlotCount = page.SlotCount,
                        FreeCount = page.FreeCount,
                        LiveSlotsStartIndex = outLiveSlots.Count,
                    }
                );
                for (int s = 0; s < page.SlotCount; s++)
                {
                    outLiveSlots.Add(false);
                }
            }

            // Pass 2: walk the side table and mark each live entry's slot. Skip
            // slot 0 (the reserved null sentinel).
            for (int i = 1; i < _sideTableLength; i++)
            {
                var entry = GetEntry(i);
                if (entry.InUse != 1)
                {
                    continue;
                }
                var headerIdx = pageIdToHeaderIdx[entry.PageId];
                TrecsDebugAssert.That(
                    headerIdx >= 0,
                    "SnapshotPageOccupancy: live entry references reclaimed page {0}",
                    entry.PageId
                );
                var header = outPages[headerIdx];
                outLiveSlots[header.LiveSlotsStartIndex + entry.SlotIndex] = true;
            }
        }

        public ref NativeChunkStoreResolver Resolver
        {
            get
            {
                TrecsDebugAssert.That(!_isDisposed);
                return ref _resolver;
            }
        }

        /// <summary>
        /// Allocate a slot at least <paramref name="size"/> bytes wide, aligned to at least
        /// <paramref name="alignment"/>. Returns a handle whose validity is checked against
        /// the side table's generation/in-use bits on every resolve.
        ///
        /// <para>The new entry is written to the side table before the handle is returned,
        /// so any Burst job scheduled afterwards with this handle can resolve it immediately.</para>
        /// </summary>
        [MethodImpl(MethodImplOptions.AggressiveInlining)]
        public PtrHandle Alloc(
            int size,
            int alignment,
            int typeHash,
            [CallerFilePath] string callerFile = null,
            [CallerLineNumber] int callerLine = 0
        )
        {
            return Alloc(size, alignment, typeHash, out _, callerFile, callerLine);
        }

        /// <summary>
        /// Same as <see cref="Alloc(int, int, int, string, int)"/>, but additionally returns
        /// the slot's address in <paramref name="address"/>. Callers that need to write the
        /// initial value into the allocation should use this overload to avoid an immediate
        /// follow-up <see cref="ResolveEntry"/>.
        ///
        /// <para><paramref name="callerFile"/> / <paramref name="callerLine"/> are filled by
        /// the compiler at the call site (<c>[CallerFilePath]</c> / <c>[CallerLineNumber]</c>);
        /// pass them through explicitly when forwarding from a wrapper so the recorded site
        /// is the user's, not the wrapper's. Stored under <c>ENABLE_UNITY_COLLECTIONS_CHECKS</c>
        /// only; the leak warning at <see cref="Dispose"/> renders them to make the warning
        /// actionable.</para>
        /// </summary>
        public unsafe PtrHandle Alloc(
            int size,
            int alignment,
            int typeHash,
            out IntPtr address,
            [CallerFilePath] string callerFile = null,
            [CallerLineNumber] int callerLine = 0
        )
        {
            using var _ = TrecsProfiling.Start("NativeChunkStore.Alloc");
            TrecsDebugAssert.That(!_isDisposed);
            TrecsDebugAssert.That(size > 0, "Allocation size must be positive");
            TrecsDebugAssert.That(
                alignment > 0 && (alignment & (alignment - 1)) == 0,
                "Alignment {0} must be a positive power of two",
                alignment
            );
            TrecsDebugAssert.That(
                UnityThreadHelper.IsMainThread,
                "NativeChunkStore.Alloc must be called from the main thread"
            );

            int pageId;
            int slotIdx;
            int bucketIdx;
            bool ownsWholePage;
            int slotByteSize;

            var neededSlotSize = Math.Max(size, alignment);
            if (neededSlotSize > MaxBucketSlotSize)
            {
                AllocHugePage(neededSlotSize, alignment, out pageId, out slotIdx, out address);
                bucketIdx = -1;
                ownsWholePage = true;
                slotByteSize = neededSlotSize;
            }
            else
            {
                bucketIdx = SelectBucket(neededSlotSize);
                AllocFromBucket(bucketIdx, out pageId, out slotIdx, out address);
                ownsWholePage = false;
                slotByteSize = _buckets[bucketIdx].SlotSize;
            }

            // Zero the entire slot — not just the caller's requested `size` — so any tail
            // bytes (between size and slotByteSize, or past Count*ElementSize for
            // variable-size containers) are deterministic. Snapshots dump full slot bytes;
            // without this, recycled slots leak the previous tenant's contents into the
            // snapshot non-deterministically across worlds.
            UnsafeUtility.MemClear(address.ToPointer(), slotByteSize);

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
                OwnsWholePage = (byte)(ownsWholePage ? 1 : 0),
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

#if ENABLE_UNITY_COLLECTIONS_CHECKS
            // Stash the user's call site so the Dispose-time leak warning can render it.
            // callerFile is null for callers that don't propagate (raw internal use); the
            // warning renders those as "<unknown caller>".
            if (callerFile != null)
            {
                _callerInfoByHandle[handleValue] = (callerFile, callerLine);
            }
#endif

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
        public void Free(PtrHandle handle)
        {
            using var _ = TrecsProfiling.Start("NativeChunkStore.Free");
            TrecsDebugAssert.That(!_isDisposed);
            TrecsDebugAssert.That(!handle.IsNull, "Attempted to free null PtrHandle");
            TrecsDebugAssert.That(
                UnityThreadHelper.IsMainThread,
                "NativeChunkStore.Free must be called from the main thread"
            );

            NativeChunkStoreResolver.DecodeHandle(handle, out var idxU, out var gen);
            var idx = (int)idxU;
            TrecsDebugAssert.That(
                idx < _sideTableLength,
                "Free: handle {0} side-table index {1} out of range",
                handle.Value,
                idx
            );
            var entry = GetEntry(idx);
            TrecsDebugAssert.That(entry.InUse == 1, "Free: handle {0} already freed", handle.Value);
            TrecsDebugAssert.That(
                entry.Generation == gen,
                "Free: stale handle {0} (slot gen {1})",
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

            entry.InUse = 0;
            entry.Address = IntPtr.Zero;
            SetEntry(idx, entry);

            ReturnSlot(entry.PageId, entry.SlotIndex, entry.OwnsWholePage != 0);
            _freeSideTableSlots.Push(idx);
            _liveCount--;
#if ENABLE_UNITY_COLLECTIONS_CHECKS
            // Free is silent on miss — not every slot was registered with caller info
            // (e.g. allocations from internal code that didn't propagate [CallerFilePath]).
            _callerInfoByHandle.Remove(handle.Value);
#endif
            _log.Trace("Free: handle={0}", handle.Value);
        }

        /// <summary>
        /// Main-thread entry resolution. Behaves identically to <see cref="Resolver"/>'s
        /// <c>ResolveEntry</c> — provided as a managed-friendly entry point that doesn't
        /// require capturing the resolver struct, and asserts main-thread.
        /// </summary>
        public NativeChunkStoreEntry ResolveEntry(PtrHandle handle)
        {
            TrecsDebugAssert.That(!_isDisposed);
            TrecsDebugAssert.That(!handle.IsNull, "Attempted to resolve null PtrHandle");
            TrecsDebugAssert.That(
                UnityThreadHelper.IsMainThread,
                "NativeChunkStore.ResolveEntry is main-thread only; jobs use NativeChunkStoreResolver"
            );

            return _resolver.ResolveEntry(handle);
        }

        /// <summary>
        /// Walks the page list and frees any page whose every slot is unallocated. Returns
        /// the number of pages reclaimed. Submit-time only: no jobs may be resolving handles
        /// while this runs.
        /// </summary>
        internal int ReclaimEmptyPages()
        {
            using var _ = TrecsProfiling.Start("NativeChunkStore.ReclaimEmptyPages");
            TrecsDebugAssert.That(!_isDisposed);
            TrecsDebugAssert.That(UnityThreadHelper.IsMainThread);

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
            TrecsDebugAssert.That(!_isDisposed);

            // Any side-table slots still in use indicate leaked allocations. Render a
            // single grouped warning (type + call site + occurrence count), then release
            // their safety handles so the safety system's slot table doesn't leak.
            // (Bucket slots themselves are reclaimed by the unconditional FreePage loop
            // below, regardless of leak state.)
#if ENABLE_UNITY_COLLECTIONS_CHECKS
            WarnAboutLiveLeaks();

            for (int i = 1; i < _sideTableLength; i++)
            {
                var entry = GetEntry(i);
                if (entry.InUse == 1)
                {
                    AtomicSafetyHandle.EnforceAllBufferJobsHaveCompletedAndRelease(entry.Safety);
                }
            }
            _callerInfoByHandle.Clear();
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

#if ENABLE_UNITY_COLLECTIONS_CHECKS
        /// <summary>
        /// Walks every still-live side-table slot at <see cref="Dispose"/> time, groups
        /// them by <c>(TypeHash, caller file, caller line)</c>, and logs one warning per
        /// distinct group with a <c>(×N)</c> suffix. Types are resolved via
        /// <see cref="TypeIdReverseLookup.TryGetTypeFromId"/>; slots whose id isn't
        /// registered there render as <c>"TypeId 0x…"</c>. Slots with no recorded call
        /// site (raw internal allocations that didn't propagate <c>[CallerFilePath]</c>)
        /// render as <c>"&lt;unknown caller&gt;"</c>.
        /// </summary>
        void WarnAboutLiveLeaks()
        {
            if (_liveCount == 0 || !_log.IsWarningEnabled())
            {
                return;
            }

            // Group by (typeHash, file, line) so identical leaks dedupe with a count.
            var groups = new Dictionary<(int TypeHash, string File, int Line), int>();
            for (int i = 1; i < _sideTableLength; i++)
            {
                var entry = GetEntry(i);
                if (entry.InUse != 1)
                    continue;

                var handleValue = NativeChunkStoreResolver.EncodeHandleValue(
                    entry.Generation,
                    (uint)i
                );
                _callerInfoByHandle.TryGetValue(handleValue, out var caller);
                var key = (entry.TypeHash, caller.File, caller.Line);
                groups.TryGetValue(key, out var count);
                groups[key] = count + 1;
            }

            _log.Warning("NativeChunkStore: {0} undisposed allocation(s) at teardown:", _liveCount);
            foreach (var (key, count) in groups)
            {
                var typeName = TypeIdReverseLookup.TryGetTypeFromId(
                    new TypeId(key.TypeHash),
                    out var t
                )
                    ? t.GetPrettyName()
                    : $"TypeId 0x{key.TypeHash:X8}";
                var site = key.File != null ? $"{key.File}:{key.Line}" : "<unknown caller>";
                var suffix = count > 1 ? $" (×{count})" : "";
                _log.Warning("  - {0} at {1}{2}", typeName, site, suffix);
            }
        }
#endif

        // ─── Serialization ───────────────────────────────────────

        const int SerializationVersion = 1;

        /// <summary>
        /// Bulk-dumps the chunk store's full state in a few wide blits. Replaces the
        /// previous model where consuming heaps drove restoration one entry at a time.
        /// Must be called before each heap's <c>Serialize</c> — on load the heaps assume
        /// every chunk-store slot they reference has already been re-materialised.
        ///
        /// <para>The wire format is deterministic: identical chunk-store state produces
        /// identical bytes. Process-local fields (<c>AtomicSafetyHandle</c>, raw page
        /// addresses) are stripped or replaced on load.</para>
        /// </summary>
        public unsafe void Serialize(ISerializationWriter writer)
        {
            using var _ = TrecsProfiling.Start("NativeChunkStore.Serialize");
            TrecsDebugAssert.That(!_isDisposed);
            TrecsDebugAssert.That(UnityThreadHelper.IsMainThread);

            writer.Write<int>("Version", SerializationVersion);
            writer.Write<int>("LiveCount", _liveCount);
            writer.Write<int>("SideTableLength", _sideTableLength);
            writer.Write<int>("NextFreshSideTableSlot", _nextFreshSideTableSlot);
            writer.Write<int>("NumPages", _pages.Count);

            // Pages: dense iteration over pageId so null slots round-trip and pageId
            // values stay stable across save/load.
            for (int pageId = 0; pageId < _pages.Count; pageId++)
            {
                var page = _pages[pageId];
                if (page == null)
                {
                    writer.Write<byte>("Kind", (byte)PageKind.Null);
                    continue;
                }

                var kind = page.BucketIdx >= 0 ? PageKind.Bucket : PageKind.SingleSlot;
                writer.Write<byte>("Kind", (byte)kind);
                writer.Write<int>("SlotSize", page.SlotSize);
                writer.Write<int>("SlotCount", page.SlotCount);
                writer.Write<int>("FreeCount", page.FreeCount);
                writer.Write<int>("Alignment", page.Alignment);
                writer.Write<int>("BucketIdx", page.BucketIdx);

                var pageBytes =
                    page.BucketIdx >= 0 ? page.SlotSize * page.SlotCount : page.SlotSize;
                writer.BlitWriteRawBytes("Data", page.Address.ToPointer(), pageBytes);
            }

            WriteSideTableAndFreeSlots(writer);

            _log.Trace(
                "Chunk store serialized: pages={0} liveEntries={1} sideTableLength={2}",
                _pages.Count,
                _liveCount,
                _sideTableLength
            );
        }

        /// <summary>
        /// Restores the chunk store from a stream produced by <see cref="Serialize"/>.
        /// Must be called on a freshly-constructed, empty chunk store (no allocations
        /// since the constructor). Each heap's <c>Deserialize</c> runs after this and
        /// can assume every saved handle resolves to live, restored memory.
        /// </summary>
        public unsafe void Deserialize(ISerializationReader reader)
        {
            using var _ = TrecsProfiling.Start("NativeChunkStore.Deserialize");
            TrecsDebugAssert.That(!_isDisposed);
            TrecsDebugAssert.That(UnityThreadHelper.IsMainThread);
            // No "live count must be 0" pre-assert: ResetForDeserialize below wipes
            // everything (pages, side-table generations, bucket bookkeeping, live count).
            // Persistent NativeUniquePtr / TrecsList allocations live entirely here, so
            // there's no upstream bookkeeping to ClearAll first; any leftover live slots
            // from before deserialize get reclaimed by the reset along with their safety
            // handles.

            // Read the header upfront so the reset below can scope its
            // side-table memclear to only the slot range that actually needs
            // clearing — slots that were "fresh" (untouched) under the old
            // _nextFreshSideTableSlot but now fall past the new
            // _nextFreshSideTableSlot. For the rollback shape (save then
            // immediately reload the same world), that range is empty, so the
            // memclear cost drops to zero — the largest single win in this
            // path. See ResetForDeserialize's ZeroChunks loop for the
            // rationale on the partial wipe.
            var version = reader.Read<int>("Version");
            TrecsDebugAssert.That(
                version == SerializationVersion,
                "Deserialize: chunk-store snapshot version {0} does not match expected {1}",
                version,
                SerializationVersion
            );

            var liveCount = reader.Read<int>("LiveCount");
            var sideTableLength = reader.Read<int>("SideTableLength");
            var nextFreshSideTableSlot = reader.Read<int>("NextFreshSideTableSlot");
            var numPages = reader.Read<int>("NumPages");

            // Reset back to a fresh-construction state: free any stale bucket pages,
            // zero-init the side-table slots that need it (see partial-wipe
            // rationale above), reset bucket and free-slot bookkeeping.
            // Required because heap.ClearAll empties live entries but leaves unused
            // bucket pages and generation-bumped slots behind.
            using (TrecsProfiling.Start("ResetForDeserialize"))
            {
                ResetForDeserialize(nextFreshSideTableSlot);
            }

            // Materialise side-table chunks up to the saved length. Chunks are zero-init
            // so unused slots read as default (InUse=0) without any extra work.
            using (TrecsProfiling.Start("EnsureSideTableLength"))
            {
                EnsureSideTableLength(sideTableLength);
            }

            // Pages: allocate fresh persistent memory, blit bytes in, rebuild Page objects.
            using (TrecsProfiling.Start("Reading pages"))
            {
                for (int pageId = 0; pageId < numPages; pageId++)
                {
                    var kind = (PageKind)reader.Read<byte>("Kind");
                    if (kind == PageKind.Null)
                    {
                        _pages.Add(null);
                        // _freePageIds is restored from the persisted stack below; we don't
                        // push here so the stack order matches the original.
                        continue;
                    }

                    var slotSize = reader.Read<int>("SlotSize");
                    var slotCount = reader.Read<int>("SlotCount");
                    var freeCount = reader.Read<int>("FreeCount");
                    var alignment = reader.Read<int>("Alignment");
                    var bucketIdx = reader.Read<int>("BucketIdx");

                    var pageBytes = bucketIdx >= 0 ? slotSize * slotCount : slotSize;
                    // Match the size/items convention used by AllocateNewPageForBucket and
                    // AllocHugePage: pass total bytes as `size`, items=1. FreePage uses the
                    // same convention symmetrically.
                    var pagePtr = AllocatorManager.Allocate(
                        Allocator.Persistent,
                        pageBytes,
                        alignment,
                        items: 1
                    );
                    TrecsDebugAssert.That(
                        pagePtr != null,
                        "Deserialize: AllocatorManager returned null for page {0} ({1} bytes)",
                        pageId,
                        pageBytes
                    );
                    reader.BlitReadRawBytes("Data", pagePtr, pageBytes);

                    var page = new Page
                    {
                        Address = new IntPtr(pagePtr),
                        BucketIdx = bucketIdx,
                        SlotSize = slotSize,
                        SlotCount = slotCount,
                        FreeCount = freeCount,
                        Alignment = alignment,
                    };
                    _pages.Add(page);
                    if (bucketIdx >= 0)
                    {
                        _buckets[bucketIdx].PageIds.Add(pageId);
                    }
                }
            }

            // Sparse side-table entries.
            var numLiveEntries = reader.Read<int>("NumLiveEntries");
            TrecsDebugAssert.That(
                numLiveEntries == liveCount,
                "Deserialize: numLiveEntries {0} does not match liveCount {1}",
                numLiveEntries,
                liveCount
            );

            using (TrecsProfiling.Start("Reading side-table entries"))
            {
                var payloadSize = UnsafeUtility.SizeOf<NativeChunkStoreEntryPayload>();
                for (int n = 0; n < numLiveEntries; n++)
                {
                    var slotIdx = reader.Read<int>("SlotIdx");
                    TrecsDebugAssert.That(
                        slotIdx > 0 && slotIdx < sideTableLength,
                        "Deserialize: slotIdx {0} out of range [1, {1})",
                        slotIdx,
                        sideTableLength
                    );

                    NativeChunkStoreEntryPayload payload = default;
                    reader.BlitReadRawBytes(
                        "Entry",
                        UnsafeUtility.AddressOf(ref payload),
                        payloadSize
                    );

                    var entry = FromPayload(payload);
                    // Patch the Address to the freshly-allocated page memory. The saved
                    // Address is stale (points at memory in the writing process).
                    var page = _pages[payload.PageId];
                    TrecsDebugAssert.That(
                        page != null,
                        "Deserialize: live entry references reclaimed page {0}",
                        payload.PageId
                    );
                    unsafe
                    {
                        entry.Address = new IntPtr(
                            (byte*)page.Address.ToPointer() + payload.SlotIndex * page.SlotSize
                        );
                    }
#if ENABLE_UNITY_COLLECTIONS_CHECKS
                    entry.Safety = AtomicSafetyHandle.Create();
                    AtomicSafetyHandle.SetBumpSecondaryVersionOnScheduleWrite(entry.Safety, true);
#endif
                    SetEntry(slotIdx, entry);
                }
            }

            // Free-slot / free-page-id stacks: read in saved order and Push directly,
            // which restores the same bottom-to-top arrangement we wrote. The
            // side-table variant also patches each slot's Generation byte from the saved
            // value (see comment in Serialize).
            using (TrecsProfiling.Start("Reading free-slot stacks"))
            {
                ReadSideTableFreeSlots(reader);
                ReadStack(reader, "freePageIds", _freePageIds);
            }

            using (TrecsProfiling.Start("Reading bucket free-slots"))
            {
                // Per-bucket FreeSlots.
                for (int b = 0; b < _buckets.Length; b++)
                {
                    var bucket = _buckets[b];
                    var count = reader.Read<int>("BucketFreeSlotsCount");
                    for (int j = 0; j < count; j++)
                    {
                        var pageId = reader.Read<int>("PageId");
                        var slotIdx = reader.Read<int>("SlotIdx");
                        bucket.FreeSlots.Push((pageId, slotIdx));
                    }
                }
            }

            _nextFreshSideTableSlot = nextFreshSideTableSlot;
            _liveCount = liveCount;

            _log.Debug(
                "Chunk store deserialized: pages={0} liveEntries={1} sideTableLength={2}",
                _pages.Count,
                _liveCount,
                _sideTableLength
            );
        }

        // Wipe everything Deserialize is about to repopulate. Takes the new
        // world's _nextFreshSideTableSlot so the side-table memclear can be
        // scoped to just the slots that need it: the live-vs-free state for
        // slots [1, newNextFreshSlot) is restored by SetEntry (live) and by
        // ReadSideTableFreeSlots (which writes a full default-state entry,
        // not just a Generation patch — so a slot that was live in the old
        // world but free in the new lands in the right state); slots
        // [newNextFreshSlot, oldNextFreshSlot) were "used" in the old world
        // (either live or in the free list) and won't be touched by the new
        // world's deserialize, so they have to be cleared here back to
        // fresh; slots beyond oldNextFreshSlot were never used and are
        // already fresh (AllocateChunk zero-inits new chunks; previous
        // resets left old chunks in the same state). For the rollback shape
        // (newNextFreshSlot == oldNextFreshSlot), the wipe range is empty.
        unsafe void ResetForDeserialize(int newNextFreshSlot)
        {
#if ENABLE_UNITY_COLLECTIONS_CHECKS
            using (TrecsProfiling.Start("SafetyHandleRelease"))
            {
                // Release safety handles for any still-live slots before we wipe them —
                // dropping the side-table chunk's bytes leaves Unity's safety-handle slots
                // dangling otherwise. (No leak warning here: ResetForDeserialize is the
                // Deserialize-time wipe, not a teardown — leaks should already have been
                // surfaced when the source world was disposed.)
                for (int i = 1; i < _sideTableLength; i++)
                {
                    var entry = GetEntry(i);
                    if (entry.InUse == 1)
                    {
                        AtomicSafetyHandle.EnforceAllBufferJobsHaveCompletedAndRelease(
                            entry.Safety
                        );
                    }
                }
                _callerInfoByHandle.Clear();
            }
#endif

            using (TrecsProfiling.Start("FreePages"))
            {
                // Free pages we still own. With stateless heaps, this also reclaims pages
                // for any handles that weren't explicitly disposed before the snapshot load.
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
            }

            using (TrecsProfiling.Start("ZeroChunks"))
            {
                // Wipe only the slot range [newNextFreshSlot, oldNextFreshSlot) —
                // slots the old world had touched (live or in the free list)
                // but the new world won't, so they need to look "fresh"
                // ({InUse=0, Generation=0, ...}) post-restore. Slots <
                // newNextFreshSlot are written explicitly by SetEntry or by
                // ReadSideTableFreeSlots; slots >= oldNextFreshSlot are
                // already fresh and need no work. Empty range when both
                // worlds have the same _nextFreshSideTableSlot (the common
                // rollback case), which avoids the bulk per-chunk memclear
                // entirely. We don't try to be cleverer than that — partial-
                // chunk memclears are fine; the case worth optimizing is the
                // rollback one, and it's already zero work above.
                var oldNextFreshSlot = _nextFreshSideTableSlot;
                if (newNextFreshSlot < oldNextFreshSlot)
                {
                    var entrySize = UnsafeUtility.SizeOf<NativeChunkStoreEntry>();
                    var chunkSize = NativeChunkStoreResolver.ChunkSize;
                    var chunkSizeBits = NativeChunkStoreResolver.ChunkSizeBits;
                    int slot = newNextFreshSlot;
                    while (slot < oldNextFreshSlot)
                    {
                        var chunkIdx = slot >> chunkSizeBits;
                        var slotInChunk = slot - (chunkIdx << chunkSizeBits);
                        var slotsLeftInChunk = chunkSize - slotInChunk;
                        var slotsToClear = Math.Min(slotsLeftInChunk, oldNextFreshSlot - slot);
                        var ptr = _chunkDirectory[chunkIdx];
                        if (ptr != IntPtr.Zero)
                        {
                            UnsafeUtility.MemClear(
                                (byte*)ptr.ToPointer() + (long)slotInChunk * entrySize,
                                (long)entrySize * slotsToClear
                            );
                        }
                        slot += slotsToClear;
                    }
                }
            }

            // Reset bucket bookkeeping.
            for (int b = 0; b < _buckets.Length; b++)
            {
                _buckets[b].PageIds.Clear();
                _buckets[b].FreeSlots.Clear();
            }

            _nextFreshSideTableSlot = 1;
            _liveCount = 0;
            // Slot 0 stays reserved as the null sentinel; _sideTableLength was already
            // ≥ 1 from construction. Existing chunks are zero so slot 0's entry is
            // {InUse=0, Generation=0} which is the same state the constructor leaves.
        }

        enum PageKind : byte
        {
            Null = 0,
            Bucket = 1,
            SingleSlot = 2, // huge or external; indistinguishable in the wire format
        }

        static NativeChunkStoreEntryPayload ToPayload(in NativeChunkStoreEntry entry)
        {
            return new NativeChunkStoreEntryPayload
            {
                Address = entry.Address,
                TypeHash = entry.TypeHash,
                Generation = entry.Generation,
                InUse = entry.InUse,
                OwnsWholePage = entry.OwnsWholePage,
                _padding = 0, // explicit for deterministic byte output
                PageId = entry.PageId,
                SlotIndex = entry.SlotIndex,
            };
        }

        static NativeChunkStoreEntry FromPayload(in NativeChunkStoreEntryPayload payload)
        {
            return new NativeChunkStoreEntry
            {
                Address = payload.Address,
                TypeHash = payload.TypeHash,
                Generation = payload.Generation,
                InUse = payload.InUse,
                OwnsWholePage = payload.OwnsWholePage,
                _padding = 0,
                PageId = payload.PageId,
                SlotIndex = payload.SlotIndex,
            };
        }

        static void WriteStack(ISerializationWriter writer, string name, Stack<int> stack)
        {
            writer.Write<int>(name + "Count", stack.Count);
            // Stack<T>.ToArray() returns top-to-bottom; write bottom-to-top so the
            // reader can Push in stream order and end up with the same top element.
            var arr = stack.ToArray();
            for (int i = arr.Length - 1; i >= 0; i--)
            {
                writer.Write<int>("Item", arr[i]);
            }
        }

        static void ReadStack(ISerializationReader reader, string name, Stack<int> stack)
        {
            var count = reader.Read<int>(name + "Count");
            for (int i = 0; i < count; i++)
            {
                stack.Push(reader.Read<int>("Item"));
            }
        }

        unsafe void WriteSideTableAndFreeSlots(ISerializationWriter writer)
        {
            writer.Write<int>("NumLiveEntries", _liveCount);
            int liveEmitted = 0;
            for (int i = 1; i < _sideTableLength; i++)
            {
                var entry = GetEntry(i);
                if (entry.InUse != 1)
                {
                    continue;
                }

                writer.Write<int>("SlotIdx", i);
                var payload = ToPayload(entry);

                // Address is a raw process-local pointer that the
                // deserializer recomputes from PageId + SlotIndex (the
                // saved value is stale). Zero it so saved recordings
                // don't contain garbage pointers.
                payload.Address = IntPtr.Zero;

                writer.BlitWriteRawBytes(
                    "Entry",
                    UnsafeUtility.AddressOf(ref payload),
                    UnsafeUtility.SizeOf<NativeChunkStoreEntryPayload>()
                );
                liveEmitted++;
            }
            TrecsDebugAssert.That(
                liveEmitted == _liveCount,
                "Serialize: live-count mismatch (emitted {0} vs _liveCount {1})",
                liveEmitted,
                _liveCount
            );

            WriteSideTableFreeSlots(writer);
            WriteStack(writer, "freePageIds", _freePageIds);

            for (int b = 0; b < _buckets.Length; b++)
            {
                var bucket = _buckets[b];
                writer.Write<int>("BucketFreeSlotsCount", bucket.FreeSlots.Count);
                var slots = bucket.FreeSlots.ToArray();
                for (int j = slots.Length - 1; j >= 0; j--)
                {
                    writer.Write<int>("PageId", slots[j].PageId);
                    writer.Write<int>("SlotIdx", slots[j].SlotIdx);
                }
            }
        }

        void WriteSideTableFreeSlots(ISerializationWriter writer)
        {
            writer.Write<int>("FreeSideTableSlotsCount", _freeSideTableSlots.Count);
            var arr = _freeSideTableSlots.ToArray();
            for (int i = arr.Length - 1; i >= 0; i--)
            {
                var slotIdx = arr[i];
                writer.Write<int>("SlotIdx", slotIdx);
                writer.Write<byte>("Generation", GetEntry(slotIdx).Generation);
            }
        }

        void ReadSideTableFreeSlots(ISerializationReader reader)
        {
            var count = reader.Read<int>("FreeSideTableSlotsCount");
            for (int i = 0; i < count; i++)
            {
                var slotIdx = reader.Read<int>("SlotIdx");
                var generation = reader.Read<byte>("Generation");
                _freeSideTableSlots.Push(slotIdx);
                // Overwrite the full slot, not just Generation. The slot may
                // have been *live* in the old world we just replaced (with
                // InUse=1 and a stale Address from a now-freed page); we
                // need to bring it back to a clean free state. Other fields
                // default to zero — Alloc overwrites them on slot reuse.
                // Pairs with the partial-wipe scoping in
                // ResetForDeserialize: slots below newNextFreshSlot are no
                // longer cleared by the reset, so the deserialize-side write
                // here is the only path that resets their state.
                SetEntry(slotIdx, new NativeChunkStoreEntry { Generation = generation });
            }
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
            throw TrecsDebugAssert.CreateException(
                "Allocation size {0} exceeds max bucket size {1}",
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
            TrecsDebugAssert.That(page != null, "Free slot pointed at reclaimed page {0}", pId);
            TrecsDebugAssert.That(page.FreeCount > 0);
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
            TrecsDebugAssert.That(
                pagePtr != null,
                "AllocatorManager returned null page (size {0}, align {1})",
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
            TrecsDebugAssert.That(
                pagePtr != null,
                "AllocatorManager returned null huge page (size {0})",
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
        /// Registers an externally-allocated pointer as a single-slot page owned by this
        /// chunk store. The pointer must have been allocated via <c>AllocatorManager.Allocate</c>
        /// with the supplied <paramref name="size"/> and <paramref name="alignment"/> — those
        /// values are stored verbatim and used to call <c>AllocatorManager.Free</c> when the
        /// returned handle is freed. After this call the chunk store takes exclusive ownership.
        /// </summary>
        public PtrHandle AllocExternal(
            IntPtr address,
            int size,
            int alignment,
            int typeHash,
            [CallerFilePath] string callerFile = null,
            [CallerLineNumber] int callerLine = 0
        )
        {
            using var _ = TrecsProfiling.Start("NativeChunkStore.AllocExternal");
            TrecsDebugAssert.That(!_isDisposed);
            TrecsDebugAssert.That(address != IntPtr.Zero, "AllocExternal: null address");
            TrecsDebugAssert.That(size > 0, "AllocExternal: size must be positive");
            TrecsDebugAssert.That(
                alignment > 0 && (alignment & (alignment - 1)) == 0,
                "AllocExternal: alignment {0} must be a positive power of two",
                alignment
            );
            TrecsDebugAssert.That(
                UnityThreadHelper.IsMainThread,
                "NativeChunkStore.AllocExternal must be called from the main thread"
            );

#if ENABLE_UNITY_COLLECTIONS_CHECKS
            TrecsDebugAssert.That(
                _externalAddresses.Add(address),
                "AllocExternal: pointer 0x{0} is already registered in this chunk store "
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
                // External pages are single-slot pages owned by this allocation; Free
                // releases the whole page via the same code path huge allocs use.
                OwnsWholePage = 1,
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

#if ENABLE_UNITY_COLLECTIONS_CHECKS
            if (callerFile != null)
            {
                _callerInfoByHandle[handleValue] = (callerFile, callerLine);
            }
#endif

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

        void ReturnSlot(int pageId, int slotIdx, bool ownsWholePage)
        {
            var page = _pages[pageId];
            TrecsDebugAssert.That(page != null, "ReturnSlot: page {0} already reclaimed", pageId);

            if (ownsWholePage)
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
            TrecsDebugAssert.That(
                page.FreeCount <= page.SlotCount,
                "Page {0} free-count overflow ({1} > {2})",
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
            if (_freeSideTableSlots.Count > 0)
            {
                var candidate = _freeSideTableSlots.Pop();
                // Materialise the side-table slot so Alloc can read its current generation
                // (the in-table Generation byte is the source of truth for slot reuse).
                EnsureSideTableLength(candidate + 1);
                return candidate;
            }

            var idx = _nextFreshSideTableSlot++;
            TrecsDebugAssert.That(
                (uint)idx <= NativeChunkStoreResolver.MaxIndex,
                "NativeChunkStore exhausted side-table index space ({0} max)",
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
            TrecsDebugAssert.That(
                chunkIdx < NativeChunkStoreResolver.MaxChunkCount,
                "AllocateChunk: chunkIdx {0} exceeds max chunk count {1}",
                chunkIdx,
                NativeChunkStoreResolver.MaxChunkCount
            );
            TrecsDebugAssert.That(
                _chunkDirectory[chunkIdx] == IntPtr.Zero,
                "AllocateChunk: chunk {0} already allocated",
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
            TrecsDebugAssert.That(
                ptr != null,
                "AllocateChunk: AllocatorManager returned null for chunk {0}",
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
            TrecsDebugAssert.That(
                idx >= 0 && idx < _sideTableLength,
                "GetEntry: idx {0} out of range (length {1})",
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
            TrecsDebugAssert.That(
                idx >= 0 && idx < _sideTableLength,
                "SetEntry: idx {0} out of range (length {1})",
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

    /// <summary>
    /// Snapshot of <see cref="NativeChunkStore"/> state at one point in time. Returned by
    /// <c>NativeChunkStore.GetStatistics</c>. Intended for diagnostics / benchmarks only;
    /// not part of the public Trecs API surface.
    /// </summary>
    internal struct HeapStatistics
    {
        public int LiveAllocations;
        public int NumPages;
        public int NumHugePages;
        public int SideTableLength;
        public int FreeSideTableSlots;
        public int NextFreshSideTableSlot;
        public long HugePagesReservedBytes;
        public long TotalReservedBytes;
        public BucketStatistics[] Buckets;
    }

    internal struct BucketStatistics
    {
        public int SlotSize;
        public int SlotsPerPage;
        public int PageCount;
        public int LiveSlotCount;
        public int FreeSlotCount;
        public long ReservedBytes;
    }

    /// <summary>
    /// One row of <see cref="NativeChunkStore.SnapshotPageOccupancy"/> output.
    /// <c>LiveSlotsStartIndex</c> indexes into the parallel <c>List&lt;bool&gt;</c>
    /// the caller passed in; <c>SlotCount</c> bools follow, one per slot, true if
    /// the slot is currently in use.
    /// </summary>
    internal struct PageOccupancyHeader
    {
        public int PageId;
        public int BucketIdx; // -1 for huge / external single-slot pages
        public int SlotSize;
        public int SlotCount;
        public int FreeCount;
        public int LiveSlotsStartIndex;
    }
}
