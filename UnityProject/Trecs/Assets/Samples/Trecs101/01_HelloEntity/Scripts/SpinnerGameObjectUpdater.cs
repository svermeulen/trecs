namespace Trecs.Samples.HelloEntity
{
    [VariableUpdate]
    public partial class SpinnerGameObjectUpdater : ISystem
    {
        readonly GameObjectRegistry _gameObjectRegistry;

        public SpinnerGameObjectUpdater(GameObjectRegistry gameObjectRegistry)
        {
            _gameObjectRegistry = gameObjectRegistry;
        }

        [ForEachEntity(MatchByComponents = true)]
        void Execute(in GameObjectId id, in Rotation rotation)
        {
            var go = _gameObjectRegistry.Resolve(id);
            go.transform.rotation = rotation.Value;
        }
    }
}
