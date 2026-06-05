using System;
using System.IO;
using System.Runtime.InteropServices;

namespace Trecs.Internal
{
    public enum BundlePlaybackState
    {
        Idle,
        Playing,
        Desynced,
    }

    /// <summary>
    /// Replays a <see cref="RecordingBundle"/> against a live world: restores
    /// the bundle's initial snapshot, feeds its captured input queue, and
    /// verifies per-frame checksums to surface desyncs. Keyframes are exposed
    /// for callers that want to recover from a desync by jumping to the
    /// nearest keyframe frame.
    ///
    /// Main-thread only.
    /// </summary>
    public sealed class BundleReplayer : IInputHistoryLocker, IDisposable
    {
        readonly TrecsLog _log;

        // Lifecycle / IO primitives (accessor + checksum calculator + reusable
        // snapshot/queue buffers + locker registration) live on this shared
        // collaborator that the recorders (BundleRecorder, TrecsRewindBuffer)
        // also own — see RecordingEngine docs for the split rationale.
        readonly RecordingEngine _core;

        BundlePlaybackState _state = BundlePlaybackState.Idle;
        RecordingBundle _bundle;
        int? _desyncedFrame;
        bool _disposed;

        public BundleReplayer(
            World world,
            SerializerRegistry registry,
            SnapshotSerializer snapshotSerializer,
            IOpaqueBlobStore opaqueBlobStore = null
        )
        {
            if (world == null)
                throw new ArgumentNullException(nameof(world));

            _log = world.Log;
            // No OpaqueBlobPersistence: replay restores opaque *input* blobs
            // through opaqueBlobStore (handled inside the engine's queue
            // deserialize); the initial snapshot's opaque blobs are expected
            // resident already.
            _core = new RecordingEngine(
                world,
                registry,
                snapshotSerializer,
                accessorLabel: nameof(BundleReplayer),
                opaqueBlobStore: opaqueBlobStore
            );
        }

        public BundlePlaybackState State => _state;
        public bool IsPlaying =>
            _state == BundlePlaybackState.Playing || _state == BundlePlaybackState.Desynced;
        public bool HasDesynced => _state == BundlePlaybackState.Desynced;

        /// <summary>
        /// Frame at which the first checksum mismatch was detected during the
        /// current playback walk, or null if the walk is consistent.
        /// </summary>
        public int? DesyncedFrame => _desyncedFrame;

        /// <summary>
        /// The bundle currently being played, or null when idle.
        /// </summary>
        public RecordingBundle Bundle => _bundle;

        /// <summary>
        /// IInputHistoryLocker implementation. Returns -1 to keep all input
        /// history while playing — we never want the queue to prune frames
        /// the bundle still has yet to apply.
        /// </summary>
        public int? MaxClearFrame
        {
            get
            {
                if (!IsPlaying)
                    return null;
                return -1;
            }
        }

