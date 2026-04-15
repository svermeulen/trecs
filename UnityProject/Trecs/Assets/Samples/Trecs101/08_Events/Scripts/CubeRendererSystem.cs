using UnityEngine;

namespace Trecs.Samples.Events
{
    /// <summary>
    /// Syncs cube scale to GameObjects each variable update frame.
    /// Scale changes as cubes grow and shrink; colors are set by
    /// EventTracker via event callbacks.
    /// </summary>
    [VariableUpdate]
    [ExecutesAfter(typeof(LifecycleSystem))]
    public partial class CubeRendererSystem : ISystem
    {
        readonly GameObjectRegistry _registry;

        public CubeRendererSystem(GameObjectRegistry registry)
        {
            _registry = registry;
        }

        [ForEachEntity(Tags = new[] { typeof(CubeTags.Cube) })]
        void Execute(in CubeView cube)
        {
            var go = _registry.Resolve(new GameObjectId(cube.GameObjectId));
            go.transform.localScale = Vector3.one * cube.UniformScale;
        }

        partial struct CubeView : IAspect, IRead<UniformScale, GameObjectId> { }
    }
}
