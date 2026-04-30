using System;
using Trecs;
using Trecs.Collections;
using Trecs.Internal;
using UnityEditor;
using UnityEngine;
using UnityEngine.UIElements;

namespace Trecs.Tools
{
    /// <summary>
    /// ScriptableObject proxy used as <see cref="Selection.activeObject"/> when
    /// the user picks a set row in <see cref="TrecsHierarchyWindow"/>. The
    /// inspector resolves the <see cref="SetDef"/> back to live state on the
    /// owning <see cref="World"/> each refresh.
    /// </summary>
    public class TrecsSetSelection : ScriptableObject
    {
        [NonSerialized]
        WeakReference<World> _worldRef;

        [NonSerialized]
        public SetDef SetDef;

        // Cache-mode payload — populated when the hierarchy is rendering
        // from a TrecsSchema rather than a live world. Live-mode setters
        // null these out, so the inspector can switch on World presence.
        [NonSerialized]
        public TrecsSchema CacheSchema;

        [NonSerialized]
        public TrecsSchemaSet CacheSet;

        public World GetWorld()
        {
            if (_worldRef == null)
            {
                return null;
            }
            return _worldRef.TryGetTarget(out var w) ? w : null;
        }

        public void Set(World world, SetDef setDef)
        {
            _worldRef = world == null ? null : new WeakReference<World>(world);
            SetDef = setDef;
            CacheSchema = null;
            CacheSet = null;
            name = setDef.DebugName ?? "Set";
        }

        public void SetCache(TrecsSchema schema, TrecsSchemaSet entry)
        {
            _worldRef = null;
            SetDef = default;
            CacheSchema = schema;
            CacheSet = entry;
            name = entry?.DebugName ?? "Set";
        }
    }

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

        // Tracks identity of the currently-rendered entry so RenderStatic
        // only fires when the underlying set actually changes. SetId.Id
        // (boxed int) for live; TrecsSchemaSet ref for cache.
        object _renderedEntryKey;

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
            TrecsInspectorLinks.WireHoverPreviewSet(
                _headerLabel,
                () =>
                {
                    var sel = target as TrecsSetSelection;
                    return (sel?.GetWorld(), sel?.SetDef.Id ?? default);
                }
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

            // Resolve identity first; entry is rebuilt only when identity
            // actually changes — avoids reallocating the schema entry every
            // 250ms for the same set.
            ResolveIdentity(selection, out var liveWorld, out var liveSetDef, out var identity);

            if (identity == null)
            {
                var w = selection.GetWorld();
                ShowStatus(
                    w == null || w.IsDisposed
                        ? "No set selected — world unavailable."
                        : "No set selected."
                );
                return;
            }

            _statusLabel.style.display = DisplayStyle.None;
            _bodyContainer.style.display = DisplayStyle.Flex;

            if (!Equals(identity, _renderedEntryKey))
            {
                _renderedEntryKey = identity;
                var entry = liveWorld != null ? BuildEntryFromLive(liveSetDef) : selection.CacheSet;
                RenderStatic(entry);
            }

            UpdateRuntimeFields(liveWorld, liveSetDef);
        }

        static void ResolveIdentity(
            TrecsSetSelection selection,
            out World liveWorld,
            out SetDef liveSetDef,
            out object identity
        )
        {
            liveWorld = null;
            liveSetDef = default;
            identity = null;

            var world = selection.GetWorld();
            if (world != null && !world.IsDisposed && selection.SetDef.Id.Id != 0)
            {
                liveWorld = world;
                liveSetDef = selection.SetDef;
                identity = selection.SetDef.Id.Id;
                return;
            }
            if (selection.CacheSet != null)
            {
                identity = selection.CacheSet;
            }
        }

        static TrecsSchemaSet BuildEntryFromLive(SetDef setDef)
        {
            var entry = new TrecsSchemaSet
            {
                DebugName = setDef.DebugName,
                Id = setDef.Id.Id,
                TypeFullName = setDef.SetType?.FullName,
                TypeNamespace = setDef.SetType?.Namespace,
            };
            if (!setDef.Tags.IsNull)
            {
                foreach (var tag in setDef.Tags.Tags)
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

        void UpdateRuntimeFields(World world, SetDef setDef)
        {
            if (world == null || world.IsDisposed)
            {
                _groupsValue.text = "(cached — N/A)";
                _entitiesValue.text = "(cached — N/A)";
                return;
            }
            try
            {
                var info = world.WorldInfo;
                var groups = setDef.Tags.IsNull
                    ? info.AllGroups
                    : info.GetGroupsWithTags(setDef.Tags);
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
                    _accessor = world.CreateAccessor("TrecsSetSelectionInspector");
                    _accessorWorld = world;
                }
                _entitiesValue.text = _accessor.CountEntitiesInSet(setDef.Id).ToString();
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
            _renderedEntryKey = null;
        }
    }
}
