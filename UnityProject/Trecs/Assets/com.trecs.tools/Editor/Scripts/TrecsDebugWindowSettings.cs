using Trecs;
using Trecs.Collections;
using Trecs.Internal;
using UnityEditor;
using UnityEngine;

namespace Trecs.Tools
{
    /// <summary>
    /// Project-level settings for the Trecs debug editor windows. Save one
    /// instance somewhere under <c>Assets/</c> via
    /// <c>Create &gt; Trecs &gt; Debug Window Settings</c> to override defaults
    /// for the whole project (committed to source). When no asset exists the
    /// windows fall back to in-memory defaults.
    /// </summary>
    [CreateAssetMenu(
        menuName = "Trecs/Debug Window Settings",
        fileName = "TrecsDebugWindowSettings"
    )]
    public class TrecsDebugWindowSettings : ScriptableObject
    {
        [SerializeField, Min(50)]
        [Tooltip(
            "How often the windows poll the live World for stats updates "
                + "(fixed frame, entity counts, recorder bookmark count, etc.). "
                + "Lower = snappier UI but more main-thread overhead."
        )]
        int _refreshIntervalMs = 250;

        [SerializeField, Min(0)]
        [Tooltip(
            "Maximum number of entity rows rendered per group in the "
                + "Entities window. More are summarized via an ellipsis row to "
                + "keep populous worlds responsive."
        )]
        int _maxEntitiesPerGroup = 50;

        [SerializeField, Min(0)]
        [Tooltip(
            "Maximum number of element rows rendered per FixedArray / "
                + "FixedList in the component inspector. More are hidden behind "
                + "an ellipsis."
        )]
        int _maxCollectionElementsShown = 16;

        public int RefreshIntervalMs => Mathf.Max(50, _refreshIntervalMs);
        public int MaxEntitiesPerGroup => Mathf.Max(0, _maxEntitiesPerGroup);
        public int MaxCollectionElementsShown => Mathf.Max(0, _maxCollectionElementsShown);

        static TrecsDebugWindowSettings _cached;

        /// <summary>
        /// Returns the project's settings asset if one exists, otherwise an
        /// in-memory instance with default values (not persisted). Result is
        /// cached for the editor session.
        /// </summary>
        public static TrecsDebugWindowSettings Get()
        {
            if (_cached != null)
            {
                return _cached;
            }
            var guids = AssetDatabase.FindAssets("t:" + nameof(TrecsDebugWindowSettings));
            if (guids.Length > 0)
            {
                var path = AssetDatabase.GUIDToAssetPath(guids[0]);
                _cached = AssetDatabase.LoadAssetAtPath<TrecsDebugWindowSettings>(path);
                if (_cached != null)
                {
                    return _cached;
                }
            }
            _cached = CreateInstance<TrecsDebugWindowSettings>();
            return _cached;
        }
    }
}
