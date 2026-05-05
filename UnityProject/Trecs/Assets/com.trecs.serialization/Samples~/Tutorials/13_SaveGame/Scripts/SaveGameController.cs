using System;
using System.Collections.Generic;
using System.IO;
using Trecs.Internal;
using UnityEngine;

namespace Trecs.Serialization.Samples.SaveGame
{
    /// <summary>
    /// Sample save-game controller that demonstrates the SnapshotSerializer
    /// file API in isolation from record/replay: three named slots that
    /// can be saved to and loaded from independently.
    ///
    /// Controls:
    ///   F1 / F2 / F3  — Save current state into slot 1 / 2 / 3.
    ///   F5 / F6 / F7  — Load state from slot 1 / 2 / 3 (if present).
    ///
    /// Save files live under {persistentDataPath}/SaveGame/slot{N}.bin.
    /// </summary>
    public class SaveGameController : IDisposable
    {
        static readonly TrecsLog _log = new(nameof(SaveGameController));

        const int SerializationVersion = 1;
        const int SlotCount = 3;

        readonly SnapshotSerializer _snapshots;
        readonly string _saveDir;
        readonly SlotInfo[] _slots;

        string _lastActionMessage;
        float _lastActionAt;

        public SaveGameController(SerializationServices serialization)
        {
            _snapshots = serialization.Snapshots;

            _saveDir = Path.Combine(Application.persistentDataPath, "SaveGame");
            Directory.CreateDirectory(_saveDir);

            _slots = new SlotInfo[SlotCount];
            for (int i = 0; i < SlotCount; i++)
            {
                _slots[i] = new SlotInfo(
                    index: i,
                    filePath: Path.Combine(_saveDir, $"slot{i + 1}.bin")
                );
                _slots[i].Refresh();
            }

            _log.Info(
                "Save-game controller ready. F1-F3 save into slots 1-3; F5-F7 load from slots 1-3. Dir: {}",
                _saveDir
            );
        }

        public IReadOnlyList<SlotInfo> Slots => _slots;
        public string LastActionMessage => _lastActionMessage;
        public float SecondsSinceLastAction =>
            _lastActionAt == 0f ? float.PositiveInfinity : Time.time - _lastActionAt;

        public void Tick()
        {
            if (Input.GetKeyDown(KeyCode.F1))
                Save(0);
            else if (Input.GetKeyDown(KeyCode.F2))
                Save(1);
            else if (Input.GetKeyDown(KeyCode.F3))
                Save(2);
            else if (Input.GetKeyDown(KeyCode.F5))
                Load(0);
            else if (Input.GetKeyDown(KeyCode.F6))
                Load(1);
            else if (Input.GetKeyDown(KeyCode.F7))
                Load(2);
        }

        void Save(int slotIndex)
        {
            var slot = _slots[slotIndex];
            try
            {
                var metadata = _snapshots.SaveSnapshot(
                    version: SerializationVersion,
                    filePath: slot.FilePath
                );
                slot.Refresh();
                SetMessage($"Saved slot {slotIndex + 1} (frame {metadata.FixedFrame})");
            }
            catch (Exception ex)
            {
                _log.Error("Failed to save slot {}: {}", slotIndex + 1, ex);
                SetMessage($"Save failed: {ex.Message}");
            }
        }

        void Load(int slotIndex)
        {
            var slot = _slots[slotIndex];
            if (!slot.Exists)
            {
                SetMessage($"Slot {slotIndex + 1} is empty");
                return;
            }
            try
            {
                var metadata = _snapshots.LoadSnapshot(slot.FilePath);
                SetMessage($"Loaded slot {slotIndex + 1} (frame {metadata.FixedFrame})");
            }
            catch (Exception ex)
            {
                _log.Error("Failed to load slot {}: {}", slotIndex + 1, ex);
                SetMessage($"Load failed: {ex.Message}");
            }
        }

        void SetMessage(string message)
        {
            _lastActionMessage = message;
            _lastActionAt = Time.time;
            _log.Info(message);
        }

        public void Dispose() { }

        public class SlotInfo
        {
            public readonly int Index;
            public readonly string FilePath;

            public SlotInfo(int index, string filePath)
            {
                Index = index;
                FilePath = filePath;
            }

            public bool Exists { get; private set; }
            public DateTime LastModifiedUtc { get; private set; }

            public void Refresh()
            {
                if (File.Exists(FilePath))
                {
                    Exists = true;
                    LastModifiedUtc = File.GetLastWriteTimeUtc(FilePath);
                }
                else
                {
                    Exists = false;
                    LastModifiedUtc = default;
                }
            }
        }
    }
}
