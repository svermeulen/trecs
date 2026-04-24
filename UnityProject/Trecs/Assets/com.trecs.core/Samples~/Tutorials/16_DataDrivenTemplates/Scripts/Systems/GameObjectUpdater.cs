namespace Trecs.Samples.DataDrivenTemplates
{
    /// <summary>
    /// Syncs ECS state to Unity's scene graph each frame. Runs in the variable
    /// update phase because it touches GameObject transforms — fixed-update
    /// systems stay deterministic and should not reach into scene state.
    /// </summary>
    [VariableUpdate]
    public partial class GameObjectUpdater : ISystem
    {
        readonly GameObjectRegistry _registry;

        public GameObjectUpdater(GameObjectRegistry registry)
        {
            _registry = registry;
        }

        [ForEachEntity(MatchByComponents = true)]
        void Execute(in GameObjectId id, in Position position, in Rotation rotation)
        {
            var go = _registry.Resolve(id);
            go.transform.position = position.Value;
            go.transform.rotation = rotation.Value;
        }
    }
}
