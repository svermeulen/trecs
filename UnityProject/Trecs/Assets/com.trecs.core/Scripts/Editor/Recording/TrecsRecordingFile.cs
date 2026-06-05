using System;
using System.Collections.Generic;
using System.IO;
using Trecs.Collections;
using UnityEngine;

namespace Trecs.Internal
{
    /// <summary>
    /// File-level marshaling for the rewind buffer's saved <c>.trec</c>
    /// recordings: header peeking, store → bundle → file on save, and
    /// file → bundle + keyframe-list reconstruction with validation
    /// warnings on load. Extracted from <see cref="TrecsRewindBuffer"/>,
    /// which keeps what genuinely belongs to it — the save-path guards and
    /// the state-machine transitions that apply a loaded recording
    /// (subscription/locker swaps, pause, error recovery).
    /// </summary>
    internal static class TrecsRecordingFile
    {
        static readonly TrecsLog _log = TrecsLog.Default;

        /// <summary>
        /// Lightweight inspection of a saved recording's header — frame span and
        /// tick rate, without loading snapshots, inputs, or checksums.
        /// Cheap enough to call per-file when listing the saves library.
        /// Returns false if the file is missing or the binary payload is invalid.
        /// </summary>
        public static bool TryReadHeader(string filePath, out RecordingHeader header)
        {
            header = default;
            if (!File.Exists(filePath))
                return false;
            try
            {
                // Caller (saves library) caches results by mtime so this only
                // runs on first read or when the file changes; constructing a
                // fresh registry+serializer per call keeps the API static.
                var registry = new SerializerRegistry();
                DefaultTrecsSerializers.RegisterCommonTrecsSerializers(registry);
                var serializer = new RecordingBundleSerializer(registry);
                var bundleHeader = serializer.PeekHeader(filePath);
                header = new RecordingHeader(
                    bundleHeader.StartFixedFrame,
                    bundleHeader.EndFixedFrame,
                    bundleHeader.FixedDeltaTime
                );
                return true;
            }
            catch
            {
                return false;
            }
        }

        /// <summary>
        /// Persist the store's snapshot list, plus the live EntityInputQueue
        /// covering its frame range, to <paramref name="filePath"/>. The
        /// store must hold at least one keyframe (the caller guards).
        /// </summary>
        public static void Save(
            RecordingEngine core,
            SnapshotStore store,
            RecordingBundleSerializer bundleSerializer,
            IOpaqueBlobStore blobStore,
            int version,
            string filePath
        )
        {
            TrecsDebugAssert.That(
                store.Keyframes.Count > 0,
                "TrecsRecordingFile.Save requires at least one keyframe — callers guard."
            );
            TrecsDebugAssert.That(
                core.OpaqueBlobPersistence != null,
                "TrecsRecordingFile.Save requires an engine constructed with an OpaqueBlobPersistence."
            );

            // Blob root seeding rationale lives on CreateBundleBlobRootSet;
            // per-snapshot referenced ids are unioned in after assembly below.
            var blobs = core.CreateBundleBlobRootSet();

            // The in-memory keyframe list is treated as the initial snapshot
            // (first entry) plus a sequence of trailing keyframes (the rest).
            // User-placed bookmarks live in their own list and round-trip
            // independently. The per-frame checksums dict is populated under
            // its own cadence in CaptureSnapshotIfDue and persisted into the
            // bundle so BundleReplayer can detect desyncs close to where they
            // happen rather than only at sparse keyframe frames.
            // Live snapshots are retained as two-section SerializationData; the bundle wire
            // format embeds contiguous payloads, so each is materialized to contiguous bytes
            // here (cold save path). WithContiguousPayload is a no-op for already-contiguous
            // (loaded) snapshots.
            var storeKeyframes = store.Keyframes;
            var initial = WorldSnapshotListUtil.WithContiguousPayload(storeKeyframes[0]);
            var trailingKeyframes = new List<WorldSnapshot>(storeKeyframes.Count - 1);
            for (int i = 1; i < storeKeyframes.Count; i++)
            {
                trailingKeyframes.Add(
                    WorldSnapshotListUtil.WithContiguousPayload(storeKeyframes[i])
                );
            }

            var storeBookmarks = store.Bookmarks;
            var bundleBookmarks = new List<WorldSnapshot>(storeBookmarks.Count);
            for (int i = 0; i < storeBookmarks.Count; i++)
            {
                bundleBookmarks.Add(WorldSnapshotListUtil.WithContiguousPayload(storeBookmarks[i]));
            }

            var bundle = core.AssembleBundle(
                version,
                initial.FixedFrame,
                // End frame is the current frame, not the last keyframe's
                // frame: per-frame checksums, scrub-cache entries, and
                // bookmarks can extend past the last keyframe when their
                // cadences differ.
                core.Accessor.FixedFrame,
                blobs,
                initial.Payload,
                trailingKeyframes,
                bundleBookmarks,
                store.CopyChecksumsForBundle()
            );

            // Header.BlobIds is this recording's GC root, so it must name every opaque blob any
            // embedded snapshot references — not just the live world's set at save time, which
            // would miss a blob referenced only by an earlier keyframe whose blob the live world
            // has since dropped. Union each snapshot's referenced ids in (the bundle header holds
            // the same set instance); the heap-derived seed additionally keeps
            // post-last-keyframe blobs (sim-held and input-window). The bytes for every id are
            // flushed to the store just below.
            AddReferencedBlobIds(core, blobs, initial.Payload);
            for (int i = 0; i < trailingKeyframes.Count; i++)
            {
                AddReferencedBlobIds(core, blobs, trailingKeyframes[i].Payload);
            }
            for (int i = 0; i < bundleBookmarks.Count; i++)
            {
                AddReferencedBlobIds(core, blobs, bundleBookmarks[i].Payload);
            }

            // Live-captured snapshots hold their opaque blobs as in-memory cache pins rather than
            // writing them through at capture, so flush every referenced blob to the shared store
            // now — the pins guarantee residency. OpaqueBlobs.Persist skips ids already present
            // (input blobs just written by AssembleBundle's queue serialization, and blobs carried
            // over from a loaded recording), so each unique blob is written once and every header
            // id resolves on a fresh-process load.
            foreach (var id in blobs)
            {
                core.OpaqueBlobPersistence.Persist(id, blobStore);
            }

            bundleSerializer.Save(bundle, filePath);

            _log.Debug(
                "Saved recording: {0} keyframes, {1} bookmarks, {2} blob refs, {3} bytes input queue",
                store.Keyframes.Count,
                store.Bookmarks.Count,
                blobs.Count,
                bundle.InputQueue.Length
            );
        }

