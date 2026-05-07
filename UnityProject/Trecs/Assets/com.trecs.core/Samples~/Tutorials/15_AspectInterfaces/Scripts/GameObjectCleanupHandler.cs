using System;
using Object = UnityEngine.Object;

namespace Trecs.Samples.AspectInterfaces
{
    // When an enemy or boss is removed from ECS (killed by damage), its
    // GameObject would otherwise linger in the scene at its last rendered
    // color — typically dim red from the just-before-removal health
    // value. This handler destroys the GO on the same frame as the
    // entity removal so the death is visually clean.
    public partial class GameObjectCleanupHandler : IDisposable
    {
        readonly DisposeCollection _disposables = new();
        readonly GameObjectRegistry _gameObjectRegistry;

        public GameObjectCleanupHandler(World world, GameObjectRegistry gameObjectRegistry)
        {
            _gameObjectRegistry = gameObjectRegistry;
            var accessor = world.CreateAccessor(AccessorRole.Fixed);

            accessor
                .Events.EntitiesWithComponents<GameObjectId>()
                .OnRemoved(OnEntityRemoved)
                .AddTo(_disposables);
        }

        [ForEachEntity]
        void OnEntityRemoved(in GameObjectId gameObjectId)
        {
            var go = _gameObjectRegistry.Resolve(gameObjectId);
            Object.Destroy(go);
            _gameObjectRegistry.Unregister(gameObjectId);
        }

        public void Dispose() => _disposables.Dispose();
    }
}
