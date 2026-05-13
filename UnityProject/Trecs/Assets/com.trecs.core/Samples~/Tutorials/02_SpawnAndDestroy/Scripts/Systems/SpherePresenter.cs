using UnityEngine;

namespace Trecs.Samples.SpawnAndDestroy
{
    [ExecuteIn(SystemPhase.Presentation)]
    public partial class SpherePresenter : ISystem
    {
        readonly RenderableGameObjectManager _goManager;

        public SpherePresenter(RenderableGameObjectManager goManager)
        {
            _goManager = goManager;
        }

        [ForEachEntity(MatchByComponents = true)]
        void Execute(in GameObjectId id, in Position position, in ColorComponent color)
        {
            var go = _goManager.Resolve(id);
            go.transform.position = (Vector3)position.Value;
            go.GetComponent<Renderer>().material.color = color.Value;
        }
    }
}
