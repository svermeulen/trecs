using UnityEngine;
using Trecs.Internal;

namespace Trecs.Samples.SpawnAndDestroy
{
    public partial class LifetimeSystem : ISystem
    {
        readonly GameObjectRegistry _gameObjectRegistry;

        public LifetimeSystem(GameObjectRegistry gameObjectRegistry)
        {
            _gameObjectRegistry = gameObjectRegistry;
        }

        [ForEachEntity(typeof(SampleTags.Sphere))]
        void Execute(in GameObjectId gameObjectId, ref Lifetime lifetime, EntityIndex entityIndex)
        {
            lifetime.Value -= World.DeltaTime;

            if (lifetime.Value <= 0)
            {
                var go = _gameObjectRegistry.Resolve(gameObjectId);
                Object.Destroy(go);
                _gameObjectRegistry.Unregister(gameObjectId);
                // Removal is deferred — the entity continues to exist until the next submission
                World.RemoveEntity(entityIndex);
            }
        }
    }
}
