using UnityEngine;

namespace Trecs.Samples.Snake
{
    /// <summary>
    /// Pushes every entity that has a GameObjectId and a GridPos to its
    /// matching GameObject's transform. Runs in the variable update phase
    /// so visuals stay smooth even when fixed updates are scarce.
    /// </summary>
    [VariableUpdate]
    public partial class SnakeRendererSystem : ISystem
    {
        readonly SnakeGameObjectManager _goManager;

        public SnakeRendererSystem(SnakeGameObjectManager goManager)
        {
            _goManager = goManager;
        }

        [ForEachEntity(MatchByComponents = true)]
        void Execute(in GameObjectId id, in GridPos pos)
        {
            var go = _goManager.Resolve(id);
            go.transform.position = new Vector3(pos.Value.x, 0.5f, pos.Value.y);
        }
    }
}
