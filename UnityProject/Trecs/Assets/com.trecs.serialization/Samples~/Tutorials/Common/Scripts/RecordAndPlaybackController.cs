using System;
using System.IO;
using Trecs.Internal;
using UnityEngine;

namespace Trecs.Serialization.Samples
{
    /// <summary>
    /// Sample-side helper that wires Unity keyboard input to the Trecs
    /// recording, playback, and snapshot serialization APIs.
    ///
    /// Keys: F5=Toggle Record, F6=Toggle Playback, F8=Save Snapshot, F9=Load Snapshot.
    ///
    /// Recordings and snapshots are written under
    /// <c>{Application.persistentDataPath}/{sampleName}/Recordings/</c>.
    /// </summary>
    public class RecordAndPlaybackController : IDisposable
    {
        static readonly TrecsLog _log = new(nameof(RecordAndPlaybackController));

        readonly BundleRecorder _recorder;
        readonly BundlePlayer _player;
        readonly RecordingBundleSerializer _bundleSerializer;
        readonly SnapshotSerializer _snapshotSerializer;
        readonly WorldAccessor _world;
        readonly string _bundlePath;
        readonly string _snapshotPath;

        ControllerState _state;
        IDisposable _playbackTickSubscription;

        public RecordAndPlaybackController(
            SerializationServices serialization,
            World world,
            string sampleName
        )
        {
            Assert.That(!string.IsNullOrEmpty(sampleName));

            _recorder = serialization.Recorder;
            _player = serialization.Player;
            _bundleSerializer = serialization.BundleSerializer;
            _snapshotSerializer = serialization.Snapshots;
            _world = world.CreateAccessor(
                AccessorRole.Unrestricted,
                nameof(RecordAndPlaybackController)
            );

            var recordingDir = Path.Combine(
                Application.persistentDataPath,
                sampleName,
                "Recordings"
            );
            _bundlePath = Path.Combine(recordingDir, "recording.trec");
            _snapshotPath = Path.Combine(recordingDir, "snapshot.snap");

            Directory.CreateDirectory(recordingDir);

            _log.Info(
                "Recording controller ready. Keys: F5=Toggle Record, F6=Toggle Playback, F8=Save Snapshot, F9=Load Snapshot"
            );
        }

        public ControllerState State => _state;

        public void Tick()
        {
            if (Input.GetKeyDown(KeyCode.F5))
            {
                HandleToggleRecording();
            }
            else if (Input.GetKeyDown(KeyCode.F6))
            {
                HandleTogglePlayback();
            }
            else if (Input.GetKeyDown(KeyCode.F8))
            {
                HandleSaveSnapshot();
            }
            else if (Input.GetKeyDown(KeyCode.F9))
            {
                HandleLoadSnapshot();
            }
        }

        void HandleToggleRecording()
        {
            switch (_state)
            {
                case ControllerState.Idle:
                    HandleStartRecording();
                    break;
                case ControllerState.Recording:
                    HandleStopRecording();
                    break;
                case ControllerState.Playback:
                    _log.Warning("Cannot toggle recording while playback is active");
                    break;
            }
        }

        void HandleTogglePlayback()
        {
            switch (_state)
            {
                case ControllerState.Idle:
                    HandleStartPlayback();
                    break;
                case ControllerState.Playback:
                    HandleStopPlayback();
                    break;
                case ControllerState.Recording:
                    _log.Warning("Cannot toggle playback while recording is active");
                    break;
            }
        }

        void HandleStartRecording()
        {
            _log.Info("Starting recording at frame {}", _world.FixedFrame);
            _recorder.Start();
            _state = ControllerState.Recording;
            _log.Info("Recording started");
        }

        void HandleStopRecording()
        {
            var bundle = _recorder.Stop();
            _bundleSerializer.Save(bundle, _bundlePath);
            _state = ControllerState.Idle;
            _log.Info("Recording saved to {}", _bundlePath);
        }

        void HandleStopPlayback()
        {
            _playbackTickSubscription?.Dispose();
            _playbackTickSubscription = null;
            _player.Stop();
            _state = ControllerState.Idle;
            _log.Info("Playback stopped");
        }

        void HandleStartPlayback()
        {
            if (!File.Exists(_bundlePath))
            {
                _log.Warning("No recording found at {}", _bundlePath);
                return;
            }

            var bundle = _bundleSerializer.Load(_bundlePath);
            _player.Start(bundle);
            // Tick the player after each fixed update so per-frame checksum
            // mismatches surface as desyncs. Without this, BundlePlayer.Tick
            // is never called and HasDesynced never flips.
            _playbackTickSubscription = _world.Events.OnFixedUpdateCompleted(
                () => _player.Tick()
            );
            _state = ControllerState.Playback;
            _log.Info(
                "Playback started from frame {} to {}",
                bundle.Header.StartFixedFrame,
                bundle.Header.EndFixedFrame
            );
        }

        void HandleSaveSnapshot()
        {
            _snapshotSerializer.SaveSnapshot(version: 1, filePath: _snapshotPath);
            _log.Info("Snapshot saved at frame {}", _world.FixedFrame);
        }

        void HandleLoadSnapshot()
        {
            if (_state == ControllerState.Recording)
            {
                _log.Warning("Cannot load snapshot while recording");
                return;
            }

            if (!File.Exists(_snapshotPath))
            {
                _log.Warning("No snapshot found at {}", _snapshotPath);
                return;
            }

            _snapshotSerializer.LoadSnapshot(_snapshotPath);
            _log.Info("Snapshot loaded, now at frame {}", _world.FixedFrame);
        }

        public void Dispose()
        {
            _playbackTickSubscription?.Dispose();
            _playbackTickSubscription = null;
        }

        public enum ControllerState
        {
            Idle,
            Recording,
            Playback,
        }
    }
}
