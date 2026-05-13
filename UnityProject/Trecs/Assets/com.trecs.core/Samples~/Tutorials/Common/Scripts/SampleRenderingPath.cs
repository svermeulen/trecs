using UnityEngine;
#if UNITY_EDITOR
using UnityEditor;
#endif

namespace Trecs.Samples
{
    // Central gate for the IndirectRenderer's two rendering paths. Runtime default
    // is "compute-capable platform uses indirect; everyone else uses fallback".
    // In the Editor, the menu toggle below lets you force the fallback path so
    // you can reproduce WebGL/mobile behavior without a full player build.
    public static class SampleRenderingPath
    {
        public static bool ForceFallback;

        public static bool UseFallback => ForceFallback || !SystemInfo.supportsComputeShaders;

#if UNITY_EDITOR
        const string MenuPath = "Tools/Trecs/Force Fallback Rendering";
        const string PrefKey = "Trecs.ForceFallbackRendering";

        [InitializeOnLoadMethod]
        static void LoadFromPrefs()
        {
            ForceFallback = EditorPrefs.GetBool(PrefKey, false);
        }

        [MenuItem(MenuPath)]
        static void Toggle()
        {
            ForceFallback = !ForceFallback;
            EditorPrefs.SetBool(PrefKey, ForceFallback);
        }

        [MenuItem(MenuPath, validate = true)]
        static bool ToggleValidate()
        {
            Menu.SetChecked(MenuPath, ForceFallback);
            return !Application.isPlaying;
        }
#endif
    }
}
