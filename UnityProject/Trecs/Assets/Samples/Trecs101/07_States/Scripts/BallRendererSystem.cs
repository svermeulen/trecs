using UnityEngine;

namespace Trecs.Samples.States
{
    /// <summary>
    /// Syncs ball positions to GameObjects and tints active balls differently
    /// from resting ones. Uses two aspects with different tag constraints
    /// to iterate each state separately.
    /// </summary>
    [VariableUpdate]
    [ExecutesAfter(typeof(WakeUpSystem))]
    public partial class BallRendererSystem : ISystem
    {
        readonly GameObjectRegistry _registry;

        public BallRendererSystem(GameObjectRegistry registry)
        {
            _registry = registry;
        }

        [ForEachEntity(Tags = new[] { typeof(BallTags.Ball), typeof(BallTags.Active) })]
        void RenderActive(in ActiveBallView ball)
        {
            var go = _registry.Resolve(new GameObjectId(ball.GameObjectId));
            go.transform.position = (Vector3)ball.Position;
            go.GetComponent<Renderer>().material.color = Color.Lerp(Color.yellow, Color.red, 0.5f);
        }

        [ForEachEntity(Tags = new[] { typeof(BallTags.Ball), typeof(BallTags.Resting) })]
        void RenderResting(in RestingBallView ball)
        {
            var go = _registry.Resolve(new GameObjectId(ball.GameObjectId));
            go.transform.position = (Vector3)ball.Position;
            go.GetComponent<Renderer>().material.color = Color.gray;
        }

        public void Execute()
        {
            RenderActive();
            RenderResting();
        }

        partial struct ActiveBallView : IAspect, IRead<Position, GameObjectId> { }

        partial struct RestingBallView : IAspect, IRead<Position, GameObjectId> { }
    }
}