        /// <summary>
        /// Restore <paramref name="bundle"/>'s initial snapshot into the live
        /// world, hydrate the EntityInputQueue from the bundle, and arm the
        /// per-frame desync checks. Verifies the post-restore world matches
        /// the bundle's recorded initial-state checksum; throws on mismatch
        /// (a serialization defect — distinct from a simulation desync).
        ///
        /// <para>
        /// Failure semantics: bundle-shape checks (null payload, queue
        /// envelope corruption) fire <i>before</i> any world mutation, so a
        /// throw at that stage leaves the live world untouched and the
        /// player Idle. Once <see cref="SnapshotSerializer.Deserialize"/>
        /// has run, the world has already been mutated and cannot be rolled
        /// back; on a later failure (checksum mismatch, queue deserialize
        /// error) the player still resets to Idle so a follow-up Start can
        /// be attempted, but callers should treat the world as needing
        /// re-restoration and not continue the previous simulation.
        /// </para>
        /// </summary>
        /// <exception cref="ArgumentNullException"><paramref name="bundle"/> is null.</exception>
        /// <exception cref="InvalidOperationException">Playback is already active, or the bundle is unreplayable.</exception>
        /// <exception cref="SerializationException">The bundle's queue envelope is corrupt, or the post-load checksum did not match.</exception>
        /// <exception cref="ObjectDisposedException">The player has been disposed.</exception>
        public void Start(RecordingBundle bundle)
        {
            ThrowIfDisposed();
            if (bundle == null)
                throw new ArgumentNullException(nameof(bundle));
            if (_state != BundlePlaybackState.Idle)
                throw new InvalidOperationException(
                    "Cannot Start: playback is already active. Call Stop first."
                );
            if (bundle.InitialSnapshot.IsEmpty)
                throw new InvalidOperationException(
                    "Bundle has no initial snapshot — cannot replay."
                );

            // Schema gate, before any world mutation: the bundle's snapshot
            // and input-queue wire formats depend on the world schema
            // matching the recording exactly. (The embedded snapshot's own
            // metadata fingerprint is re-checked by LoadSnapshot below; this
            // earlier header-level check keeps the world untouched and words
            // the error for recordings.)
            var currentFingerprint = _core.World.SchemaFingerprint;
            if (bundle.Header.SchemaFingerprint != currentFingerprint)
            {
                throw new SerializationException(
                    WorldSchemaFingerprintCalculator.BuildMismatchMessage(
                        "recording",
                        bundle.Header.SchemaFingerprint,
                        currentFingerprint
                    )
                );
            }

            // Replaying inputs against a different fixed tick rate desyncs
            // even with an identical schema — flag it up-front rather than
            // letting the user chase a checksum mismatch mid-playback.
            var liveFixedDeltaTime = _core.World.SystemRunner.FixedDeltaTime;
            if (bundle.Header.FixedDeltaTime != liveFixedDeltaTime)
            {
                _log.Warning(
                    "Recording was captured with FixedDeltaTime {0} but the live world runs at "
                        + "{1} — input replay against a different tick rate will desync.",
                    bundle.Header.FixedDeltaTime,
                    liveFixedDeltaTime
                );
            }

            // Pre-flight the queue payload's outer envelope (Trecs header
            // magic + format version) before mutating the world. A corrupt
            // envelope here means the queue deserialize is doomed to fail —
            // catching it now keeps the world untouched. The full parse still
            // happens after LoadSnapshot (the queue's heaps need the
            // post-load world state), but the envelope check is cheap and
            // catches an entire class of corruption up-front.
            if (!bundle.InputQueue.IsEmpty)
            {
                ValidateQueueEnvelope(bundle.InputQueue);
            }

            // Restore initial state from the bundle's embedded snapshot.
            // Past this point, world has been mutated and we can't roll back.
            _core.LoadSnapshot(bundle.InitialSnapshot);

            try
            {
                VerifyPostDeserializationChecksum(bundle);

                // Wipe the live queue and replace it with the bundle's serialized
                // inputs. ClearAllInputs (vs ClearFutureInputsAfterOrAt) is
                // correct here: we're switching timelines wholesale.
                _core.Accessor.GetEntityInputQueue().ClearAllInputs();
                if (!bundle.InputQueue.IsEmpty)
                {
                    // Wraps the bundle's input-queue bytes directly (zero copy)
                    // and reads them back through the engine's reused buffers.
                    _core.DeserializeEntityInputQueue(bundle.InputQueue);
                }

                _core.AddHistoryLocker(this);
                SetInputSystemsEnabled(false);
            }
            catch
            {
                // Restoration succeeded but the post-restore wiring failed.
                // Leave the player Idle and let the exception propagate; the
                // caller can decide whether to dispose the (now half-set-up)
                // world or retry. _state stays Idle, no locker installed,
                // input systems still enabled — the recorder is in the same
                // shape it would be after a clean Stop.
                _state = BundlePlaybackState.Idle;
                _bundle = null;
                // Force the reused queue reader back to idle so a subsequent
                // Start() can safely re-read. No-op unless a step *outside*
                // DeserializeEntityInputQueue's own try faulted mid-read.
                _core.ResetQueueBufferForErrorRecovery();
                throw;
            }

            _bundle = bundle;
            _desyncedFrame = null;
            _state = BundlePlaybackState.Playing;

            _log.Info(
                "Playback started: frames {0} .. {1}",
                bundle.Header.StartFixedFrame,
                bundle.Header.EndFixedFrame
            );
        }

        // Peek the queue payload's Trecs envelope without consuming it. Throws
        // SerializationException on magic-byte / format-version mismatch.
        // Cheap (a handful of bytes) and called before any world mutation so a
        // corrupt envelope is caught up-front.
        static void ValidateQueueEnvelope(ReadOnlyMemory<byte> payload)
        {
            if (!MemoryMarshal.TryGetArray(payload, out var seg))
            {
                return; // non-array-backed payloads (future Memory<T> sources) — skip
            }
            using var stream = new MemoryStream(seg.Array, seg.Offset, seg.Count, writable: false);
            // PayloadHeader.Peek throws SerializationException on bad magic /
            // unsupported format version; let those propagate up to the
            // caller. (Peek restores the stream position, but the stream is
            // throwaway here.)
            _ = PayloadHeader.Peek(stream);
        }

