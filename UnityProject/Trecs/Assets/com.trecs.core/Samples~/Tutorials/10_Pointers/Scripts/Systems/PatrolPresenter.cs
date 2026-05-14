using UnityEngine;

namespace Trecs.Samples.Pointers
{
    /// <summary>
    /// Variable-update renderer that syncs ECS state to GameObjects.
    /// Reads UniquePtr&lt;TrailHistory&gt; to update each entity's LineRenderer trail.
    /// </summary>
    [ExecuteIn(SystemPhase.Presentation)]
    [ExecuteAfter(typeof(PatrolMovementSystem))]
    public partial class PatrolPresenter : ISystem
    {
        readonly RenderableGameObjectManager _goManager;

        public PatrolPresenter(RenderableGameObjectManager goManager)
        {
            _goManager = goManager;
        }

        [ForEachEntity(MatchByComponents = true)]
        void Execute(in Position position, in Trail trail, in GameObjectId goId)
        {
            var go = _goManager.Resolve(goId);
            go.transform.position = (Vector3)position.Value;

            var trailHistory = trail.Value.Get(World);
            var lineRenderer = go.GetComponent<LineRenderer>();
            lineRenderer.positionCount = trailHistory.Positions.Count;

            for (int i = 0; i < trailHistory.Positions.Count; i++)
                lineRenderer.SetPosition(i, trailHistory.Positions[i]);
        }
    }
}
