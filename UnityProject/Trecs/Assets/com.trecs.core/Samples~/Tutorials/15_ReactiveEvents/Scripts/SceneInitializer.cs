using UnityEngine;

namespace Trecs.Samples.ReactiveEvents
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
            _goManager.RegisterFactory(ReactiveEventsPrefabs.Bubble, CreateBubble);
        }

        static GameObject CreateBubble()
        {
            var go = SampleUtil.CreatePrimitive(PrimitiveType.Sphere);
            go.transform.localScale = new Vector3(0.3f, 0.3f, 0.3f);
            return go;
        }
    }
}
