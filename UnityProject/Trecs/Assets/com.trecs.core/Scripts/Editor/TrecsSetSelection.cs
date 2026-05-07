using System;
using UnityEditor;
using UnityEngine;
using UnityEngine.UIElements;

namespace Trecs
{
    /// <summary>
    /// ScriptableObject proxy used as <see cref="Selection.activeObject"/> when
    /// the user picks a set row in <see cref="TrecsHierarchyWindow"/>. Carries
    /// only a serialized <see cref="TrecsSelectionProxy.Identity"/> string
    /// (e.g. <c>"set:Players"</c>); the inspector resolves the live
    /// <see cref="SetRef"/> dynamically against
    /// <see cref="TrecsHierarchyWindow.ActiveSource"/> on every refresh.
    /// </summary>
    public class TrecsSetSelection : TrecsSelectionProxy { }

    [CustomEditor(typeof(TrecsSetSelection))]
    public class TrecsSetSelectionInspector : Editor
    {
        [InitializeOnLoadMethod]
        static void RegisterEditorAccessorName() =>
            TrecsEditorAccessorNames.Register("TrecsSetSelectionInspector");

        VisualElement _bodyContainer;
        Label _statusLabel;
        Label _headerLabel;
        Label _idValue;
        Label _typeValue;
        Label _namespaceValue;
        Label _tagsValue;
        Label _groupsValue;
        Label _entitiesValue;

        WorldAccessor _accessor;
        World _accessorWorld;

        // Composite render key — identity + source mode + source name.
        string _renderedKey;

        public override VisualElement CreateInspectorGUI()
        {
            var root = new VisualElement();
            root.style.paddingLeft = 8;
            root.style.paddingRight = 8;
            root.style.paddingTop = 8;
            root.style.paddingBottom = 8;

            _statusLabel = new Label();
            _statusLabel.style.opacity = 0.7f;
            _statusLabel.style.whiteSpace = WhiteSpace.Normal;
            _statusLabel.style.display = DisplayStyle.None;
            root.Add(_statusLabel);

            _bodyContainer = new VisualElement();

            _headerLabel = new Label();
            _headerLabel.style.unityFontStyleAndWeight = FontStyle.Bold;
            _headerLabel.style.fontSize = 13;
            _bodyContainer.Add(_headerLabel);

            // Shift+hover the heading → hierarchy scrolls back to this set's
            // tree row.
            TrecsInspectorLinks.WireHoverPreview(
                _headerLabel,
                () => (target as TrecsSetSelection)?.Identity
            );

            _idValue = AddRow(_bodyContainer, "Id", "");
            _typeValue = AddRow(_bodyContainer, "Type", "");
            _namespaceValue = AddRow(_bodyContainer, "Namespace", "");
            _tagsValue = AddRow(_bodyContainer, "Tags", "");
            _groupsValue = AddRow(_bodyContainer, "Groups", "");
            _entitiesValue = AddRow(_bodyContainer, "Entities", "");

            root.Add(_bodyContainer);

            Refresh();

            var refreshMs = TrecsDebugWindowSettings.Get().RefreshIntervalMs;
            root.schedule.Execute(Refresh).Every(refreshMs);

            return root;
        }

        static Label AddRow(VisualElement container, string label, string initial)
        {
            var row = new VisualElement();
            row.style.flexDirection = FlexDirection.Row;
            row.style.marginTop = 2;

            var name = new Label(label);
            name.style.minWidth = 120;
            name.style.opacity = 0.7f;
            row.Add(name);

            var value = new Label(initial);
            value.style.flexGrow = 1;
            row.Add(value);

            container.Add(row);
            return value;
        }

        void Refresh()
        {
            var selection = (TrecsSetSelection)target;
            if (selection == null)
            {
                return;
            }

            var src = TrecsHierarchyWindow.ActiveSource;
            if (src == null || string.IsNullOrEmpty(selection.Identity))
            {
                ShowStatus("No set selected.");
                return;
            }

            var sref = src.ResolveSet(selection.Identity);
            if (sref == null)
            {
                ShowStatus(
                    $"Set '{selection.name}' not found in {(src.IsLive ? "live world" : "cached schema")} '{src.DisplayName}'."
                );
                return;
            }

            _statusLabel.style.display = DisplayStyle.None;
            _bodyContainer.style.display = DisplayStyle.Flex;

            var renderKey = src.RenderKey(selection.Identity);
            if (renderKey != _renderedKey)
            {
                _renderedKey = renderKey;
                var entry = BuildEntryFromRef(sref);
                RenderStatic(entry);
            }

            UpdateRuntimeFields(src, sref);
        }

        static TrecsSchemaSet BuildEntryFromRef(SetRef sref)
        {
            if (sref.CacheNative != null)
            {
                return sref.CacheNative;
            }
            return BuildEntryFromLive(sref.LiveSet);
        }

        static TrecsSchemaSet BuildEntryFromLive(EntitySet entitySet)
        {
            var entry = new TrecsSchemaSet
            {
                DebugName = entitySet.DebugName,
                Id = entitySet.Id.Id,
                TypeFullName = entitySet.SetType?.FullName,
                TypeNamespace = entitySet.SetType?.Namespace,
            };
            if (!entitySet.Tags.IsNull)
            {
                foreach (var tag in entitySet.Tags.Tags)
                {
                    entry.TagNames.Add(tag.ToString());
                }
            }
            return entry;
        }

        void RenderStatic(TrecsSchemaSet entry)
        {
            _headerLabel.text = entry.DebugName ?? "(unnamed)";
            _idValue.text = entry.Id.ToString();
            _typeValue.text = string.IsNullOrEmpty(entry.TypeFullName)
                ? "(unknown)"
                : entry.TypeFullName;
            _namespaceValue.text = string.IsNullOrEmpty(entry.TypeNamespace)
                ? "(none)"
                : entry.TypeNamespace;
            if (entry.TagNames == null || entry.TagNames.Count == 0)
            {
                _tagsValue.text = "(global — all groups)";
            }
            else
            {
                _tagsValue.text = string.Join(", ", entry.TagNames);
            }
        }

        void UpdateRuntimeFields(ITrecsSchemaSource src, SetRef sref)
        {
            if (!src.IsLive || !sref.HasLiveSet)
            {
                _groupsValue.text = "(cached — N/A)";
                _entitiesValue.text = "(cached — N/A)";
                return;
            }
            var world = (src as LiveSchemaSource)?.World;
            if (world == null || world.IsDisposed)
            {
                _groupsValue.text = "(unavailable)";
                _entitiesValue.text = "(unavailable)";
                return;
            }
            var entitySet = sref.LiveSet;
            try
            {
                var info = world.WorldInfo;
                var groups = entitySet.Tags.IsNull
                    ? info.AllGroups
                    : info.GetGroupsWithTags(entitySet.Tags);
                _groupsValue.text = groups.Count.ToString();
            }
            catch (Exception)
            {
                _groupsValue.text = "(unavailable)";
            }
            try
            {
                if (_accessor == null || _accessorWorld != world)
                {
                    _accessor = world.CreateAccessor(
                        AccessorRole.Unrestricted,
                        "TrecsSetSelectionInspector"
                    );
                    _accessorWorld = world;
                }
                _entitiesValue.text = _accessor.CountEntitiesInSet(entitySet.Id).ToString();
            }
            catch
            {
                _entitiesValue.text = "(unavailable)";
            }
        }

        void ShowStatus(string text)
        {
            _statusLabel.text = text;
            _statusLabel.style.display = DisplayStyle.Flex;
            _bodyContainer.style.display = DisplayStyle.None;
            _renderedKey = null;
        }
    }
}
