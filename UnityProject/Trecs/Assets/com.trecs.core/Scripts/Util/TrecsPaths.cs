using System.IO;
using UnityEngine;

namespace Trecs
{
    /// <summary>
    /// Central resolver for the on-disk locations Trecs uses for ephemeral,
    /// per-machine data. Everything lives under <c>&lt;project&gt;/Library/com.trecs/</c>
    /// — Unity's standard location for regeneratable cache data, gitignored by
    /// the default Unity .gitignore. Safe to delete at any time; Trecs will
    /// regenerate caches and snapshots on the next run.
    /// </summary>
    internal static class TrecsPaths
    {
        public static string LibraryRoot =>
            Path.GetFullPath(Path.Combine(Application.dataPath, "..", "Library", "com.trecs"));

        public static string InspectorSchema => Path.Combine(LibraryRoot, "inspector_schema");

        public static string ProfilingFixed => Path.Combine(LibraryRoot, "profiling", "fixed");

        public static string ProfilingVariable =>
            Path.Combine(LibraryRoot, "profiling", "variable");

        public static string Recordings => Path.Combine(LibraryRoot, "recordings");

        public static string Snapshots => Path.Combine(LibraryRoot, "snapshots");
    }
}
