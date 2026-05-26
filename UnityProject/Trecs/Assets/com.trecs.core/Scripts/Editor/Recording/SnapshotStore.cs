using System;
using System.Collections.Generic;
using Trecs.Collections;

namespace Trecs.Internal
{
    /// <summary>
    /// In-memory store for the four collections the editor recorder
    /// maintains during a session: persisted anchors, user-placed bookmarks,
    /// the transient scrub cache, and per-frame desync-detection checksums.
    /// Centralises payload-pool return discipline, byte accounting, and
    /// capacity enforcement so callers don't have to remember the
    /// "every remove must <see cref="SnapshotSerializer.ReturnPayloadBuffer"/>
    /// the displaced payload" rule at each site.
    ///
    /// Owns the lifetime of every <see cref="WorldSnapshot.Payload"/> it
    /// holds — payloads handed in via Append/Insert/Set are released back
    /// to the pool on remove / trim / cap-eviction / Clear / Dispose. Read-
    /// only callers can iterate the exposed lists freely; they must NOT
    /// mutate the payload memory or hold a slice past the corresponding
    /// remove call.
    ///
    /// Not thread-safe; mirrors <see cref="TrecsRewindBuffer"/>'s
    /// main-thread-only contract.
    /// </summary>
    internal sealed class SnapshotStore : IDisposable
    {
        readonly SnapshotPayloadPool _pool;

        // Sparse, capacity-capped (drop-oldest). Sorted by FixedFrame
        // ascending. Doubles as the desync-recovery + scrub backbone for
        // BundleReplayer-style playback.
        readonly List<WorldSnapshot> _anchors = new();

        // User-placed labelled markers. Sorted by FixedFrame ascending.
        // Never auto-evicted.
        readonly List<WorldSnapshot> _bookmarks = new();

        // Transient dense cache used to make recent-frame scrub-back
        // instant. Cleared on Start/Reset/Fork/Trim/Load. Sorted by
        // FixedFrame ascending. Capped by scrub-cache bytes (drop-oldest).
        readonly List<WorldSnapshot> _scrubCache = new();

        // Per-frame world-state hashes. Tiny entries (16 bytes each) but
        // computing one is a full state walk, so cadence is set sparsely
        // by the caller. Persisted into saved bundles for offline desync
        // detection in BundleReplayer. Stays empty under TRECS_IS_PROFILING
        // since callers gate the SetChecksum calls — the empty-dict
        // overhead is single-digit bytes per recorder.
        readonly IterableDictionary<int, ulong> _checksums = new();

        long _totalAnchorBytes;
        long _scrubCacheBytes;
        bool _disposed;

        public SnapshotStore(SnapshotPayloadPool pool)
        {
            _pool = pool ?? throw new ArgumentNullException(nameof(pool));
        }

        public IReadOnlyList<WorldSnapshot> Anchors => _anchors;
        public IReadOnlyList<WorldSnapshot> Bookmarks => _bookmarks;
        public IReadOnlyList<WorldSnapshot> ScrubCache => _scrubCache;

        public IterableDictionary<int, ulong> Checksums => _checksums;

        public long TotalAnchorBytes => _totalAnchorBytes;
        public long ScrubCacheBytes => _scrubCacheBytes;

        public bool IsEmpty =>
            _anchors.Count == 0 && _bookmarks.Count == 0 && _scrubCache.Count == 0;

        /// <summary>
        /// Earliest captured frame across all three lists, or null if the
        /// store is empty. Walks one tail per list so it's O(1) (lists are
        /// kept sorted by frame ascending).
        /// </summary>
        public int? EarliestCapturedFrame
        {
            get
            {
                int earliest = int.MaxValue;
                if (_anchors.Count > 0)
                    earliest = _anchors[0].FixedFrame;
                if (_bookmarks.Count > 0 && _bookmarks[0].FixedFrame < earliest)
                    earliest = _bookmarks[0].FixedFrame;
                if (_scrubCache.Count > 0 && _scrubCache[0].FixedFrame < earliest)
                    earliest = _scrubCache[0].FixedFrame;
                return earliest == int.MaxValue ? (int?)null : earliest;
            }
        }

        /// <summary>
        /// Latest captured frame across all three lists, or null if the
        /// store is empty.
        /// </summary>
        public int? LatestCapturedFrame
        {
            get
            {
                int latest = int.MinValue;
                if (_anchors.Count > 0)
                    latest = _anchors[_anchors.Count - 1].FixedFrame;
                if (_bookmarks.Count > 0 && _bookmarks[_bookmarks.Count - 1].FixedFrame > latest)
                    latest = _bookmarks[_bookmarks.Count - 1].FixedFrame;
                if (_scrubCache.Count > 0 && _scrubCache[_scrubCache.Count - 1].FixedFrame > latest)
                    latest = _scrubCache[_scrubCache.Count - 1].FixedFrame;
                return latest == int.MinValue ? (int?)null : latest;
            }
        }

