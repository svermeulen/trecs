using UnityEngine;

namespace Trecs.Serialization.Samples.NativePointers
{
    /// <summary>
    /// Variable-update renderer that syncs ECS state to GameObjects.
    /// Reads the NativeUniquePtr&lt;TrailHistory&gt; on the main thread via
    /// <see cref="HeapAccessor.Read{T}"/>. The same pointer resolves from a
    /// job (via <see cref="NativeUniquePtrResolver.Read{T}"/>) or from the
    /// main thread without any conversion.
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

            ref readonly var trailData = ref World.Heap.Read(in trail.Value).Value;
            var lineRenderer = go.GetComponent<LineRenderer>();
            lineRenderer.positionCount = trailData.Positions.Count;

            for (int i = 0; i < trailData.Positions.Count; i++)
            {
                lineRenderer.SetPosition(i, (Vector3)trailData.Positions[i]);
            }
        }
    }
}
