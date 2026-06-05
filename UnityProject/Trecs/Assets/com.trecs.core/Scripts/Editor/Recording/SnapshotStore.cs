using System;
using System.Collections.Generic;
using Trecs.Collections;

namespace Trecs.Internal
{
    /// <summary>
    /// In-memory store for the four collections the editor recorder
    /// maintains during a session: persisted keyframes, user-placed bookmarks,
    /// the transient scrub cache, and per-frame desync-detection checksums.
    /// Centralises snapshot release discipline, byte accounting, and
    /// capacity enforcement so callers don't have to remember the
    /// "every remove must release the displaced snapshot's resources"
    /// rule at each site.
    ///
    /// Owns the lifetime of every snapshot it holds — snapshots handed in
    /// via Append/Insert/Set are released on remove / trim / cap-eviction /
    /// Clear / Dispose: retained data despawns to the
    /// <see cref="SerializationDataPool"/>, blob pins are released, and
    /// loaded-bundle contiguous payloads just become garbage. Read-only
    /// callers can iterate the exposed lists freely; they must NOT
    /// mutate the payload memory or hold a slice past the corresponding
    /// remove call.
    ///
    /// Not thread-safe; mirrors <see cref="TrecsRewindBuffer"/>'s
    /// main-thread-only contract.
    /// </summary>
    internal sealed class SnapshotStore : IDisposable
    {
        // Pool for RetainedData-backed snapshots (the live capture path); see ReturnSnapshot.
        readonly SerializationDataPool _serDataPool;

        // Releases the cache pins a snapshot holds on its opaque blobs, invoked from the single
        // ReturnSnapshot choke point so every remove / trim / cap / clear / replace site unpins in
        // lockstep with returning the payload buffer. Supplied by TrecsRewindBuffer, which guards
        // against a disposed world (the cache is gone by then). No-op for pin-less (loaded) snapshots.
        readonly Action<List<BlobPin>> _releasePins;

        // Sparse, capacity-capped (drop-oldest). Sorted by FixedFrame
        // ascending. Doubles as the desync-recovery + scrub backbone for
        // BundleReplayer-style playback.
        readonly List<WorldSnapshot> _keyframes = new();

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

        long _totalKeyframeBytes;
        long _scrubCacheBytes;
        bool _disposed;

        public SnapshotStore(SerializationDataPool serDataPool, Action<List<BlobPin>> releasePins)
        {
            _serDataPool = serDataPool ?? throw new ArgumentNullException(nameof(serDataPool));
            _releasePins = releasePins ?? throw new ArgumentNullException(nameof(releasePins));
        }

        // Release a dropped snapshot's resources: despawn retained data to its pool and release
        // any opaque-blob cache pins it held. Both must happen at every drop site — hence the
        // single choke point. Loaded-bundle contiguous payloads have nothing to release — the
        // byte[] just becomes garbage (cold, user-initiated path; no pooling).
        void ReturnSnapshot(WorldSnapshot snapshot)
        {
            _releasePins(snapshot.PinnedBlobs);
            if (snapshot.RetainedData != null)
            {
                _serDataPool.Despawn(snapshot.RetainedData);
            }
        }

        public IReadOnlyList<WorldSnapshot> Keyframes => _keyframes;
        public IReadOnlyList<WorldSnapshot> Bookmarks => _bookmarks;
        public IReadOnlyList<WorldSnapshot> ScrubCache => _scrubCache;

        public IterableDictionary<int, ulong> Checksums => _checksums;

        public long TotalKeyframeBytes => _totalKeyframeBytes;
        public long ScrubCacheBytes => _scrubCacheBytes;

