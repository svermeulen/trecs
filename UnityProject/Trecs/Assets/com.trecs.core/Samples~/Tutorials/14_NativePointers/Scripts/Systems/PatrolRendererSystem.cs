using UnityEngine;

namespace Trecs.Samples.NativePointers
{
    /// <summary>
    /// Variable-update renderer that syncs ECS state to GameObjects.
    /// Reads the NativeUniquePtr&lt;TTrail&gt; on the main thread via the
    /// WorldAccessor overload of Get — the same pointer resolves from a
    /// job or from the main thread without any conversion.
    /// </summary>
    [VariableUpdate]
    [ExecutesAfter(typeof(PatrolMovementSystem))]
    public partial class PatrolRendererSystem : ISystem
    {
        readonly GameObjectRegistry _registry;

        public PatrolRendererSystem(GameObjectRegistry registry)
        {
            _registry = registry;
        }

        [ForEachEntity(MatchByComponents = true)]
        void Execute(in Position position, in CNativeTrail trail, in GameObjectId goId)
        {
            var go = _registry.Resolve(goId);
            go.transform.position = (Vector3)position.Value;

            ref readonly var trailData = ref trail.Value.Get(World);
            var lineRenderer = go.GetComponent<LineRenderer>();
            lineRenderer.positionCount = trailData.Positions.Length;

            for (int i = 0; i < trailData.Positions.Length; i++)
            {
                lineRenderer.SetPosition(i, (Vector3)trailData.Positions[i]);
            }
        }
    }
}
