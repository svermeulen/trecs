using UnityEngine;

namespace Trecs.Samples.Interpolation
{
    /// <summary>
    /// Variable-update renderer: demonstrates the key difference between
    /// interpolated and raw position reading.
    ///
    /// Smooth entities: reads Interpolated&lt;Position&gt; — the value is
    /// blended between previous and current fixed-frame positions based on
    /// how far through the current fixed timestep we are. Result: fluid motion.
    ///
    /// Raw entities: reads Position directly — the value only changes at
    /// fixed-update boundaries (10 Hz). Between fixed frames, the position
    /// stays constant, causing visible stutter when rendering at 60+ fps.
    /// </summary>
    [VariableUpdate]
    public partial class OrbitRendererSystem : ISystem
    {
        readonly GameObjectRegistry _registry;

        public OrbitRendererSystem(GameObjectRegistry registry)
        {
            _registry = registry;
        }

        public void Execute()
        {
            RenderSmooth();
            RenderRaw();
        }

        void RenderSmooth()
        {
            // Interpolated<Position> is only present on entities whose
            // template marked Position with [Interpolated].
            foreach (
                var group in World.WorldInfo.GetGroupsWithComponents<
                    Interpolated<Position>,
                    GameObjectId
                >()
            )
            {
                var positions = World.ComponentBuffer<Interpolated<Position>>(group).Read;
                var goIds = World.ComponentBuffer<GameObjectId>(group).Read;
                int count = World.CountEntitiesInGroup(group);

                for (int i = 0; i < count; i++)
                {
                    var go = _registry.Resolve(goIds[i]);
                    // Interpolated<Position>.Value → Position, .Value → float3
                    go.transform.position = (Vector3)positions[i].Value.Value;
                }
            }
        }

        void RenderRaw()
        {
            // Raw entities only have Position (no Interpolated wrapper).
            // Reading Position in variable update shows the fixed-frame
            // position, which "jumps" at each fixed timestep boundary.
            foreach (var group in World.WorldInfo.GetGroupsWithTags<OrbitTags.Raw>())
            {
                var positions = World.ComponentBuffer<Position>(group).Read;
                var goIds = World.ComponentBuffer<GameObjectId>(group).Read;
                int count = World.CountEntitiesInGroup(group);

                for (int i = 0; i < count; i++)
                {
                    var go = _registry.Resolve(goIds[i]);
                    go.transform.position = (Vector3)positions[i].Value;
                }
            }
        }
    }
}
