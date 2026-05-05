using UnityEngine;

namespace Trecs.Samples.MultipleWorlds
{
    [Phase(SystemPhase.Presentation)]
    public partial class PrimitiveRendererSystem : ISystem
    {
        readonly GameObjectRegistry _gameObjectRegistry;

        public PrimitiveRendererSystem(GameObjectRegistry gameObjectRegistry)
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
