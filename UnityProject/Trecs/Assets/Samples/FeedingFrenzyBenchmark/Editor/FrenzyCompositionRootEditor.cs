using UnityEditor;

namespace Trecs.Samples.FeedingFrenzyBenchmark.Editor
{
    [CustomEditor(typeof(FrenzyCompositionRoot))]
    public class FrenzyCompositionRootEditor : UnityEditor.Editor
    {
        public override void OnInspectorGUI()
        {
            // Framework-level defines (still compile-time)
            var definesConfig = FrenzyDefinesConfigHelper.GetCurrent();

            EditorGUILayout.LabelField("Compile-Time Defines", EditorStyles.boldLabel);

            bool definesChanged = false;

            bool newProfiling = EditorGUILayout.Toggle("Is Profiling", definesConfig.IsProfiling);
            if (newProfiling != definesConfig.IsProfiling)
            {
                definesChanged = true;
                definesConfig.IsProfiling = newProfiling;
            }

            bool newInternalChecks = EditorGUILayout.Toggle(
                "Internal Checks",
                definesConfig.InternalChecks
            );
            if (newInternalChecks != definesConfig.InternalChecks)
            {
                definesChanged = true;
                definesConfig.InternalChecks = newInternalChecks;
            }

            if (definesChanged)
            {
                FrenzyDefinesConfigHelper.SetCurrent(definesConfig);
            }

            EditorGUILayout.Space();
            DrawDefaultInspector();
        }
    }
}
