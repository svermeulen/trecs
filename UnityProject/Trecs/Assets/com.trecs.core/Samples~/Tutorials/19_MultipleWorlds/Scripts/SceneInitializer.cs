using UnityEngine;

namespace Trecs.Samples.MultipleWorlds
{
    public class SceneInitializer
    {
        readonly RenderableGameObjectManager _goManager;
        readonly PrimitiveType _primitive;
        readonly Color _color;

        public SceneInitializer(
            RenderableGameObjectManager goManager,
            PrimitiveType primitive,
            Color color
        )
        {
            _goManager = goManager;
            _primitive = primitive;
            _color = color;
        }

        public void Initialize()
        {
            _goManager.RegisterFactory(MultipleWorldsPrefabs.Critter, CreateCritter);
        }

        GameObject CreateCritter()
        {
            var go = SampleUtil.CreatePrimitive(_primitive);
            go.GetComponent<Renderer>().material.color = _color;
            return go;
        }
    }
}
