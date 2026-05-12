using System.Collections.Generic;
using Trecs.Internal;

namespace Trecs.Serialization
{
    /// <summary>
    /// Static per-<see cref="World"/> lookup for <see cref="SerializerRegistry"/>
    /// instances. Populated automatically by
    /// <see cref="TrecsSerialization.CreateSerializerRegistry"/> and consumed by
    /// editor tooling (e.g. the Trecs Player window) that needs to discover the
    /// registry for a given world without going through application-side DI.
    ///
    /// Entries are evicted automatically when the world is disposed (via
    /// <see cref="WorldRegistry.WorldUnregistered"/>).
    /// </summary>
    public static class TrecsSerializerRegistries
    {
        static readonly Dictionary<World, SerializerRegistry> _byWorld = new();

        static TrecsSerializerRegistries()
        {
            WorldRegistry.WorldUnregistered += world => _byWorld.Remove(world);
        }

        /// <summary>
        /// Associate <paramref name="registry"/> with <paramref name="world"/>.
        /// Called by <see cref="TrecsSerialization.CreateSerializerRegistry"/>
        /// so users don't have to register manually. Asserts on duplicate
        /// registration — silently replacing a registry mid-life would let
        /// downstream serializers diverge from upstream callers' expectations.
        /// </summary>
        internal static void Set(World world, SerializerRegistry registry)
        {
            Assert.That(world != null);
            Assert.That(registry != null);
            Assert.That(
                !_byWorld.ContainsKey(world),
                "A SerializerRegistry is already associated with this World"
            );
            _byWorld[world] = registry;
        }

        public static bool TryGet(World world, out SerializerRegistry registry)
        {
            return _byWorld.TryGetValue(world, out registry);
        }

        /// <summary>
        /// Return the registry registered for <paramref name="world"/>, or
        /// lazily create a default one (containing only built-in Trecs
        /// serializers) and associate it. Used by editor tooling that needs
        /// *some* registry to function — for all-blittable games this is
        /// enough; games with custom serializers should create their registry
        /// via <see cref="TrecsSerialization.CreateSerializerRegistry"/>
        /// before opening editor windows so this fallback never fires.
        /// </summary>
        public static SerializerRegistry GetOrCreateDefault(World world)
        {
            if (_byWorld.TryGetValue(world, out var existing))
            {
                return existing;
            }
            return TrecsSerialization.CreateSerializerRegistry(world);
        }
    }
}
