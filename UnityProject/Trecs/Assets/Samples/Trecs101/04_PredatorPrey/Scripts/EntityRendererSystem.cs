using Unity.Mathematics;
using UnityEngine;

namespace Trecs.Samples.PredatorPrey
{
    [VariableUpdate]
    public partial class EntityRendererSystem : ISystem
    {
        readonly GameObjectRegistry _gameObjectRegistry;

        public EntityRendererSystem(GameObjectRegistry gameObjectRegistry)
        {
            _gameObjectRegistry = gameObjectRegistry;
        }

        [ForEachEntity(Tags = new[] { typeof(SampleTags.Predator) })]
        void UpdatePredators(in PredatorView predator)
        {
            var go = _gameObjectRegistry.Resolve(new GameObjectId(predator.GameObjectId));
            go.transform.position = (Vector3)predator.Position;

            if (math.lengthsq(predator.Velocity) > 0.001f)
            {
                go.transform.rotation = Quaternion.LookRotation((Vector3)predator.Velocity);
            }
        }

        [ForEachEntity(Tags = new[] { typeof(SampleTags.Prey) })]
        void UpdatePrey(in PreyView prey)
        {
            var go = _gameObjectRegistry.Resolve(new GameObjectId(prey.GameObjectId));
            go.transform.position = (Vector3)prey.Position;
        }

        public void Execute()
        {
            UpdatePredators();
            UpdatePrey();
        }

        partial struct PredatorView : IAspect, IRead<GameObjectId, Position, Velocity> { }

        partial struct PreyView : IAspect, IRead<GameObjectId, Position> { }
    }
}