        // ---- Add ------------------------------------------------------------

        /// <summary>
        /// Append an anchor whose frame is strictly greater than every
        /// existing anchor's frame. Asserts in debug builds that the list
        /// stays sorted. Use <see cref="InsertOrReplaceAnchorAt"/> for
        /// manual at-current-frame captures that can land anywhere.
        /// </summary>
        public void AppendAnchor(WorldSnapshot snapshot)
        {
            TrecsDebugAssert.That(
                _anchors.Count == 0
                    || _anchors[_anchors.Count - 1].FixedFrame < snapshot.FixedFrame,
                "AppendAnchor expects strictly increasing frames"
            );
            _anchors.Add(snapshot);
            _totalAnchorBytes += snapshot.Payload.Length;
        }

        /// <summary>
        /// Append a scrub-cache entry whose frame is strictly greater than
        /// every existing entry's frame.
        /// </summary>
        public void AppendScrubCacheEntry(WorldSnapshot snapshot)
        {
            TrecsDebugAssert.That(
                _scrubCache.Count == 0
                    || _scrubCache[_scrubCache.Count - 1].FixedFrame < snapshot.FixedFrame,
                "AppendScrubCacheEntry expects strictly increasing frames"
            );
            _scrubCache.Add(snapshot);
            _scrubCacheBytes += snapshot.Payload.Length;
        }

        /// <summary>
        /// Insert <paramref name="snapshot"/> into the anchor list at the
        /// position dictated by its frame, or replace an existing anchor at
        /// that frame. Used for manual at-current-frame anchor captures
        /// which can land at any frame (paused after a scrub, etc.).
        /// </summary>
        public void InsertOrReplaceAnchorAt(WorldSnapshot snapshot)
        {
            for (int i = 0; i < _anchors.Count; i++)
            {
                if (_anchors[i].FixedFrame == snapshot.FixedFrame)
                {
                    _totalAnchorBytes -= _anchors[i].Payload.Length;
                    _pool.Return(_anchors[i].Payload);
                    _anchors[i] = snapshot;
                    _totalAnchorBytes += snapshot.Payload.Length;
                    return;
                }
            }
            int insertAt = 0;
            while (insertAt < _anchors.Count && _anchors[insertAt].FixedFrame < snapshot.FixedFrame)
            {
                insertAt++;
            }
            _anchors.Insert(insertAt, snapshot);
            _totalAnchorBytes += snapshot.Payload.Length;
        }

        /// <summary>
        /// Insert or replace a bookmark by frame. Replacing pool-returns the
        /// displaced payload.
        /// </summary>
        public void InsertOrReplaceBookmarkAt(WorldSnapshot snapshot)
        {
            for (int i = 0; i < _bookmarks.Count; i++)
            {
                if (_bookmarks[i].FixedFrame == snapshot.FixedFrame)
                {
                    _pool.Return(_bookmarks[i].Payload);
                    _bookmarks[i] = snapshot;
                    return;
                }
            }
            int insertAt = 0;
            while (
                insertAt < _bookmarks.Count && _bookmarks[insertAt].FixedFrame < snapshot.FixedFrame
            )
            {
                insertAt++;
            }
            _bookmarks.Insert(insertAt, snapshot);
        }

        /// <summary>
        /// Record a per-frame world-state checksum. Overwrites any existing
        /// entry at the same frame — scrubbed-back-then-resumed recordings
        /// re-walk previously recorded frames during the playback portion,
        /// and a subsequent fork can land at any frame; we want the most
        /// recent live-capture value.
        /// </summary>
        public void SetChecksum(int frame, ulong checksum)
        {
            _checksums[frame] = checksum;
        }

        // ---- Remove --------------------------------------------------------

        /// <summary>
        /// Remove the bookmark at <paramref name="frame"/>, returning its
        /// payload to the pool. Returns true iff a bookmark was found.
        /// </summary>
        public bool RemoveBookmarkAt(int frame)
        {
            for (int i = 0; i < _bookmarks.Count; i++)
            {
                if (_bookmarks[i].FixedFrame == frame)
                {
                    _pool.Return(_bookmarks[i].Payload);
                    _bookmarks.RemoveAt(i);
                    return true;
                }
            }
            return false;
        }

        /// <summary>
        /// Drop anchors whose frame &gt; <paramref name="frame"/>. Returns
        /// the number of anchors dropped.
        /// </summary>
        public int TrimAnchorsAfter(int frame)
        {
            int dropped = 0;
            while (_anchors.Count > 0 && _anchors[_anchors.Count - 1].FixedFrame > frame)
            {
                var last = _anchors[_anchors.Count - 1];
                _totalAnchorBytes -= last.Payload.Length;
                _pool.Return(last.Payload);
                _anchors.RemoveAt(_anchors.Count - 1);
                dropped++;
            }
            return dropped;
        }

