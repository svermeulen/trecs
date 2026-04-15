using Unity.Mathematics;
using UnityEngine;

namespace Trecs.Samples.PredatorPrey
{
    /// <summary>
    /// Removes prey entities when a predator gets close enough.
    /// Demonstrates RemoveEntity with cross-tag distance checks.
    /// </summary>
    [ExecutesAfter(typeof(MovementSystem))]
    public partial class CatchSystem : ISystem
    {
        const float CatchRadius = 0.8f;

        readonly GameObjectRegistry _gameObjectRegistry;

        public CatchSystem(GameObjectRegistry gameObjectRegistry)
        {
            _gameObjectRegistry = gameObjectRegistry;
        }

        public void Execute()
        {
            var predatorGroups = World.WorldInfo.GetGroupsWithTags<SampleTags.Predator>();
            var preyGroups = World.WorldInfo.GetGroupsWithTags<SampleTags.Prey>();

            if (predatorGroups.Count == 0 || preyGroups.Count == 0)
            {
                return;
            }

            var preyGroup = preyGroups[0];
            var preyPositions = World.ComponentBuffer<Position>(preyGroup).Read;
            var preyGameObjectIds = World.ComponentBuffer<GameObjectId>(preyGroup).Read;
            var preyCount = World.CountEntitiesInGroup(preyGroup);

            foreach (var predatorGroup in predatorGroups)
            {
                var predatorPositions = World.ComponentBuffer<Position>(predatorGroup).Read;
                var predatorCount = World.CountEntitiesInGroup(predatorGroup);

                for (int pi = 0; pi < predatorCount; pi++)
                {
                    ref readonly var predPos = ref predatorPositions[pi];

                    // Check against all prey (iterate backwards since we remove)
                    for (int qi = preyCount - 1; qi >= 0; qi--)
                    {
                        float distSq = math.distancesq(predPos.Value, preyPositions[qi].Value);

                        if (distSq < CatchRadius * CatchRadius)
                        {
                            var goId = preyGameObjectIds[qi];
                            var go = _gameObjectRegistry.Resolve(goId);
                            Object.Destroy(go);
                            _gameObjectRegistry.Unregister(goId);

                            var preyEntityIndex = new EntityIndex(qi, preyGroup);
                            World.RemoveEntity(preyEntityIndex);
                            preyCount--;
                            break;
                        }
                    }
                }
            }
        }
    }
}
