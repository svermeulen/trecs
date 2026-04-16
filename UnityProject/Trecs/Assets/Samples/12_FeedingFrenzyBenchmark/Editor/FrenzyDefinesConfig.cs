using System;
using System.Linq;
using UnityEditor;
using UnityEditor.Build;

namespace Trecs.Samples.FeedingFrenzyBenchmark.Editor
{
    // Manages framework-level scripting defines (profiling, internal checks).
    // Frenzy-specific settings (StateApproach, IterationStyle, Deterministic)
    // are now runtime fields on FrenzyCompositionRoot.FrenzyConfig.
    [Serializable]
    public class FrenzyDefinesConfig : IEquatable<FrenzyDefinesConfig>
    {
        public bool IsProfiling;
        public bool InternalChecks;

        public bool Equals(FrenzyDefinesConfig other)
        {
            if (other == null)
                return false;

            return IsProfiling == other.IsProfiling && InternalChecks == other.InternalChecks;
        }

        public override bool Equals(object obj)
        {
            return Equals(obj as FrenzyDefinesConfig);
        }

        public override int GetHashCode()
        {
            return HashCode.Combine(IsProfiling, InternalChecks);
        }
    }

    public static class FrenzyDefinesConfigHelper
    {
        const string DefineProfiling = "TRECS_IS_PROFILING";
        const string DefineInternalChecks = "TRECS_INTERNAL_CHECKS";

        public static FrenzyDefinesConfig GetCurrent()
        {
            var target = NamedBuildTarget.FromBuildTargetGroup(
                EditorUserBuildSettings.selectedBuildTargetGroup
            );
            var currentDefines = PlayerSettings
                .GetScriptingDefineSymbols(target)
                .Split(';')
                .Where(d => !string.IsNullOrEmpty(d))
                .ToList();

            return new FrenzyDefinesConfig
            {
                IsProfiling = currentDefines.Contains(DefineProfiling),
                InternalChecks = currentDefines.Contains(DefineInternalChecks),
            };
        }

        public static void SetCurrent(FrenzyDefinesConfig config)
        {
            var target = NamedBuildTarget.FromBuildTargetGroup(
                EditorUserBuildSettings.selectedBuildTargetGroup
            );
            var currentDefines = PlayerSettings
                .GetScriptingDefineSymbols(target)
                .Split(';')
                .Where(d => !string.IsNullOrEmpty(d))
                .ToList();

            // Clean up old frenzy defines that are now runtime settings
            currentDefines.Remove("FRENZY_USE_FILTERS");
            currentDefines.Remove("FRENZY_USE_STATES");
            currentDefines.Remove("FRENZY_USE_ASPECTS");
            currentDefines.Remove("FRENZY_USE_JOBS");
            currentDefines.Remove("FRENZY_MANUAL_QUERY");

            SetDefine(currentDefines, DefineProfiling, config.IsProfiling);
            SetDefine(currentDefines, DefineInternalChecks, config.InternalChecks);

            PlayerSettings.SetScriptingDefineSymbols(target, string.Join(";", currentDefines));
        }

        static void SetDefine(
            System.Collections.Generic.List<string> defines,
            string define,
            bool value
        )
        {
            if (value)
            {
                if (!defines.Contains(define))
                    defines.Add(define);
            }
            else
            {
                defines.Remove(define);
            }
        }
    }
}