        public bool IsEmpty =>
            _keyframes.Count == 0 && _bookmarks.Count == 0 && _scrubCache.Count == 0;

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
                if (_keyframes.Count > 0)
                    earliest = _keyframes[0].FixedFrame;
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
                if (_keyframes.Count > 0)
                    latest = _keyframes[_keyframes.Count - 1].FixedFrame;
                if (_bookmarks.Count > 0 && _bookmarks[_bookmarks.Count - 1].FixedFrame > latest)
                    latest = _bookmarks[_bookmarks.Count - 1].FixedFrame;
                if (_scrubCache.Count > 0 && _scrubCache[_scrubCache.Count - 1].FixedFrame > latest)
                    latest = _scrubCache[_scrubCache.Count - 1].FixedFrame;
                return latest == int.MinValue ? (int?)null : latest;
            }
        }

        // ---- Add ------------------------------------------------------------

        /// <summary>
        /// Append a keyframe whose frame is strictly greater than every
        /// existing keyframe's frame. Asserts in debug builds that the list
        /// stays sorted. Use <see cref="InsertOrReplaceKeyframeAt"/> for
        /// manual at-current-frame captures that can land anywhere.
        /// </summary>
        public void AppendKeyframe(WorldSnapshot snapshot)
        {
            TrecsDebugAssert.That(
                _keyframes.Count == 0
                    || _keyframes[_keyframes.Count - 1].FixedFrame < snapshot.FixedFrame,
                "AppendKeyframe expects strictly increasing frames"
            );
            _keyframes.Add(snapshot);
            _totalKeyframeBytes += snapshot.PayloadByteSize;
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
            _scrubCacheBytes += snapshot.PayloadByteSize;
        }

        /// <summary>
        /// Insert <paramref name="snapshot"/> into the keyframe list at the
        /// position dictated by its frame, or replace an existing keyframe at
        /// that frame. Used for manual at-current-frame keyframe captures
        /// which can land at any frame (paused after a scrub, etc.).
        /// </summary>
        public void InsertOrReplaceKeyframeAt(WorldSnapshot snapshot)
        {
            for (int i = 0; i < _keyframes.Count; i++)
            {
                if (_keyframes[i].FixedFrame == snapshot.FixedFrame)
                {
                    _totalKeyframeBytes -= _keyframes[i].PayloadByteSize;
                    ReturnSnapshot(_keyframes[i]);
                    _keyframes[i] = snapshot;
                    _totalKeyframeBytes += snapshot.PayloadByteSize;
                    return;
                }
            }
            int insertAt = 0;
            while (
                insertAt < _keyframes.Count && _keyframes[insertAt].FixedFrame < snapshot.FixedFrame
            )
            {
                insertAt++;
            }
            _keyframes.Insert(insertAt, snapshot);
            _totalKeyframeBytes += snapshot.PayloadByteSize;
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
                    ReturnSnapshot(_bookmarks[i]);
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
                    ReturnSnapshot(_bookmarks[i]);
                    _bookmarks.RemoveAt(i);
                    return true;
                }
            }
            return false;
        }

        /// <summary>
        /// Drop keyframes whose frame &gt; <paramref name="frame"/>. Returns
        /// the number of keyframes dropped.
        /// </summary>
        public int TrimKeyframesAfter(int frame)
        {
            int dropped = 0;
            while (_keyframes.Count > 0 && _keyframes[_keyframes.Count - 1].FixedFrame > frame)
            {
                var last = _keyframes[_keyframes.Count - 1];
                _totalKeyframeBytes -= last.PayloadByteSize;
                ReturnSnapshot(last);
                _keyframes.RemoveAt(_keyframes.Count - 1);
                dropped++;
            }
            return dropped;
        }

        /// <summary>
        /// Drop keyframes whose frame is strictly less than the keyframe closest
        /// to <paramref name="frame"/> from below. The closest at-or-before
        /// keyframe is preserved (so a JumpToFrame at the trim point still
        /// works). Returns the number of keyframes dropped (0 if nothing
        /// could be trimmed).
        /// </summary>
        public int TrimKeyframesBefore(int frame)
        {
            int keepFrom = FindKeyframeIndexAtOrBefore(frame);
            if (keepFrom <= 0)
            {
                return 0;
            }
            for (int i = 0; i < keepFrom; i++)
            {
                _totalKeyframeBytes -= _keyframes[i].PayloadByteSize;
                ReturnSnapshot(_keyframes[i]);
            }
            _keyframes.RemoveRange(0, keepFrom);
            return keepFrom;
        }

        public void TrimBookmarksAfter(int frame)
        {
            while (_bookmarks.Count > 0 && _bookmarks[_bookmarks.Count - 1].FixedFrame > frame)
            {
                ReturnSnapshot(_bookmarks[_bookmarks.Count - 1]);
                _bookmarks.RemoveAt(_bookmarks.Count - 1);
            }
        }

        public void TrimBookmarksBefore(int frame)
        {
            int removeCount = 0;
            while (removeCount < _bookmarks.Count && _bookmarks[removeCount].FixedFrame < frame)
            {
                ReturnSnapshot(_bookmarks[removeCount]);
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
                // Use the polymorphic size + release (like TrimScrubCacheBefore): live-captured
                // scrub entries are RetainedData-backed, so the contiguous Payload is empty for
                // them — .Payload.Length would mis-account 0 bytes, and skipping ReturnSnapshot
                // would leak the SerializationData instead of despawning it to _serDataPool.
                _scrubCacheBytes -= last.PayloadByteSize;
                ReturnSnapshot(last);
                _scrubCache.RemoveAt(_scrubCache.Count - 1);
            }
        }

        public void TrimScrubCacheBefore(int frame)
        {
            int removeCount = 0;
            while (removeCount < _scrubCache.Count && _scrubCache[removeCount].FixedFrame < frame)
            {
                _scrubCacheBytes -= _scrubCache[removeCount].PayloadByteSize;
                ReturnSnapshot(_scrubCache[removeCount]);
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
            ReturnPayloadsAndClear(_keyframes);
            ReturnPayloadsAndClear(_bookmarks);
            ReturnPayloadsAndClear(_scrubCache);
            ClearChecksums();
            _totalKeyframeBytes = 0;
            _scrubCacheBytes = 0;
        }

        // ---- Capacity ------------------------------------------------------

        /// <summary>
        /// Drop oldest keyframes until the count is &lt;= <paramref name="max"/>.
        /// <paramref name="max"/> == 0 means "unbounded" (no-op). Returns
        /// the number of keyframes evicted; the caller may want to refresh
        /// any cached earliest-frame state if non-zero.
        /// </summary>
        public int EnforceKeyframeCountCap(int max)
        {
            if (max <= 0)
            {
                return 0;
            }
            int evicted = 0;
            while (_keyframes.Count > max)
            {
                var oldest = _keyframes[0];
                _totalKeyframeBytes -= oldest.PayloadByteSize;
                ReturnSnapshot(oldest);
                _keyframes.RemoveAt(0);
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
                _scrubCacheBytes -= oldest.PayloadByteSize;
                ReturnSnapshot(oldest);
                _scrubCache.RemoveAt(0);
            }
        }

        // ---- Lookups -------------------------------------------------------

        /// <summary>
        /// Find the nearest scrubbable snapshot whose frame is
        /// &lt;= <paramref name="target"/>, searching keyframes, the scrub
        /// cache, and bookmarks. All three lists are kept sorted by frame
        /// ascending. On a tie at the same frame, prefers keyframes &gt;
        /// scrub cache &gt; bookmarks (keyframes are guaranteed-live-timeline
        /// bytes; scrub-cache entries reflect the same timeline up to
        /// drop-oldest eviction; bookmarks may capture an earlier divergent
        /// moment).
        /// </summary>
        public WorldSnapshot FindNearestAtOrBefore(int target)
        {
            int bestFrame = int.MinValue;
            WorldSnapshot best = null;
            for (int i = _keyframes.Count - 1; i >= 0; i--)
            {
                if (_keyframes[i].FixedFrame <= target)
                {
                    bestFrame = _keyframes[i].FixedFrame;
                    best = _keyframes[i];
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
                        best = _scrubCache[i];
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
                        best = b;
                    }
                    break;
                }
            }
            return best;
        }

        /// <summary>
        /// Earliest scrubbable snapshot across all three lists. Requires
        /// the store to be non-empty; callers should gate on
        /// <see cref="IsEmpty"/>.
        /// </summary>
        public WorldSnapshot FindEarliest()
        {
            TrecsAssert.That(!IsEmpty, "FindEarliest called on empty store");
            int frame = int.MaxValue;
            WorldSnapshot earliest = null;
            if (_keyframes.Count > 0)
            {
                frame = _keyframes[0].FixedFrame;
                earliest = _keyframes[0];
            }
            if (_scrubCache.Count > 0 && _scrubCache[0].FixedFrame < frame)
            {
                frame = _scrubCache[0].FixedFrame;
                earliest = _scrubCache[0];
            }
            if (_bookmarks.Count > 0 && _bookmarks[0].FixedFrame < frame)
            {
                earliest = _bookmarks[0];
            }
            return earliest;
        }

        /// <summary>
        /// Index in <see cref="Keyframes"/> of the latest keyframe whose frame
        /// is &lt;= <paramref name="frame"/>. Returns -1 if no keyframe fits.
        /// </summary>
        public int FindKeyframeIndexAtOrBefore(int frame)
        {
            for (int i = _keyframes.Count - 1; i >= 0; i--)
            {
                if (_keyframes[i].FixedFrame <= frame)
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
                ReturnSnapshot(list[i]);
            }
            list.Clear();
        }
    }
}