        /// <summary>
        /// Read and validate a saved recording: load the bundle, warn on
        /// version / tick-rate mismatches (loads proceed — they may desync),
        /// and reconstruct the keyframe list with the initial snapshot at the
        /// head. Returns false when the file is missing, unreadable, or has
        /// no initial snapshot. Pure marshaling — applying the result to a
        /// live world is <see cref="TrecsRewindBuffer.LoadRecordingFromFile"/>'s job.
        /// </summary>
        public static bool TryLoad(
            string filePath,
            RecordingBundleSerializer bundleSerializer,
            int expectedVersion,
            float currentFixedDeltaTime,
            out RecordingBundle bundle,
            out List<WorldSnapshot> keyframes
        )
        {
            bundle = null;
            keyframes = null;

            if (!File.Exists(filePath))
            {
                _log.Warning("Recording file does not exist: {0}", filePath);
                return false;
            }

            try
            {
                bundle = bundleSerializer.Load(filePath);
            }
            catch (Exception e)
            {
                _log.Error("Failed to read recording from {0}: {1}", filePath, e);
                return false;
            }

            if (bundle.InitialSnapshot.IsEmpty)
            {
                _log.Warning("Loaded recording has no initial snapshot");
                bundle = null; // Try-pattern: outputs are unset on false.
                return false;
            }

            if (bundle.Header.Version != expectedVersion)
            {
                _log.Warning(
                    "Recording schema version {0} does not match current {1} — "
                        + "load may fail or the simulation may desync",
                    bundle.Header.Version,
                    expectedVersion
                );
            }

            if (!Mathf.Approximately(bundle.Header.FixedDeltaTime, currentFixedDeltaTime))
            {
                _log.Warning(
                    "Recording fixed delta time {0} differs from current {1} — input replay may desync",
                    bundle.Header.FixedDeltaTime,
                    currentFixedDeltaTime
                );
            }

            // Reconstruct the in-memory keyframe list: initial snapshot at the
            // head, then trailing keyframes. Persisted keyframes and the transient
            // scrub cache are kept separate (keyframes survive Save/Load; the
            // scrub cache is rebuilt per session).
            keyframes = new List<WorldSnapshot>(bundle.Keyframes.Count + 1)
            {
                new WorldSnapshot
                {
                    FixedFrame = bundle.Header.StartFixedFrame,
                    Kind = SnapshotKind.Keyframe,
                    Label = "",
                    Payload = bundle.InitialSnapshot,
                },
            };
            foreach (var keyframe in bundle.Keyframes)
            {
                keyframes.Add(keyframe);
            }
            return true;
        }

        // Union the opaque blob ids a serialized snapshot payload references into the set, read
        // cheaply from its metadata header (no world restore).
        static void AddReferencedBlobIds(
            RecordingEngine core,
            IterableHashSet<BlobId> set,
            ReadOnlyMemory<byte> payload
        )
        {
            var metadata = core.PeekMetadata(payload);
            foreach (var id in metadata.BlobIds)
            {
                set.Add(id);
            }
        }
    }

    /// <summary>
    /// Header summary parsed from a saved recording file. Exposed so editor
    /// tooling can inspect frame span / tick rate without loading the full
    /// snapshot list. See <see cref="TrecsRewindBuffer.TryReadRecordingHeader"/>.
    /// </summary>
    internal readonly struct RecordingHeader
    {
        public readonly int StartFrame;
        public readonly int EndFrame;
        public readonly float FixedDeltaTime;

        public RecordingHeader(int startFrame, int endFrame, float fixedDeltaTime)
        {
            StartFrame = startFrame;
            EndFrame = endFrame;
            FixedDeltaTime = fixedDeltaTime;
        }

        public int FrameCount => EndFrame - StartFrame + 1;
        public float DurationSeconds => FrameCount * FixedDeltaTime;
    }
}
