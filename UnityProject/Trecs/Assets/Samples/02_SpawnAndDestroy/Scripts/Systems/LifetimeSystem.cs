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
        void Execute(in GameObjectId gameObjectId, ref Lifetime lifetime, EntityIndex entityIndex)
        {
            lifetime.Value -= World.DeltaTime;

            if (lifetime.Value <= 0)
            {
                var go = _gameObjectRegistry.Resolve(gameObjectId);
                Object.Destroy(go);
                _gameObjectRegistry.Unregister(gameObjectId);
                World.RemoveEntity(entityIndex);
            }
        }
    }
}
