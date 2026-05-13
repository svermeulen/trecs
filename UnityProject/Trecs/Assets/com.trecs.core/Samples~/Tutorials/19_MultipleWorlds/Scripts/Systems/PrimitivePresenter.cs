using UnityEngine;

namespace Trecs.Samples.MultipleWorlds
{
    [ExecuteIn(SystemPhase.Presentation)]
    public partial class PrimitivePresenter : ISystem
    {
        readonly RenderableGameObjectManager _goManager;

        public PrimitivePresenter(RenderableGameObjectManager goManager)
        {
            _goManager = goManager;
        }

        [ForEachEntity(MatchByComponents = true)]
        void Execute(in GameObjectId id, in Position position)
        {
            var go = _goManager.Resolve(id);
            go.transform.position = (Vector3)position.Value;
        }
    }
}
