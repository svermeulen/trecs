using UnityEngine;

namespace Trecs.Serialization.Samples.Snake
{
    /// <summary>
    /// Pushes every entity that has a GameObjectId and a GridPos to its
    /// matching GameObject's transform. Runs in the variable update phase
    /// so visuals stay smooth even when fixed updates are scarce.
    /// </summary>
    [VariableUpdate]
    public partial class SnakeRendererSystem : ISystem
    {
        readonly RenderableGameObjectManager _goManager;

        public SnakeRendererSystem(RenderableGameObjectManager goManager)
        {
            _goManager = goManager;
        }

        [ForEachEntity(MatchByComponents = true)]
        void Execute(in GameObjectId id, in GridPos pos)
        {
            if (id.Value == 0)
            {
                return;
            }

            var go = _goManager.Resolve(id);
            go.transform.position = new Vector3(pos.Value.x, 0.5f, pos.Value.y);
        }
    }
}