        /// <summary>
        /// Drop anchors whose frame is strictly less than the anchor closest
        /// to <paramref name="frame"/> from below. The closest at-or-before
        /// anchor is preserved (so a JumpToFrame at the trim point still
        /// works). Returns the number of anchors dropped (0 if nothing
        /// could be trimmed).
        /// </summary>
        public int TrimAnchorsBefore(int frame)
        {
            int keepFrom = FindAnchorIndexAtOrBefore(frame);
            if (keepFrom <= 0)
            {
                return 0;
            }
            for (int i = 0; i < keepFrom; i++)
            {
                _totalAnchorBytes -= _anchors[i].Payload.Length;
                _pool.Return(_anchors[i].Payload);
            }
            _anchors.RemoveRange(0, keepFrom);
            return keepFrom;
        }

        public void TrimBookmarksAfter(int frame)
        {
            while (_bookmarks.Count > 0 && _bookmarks[_bookmarks.Count - 1].FixedFrame > frame)
            {
                _pool.Return(_bookmarks[_bookmarks.Count - 1].Payload);
                _bookmarks.RemoveAt(_bookmarks.Count - 1);
            }
        }

        public void TrimBookmarksBefore(int frame)
        {
            int removeCount = 0;
            while (removeCount < _bookmarks.Count && _bookmarks[removeCount].FixedFrame < frame)
            {
                _pool.Return(_bookmarks[removeCount].Payload);
                removeCount++;
            }
            if (removeCount > 0)
            {
                _bookmarks.RemoveRange(0, removeCount);
            }
        }

        public void TrimScrubCacheAfter(int frame)
        {
            while (_scrubCache.Count > 0 && _scrubCache[_scrubCache.Count - 1].FixedFrame > frame)
            {
                var last = _scrubCache[_scrubCache.Count - 1];
                _scrubCacheBytes -= last.Payload.Length;
                _pool.Return(last.Payload);
                _scrubCache.RemoveAt(_scrubCache.Count - 1);
            }
        }

        public void TrimScrubCacheBefore(int frame)
        {
            int removeCount = 0;
            while (removeCount < _scrubCache.Count && _scrubCache[removeCount].FixedFrame < frame)
            {
                _scrubCacheBytes -= _scrubCache[removeCount].Payload.Length;
                _pool.Return(_scrubCache[removeCount].Payload);
                removeCount++;
            }
            if (removeCount > 0)
            {
                _scrubCache.RemoveRange(0, removeCount);
            }
        }

        // IterableDictionary doesn't support remove-during-enumeration, so the
        // keys to drop are collected first.
        public void TrimChecksumsAfter(int frame)
        {
            if (_checksums.IsEmpty)
            {
                return;
            }
            List<int> toRemove = null;
            foreach (var (k, _) in _checksums)
            {
                if (k > frame)
                {
                    toRemove ??= new List<int>();
                    toRemove.Add(k);
                }
            }
            if (toRemove == null)
            {
                return;
            }
            foreach (var k in toRemove)
            {
                _checksums.TryRemove(k);
            }
        }

        public void TrimChecksumsBefore(int frame)
        {
            if (_checksums.IsEmpty)
            {
                return;
            }
            List<int> toRemove = null;
            foreach (var (k, _) in _checksums)
            {
                if (k < frame)
                {
                    toRemove ??= new List<int>();
                    toRemove.Add(k);
                }
            }
            if (toRemove == null)
            {
                return;
            }
            foreach (var k in toRemove)
            {
                _checksums.TryRemove(k);
            }
        }

        public void ClearChecksums() => _checksums.Clear();

        public IterableDictionary<int, ulong> CopyChecksumsForBundle() =>
            WorldSnapshotListUtil.CopyChecksums(_checksums);

        /// <summary>
        /// Drop everything — three lists, checksum dict, byte counters.
        /// Returns every payload to the pool.
        /// </summary>
        public void Clear()
        {
            ReturnPayloadsAndClear(_anchors);
            ReturnPayloadsAndClear(_bookmarks);
            ReturnPayloadsAndClear(_scrubCache);
            ClearChecksums();
            _totalAnchorBytes = 0;
            _scrubCacheBytes = 0;
        }

        // ---- Capacity ------------------------------------------------------

        /// <summary>
        /// Drop oldest anchors until the count is &lt;= <paramref name="max"/>.
        /// <paramref name="max"/> == 0 means "unbounded" (no-op). Returns
        /// the number of anchors evicted; the caller may want to refresh
        /// any cached earliest-frame state if non-zero.
        /// </summary>
        public int EnforceAnchorCountCap(int max)
        {
            if (max <= 0)
            {
                return 0;
            }
            int evicted = 0;
            while (_anchors.Count > max)
            {
                var oldest = _anchors[0];
                _totalAnchorBytes -= oldest.Payload.Length;
                _pool.Return(oldest.Payload);
                _anchors.RemoveAt(0);
                evicted++;
            }
            return evicted;
        }

