using System;
using System.Collections.Generic;
using UnityEditor;
using UnityEngine;
using UnityEngine.UIElements;

namespace Trecs
{
    /// <summary>
    /// ScriptableObject proxy used as <see cref="Selection.activeObject"/> when
    /// the user picks a tag row in <see cref="TrecsHierarchyWindow"/>. Tags
    /// are identified by their stable <see cref="Tag.Guid"/>.
    /// </summary>
    public class TrecsTagSelection : ScriptableObject
    {
        [NonSerialized]
        WeakReference<World> _worldRef;

        [NonSerialized]
        public Tag Tag;

        [NonSerialized]
        public TrecsSchema CacheSchema;

        [NonSerialized]
        public TrecsSchemaTag CacheTag;

        public World GetWorld()
        {
            if (_worldRef == null)
            {
                return null;
            }
            return _worldRef.TryGetTarget(out var w) ? w : null;
        }

        public void Set(World world, Tag tag)
        {
            _worldRef = world == null ? null : new WeakReference<World>(world);
            Tag = tag;
            CacheSchema = null;
            CacheTag = null;
            name = tag.ToString() ?? "Tag";
        }

        public void SetCache(TrecsSchema schema, TrecsSchemaTag entry)
        {
            _worldRef = null;
            Tag = default;
            CacheSchema = schema;
            CacheTag = entry;
            name = entry?.Name ?? "Tag";
        }
    }

    [CustomEditor(typeof(TrecsTagSelection))]
    public class TrecsTagSelectionInspector : Editor
    {
        VisualElement _bodyContainer;
        Label _statusLabel;
        Label _headerLabel;
        Label _idValue;
        Foldout _templatesFoldout;
        Foldout _setsFoldout;
        Foldout _accessorsFoldout;
        Label _accessorsCaveat;

        // Tracks the identity of whatever entry we last rendered so the
        // static section only rebuilds when the underlying tag changes
        // (Tag.Guid in live mode, TrecsSchemaTag ref in cache mode).
        object _renderedEntryKey;
        int _lastAccessorHash;

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

            // Shift+hover the heading → hierarchy scrolls back to this tag's
            // tree row.
            TrecsInspectorLinks.WireHoverPreviewTag(
                _headerLabel,
                () =>
                {
                    var sel = target as TrecsTagSelection;
                    return (sel?.GetWorld(), sel?.Tag ?? default);
                }
            );

            _idValue = AddRow(_bodyContainer, "Guid", "");

            _templatesFoldout = new Foldout { text = "Templates with tag", value = true };
            _templatesFoldout.style.marginTop = 6;
            _bodyContainer.Add(_templatesFoldout);

            _setsFoldout = new Foldout { text = "Sets scoped to tag", value = true };
            _setsFoldout.style.marginTop = 6;
            _bodyContainer.Add(_setsFoldout);

            _accessorsFoldout = new Foldout { text = "Accessors", value = true };
            _accessorsFoldout.style.marginTop = 6;
            _bodyContainer.Add(_accessorsFoldout);

            _accessorsCaveat = new Label(
                "Approximate — derived from groups touched at runtime that contain this tag. "
                    + "A system listed here may not actually care about the tag specifically."
            );
            _accessorsCaveat.style.opacity = 0.55f;
            _accessorsCaveat.style.fontSize = 10;
            _accessorsCaveat.style.whiteSpace = WhiteSpace.Normal;
            _accessorsCaveat.style.marginTop = 2;
            _accessorsCaveat.style.marginLeft = 4;
            _accessorsCaveat.style.marginRight = 4;
            _accessorsFoldout.Add(_accessorsCaveat);

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
            var selection = (TrecsTagSelection)target;
            if (selection == null)
            {
                return;
            }

            // Resolve the identity (Tag.Guid in live mode, schema entry ref
            // in cache mode) and the live context. Entry is built only when
            // the identity actually changes — avoids reallocating the
            // schema entry every 250ms for the same tag.
            ResolveIdentity(selection, out var liveWorld, out var liveTag, out var identity);

            if (identity == null)
            {
                var w = selection.GetWorld();
                ShowStatus(
                    w == null || w.IsDisposed
                        ? "No tag selected — world unavailable."
                        : "No tag selected."
                );
                return;
            }

            _statusLabel.style.display = DisplayStyle.None;
            _bodyContainer.style.display = DisplayStyle.Flex;

            if (!Equals(identity, _renderedEntryKey))
            {
                _renderedEntryKey = identity;
                _lastAccessorHash = 0;
                var entry =
                    liveWorld != null ? BuildEntryFromLive(liveWorld, liveTag) : selection.CacheTag;
                var linker =
                    liveWorld != null
                        ? (InspectorLinker)new LiveInspectorLinker(liveWorld)
                        : new CacheInspectorLinker(selection.CacheSchema);
                RenderStatic(entry, linker);
            }

            UpdateRuntimeFields(liveWorld, liveTag);
        }

        static void ResolveIdentity(
            TrecsTagSelection selection,
            out World liveWorld,
            out Tag liveTag,
            out object identity
        )
        {
            liveWorld = null;
            liveTag = default;
            identity = null;

            var world = selection.GetWorld();
            if (world != null && !world.IsDisposed && selection.Tag.Guid != 0)
            {
                liveWorld = world;
                liveTag = selection.Tag;
                identity = selection.Tag.Guid;
                return;
            }
            if (selection.CacheTag != null)
            {
                identity = selection.CacheTag;
            }
        }

