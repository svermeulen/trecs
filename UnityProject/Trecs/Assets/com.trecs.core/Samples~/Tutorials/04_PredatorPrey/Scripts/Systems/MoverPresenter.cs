using Unity.Mathematics;
using UnityEngine;

namespace Trecs.Samples.PredatorPrey
{
    [ExecuteIn(SystemPhase.Presentation)]
    public partial class MoverPresenter : ISystem
    {
        readonly RenderableGameObjectManager _goManager;

        public MoverPresenter(RenderableGameObjectManager goManager)
        {
            _goManager = goManager;
        }

        [ForEachEntity(typeof(SampleTags.Movable))]
        void Execute(in Mover mover)
        {
            var go = _goManager.Resolve(mover.GameObjectId);
            go.transform.position = (Vector3)mover.Position;

            if (math.lengthsq(mover.MoveDirection) > 0.001f)
            {
                var desiredRotation = Quaternion.LookRotation((Vector3)mover.MoveDirection);

                go.transform.rotation = Quaternion.Lerp(
                    go.transform.rotation,
                    desiredRotation,
                    World.DeltaTime * 10f
                );
            }
        }

        partial struct Mover : IAspect, IRead<GameObjectId, Position, MoveDirection> { }
    }
}
