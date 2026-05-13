using UnityEditor;

namespace Trecs.Serialization.Internal
{
    /// <summary>
    /// Wires Trecs game-state recording to the time travel window's open
    /// state. When a <see cref="TrecsGameStateController"/> registers itself
    /// (typically at scene startup, after the World is built), if a
    /// <see cref="TrecsPlayerWindow"/> instance is currently open and
    /// <see cref="AutoRecordEnabled"/> is true, we ask the controller to enter
    /// <see cref="GameStateMode.Recording"/>.
    /// </summary>
    [InitializeOnLoad]
    static class TrecsGameStateActivator
    {
        const string AutoRecordEnabledPrefKey = "Trecs.TimeTravelWindow.AutoRecordEnabled";

        static TrecsGameStateActivator()
        {
            TrecsGameStateRegistry.ControllerRegistered += OnControllerRegistered;
        }

        /// <summary>
        /// User toggle (persisted via EditorPrefs) for whether the time travel
        /// window auto-starts recording when it's open and a controller
        /// becomes available. When false, the window can stay in the user's
        /// layout without recording; flipping this back on starts capture on
        /// demand. Defaults to true to preserve existing behaviour.
        /// </summary>
        public static bool AutoRecordEnabled
        {
            get => EditorPrefs.GetBool(AutoRecordEnabledPrefKey, true);
            set => EditorPrefs.SetBool(AutoRecordEnabledPrefKey, value);
        }

        static void OnControllerRegistered(TrecsGameStateController controller)
        {
            if (HasOpenWindow() && AutoRecordEnabled)
            {
                controller.StartAutoRecording();
            }
        }

        /// <summary>
        /// Called by the timeline window when it opens during Play mode so a
        /// late-opened window doesn't have to wait for the next world-register
        /// event to start capturing. No-op when <see cref="AutoRecordEnabled"/>
        /// is false.
        /// </summary>
        public static void StartAllIdleControllers()
        {
            if (!AutoRecordEnabled)
            {
                return;
            }
            foreach (var controller in TrecsGameStateRegistry.All)
            {
                if (controller.CurrentMode == GameStateMode.Idle)
                {
                    controller.StartAutoRecording();
                }
            }
        }

        /// <summary>
        /// Stop active recordings on every registered controller. Called when
        /// the user toggles auto-record off — keeps the window open in their
        /// layout but immediately halts capture so the recorder isn't quietly
        /// buffering in the background.
        /// </summary>
        public static void StopAllRecordingControllers()
        {
            foreach (var controller in TrecsGameStateRegistry.All)
            {
                if (controller.CurrentMode != GameStateMode.Idle)
                {
                    controller.StopAutoRecording();
                }
            }
        }

        static bool HasOpenWindow()
        {
            return EditorWindow.HasOpenInstances<TrecsPlayerWindow>();
        }
    }
}
