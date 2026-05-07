namespace Trecs.Samples.ReactiveEvents
{
    [ExecuteIn(SystemPhase.Presentation)]
    public partial class BubbleRendererSystem : ISystem
    {
        readonly GameObjectRegistry _registry;

        public BubbleRendererSystem(GameObjectRegistry registry)
        {
            _registry = registry;
        }

        [ForEachEntity(typeof(SampleTags.Bubble))]
        void Execute(in GameObjectId id, in Position position)
        {
            _registry.Resolve(id).transform.position = position.Value;
        }
    }
}
