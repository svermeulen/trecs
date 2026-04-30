using System;
using System.Collections.Generic;

namespace Trecs.Tools
{
    /// <summary>
    /// Tracks every <see cref="TrecsGameStateController"/> currently alive (one
    /// per Trecs <see cref="World"/>). Mirrors <see cref="WorldRegistry"/> in
    /// shape — editor tooling subscribes to register/unregister events and
    /// looks up the controller for a chosen World.
    /// </summary>
    public static class TrecsGameStateRegistry
    {
        static readonly List<TrecsGameStateController> _all = new();

        public static IReadOnlyList<TrecsGameStateController> All => _all;

        public static event Action<TrecsGameStateController> ControllerRegistered;
        public static event Action<TrecsGameStateController> ControllerUnregistered;

        public static TrecsGameStateController GetForWorld(World world)
        {
            for (int i = 0; i < _all.Count; i++)
            {
                if (_all[i].World == world)
                {
                    return _all[i];
                }
            }
            return null;
        }

        internal static void Register(TrecsGameStateController controller)
        {
            _all.Add(controller);
            ControllerRegistered?.Invoke(controller);
        }

        internal static void Unregister(TrecsGameStateController controller)
        {
            if (_all.Remove(controller))
            {
                ControllerUnregistered?.Invoke(controller);
            }
        }
    }
}
