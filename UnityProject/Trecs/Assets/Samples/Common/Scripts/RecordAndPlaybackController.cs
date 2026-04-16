using System;
using System.IO;
using Trecs.Internal;
using Trecs.Serialization;
using UnityEngine;

namespace Trecs.Samples
{
    /// <summary>
    /// Sample-side helper that wires Unity keyboard input to the Trecs
    /// recording, playback, and bookmark serialization APIs.
    ///
    /// Keys: F5=Record, F6=Stop, F7=Playback, F8=Save Bookmark, F9=Load Bookmark.
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
        readonly IGameStateSerializer _gameStateSerializer;
        readonly SerializationBuffer _serializerHelper;
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

            _recordingHandler = serialization.RecordingHandler;
            _playbackHandler = serialization.PlaybackHandler;
            _bookmarkSerializer = serialization.BookmarkSerializer;
            _gameStateSerializer = serialization.GameStateSerializer;
            _serializerHelper = new SerializationBuffer(serialization.Registry);
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
                "Recording controller ready. Keys: F5=Record, F6=Stop, F7=Playback, F8=Save Bookmark, F9=Load Bookmark"
            );
        }

        public ControllerState State => _state;

        public void Tick()
        {
            if (Input.GetKeyDown(KeyCode.F5))
            {
                HandleStartRecording();
            }
            else if (Input.GetKeyDown(KeyCode.F6))
            {
                HandleStop();
            }
            else if (Input.GetKeyDown(KeyCode.F7))
            {
                HandleStartPlayback();
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

        void HandleStartRecording()
        {
            if (_state != ControllerState.Idle)
            {
                _log.Warning("Cannot start recording: currently in {} state", _state);
                return;
            }

            _log.Info("Starting recording at frame {}", _world.FixedFrame);

            _recordingHandler.StartRecording(
                version: _serializationVersion,
                checksumsEnabled: true,
                checksumFrameInterval: 30
            );

            // Save initial state bookmark
            _bookmarkSerializer.Save(version: _serializationVersion, includeTypeChecks: true);

            _bookmarkSerializer.SerializerHelper.MemoryStream.Position = 0;
            SaveMemoryStreamToFile(_bookmarkSerializer.SerializerHelper, _bookmarkPath);

            _state = ControllerState.Recording;
            _log.Info("Recording started");
        }

        void HandleStop()
        {
            if (_state == ControllerState.Recording)
            {
                _recordingHandler.EndRecording(_serializerHelper);
                SaveMemoryStreamToFile(_serializerHelper, _recordingPath);

                _state = ControllerState.Idle;
                _log.Info("Recording saved to {}", _recordingPath);
            }
            else if (_state == ControllerState.Playback)
            {
                _playbackHandler.EndPlayback();

                _state = ControllerState.Idle;
                _log.Info("Playback stopped");
            }
            else
            {
                _log.Warning("Nothing to stop");
            }
        }

        void HandleStartPlayback()
        {
            if (_state != ControllerState.Idle)
            {
                _log.Warning("Cannot start playback: currently in {} state", _state);
                return;
            }

            if (!File.Exists(_recordingPath))
            {
                _log.Warning("No recording found at {}", _recordingPath);
                return;
            }

            // Load initial state bookmark if it exists
            if (File.Exists(_bookmarkPath))
            {
                ReadFileIntoMemoryStream(_serializerHelper, _bookmarkPath);

                if (
                    !_playbackHandler.LoadInitialState(
                        _serializerHelper,
                        expectedInitialChecksum: null,
                        version: _serializationVersion
                    )
                )
                {
                    _log.Error("Failed to load initial state bookmark");
                    return;
                }

                _log.Info("Loaded initial state from bookmark");
            }
            else
            {
                _log.Warning(
                    "No initial state bookmark found, starting playback from current state"
                );
            }

            // Load recording
            ReadFileIntoMemoryStream(_serializerHelper, _recordingPath);

            _playbackHandler.StartPlayback(
                new PlaybackStartParams
                {
                    SerializerHelper = _serializerHelper,
                    SerializationFlags = _gameStateSerializer.SerializationFlags,
                    InputsOnly = false,
                    Version = _serializationVersion,
                }
            );

            _state = ControllerState.Playback;
            _log.Info(
                "Playback started from frame {} to {}",
                _playbackHandler.PlaybackMetadata.StartFixedFrame,
                _playbackHandler.PlaybackMetadata.EndFixedFrame
            );
        }

        void HandleSaveBookmark()
        {
            _bookmarkSerializer.Save(version: _serializationVersion, includeTypeChecks: true);

            _bookmarkSerializer.SerializerHelper.MemoryStream.Position = 0;
            SaveMemoryStreamToFile(_bookmarkSerializer.SerializerHelper, _bookmarkPath);

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

            ReadFileIntoMemoryStream(_serializerHelper, _bookmarkPath);

            var succeeded = _bookmarkSerializer.Load(_serializerHelper);
            Assert.That(succeeded, "Failed to load bookmark");

            _log.Info("Bookmark loaded, now at frame {}", _world.FixedFrame);
        }

        static void ReadFileIntoMemoryStream(SerializationBuffer helper, string filePath)
        {
            var memoryStream = helper.MemoryStream;
            memoryStream.Position = 0;
            memoryStream.SetLength(0);

            using var fileStream = File.OpenRead(filePath);
            fileStream.CopyTo(memoryStream);

            memoryStream.Position = 0;
        }

        static void SaveMemoryStreamToFile(SerializationBuffer helper, string filePath)
        {
            var memoryStream = helper.MemoryStream;
            memoryStream.Position = 0;

            using var fileStream = File.Create(filePath);
            memoryStream.CopyTo(fileStream);
        }

        public void Dispose()
        {
            if (_state == ControllerState.Playback)
            {
                _playbackHandler.EndPlayback();
            }
            else if (_state == ControllerState.Recording)
            {
                _recordingHandler.EndRecording(_serializerHelper);
                // Recording data is discarded — user closed play mode mid-recording
            }

            _serializerHelper.Dispose();
        }

        public enum ControllerState
        {
            Idle,
            Recording,
            Playback,
        }
    }
}
