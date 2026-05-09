using System;
using System.IO;
using Trecs.Internal;

namespace Trecs.Serialization
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
    /// verifies per-frame checksums to surface desyncs. Anchors are exposed
    /// for callers that want to recover from a desync by jumping to the
    /// nearest anchor frame.
    ///
    /// Main-thread only.
    /// </summary>
    public sealed class BundlePlayer : IInputHistoryLocker, IDisposable
    {
        static readonly TrecsLog _log = new(nameof(BundlePlayer));

        readonly IWorldStateSerializer _worldStateSerializer;
        readonly RecordingChecksumCalculator _checksumCalculator;
        readonly SnapshotSerializer _snapshotSerializer;
        readonly SerializerRegistry _serializerRegistry;
        readonly SerializationBuffer _checksumBuffer;
        readonly World _worldOwner;

        WorldAccessor _world;
        BundlePlaybackState _state = BundlePlaybackState.Idle;
        RecordingBundle _bundle;
        int? _desyncedFrame;
        bool _disposed;

        public BundlePlayer(
            World world,
            IWorldStateSerializer worldStateSerializer,
            SerializerRegistry registry,
            SnapshotSerializer snapshotSerializer
        )
        {
            if (world == null)
                throw new ArgumentNullException(nameof(world));
            if (worldStateSerializer == null)
                throw new ArgumentNullException(nameof(worldStateSerializer));
            if (registry == null)
                throw new ArgumentNullException(nameof(registry));
            if (snapshotSerializer == null)
                throw new ArgumentNullException(nameof(snapshotSerializer));

            _worldOwner = world;
            _worldStateSerializer = worldStateSerializer;
            _serializerRegistry = registry;
            _snapshotSerializer = snapshotSerializer;
            _checksumCalculator = new RecordingChecksumCalculator(worldStateSerializer);
            _checksumBuffer = new SerializationBuffer(registry);
        }

        public void Initialize()
        {
            ThrowIfDisposed();
            _world = _worldOwner.CreateAccessor(AccessorRole.Unrestricted, nameof(BundlePlayer));
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
        /// </summary>
        /// <exception cref="ArgumentNullException"><paramref name="bundle"/> is null.</exception>
        /// <exception cref="InvalidOperationException">Playback is already active.</exception>
        /// <exception cref="SerializationException">The post-load checksum did not match.</exception>
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
            if (bundle.InitialSnapshot == null || bundle.InitialSnapshot.Length == 0)
                throw new InvalidOperationException(
                    "Bundle has no initial snapshot — cannot replay."
                );

            // Restore initial state from the bundle's embedded snapshot.
            using (var ms = new MemoryStream(bundle.InitialSnapshot, writable: false))
            {
                _snapshotSerializer.LoadSnapshot(ms);
            }
            VerifyPostDeserializationChecksum(bundle);

            // Wipe the live queue and replace it with the bundle's serialized
            // inputs. ClearAllInputs (vs ClearFutureInputsAfterOrAt) is
            // correct here: we're switching timelines wholesale.
            var inputQueue = _world.GetEntityInputQueue();
            inputQueue.ClearAllInputs();
            if (bundle.InputQueue != null && bundle.InputQueue.Length > 0)
            {
                using var queueBuffer = new SerializationBuffer(_serializerRegistry);
                queueBuffer.MemoryStream.Write(bundle.InputQueue, 0, bundle.InputQueue.Length);
                queueBuffer.MemoryStream.Position = 0;
                queueBuffer.StartRead();
                inputQueue.Deserialize(new TrecsSerializationReaderAdapter(queueBuffer));
                queueBuffer.StopRead(verifySentinel: false);
            }

            inputQueue.AddHistoryLocker(this);
            SetInputSystemsEnabled(false);

            _bundle = bundle;
            _desyncedFrame = null;
            _state = BundlePlaybackState.Playing;

            _log.Info(
                "Playback started: frames {} .. {}",
                bundle.Header.StartFixedFrame,
                bundle.Header.EndFixedFrame
            );
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

            var currentFrame = _world.FixedFrame;
            if (!_bundle.Checksums.TryGetValue(currentFrame, out var expected))
            {
                return default;
            }

            var actual = _checksumCalculator.CalculateCurrentChecksum(
                version: _bundle.Header.Version,
                _checksumBuffer,
                _bundle.Header.ChecksumFlags
            );
            if (expected != actual)
            {
                _log.Warning(
                    "Desync detected at frame {}. expected={} actual={}",
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

            var queue = _world.GetEntityInputQueue();
            queue.RemoveHistoryLocker(this);
            queue.ClearFutureInputsAfterOrAt(_world.FixedFrame);
            SetInputSystemsEnabled(true);

            _bundle = null;
            _state = BundlePlaybackState.Idle;
            _log.Info("Playback stopped");
        }

        public void Dispose()
        {
            if (_disposed)
                return;
            _disposed = true;

            if (IsPlaying)
            {
                _log.Warning("Disposing BundlePlayer while playback is active — stopping first");
                var queue = _world.GetEntityInputQueue();
                queue.RemoveHistoryLocker(this);
                queue.ClearFutureInputsAfterOrAt(_world.FixedFrame);
                SetInputSystemsEnabled(true);
                _bundle = null;
                _state = BundlePlaybackState.Idle;
            }

            _checksumBuffer?.Dispose();
        }

        void VerifyPostDeserializationChecksum(RecordingBundle bundle)
        {
            if (bundle.InitialSnapshotChecksum == 0u)
            {
                // Sentinel for "no checksum recorded" — skip rather than
                // surface a phantom desync.
                return;
            }
            var actual = _checksumCalculator.CalculateCurrentChecksum(
                version: bundle.Header.Version,
                _checksumBuffer,
                bundle.Header.ChecksumFlags
            );
            if (actual != bundle.InitialSnapshotChecksum)
            {
                throw new SerializationException(
                    $"Post-deserialization checksum MISMATCH (expected: {bundle.InitialSnapshotChecksum}, "
                        + $"got: {actual}). This indicates a serialization/deserialization defect — not a simulation desync."
                );
            }
        }

        void SetInputSystemsEnabled(bool enable)
        {
            if (_world == null || _worldOwner.IsDisposed)
                return;
            for (int i = 0; i < _worldOwner.SystemCount; i++)
            {
                if (_worldOwner.GetSystemMetadata(i).Phase != SystemPhase.Input)
                    continue;
                if (_world.IsSystemEnabled(i, EnableChannel.Playback) != enable)
                {
                    _world.SetSystemEnabled(i, EnableChannel.Playback, enable);
                }
            }
        }

        void ThrowIfDisposed()
        {
            if (_disposed)
                throw new ObjectDisposedException(nameof(BundlePlayer));
        }
    }
}
