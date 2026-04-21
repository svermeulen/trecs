using UnityEngine;
using UnityEngine.Rendering;

namespace Trecs.Serialization.Samples
{
    public static class SampleUtil
    {
        public static bool IsUrp => GraphicsSettings.currentRenderPipeline != null;

        // Wraps GameObject.CreatePrimitive so primitives render correctly under URP.
        // Unity's Default-Material uses the Built-in Standard shader, which shows up
        // pink in URP builds — replace it with a URP/Lit material when URP is active.
        public static GameObject CreatePrimitive(PrimitiveType type)
        {
            var go = GameObject.CreatePrimitive(type);
            if (IsUrp)
                go.GetComponent<MeshRenderer>().sharedMaterial = CreateMaterial(Color.white);
            return go;
        }

        public static Material CreateMaterial(Color color)
        {
            var shaderName = IsUrp ? "Universal Render Pipeline/Lit" : "Standard";
            var material = new Material(Shader.Find(shaderName));
            material.color = color;
            material.enableInstancing = true;
            return material;
        }
    }
}
