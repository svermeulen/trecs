namespace Trecs.Samples.ReactiveEvents
{
    [ExecuteIn(SystemPhase.Presentation)]
    public partial class BubblePresenter : ISystem
    {
        readonly RenderableGameObjectManager _goManager;

        public BubblePresenter(RenderableGameObjectManager goManager)
        {
            _goManager = goManager;
        }

        [ForEachEntity(typeof(SampleTags.Bubble))]
        void Execute(in GameObjectId id, in Position position)
        {
            _goManager.Resolve(id).transform.position = position.Value;
        }
    }
}
