using UnityEngine;

namespace Trecs.Samples.MultipleWorlds
{
    public partial class LifetimeSystem : ISystem
    {
        readonly GameObjectRegistry _gameObjectRegistry;

        public LifetimeSystem(GameObjectRegistry gameObjectRegistry)
        {
            _gameObjectRegistry = gameObjectRegistry;
        }

        [ForEachEntity(typeof(SampleTags.Critter))]
        void Execute(in GameObjectId gameObjectId, ref Lifetime lifetime, EntityAccessor entity)
        {
            lifetime.Value -= World.DeltaTime;

            if (lifetime.Value <= 0)
            {
                var go = _gameObjectRegistry.Resolve(gameObjectId);
                Object.Destroy(go);
                _gameObjectRegistry.Unregister(gameObjectId);
                entity.Remove();
            }
        }
    }
}
