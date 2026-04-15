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

        [ForEachEntity(MatchByComponents = true)]
        void Execute(in Boid boid)
        {
            var go = _gameObjectRegistry.Resolve(new GameObjectId(boid.GameObjectId));
            go.transform.position = (Vector3)boid.Position;

            if (math.lengthsq(boid.Velocity) > 0.001f)
            {
                go.transform.rotation = Quaternion.LookRotation((Vector3)boid.Velocity);
            }
        }

        partial struct Boid : IAspect, IRead<GameObjectId, Position, Velocity> { }
    }
}
