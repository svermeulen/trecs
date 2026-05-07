using UnityEngine;

namespace Trecs.Samples.SpawnAndDestroy
{
    [ExecuteIn(SystemPhase.Presentation)]
    public partial class SphereRendererSystem : ISystem
    {
        readonly GameObjectRegistry _gameObjectRegistry;

        public SphereRendererSystem(GameObjectRegistry gameObjectRegistry)
        {
            _gameObjectRegistry = gameObjectRegistry;
        }

        [ForEachEntity(MatchByComponents = true)]
        void Execute(in GameObjectId id, in Position position)
        {
            var go = _gameObjectRegistry.Resolve(id);
            go.transform.position = (Vector3)position.Value;
        }
    }
}