        /// <summary>
        /// Drop oldest scrub-cache entries until <see cref="ScrubCacheBytes"/>
        /// is &lt;= <paramref name="max"/>. <paramref name="max"/> == 0 means
        /// "unbounded" (no-op).
        /// </summary>
        public void EnforceScrubCacheBytesCap(long max)
        {
            if (max <= 0)
            {
                return;
            }
            while (_scrubCache.Count > 0 && _scrubCacheBytes > max)
            {
                var oldest = _scrubCache[0];
                _scrubCacheBytes -= oldest.Payload.Length;
                _pool.Return(oldest.Payload);
                _scrubCache.RemoveAt(0);
            }
        }

        // ---- Lookups -------------------------------------------------------

        /// <summary>
        /// Find the nearest scrubbable snapshot whose frame is
        /// &lt;= <paramref name="target"/>, searching anchors, the scrub
        /// cache, and bookmarks. All three lists are kept sorted by frame
        /// ascending. On a tie at the same frame, prefers anchors &gt;
        /// scrub cache &gt; bookmarks (anchors are guaranteed-live-timeline
        /// bytes; scrub-cache entries reflect the same timeline up to
        /// drop-oldest eviction; bookmarks may capture an earlier divergent
        /// moment).
        /// </summary>
        public (int frame, ReadOnlyMemory<byte> payload)? FindNearestAtOrBefore(int target)
        {
            int bestFrame = int.MinValue;
            ReadOnlyMemory<byte> bestPayload = default;
            bool found = false;
            for (int i = _anchors.Count - 1; i >= 0; i--)
            {
                if (_anchors[i].FixedFrame <= target)
                {
                    bestFrame = _anchors[i].FixedFrame;
                    bestPayload = _anchors[i].Payload;
                    found = true;
                    break;
                }
            }
            for (int i = _scrubCache.Count - 1; i >= 0; i--)
            {
                if (_scrubCache[i].FixedFrame <= target)
                {
                    if (_scrubCache[i].FixedFrame > bestFrame)
                    {
                        bestFrame = _scrubCache[i].FixedFrame;
                        bestPayload = _scrubCache[i].Payload;
                        found = true;
                    }
                    break;
                }
            }
            for (int i = _bookmarks.Count - 1; i >= 0; i--)
            {
                var b = _bookmarks[i];
                if (b.FixedFrame <= target)
                {
                    if (b.FixedFrame > bestFrame)
                    {
                        bestFrame = b.FixedFrame;
                        bestPayload = b.Payload;
                        found = true;
                    }
                    break;
                }
            }
            return found ? (bestFrame, bestPayload) : null;
        }

        /// <summary>
        /// Earliest scrubbable snapshot across all three lists. Requires
        /// the store to be non-empty; callers should gate on
        /// <see cref="IsEmpty"/>.
        /// </summary>
        public (int frame, ReadOnlyMemory<byte> payload) FindEarliest()
        {
            TrecsAssert.That(!IsEmpty, "FindEarliest called on empty store");
            int frame = int.MaxValue;
            ReadOnlyMemory<byte> payload = default;
            if (_anchors.Count > 0)
            {
                frame = _anchors[0].FixedFrame;
                payload = _anchors[0].Payload;
            }
            if (_scrubCache.Count > 0 && _scrubCache[0].FixedFrame < frame)
            {
                frame = _scrubCache[0].FixedFrame;
                payload = _scrubCache[0].Payload;
            }
            if (_bookmarks.Count > 0 && _bookmarks[0].FixedFrame < frame)
            {
                frame = _bookmarks[0].FixedFrame;
                payload = _bookmarks[0].Payload;
            }
            return (frame, payload);
        }

        /// <summary>
        /// Index in <see cref="Anchors"/> of the latest anchor whose frame
        /// is &lt;= <paramref name="frame"/>. Returns -1 if no anchor fits.
        /// </summary>
        public int FindAnchorIndexAtOrBefore(int frame)
        {
            for (int i = _anchors.Count - 1; i >= 0; i--)
            {
                if (_anchors[i].FixedFrame <= frame)
                {
                    return i;
                }
            }
            return -1;
        }

        public void Dispose()
        {
            if (_disposed)
            {
                return;
            }
            _disposed = true;
            Clear();
        }

        void ReturnPayloadsAndClear(List<WorldSnapshot> list)
        {
            for (int i = 0; i < list.Count; i++)
            {
                _pool.Return(list[i].Payload);
            }
            list.Clear();
        }
    }
}
