using System.Globalization;
using UnityEditor;

namespace Trecs.Serialization
{
    /// <summary>
    /// EditorPrefs-backed defaults for the auto-recorder. Decouples settings
    /// from the per-session <see cref="TrecsAutoRecorder"/> instance so users
    /// can tune values outside play mode and have them persist across editor
    /// sessions.
    ///
    /// Values are pushed onto each new recorder when its controller registers
    /// (subscribed via <see cref="TrecsGameStateRegistry.ControllerRegistered"/>).
    /// The <see cref="TrecsPlayerSettingsWindow"/> reads/writes these
    /// EditorPrefs and propagates Save to any live recorders so changes are
    /// instantly visible mid-session too.
    /// </summary>
    // EditorPrefs key prefix stays "Trecs.RecorderSettings.*" rather than
    // "Trecs.PlayerSettings.*" — the window was renamed Player Settings, but
    // changing the prefs prefix would silently wipe every existing user's
    // tuning.
    [InitializeOnLoad]
    static class TrecsPlayerSettingsStore
    {
        const string IntervalKey = "Trecs.RecorderSettings.SnapshotIntervalSeconds";
        const string MaxCountKey = "Trecs.RecorderSettings.MaxSnapshotCount";

        // long → stored as string since EditorPrefs only exposes int. Default
        // 256 MB fits in int but users may push past 2 GB on big projects.
        const string MaxMemoryKey = "Trecs.RecorderSettings.MaxSnapshotMemoryBytes";
        const string OverflowKey = "Trecs.RecorderSettings.OverflowAction";

        // Keep these in sync with TrecsAutoRecorderSettings's field defaults
        // — used only when the EditorPref is absent (first run / fresh user).
        const float DefaultIntervalSeconds = 0.5f;
        const int DefaultMaxCount = 0;
        const long DefaultMaxMemoryBytes = 256L * 1024 * 1024;
        const CapacityOverflowAction DefaultOverflowAction = CapacityOverflowAction.DropOldest;

        static TrecsPlayerSettingsStore()
        {
            // Patch each new recorder's settings as the controller registers,
            // before any recording starts. Idempotent and tiny — safe for
            // multiple play-mode entries.
            TrecsGameStateRegistry.ControllerRegistered += controller =>
            {
                ApplyTo(controller.AutoRecorder);
            };
        }

        public static float SnapshotIntervalSeconds =>
            EditorPrefs.GetFloat(IntervalKey, DefaultIntervalSeconds);

        public static int MaxSnapshotCount => EditorPrefs.GetInt(MaxCountKey, DefaultMaxCount);

        public static long MaxSnapshotMemoryBytes =>
            long.TryParse(
                EditorPrefs.GetString(
                    MaxMemoryKey,
                    DefaultMaxMemoryBytes.ToString(CultureInfo.InvariantCulture)
                ),
                NumberStyles.Integer,
                CultureInfo.InvariantCulture,
                out var v
            )
                ? v
                : DefaultMaxMemoryBytes;

        public static CapacityOverflowAction OverflowAction =>
            (CapacityOverflowAction)EditorPrefs.GetInt(OverflowKey, (int)DefaultOverflowAction);

        public static void Save(
            float intervalSeconds,
            int maxCount,
            long maxMemoryBytes,
            CapacityOverflowAction overflowAction
        )
        {
            EditorPrefs.SetFloat(IntervalKey, intervalSeconds);
            EditorPrefs.SetInt(MaxCountKey, maxCount);
            EditorPrefs.SetString(
                MaxMemoryKey,
                maxMemoryBytes.ToString(CultureInfo.InvariantCulture)
            );
            EditorPrefs.SetInt(OverflowKey, (int)overflowAction);
        }

        /// <summary>
        /// Apply the persisted defaults to <paramref name="recorder"/>.
        /// Called both when a new recorder registers (via the static ctor's
        /// hook) and after the user clicks Save in the settings window so
        /// live recorders pick up the change immediately.
        /// </summary>
        public static void ApplyTo(TrecsAutoRecorder recorder)
        {
            if (recorder == null)
                return;
            recorder.SnapshotIntervalSeconds = SnapshotIntervalSeconds;
            recorder.MaxSnapshotCount = MaxSnapshotCount;
            recorder.MaxSnapshotMemoryBytes = MaxSnapshotMemoryBytes;
            recorder.OverflowAction = OverflowAction;
        }

        /// <summary>
        /// Push the persisted defaults onto every currently registered
        /// recorder. Used by the settings window's Save so a tuning change
        /// mid-session reaches all live worlds.
        /// </summary>
        public static void ApplyToAllLiveRecorders()
        {
            foreach (var controller in TrecsGameStateRegistry.All)
            {
                ApplyTo(controller.AutoRecorder);
            }
        }
    }
}