        /// <summary>
        /// Call after each fixed update during playback. Returns the result
        /// of the per-frame checksum check (no checksum recorded for this
        /// frame produces an idle result with <see cref="PlaybackTickResult.ChecksumVerified"/>
        /// false).
        /// </summary>
        /// <exception cref="InvalidOperationException">Playback is not active.</exception>
        public PlaybackTickResult Tick()
        {
            ThrowIfDisposed();
            if (!IsPlaying)
                throw new InvalidOperationException(
                    "Cannot Tick: playback is not active. Call Start first."
                );
            if (_state == BundlePlaybackState.Desynced)
            {
                return default;
            }

            var currentFrame = _core.Accessor.FixedFrame;
            if (!_bundle.Checksums.TryGetValue(currentFrame, out var expected))
            {
                return default;
            }

            var actual = _core.ComputeChecksum(_bundle.Header.Version);
            if (expected != actual)
            {
                _log.Warning(
                    "Desync detected at frame {0}. expected={1} actual={2}",
                    currentFrame,
                    expected,
                    actual
                );
                _desyncedFrame = currentFrame;
                _state = BundlePlaybackState.Desynced;
            }

            return new PlaybackTickResult { ExpectedChecksum = expected, ActualChecksum = actual };
        }

        /// <summary>
        /// Stop playback. Re-enables input phase systems, releases the input
        /// history lock, and clears the active bundle reference.
        /// </summary>
        /// <exception cref="InvalidOperationException">Playback is not active.</exception>
        public void Stop()
        {
            ThrowIfDisposed();
            if (!IsPlaying)
                throw new InvalidOperationException("Cannot Stop: playback is not active.");

            TearDownPlayback();
            _log.Info("Playback stopped");
        }

        public void Dispose()
        {
            if (_disposed)
                return;
            _disposed = true;

            if (IsPlaying)
            {
                _log.Warning("Disposing BundleReplayer while playback is active — stopping first");
                TearDownPlayback();
            }
        }

        // Shared teardown for Stop and dispose-while-playing: release the
        // input history lock, drop the abandoned timeline's queued future
        // inputs, re-enable input systems, and return to Idle. Skips the
        // queue cleanup when the world is already disposed (the queue went
        // down with it); the locker removal and SetInputSystemsEnabled
        // guard themselves.
        void TearDownPlayback()
        {
            _core.RemoveHistoryLocker(this);
            if (!_core.World.IsDisposed)
            {
                _core
                    .Accessor.GetEntityInputQueue()
                    .ClearFutureInputsAfterOrAt(_core.Accessor.FixedFrame);
            }
            SetInputSystemsEnabled(true);
            _bundle = null;
            _state = BundlePlaybackState.Idle;
        }

        void VerifyPostDeserializationChecksum(RecordingBundle bundle)
        {
            // The initial snapshot's checksum lives in the per-frame Checksums
            // dict, keyed by the bundle's start frame. Absent / zero = "no
            // checksum recorded" (legacy sentinel) — skip rather than surface
            // a phantom desync.
            if (
                !bundle.Checksums.TryGetValue(bundle.Header.StartFixedFrame, out var expected)
                || expected == 0UL
            )
            {
                return;
            }
            var actual = _core.ComputeChecksum(bundle.Header.Version);
            if (actual != expected)
            {
                throw new SerializationException(
                    $"Post-deserialization checksum MISMATCH (expected: {expected}, "
                        + $"got: {actual}). This indicates a serialization/deserialization defect — not a simulation desync."
                );
            }
        }

        void SetInputSystemsEnabled(bool enable)
        {
            if (_core.World.IsDisposed)
                return;
            var systems = _core.World.GetSystems();
            for (int i = 0; i < systems.Count; i++)
            {
                if (systems[i].Phase != SystemPhase.Input)
                    continue;
                if (_core.Accessor.IsSystemEnabled(i, EnableChannel.Playback) != enable)
                {
                    _core.Accessor.SetSystemEnabled(i, EnableChannel.Playback, enable);
                }
            }
        }

        void ThrowIfDisposed()
        {
            if (_disposed)
                throw new ObjectDisposedException(nameof(BundleReplayer));
        }
    }
}
