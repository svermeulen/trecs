using UnityEditor;
using UnityEngine;
using UnityEngine.UIElements;

namespace Trecs
{
    /// <summary>
    /// ScriptableObject proxy used as <see cref="Selection.activeObject"/>
    /// when the user multi-selects rows in <see cref="TrecsHierarchyWindow"/>.
    /// Renders a placeholder body explaining that multi-edit isn't wired up
    /// yet — a single-row selection still routes through the per-kind
    /// proxies (entity / template / accessor / component type).
    /// </summary>
    public class TrecsMultiSelection : ScriptableObject
    {
        public int Count;
    }

    [CustomEditor(typeof(TrecsMultiSelection))]
    public class TrecsMultiSelectionInspector : Editor
    {
        Label _label;

        public override VisualElement CreateInspectorGUI()
        {
            var root = new VisualElement();
            root.style.paddingLeft = 8;
            root.style.paddingRight = 8;
            root.style.paddingTop = 8;
            root.style.paddingBottom = 8;

            _label = new Label();
            _label.style.whiteSpace = WhiteSpace.Normal;
            _label.style.opacity = 0.75f;
            root.Add(_label);

            Refresh();
            // Re-poll so the count stays in sync as the user adjusts the
            // tree selection without leaving multi-select mode.
            root.schedule.Execute(Refresh)
                .Every(TrecsDebugWindowSettings.Get().RefreshIntervalMs);
            return root;
        }

        void Refresh()
        {
            var sel = (TrecsMultiSelection)target;
            if (sel == null)
            {
                _label.text = string.Empty;
                return;
            }
            _label.text = $"{sel.Count} items selected — multi-select inspector not yet supported.";
        }
    }
}
