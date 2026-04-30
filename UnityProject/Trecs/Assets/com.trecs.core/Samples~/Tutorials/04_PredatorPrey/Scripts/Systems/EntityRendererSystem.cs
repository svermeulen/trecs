using Unity.Mathematics;
using UnityEngine;

namespace Trecs.Samples.PredatorPrey
{
    [Phase(SystemPhase.Presentation)]
    public partial class EntityRendererSystem : ISystem
    {
        readonly GameObjectRegistry _gameObjectRegistry;

        public EntityRendererSystem(GameObjectRegistry gameObjectRegistry)
        {
            _gameObjectRegistry = gameObjectRegistry;
        }

        [ForEachEntity(Tags = new[] { typeof(SampleTags.Movable) })]
        void Execute(in Mover mover)
        {
            var go = _gameObjectRegistry.Resolve(mover.GameObjectId);
            go.transform.position = (Vector3)mover.Position;

            if (math.lengthsq(mover.MoveDirection) > 0.001f)
            {
                var desiredRotation = Quaternion.LookRotation((Vector3)mover.MoveDirection);

                go.transform.rotation = Quaternion.Lerp(
                    go.transform.rotation,
                    desiredRotation,
                    Time.deltaTime * 10f
                );
            }
        }

        partial struct Mover : IAspect, IRead<GameObjectId, Position, MoveDirection> { }
    }
}
