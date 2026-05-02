using System;

namespace Trecs
{
    /// <summary>
    /// Editor-side shared state for "which World is the user currently
    /// debugging?". The Trecs editor windows (Time Travel, Systems, Entities)
    /// all read and write this so selecting a world in one window switches
    /// the others to match. Mirrors the role <c>UnityEditor.Selection</c>
    /// plays for GameObjects.
    /// </summary>
    public static class TrecsEditorSelection
    {
        static World _activeWorld;

        public static World ActiveWorld
        {
            get => _activeWorld;
            set
            {
                if (_activeWorld == value)
                {
                    return;
                }
                _activeWorld = value;
                ActiveWorldChanged?.Invoke(value);
            }
        }

        public static event Action<World> ActiveWorldChanged;
    }
}
