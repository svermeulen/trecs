using System;
using System.Collections.Generic;

namespace Trecs
{
    /// <summary>
    /// Tracks every <see cref="World"/> instance that is currently alive (between its
    /// constructor and <see cref="World.Dispose()"/>). Intended for editor tooling that
    /// needs to discover active worlds without going through application-side DI.
    /// <para>
    /// Main-thread-only, like <see cref="World"/> itself.
    /// </para>
    /// </summary>
    public static class WorldRegistry
    {
        static readonly List<World> _activeWorlds = new();

        public static IReadOnlyList<World> ActiveWorlds => _activeWorlds;

        public static event Action<World> WorldRegistered;
        public static event Action<World> WorldUnregistered;

        internal static void Register(World world)
        {
            _activeWorlds.Add(world);
            WorldRegistered?.Invoke(world);
        }

        internal static void Unregister(World world)
        {
            if (_activeWorlds.Remove(world))
            {
                WorldUnregistered?.Invoke(world);
            }
        }
    }
}
