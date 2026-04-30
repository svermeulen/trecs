using System.Collections.Generic;
using Trecs;
using Trecs.Collections;
using Trecs.Internal;

namespace Trecs.Tools
{
    /// <summary>
    /// Registry of <see cref="Trecs.WorldAccessor.DebugName"/>s that belong
    /// to the Trecs editor / debug tooling itself. The hierarchy window
    /// filters them out of the Accessors section, and the schema cache
    /// writer filters them out of the on-disk ManualAccessors list, so
    /// users don't see "TrecsHierarchyWindow" sitting next to their own
    /// systems.
    ///
    /// Each inspector / window self-registers its accessor name from a
    /// <c>[InitializeOnLoadMethod]</c> hook so adding a new debug inspector
    /// doesn't require remembering to also edit a central list. Pre-rename
    /// names live here as a small starter set so stale on-disk snapshots
    /// don't leak old debug accessors into the user-facing list.
    /// </summary>
    public static class TrecsEditorAccessorNames
    {
        static readonly HashSet<string> _all = new()
        {
            // Pre-rename names kept around in case of stale instances.
            "TrecsSystemsWindow",
            "TrecsEntitiesWindow",
            "TrecsSystemSelectionInspector",
        };

        public static void Register(string accessorDebugName)
        {
            if (!string.IsNullOrEmpty(accessorDebugName))
            {
                _all.Add(accessorDebugName);
            }
        }

        public static bool Contains(string accessorDebugName) =>
            !string.IsNullOrEmpty(accessorDebugName) && _all.Contains(accessorDebugName);
    }
}
