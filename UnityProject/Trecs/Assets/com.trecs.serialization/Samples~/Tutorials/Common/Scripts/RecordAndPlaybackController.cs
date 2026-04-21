using System;
using System.IO;
using Trecs.Internal;
using UnityEngine;

namespace Trecs.Serialization.Samples
{
    /// <summary>
    /// Sample-side helper that wires Unity keyboard input to the Trecs
    /// recording, playback, and bookmark serialization APIs.
    ///
    /// Keys: F5=Toggle Record, F6=Toggle Playback, F8=Save Bookmark, F9=Load Bookmark.
    ///
    /// Recordings and bookmarks are written under
    /// <c>{Application.persistentDataPath}/{sampleName}/Recordings/</c>.
    /// </summary>
    public class RecordAndPlaybackController : IDisposable
    {
        static readonly TrecsLog _log = new(nameof(RecordAndPlaybackController));

        readonly RecordingHandler _recordingHandler;
        readonly PlaybackHandler _playbackHandler;
        readonly BookmarkSerializer _bookmarkSerializer;
        readonly WorldAccessor _world;
        readonly int _serializationVersion;
        readonly string _recordingPath;
        readonly string _bookmarkPath;

        ControllerState _state;

        public RecordAndPlaybackController(
            SerializationServices serialization,
            World world,
            string sampleName,
            int serializationVersion = 1
        )
        {
            Assert.That(!string.IsNullOrEmpty(sampleName));

            _recordingHandler = serialization.Recorder;
            _playbackHandler = serialization.Playback;
            _bookmarkSerializer = serialization.Bookmarks;
            _world = world.CreateAccessor(nameof(RecordAndPlaybackController));
            _serializationVersion = serializationVersion;

            var recordingDir = Path.Combine(
                Application.persistentDataPath,
                sampleName,
                "Recordings"
            );
            _recordingPath = Path.Combine(recordingDir, "recording.bin");
            _bookmarkPath = Path.Combine(recordingDir, "bookmark.bin");

            Directory.CreateDirectory(recordingDir);

            _log.Info(
                "Recording controller ready. Keys: F5=Toggle Record, F6=Toggle Playback, F8=Save Bookmark, F9=Load Bookmark"
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
                HandleSaveBookmark();
            }
            else if (Input.GetKeyDown(KeyCode.F9))
            {
                HandleLoadBookmark();
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

            _recordingHandler.StartRecording(
                version: _serializationVersion,
                checksumsEnabled: true,
                checksumFrameInterval: 30
            );

            // Save initial state bookmark so playback can start from the correct point
            _bookmarkSerializer.SaveBookmark(
                version: _serializationVersion,
                filePath: _bookmarkPath
            );

            _state = ControllerState.Recording;
            _log.Info("Recording started");
        }

        void HandleStopRecording()
        {
            _recordingHandler.EndRecording(_recordingPath);
            _state = ControllerState.Idle;
            _log.Info("Recording saved to {}", _recordingPath);
        }

        void HandleStopPlayback()
        {
            _playbackHandler.EndPlayback();
            _state = ControllerState.Idle;
            _log.Info("Playback stopped");
        }

        void HandleStartPlayback()
        {
            if (!File.Exists(_recordingPath))
            {
                _log.Warning("No recording found at {}", _recordingPath);
                return;
            }

            _playbackHandler.StartPlayback(
                _recordingPath,
                new PlaybackStartParams { InputsOnly = false, Version = _serializationVersion }
            );

            if (File.Exists(_bookmarkPath))
            {
                _playbackHandler.LoadInitialState(_bookmarkPath, expectedInitialChecksum: null);
                _log.Info("Loaded initial state from bookmark");
            }
            else
            {
                _log.Warning(
                    "No initial state bookmark found, starting playback from current state"
                );
            }

            _state = ControllerState.Playback;
            _log.Info(
                "Playback started from frame {} to {}",
                _playbackHandler.PlaybackMetadata.StartFixedFrame,
                _playbackHandler.PlaybackMetadata.EndFixedFrame
            );
        }

        void HandleSaveBookmark()
        {
            _bookmarkSerializer.SaveBookmark(
                version: _serializationVersion,
                filePath: _bookmarkPath
            );

            _log.Info("Bookmark saved at frame {}", _world.FixedFrame);
        }

        void HandleLoadBookmark()
        {
            if (_state == ControllerState.Recording)
            {
                _log.Warning("Cannot load bookmark while recording");
                return;
            }

            if (!File.Exists(_bookmarkPath))
            {
                _log.Warning("No bookmark found at {}", _bookmarkPath);
                return;
            }

            _bookmarkSerializer.LoadBookmark(_bookmarkPath);
            _log.Info("Bookmark loaded, now at frame {}", _world.FixedFrame);
        }

        public void Dispose()
        {
            // Handlers' Dispose now gracefully stops mid-op work; nothing extra to do here.
        }

        public enum ControllerState
        {
            Idle,
            Recording,
            Playback,
        }
    }
}
