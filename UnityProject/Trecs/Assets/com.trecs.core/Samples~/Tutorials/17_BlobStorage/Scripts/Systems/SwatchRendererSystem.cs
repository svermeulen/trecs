using UnityEngine;

namespace Trecs.Samples.BlobStorage
{
    /// <summary>
    /// Syncs ECS state to Unity transforms and material colours. Runs in the
    /// variable update phase because it touches scene-side state.
    /// </summary>
    [Phase(SystemPhase.Presentation)]
    public partial class SwatchRendererSystem : ISystem
    {
        readonly GameObjectRegistry _registry;

        public SwatchRendererSystem(GameObjectRegistry registry)
        {
            _registry = registry;
        }

        [ForEachEntity(Tag = typeof(SampleTags.Swatch))]
        void Execute(
            in GameObjectId id,
            in Position position,
            in UniformScale scale,
            in ColorComponent color
        )
        {
            var go = _registry.Resolve(id);
            go.transform.position = position.Value;
            go.transform.localScale = new Vector3(scale.Value, scale.Value, scale.Value);
            go.GetComponent<Renderer>().material.color = color.Value;
        }
    }
}
