using UnityEngine;

namespace Trecs.Samples.SpawnAndDestroy
{
    public class SceneInitializer
    {
        readonly RenderableGameObjectManager _goManager;

        public SceneInitializer(RenderableGameObjectManager goManager)
        {
            _goManager = goManager;
        }

        public void Initialize()
        {
            _goManager.RegisterFactory(SpawnAndDestroyPrefabs.Sphere, CreateSphere);
        }

        static GameObject CreateSphere()
        {
            var go = SampleUtil.CreatePrimitive(PrimitiveType.Sphere);
            go.name = "Sphere";
            return go;
        }
    }
}
