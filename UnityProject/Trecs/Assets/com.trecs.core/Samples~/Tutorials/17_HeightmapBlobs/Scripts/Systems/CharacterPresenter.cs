namespace Trecs.Samples.HeightmapBlobs
{
    /// <summary>
    /// Syncs character entity positions to their Unity GameObjects.
    /// Variable-phase because it touches scene-side state.
    /// </summary>
    [ExecuteIn(SystemPhase.Presentation)]
    public partial class CharacterPresenter : ISystem
    {
        readonly RenderableGameObjectManager _goManager;

        public CharacterPresenter(RenderableGameObjectManager goManager)
        {
            _goManager = goManager;
        }

        [ForEachEntity(typeof(SampleTags.Character))]
        void Execute(in GameObjectId id, in Position position)
        {
            var go = _goManager.Resolve(id);
            go.transform.position = position.Value;
        }
    }
}
