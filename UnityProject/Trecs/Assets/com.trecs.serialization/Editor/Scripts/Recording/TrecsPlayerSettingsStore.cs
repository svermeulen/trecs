using System.Globalization;
using UnityEditor;

namespace Trecs.Serialization.Internal
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
    [InitializeOnLoad]
    static class TrecsPlayerSettingsStore
    {
        const string AnchorIntervalKey = "Trecs.RecorderSettings.AnchorIntervalSeconds";
        const string ScrubCacheIntervalKey = "Trecs.RecorderSettings.ScrubCacheIntervalSeconds";
        const string MaxAnchorCountKey = "Trecs.RecorderSettings.MaxAnchorCount";

        // long → stored as string since EditorPrefs only exposes int. Default
        // 64 MB fits in int but users may push past 2 GB on big projects.
        const string MaxScrubCacheBytesKey = "Trecs.RecorderSettings.MaxScrubCacheBytes";

        // Keep these in sync with TrecsAutoRecorderSettings's field defaults
        // — used only when the EditorPref is absent (first run / fresh user).
        const float DefaultAnchorIntervalSeconds = 30f;
        const float DefaultScrubCacheIntervalSeconds = 1f;
        const int DefaultMaxAnchorCount = 0;
        const long DefaultMaxScrubCacheBytes = 64L * 1024 * 1024;

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

        public static float AnchorIntervalSeconds =>
            EditorPrefs.GetFloat(AnchorIntervalKey, DefaultAnchorIntervalSeconds);

        public static float ScrubCacheIntervalSeconds =>
            EditorPrefs.GetFloat(ScrubCacheIntervalKey, DefaultScrubCacheIntervalSeconds);

        public static int MaxAnchorCount =>
            EditorPrefs.GetInt(MaxAnchorCountKey, DefaultMaxAnchorCount);

        public static long MaxScrubCacheBytes =>
            long.TryParse(
                EditorPrefs.GetString(
                    MaxScrubCacheBytesKey,
                    DefaultMaxScrubCacheBytes.ToString(CultureInfo.InvariantCulture)
                ),
                NumberStyles.Integer,
                CultureInfo.InvariantCulture,
                out var v
            )
                ? v
                : DefaultMaxScrubCacheBytes;

        public static void Save(
            float anchorIntervalSeconds,
            float scrubCacheIntervalSeconds,
            int maxAnchorCount,
            long maxScrubCacheBytes
        )
        {
            EditorPrefs.SetFloat(AnchorIntervalKey, anchorIntervalSeconds);
            EditorPrefs.SetFloat(ScrubCacheIntervalKey, scrubCacheIntervalSeconds);
            EditorPrefs.SetInt(MaxAnchorCountKey, maxAnchorCount);
            EditorPrefs.SetString(
                MaxScrubCacheBytesKey,
                maxScrubCacheBytes.ToString(CultureInfo.InvariantCulture)
            );
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
            recorder.AnchorIntervalSeconds = AnchorIntervalSeconds;
            recorder.ScrubCacheIntervalSeconds = ScrubCacheIntervalSeconds;
            recorder.MaxAnchorCount = MaxAnchorCount;
            recorder.MaxScrubCacheBytes = MaxScrubCacheBytes;
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
