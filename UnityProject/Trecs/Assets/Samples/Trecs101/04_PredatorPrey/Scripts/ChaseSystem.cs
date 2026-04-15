using Unity.Mathematics;

namespace Trecs.Samples.PredatorPrey
{
    /// <summary>
    /// Each predator chases the nearest prey.
    /// Demonstrates cross-tag queries: reading prey positions
    /// while writing predator velocities.
    /// </summary>
    public partial class ChaseSystem : ISystem
    {
        public void Execute()
        {
            // Query prey positions from a different tag group
            var preyGroups = World.WorldInfo.GetGroupsWithTags<SampleTags.Prey>();

            if (preyGroups.Count == 0)
            {
                return;
            }

            var preyGroup = preyGroups[0];
            var preyPositions = World.ComponentBuffer<Position>(preyGroup).Read;
            var preyCount = World.CountEntitiesInGroup(preyGroup);

            if (preyCount == 0)
            {
                return;
            }

            // Iterate predators and steer each toward nearest prey
            var predatorGroups = World.WorldInfo.GetGroupsWithTags<SampleTags.Predator>();

            foreach (var predatorGroup in predatorGroups)
            {
                var predatorPositions = World.ComponentBuffer<Position>(predatorGroup).Read;
                var predatorVelocities = World.ComponentBuffer<Velocity>(predatorGroup).Write;
                var predatorSpeeds = World.ComponentBuffer<Speed>(predatorGroup).Read;
                var predatorCount = World.CountEntitiesInGroup(predatorGroup);

                for (int i = 0; i < predatorCount; i++)
                {
                    ref readonly var pos = ref predatorPositions[i];
                    ref readonly var speed = ref predatorSpeeds[i];

                    // Find nearest prey
                    float nearestDistSq = float.MaxValue;
                    float3 nearestPreyPos = float3.zero;

                    for (int p = 0; p < preyCount; p++)
                    {
                        float distSq = math.distancesq(pos.Value, preyPositions[p].Value);
                        if (distSq < nearestDistSq)
                        {
                            nearestDistSq = distSq;
                            nearestPreyPos = preyPositions[p].Value;
                        }
                    }

                    // Steer toward nearest prey
                    float3 delta = nearestPreyPos - pos.Value;
                    if (math.lengthsq(delta) > 0.001f)
                    {
                        ref var vel = ref predatorVelocities[i];
                        vel.Value = math.normalize(delta);
                    }
                }
            }
        }
    }
}
