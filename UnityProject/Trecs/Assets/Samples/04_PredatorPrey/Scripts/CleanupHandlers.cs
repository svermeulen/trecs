using UnityEngine;

namespace Trecs.Samples.PredatorPrey
{
    public partial class CleanupHandlers
    {
        readonly GameObjectRegistry _gameObjectRegistry;
        readonly DisposeCollection _disposables = new();

        public CleanupHandlers(World world, GameObjectRegistry gameObjectRegistry)
        {
            World = world.CreateAccessor();

            _gameObjectRegistry = gameObjectRegistry;

            World
                .Events.EntitiesWithTags<SampleTags.Prey>()
                .OnRemoved(OnPreyRemoved)
                .AddTo(_disposables);
        }

        WorldAccessor World { get; }

        // We could also do this in PredatorChaseSystem.cs but its safer here
        // in case we remove from multiple places in the future
        [ForEachEntity]
        void OnPreyRemoved(in Prey prey)
        {
            var go = _gameObjectRegistry.Resolve(prey.GameObjectId);
            GameObject.Destroy(go);
            _gameObjectRegistry.Unregister(prey.GameObjectId);
        }

        public void Dispose()
        {
            _disposables.Dispose();
        }

        partial struct Prey : IAspect, IRead<GameObjectId, ApproachingPredator> { }
    }
}
