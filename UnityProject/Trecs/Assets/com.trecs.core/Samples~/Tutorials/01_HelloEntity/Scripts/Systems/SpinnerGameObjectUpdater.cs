namespace Trecs.Samples.HelloEntity
{
    // Use variable update since it's best practice to not reference
    // outside simulation when in Fixed update, and we reference game objects here
    // This is because fixed update should be deterministic and not rely on outside state
    // so that we can serialize, bookmark, make recordings, playback recordings
    // Determinism might be important for other reasons as well
    [Phase(SystemPhase.Presentation)]
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
