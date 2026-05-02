using UnityEditor;

namespace Trecs.Serialization
{
    /// <summary>
    /// Wires Trecs game-state recording to the time travel window's open
    /// state. When a <see cref="TrecsGameStateController"/> registers itself
    /// (typically at scene startup, after the World is built), if a
    /// <see cref="TrecsTimeTravelWindow"/> instance is currently open we ask
    /// the controller to enter <see cref="GameStateMode.Recording"/>.
    /// </summary>
    [InitializeOnLoad]
    static class TrecsGameStateActivator
    {
        static TrecsGameStateActivator()
        {
            TrecsGameStateRegistry.ControllerRegistered += OnControllerRegistered;
        }

        static void OnControllerRegistered(TrecsGameStateController controller)
        {
            if (HasOpenWindow())
            {
                controller.StartAutoRecording();
            }
        }

        /// <summary>
        /// Called by the timeline window when it opens during Play mode so a
        /// late-opened window doesn't have to wait for the next world-register
        /// event to start capturing.
        /// </summary>
        public static void StartAllIdleControllers()
        {
            foreach (var controller in TrecsGameStateRegistry.All)
            {
                if (controller.CurrentMode == GameStateMode.Idle)
                {
                    controller.StartAutoRecording();
                }
            }
        }

        static bool HasOpenWindow()
        {
            return EditorWindow.HasOpenInstances<TrecsTimeTravelWindow>();
        }
    }
}
