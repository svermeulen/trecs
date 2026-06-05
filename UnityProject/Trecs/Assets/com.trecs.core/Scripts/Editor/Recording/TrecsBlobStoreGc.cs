using System;
using System.Collections.Generic;
using System.IO;

namespace Trecs.Internal
{
    /// <summary>
    /// Recording-rooted garbage collection for the shared opaque-blob store under
    /// <see cref="TrecsPaths.Blobs"/>. Deletes every stored blob no longer referenced by any saved
    /// snapshot/recording or by the attached recorder's in-memory state. Editor-only; main thread.
    /// </summary>
    static class TrecsBlobStoreGc
    {
        static readonly TrecsLog _log = TrecsLog.Default;

        /// <summary>
        /// Run a sweep. The live root set is the union of every saved <c>.snap</c>'s metadata
        /// <c>BlobIds</c> and every saved <c>.trec</c>'s header <c>BlobIds</c> (both complete by
        /// construction). In-memory recordings need no rooting here: their opaque blobs are pinned
        /// resident in the <c>BlobCache</c>, not written to this store, so a sweep can never evict a
        /// blob a live in-progress recording depends on — only an actual save persists bytes, and a
        /// saved file roots exactly those.
        /// <para>
        /// <b>Fail-safe:</b> if any saved file can't be read, the sweep aborts <i>without deleting</i>
        /// — an unreadable file might reference blobs we'd otherwise drop. A missing store directory
        /// is a no-op. Never throws into the caller (a GC hiccup must not fail a save/delete).
        /// </para>
        /// <para>
        /// <b>Edge case:</b> a recording loaded into memory whose <c>.trec</c> is then deleted from
        /// disk still resolves its not-yet-scrubbed-to blobs from this store, so deleting that file
        /// makes those blobs collectible while it is still loaded. Treated as acceptable ("deleting a
        /// file you are using"); live-captured (pinned) snapshots are unaffected, as their blobs live
        /// in RAM, not the store.
        /// </para>
        /// </summary>
        public static void Sweep(SnapshotSerializer snapshots, SerializerRegistry registry)
        {
            TrecsDebugAssert.That(snapshots != null, "snapshots must not be null");
            TrecsDebugAssert.That(registry != null, "registry must not be null");

            if (!Directory.Exists(TrecsPaths.Blobs))
            {
                return;
            }

            var live = new HashSet<BlobId>();
            try
            {
                // Cold path: a fresh read view per sweep is fine; Wrap reads the
                // file bytes in place without a contiguous copy.
                var readBuffer = new SerializationReadBuffer();
                foreach (var name in TrecsSnapshotLibrary.GetSavedSnapshotNames())
                {
                    var bytes = File.ReadAllBytes(TrecsSnapshotLibrary.GetSnapshotPath(name));
                    var metadata = snapshots.PeekMetadata(readBuffer.Wrap(bytes));
                    foreach (var id in metadata.BlobIds)
                    {
                        live.Add(id);
                    }
                }

                var bundles = new RecordingBundleSerializer(registry);
                foreach (var name in TrecsRecordingSession.GetSavedRecordingNames())
                {
                    var path = Path.Combine(
                        TrecsRecordingSession.GetRecordingsDirectory(),
                        name + TrecsRecordingSession.RecordingExtension
                    );
                    foreach (var id in bundles.PeekHeader(path).BlobIds)
                    {
                        live.Add(id);
                    }
                }
            }
            catch (Exception e)
            {
                _log.Warning(
                    "Blob-store GC aborted (could not read a saved file's blob refs): {0}",
                    e.Message
                );
                return;
            }

            try
            {
                var deleted = new FileBlobStore(TrecsPaths.Blobs).CollectGarbage(live);
                if (deleted > 0)
                {
                    _log.Info("Blob-store GC deleted {0} unreferenced blob file(s)", deleted);
                }
            }
            catch (Exception e)
            {
                _log.Warning("Blob-store GC failed to delete unreferenced blobs: {0}", e.Message);
            }
        }
    }
}
