using Unity.Mathematics;
using UnityEngine;

namespace Trecs.Samples.Aspects
{
    [VariableUpdate]
    public partial class BoidRendererSystem : ISystem
    {
        readonly GameObjectRegistry _gameObjectRegistry;

        public BoidRendererSystem(GameObjectRegistry gameObjectRegistry)
        {
            _gameObjectRegistry = gameObjectRegistry;
        }

        // One way to iterate over aspects is via a method marked with ForEachEntity attribute
        // If this method is also called Execute then this becomes entry point for System
        // We can then specify tags or MatchByComponents
        [ForEachEntity(Tag = typeof(SampleTags.Boid))]
        void Execute(in Boid boid)
        {
            var go = _gameObjectRegistry.Resolve(boid.GameObjectId);
            go.transform.position = (Vector3)boid.Position;

            if (math.lengthsq(boid.Velocity) > 0.001f)
            {
                go.transform.rotation = Quaternion.LookRotation((Vector3)boid.Velocity);
            }
        }

        partial struct Boid : IAspect, IRead<GameObjectId, Position, Velocity> { }
    }
}