        // Walks the live world's templates + sets to gather everything
        // that references this tag, then folds the cross-links into a
        // TrecsSchemaTag entry — the same shape the cache stores on disk.
        static TrecsSchemaTag BuildEntryFromLive(World world, Tag tag)
        {
            var entry = new TrecsSchemaTag { Name = tag.ToString(), Guid = tag.Guid };
            var info = world.WorldInfo;
            var seenTemplates = new HashSet<Template>();
            foreach (var rt in info.ResolvedTemplates)
            {
                bool matches = TagSetContains(rt.AllTags, tag);
                if (!matches)
                {
                    foreach (var p in rt.Partitions)
                    {
                        if (TagSetContains(p, tag))
                        {
                            matches = true;
                            break;
                        }
                    }
                }
                if (matches && seenTemplates.Add(rt.Template))
                {
                    entry.TemplateNames.Add(rt.DebugName ?? "(unnamed)");
                }
            }
            entry.TemplateNames.Sort(StringComparer.OrdinalIgnoreCase);
            foreach (var entitySet in info.AllSets)
            {
                if (TagSetContains(entitySet.Tags, tag))
                {
                    entry.SetNames.Add(entitySet.DebugName ?? $"#{entitySet.Id.Id}");
                }
            }
            entry.SetNames.Sort(StringComparer.OrdinalIgnoreCase);
            return entry;
        }

        void RenderStatic(TrecsSchemaTag entry, InspectorLinker linker)
        {
            _headerLabel.text = entry.Name ?? "(unnamed)";
            _idValue.text = entry.Guid.ToString();

            FillFoldout(_templatesFoldout, entry.TemplateNames, linker.TemplateLink);
            FillFoldout(_setsFoldout, entry.SetNames, linker.SetLink);
        }

        // Live-only: walk groups containing the tag, ask the tracker which
        // systems touched any of them, render their links. In cache mode
        // there's no tracker so the foldout shows a not-available marker.
        void UpdateRuntimeFields(World world, Tag tag)
        {
            // Strip everything except the caveat (the foldout's first child).
            for (int i = _accessorsFoldout.childCount - 1; i >= 0; i--)
            {
                if (_accessorsFoldout[i] == _accessorsCaveat)
                    continue;
                // Don't touch — wait until we know we need to rebuild
                break;
            }

            if (world == null || world.IsDisposed)
            {
                if (_lastAccessorHash != -1)
                {
                    _lastAccessorHash = -1;
                    StripAccessorRows();
                    _accessorsFoldout.Add(
                        MakeMutedLine("(cached — runtime tracker data not available)")
                    );
                }
                return;
            }

            var tracker = TrecsAccessRegistry.GetTracker(world);
            var info = world.WorldInfo;

            var groupsWithTag = new HashSet<GroupIndex>();
            try
            {
                foreach (var g in info.AllGroups)
                {
                    var tags = info.GetGroupTags(g);
                    foreach (var t in tags)
                    {
                        if (t.Equals(tag))
                        {
                            groupsWithTag.Add(g);
                            break;
                        }
                    }
                }
            }
            catch (Exception)
            {
                return;
            }

            var systems = new HashSet<string>();
            if (tracker != null)
            {
                foreach (var g in groupsWithTag)
                {
                    foreach (var s in tracker.GetSystemsTouchingGroup(g))
                    {
                        systems.Add(s);
                    }
                }
            }

            int hash = systems.Count;
            foreach (var s in systems)
            {
                hash ^= s == null ? 0 : s.GetHashCode();
            }
            if (hash == _lastAccessorHash)
                return;
            _lastAccessorHash = hash;

            StripAccessorRows();
            if (systems.Count == 0)
            {
                _accessorsFoldout.Add(MakeMutedLine("(none recorded)"));
                return;
            }
            var sorted = new List<string>(systems);
            sorted.Sort(StringComparer.OrdinalIgnoreCase);
            var linker = new LiveInspectorLinker(world);
            foreach (var name in sorted)
            {
                _accessorsFoldout.Add(linker.AccessorLink(name));
            }
        }

        void StripAccessorRows()
        {
            for (int i = _accessorsFoldout.childCount - 1; i >= 0; i--)
            {
                if (_accessorsFoldout[i] != _accessorsCaveat)
                {
                    _accessorsFoldout.RemoveAt(i);
                }
            }
        }

        // Renders link rows; hides the foldout when empty.
        static void FillFoldout(
            Foldout foldout,
            List<string> names,
            Func<string, VisualElement> linker
        )
        {
            foldout.Clear();
            if (names == null || names.Count == 0)
            {
                foldout.style.display = DisplayStyle.None;
                return;
            }
            foldout.style.display = DisplayStyle.Flex;
            foreach (var n in names)
            {
                foldout.Add(linker(n));
            }
        }

        void ShowStatus(string text)
        {
            _statusLabel.text = text;
            _statusLabel.style.display = DisplayStyle.Flex;
            _bodyContainer.style.display = DisplayStyle.None;
            _renderedEntryKey = null;
        }

        static bool TagSetContains(TagSet ts, Tag tag)
        {
            if (ts.IsNull || tag.Guid == 0)
            {
                return false;
            }
            foreach (var t in ts.Tags)
            {
                if (t.Equals(tag))
                {
                    return true;
                }
            }
            return false;
        }

        static Label MakeMutedLine(string text)
        {
            var l = new Label(text);
            l.style.opacity = 0.85f;
            l.style.paddingLeft = 4;
            l.style.paddingTop = 1;
            l.style.paddingBottom = 1;
            l.style.whiteSpace = WhiteSpace.Normal;
            return l;
        }
    }
}
