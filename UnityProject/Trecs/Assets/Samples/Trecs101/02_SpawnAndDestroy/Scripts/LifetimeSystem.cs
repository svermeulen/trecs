using UnityEngine;

namespace Trecs.Samples.SpawnAndDestroy
{
    [ExecutesAfter(typeof(SpawnSystem))]
    public partial class LifetimeSystem : ISystem
    {
        readonly GameObjectRegistry _gameObjectRegistry;

        public LifetimeSystem(GameObjectRegistry gameObjectRegistry)
        {
            _gameObjectRegistry = gameObjectRegistry;
        }

        [ForEachEntity(Tags = new[] { typeof(SampleTags.Sphere) })]
        void Execute(in SphereView sphere)
        {
            sphere.Lifetime -= World.FixedDeltaTime;

            if (sphere.Lifetime <= 0)
            {
                var goId = new GameObjectId(sphere.GameObjectId);
                var go = _gameObjectRegistry.Resolve(goId);
                Object.Destroy(go);
                _gameObjectRegistry.Unregister(goId);
                World.RemoveEntity(sphere.EntityIndex);
            }
        }

        partial struct SphereView : IAspect, IRead<GameObjectId>, IWrite<Lifetime> { }
    }
}
