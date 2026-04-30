using System;
using System.Collections.Generic;
using System.Linq;
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
    /// the user picks a component type row in <see cref="TrecsHierarchyWindow"/>.
    /// Identifies the component by its <see cref="System.Type"/>; the world
    /// reference is held weakly so we can list its templates / accessors at
    /// inspect time without keeping it alive.
    /// </summary>
    public class TrecsComponentTypeSelection : ScriptableObject
    {
        [NonSerialized]
        WeakReference<World> _worldRef;

        [NonSerialized]
        public Type ComponentType;

        [NonSerialized]
        public TrecsSchema CacheSchema;

        [NonSerialized]
        public TrecsSchemaComponentType CacheComponent;

        public World GetWorld()
        {
            if (_worldRef == null)
            {
                return null;
            }
            return _worldRef.TryGetTarget(out var w) ? w : null;
        }

        public void Set(World world, Type componentType)
        {
            _worldRef = world == null ? null : new WeakReference<World>(world);
            ComponentType = componentType;
            CacheSchema = null;
            CacheComponent = null;
            name = componentType?.Name ?? "Component";
        }

        public void SetCache(TrecsSchema schema, TrecsSchemaComponentType entry)
        {
            _worldRef = null;
            ComponentType = null;
            CacheSchema = schema;
            CacheComponent = entry;
            name = entry?.DisplayName ?? "Component";
        }
    }

    [CustomEditor(typeof(TrecsComponentTypeSelection))]
    public class TrecsComponentTypeSelectionInspector : Editor
    {
        [InitializeOnLoadMethod]
        static void RegisterEditorAccessorName() =>
            TrecsEditorAccessorNames.Register("TrecsComponentTypeSelectionInspector");

        VisualElement _root;
        Label _statusLabel;
        VisualElement _bodyContainer;
        Label _headerLabel;
        Label _typeLabel;
        Label _attributesLabel;
        Foldout _fieldsFoldout;
        Foldout _templatesFoldout;
        Foldout _readByFoldout;
        Foldout _writtenByFoldout;

        // Identity of the currently-rendered entry: Type ref in live mode,
        // TrecsSchemaComponentType ref in cache mode.
        object _renderedEntryKey;
        int _lastReaderHash;
        int _lastWriterHash;

        public override VisualElement CreateInspectorGUI()
        {
            _root = new VisualElement();
            _root.style.paddingLeft = 8;
            _root.style.paddingRight = 8;
            _root.style.paddingTop = 8;
            _root.style.paddingBottom = 8;

            _statusLabel = new Label();
            _statusLabel.style.opacity = 0.7f;
            _statusLabel.style.whiteSpace = WhiteSpace.Normal;
            _statusLabel.style.display = DisplayStyle.None;
            _root.Add(_statusLabel);

            _bodyContainer = new VisualElement();

            _headerLabel = new Label();
            _headerLabel.style.unityFontStyleAndWeight = FontStyle.Bold;
            _headerLabel.style.fontSize = 13;
            _bodyContainer.Add(_headerLabel);

            // Shift+hover the heading → hierarchy scrolls back to this
            // component-type's tree row.
            TrecsInspectorLinks.WireHoverPreviewComponentType(
                _headerLabel,
                () =>
                {
                    var sel = target as TrecsComponentTypeSelection;
                    return (sel?.GetWorld(), sel?.ComponentType);
                }
            );

            _typeLabel = new Label();
            _typeLabel.style.opacity = 0.6f;
            _typeLabel.style.whiteSpace = WhiteSpace.Normal;
            _bodyContainer.Add(_typeLabel);

            _attributesLabel = new Label();
            _attributesLabel.style.opacity = 0.6f;
            _attributesLabel.style.whiteSpace = WhiteSpace.Normal;
            _bodyContainer.Add(_attributesLabel);

            _fieldsFoldout = new Foldout { text = "Fields", value = true };
            _fieldsFoldout.style.marginTop = 6;
            _bodyContainer.Add(_fieldsFoldout);

            _templatesFoldout = new Foldout { text = "Templates", value = true };
            _templatesFoldout.style.marginTop = 6;
            _bodyContainer.Add(_templatesFoldout);

            _readByFoldout = new Foldout { text = "Read by", value = true };
            _readByFoldout.style.marginTop = 6;
            _bodyContainer.Add(_readByFoldout);

            _writtenByFoldout = new Foldout { text = "Written by", value = true };
            _writtenByFoldout.style.marginTop = 6;
            _bodyContainer.Add(_writtenByFoldout);

            _root.Add(_bodyContainer);

            Refresh();
            var refreshMs = TrecsDebugWindowSettings.Get().RefreshIntervalMs;
            _root.schedule.Execute(Refresh).Every(refreshMs);

            return _root;
        }

        void Refresh()
        {
            var selection = (TrecsComponentTypeSelection)target;
            if (selection == null)
            {
                return;
            }

            ResolveIdentity(selection, out var liveWorld, out var liveType, out var identity);

            if (identity == null)
            {
                var w = selection.GetWorld();
                ShowStatus(
                    w == null || w.IsDisposed
                        ? "No component type selected — world unavailable."
                        : "No component type selected."
                );
                return;
            }

            _statusLabel.style.display = DisplayStyle.None;
            _bodyContainer.style.display = DisplayStyle.Flex;

            if (!Equals(identity, _renderedEntryKey))
            {
                _renderedEntryKey = identity;
                _lastReaderHash = 0;
                _lastWriterHash = 0;
                var entry =
                    liveWorld != null
                        ? BuildEntryFromLive(liveWorld, liveType)
                        : selection.CacheComponent;
                var linker =
                    liveWorld != null
                        ? (InspectorLinker)new LiveInspectorLinker(liveWorld)
                        : new CacheInspectorLinker(selection.CacheSchema);
                var attributes = liveType != null ? CollectAttributes(liveType) : null;
                var owningTemplates = ResolveOwningTemplates(
                    entry,
                    liveWorld,
                    selection.CacheSchema
                );
                RenderStatic(entry, linker, attributes, owningTemplates);
            }

            UpdateRuntimeFields(
                liveWorld,
                liveType,
                selection.CacheSchema,
                selection.CacheComponent
            );
        }

        static void ResolveIdentity(
            TrecsComponentTypeSelection selection,
            out World liveWorld,
            out Type liveType,
            out object identity
        )
        {
            liveWorld = null;
            liveType = null;
            identity = null;

            var world = selection.GetWorld();
            if (world != null && !world.IsDisposed && selection.ComponentType != null)
            {
                liveWorld = world;
                liveType = selection.ComponentType;
                identity = selection.ComponentType;
                return;
            }
            if (selection.CacheComponent != null)
            {
                identity = selection.CacheComponent;
            }
        }

        // Walks the live world's resolved templates to gather every template
        // that owns this component, then folds the cross-links into a
        // TrecsSchemaComponentType entry — same shape as the cache.
        static TrecsSchemaComponentType BuildEntryFromLive(World world, Type t)
        {
            var entry = new TrecsSchemaComponentType
            {
                DisplayName = TrecsHierarchyWindow.ComponentTypeDisplayName(t),
                FullName = t.FullName ?? t.Name,
            };
            // Field walk shared with the cache writer so live and cache
            // rendering produce identical entries.
            TrecsSchemaCache.PopulateFields(entry.Fields, t);
            return entry;
        }

        static List<string> CollectAttributes(Type t)
        {
            var attrs = new List<string>();
            try
            {
                if (t.GetCustomAttributes(typeof(UnwrapAttribute), false).Length > 0)
                {
                    attrs.Add("[Unwrap]");
                }
            }
            catch { }
            return attrs;
        }

        void RenderStatic(
            TrecsSchemaComponentType entry,
            InspectorLinker linker,
            List<string> attributes,
            List<string> owningTemplates
        )
        {
            _headerLabel.text = entry.DisplayName ?? "(unnamed)";
            _typeLabel.text = entry.FullName ?? string.Empty;

            if (attributes == null || attributes.Count == 0)
            {
                _attributesLabel.text = string.Empty;
                _attributesLabel.style.display = DisplayStyle.None;
            }
            else
            {
                _attributesLabel.text = string.Join(" ", attributes);
                _attributesLabel.style.display = DisplayStyle.Flex;
            }

            _fieldsFoldout.Clear();
            if (entry.Fields == null || entry.Fields.Count == 0)
            {
                _fieldsFoldout.Add(MakeMutedLine("(none)"));
            }
            else
            {
                foreach (var f in entry.Fields)
                {
                    _fieldsFoldout.Add(MakeFieldRow(f.Name, f.TypeName));
                }
            }

            _templatesFoldout.Clear();
            if (owningTemplates == null || owningTemplates.Count == 0)
            {
                _templatesFoldout.Add(MakeMutedLine("(none)"));
            }
            else
            {
                foreach (var name in owningTemplates)
                {
                    _templatesFoldout.Add(linker.TemplateLink(name));
                }
            }
        }

        // Both modes derive owning-templates from the relevant source: live
        // walks ResolvedTemplates, cache walks schema.Templates. Returned
        // sorted so the foldout is stable.
        static List<string> ResolveOwningTemplates(
            TrecsSchemaComponentType entry,
            World liveWorld,
            TrecsSchema cacheSchema
        )
        {
            var names = new List<string>();
            if (liveWorld != null && !liveWorld.IsDisposed)
            {
                try
                {
                    foreach (var rt in liveWorld.WorldInfo.ResolvedTemplates)
                    {
                        foreach (var d in rt.ComponentDeclarations)
                        {
                            if (
                                d.ComponentType != null
                                && TrecsHierarchyWindow.ComponentTypeDisplayName(d.ComponentType)
                                    == entry.DisplayName
                            )
                            {
                                names.Add(rt.DebugName ?? "(unnamed)");
                                break;
                            }
                        }
                    }
                }
                catch (Exception) { }
            }
            else if (cacheSchema?.Templates != null)
            {
                foreach (var st in cacheSchema.Templates)
                {
                    if (
                        st.ComponentTypeNames != null
                        && st.ComponentTypeNames.Contains(entry.DisplayName)
                    )
                    {
                        names.Add(st.DebugName ?? "(unnamed)");
                    }
                }
            }
            names.Sort(StringComparer.OrdinalIgnoreCase);
            return names;
        }

        // Per-tick refresh of read-by / written-by lists. Live mode pulls
        // current names from the access tracker (changes as systems run);
        // cache mode pulls a frozen list from schema.Access.
        void UpdateRuntimeFields(
            World world,
            Type t,
            TrecsSchema cacheSchema,
            TrecsSchemaComponentType cacheEntry
        )
        {
            IReadOnlyCollection<string> readers = null;
            IReadOnlyCollection<string> writers = null;
            InspectorLinker linker;

            if (world != null && !world.IsDisposed && t != null)
            {
                var tracker = TrecsAccessRegistry.GetTracker(world);
                if (tracker != null)
                {
                    var componentId = new ComponentId(TypeIdProvider.GetTypeId(t));
                    readers = tracker.GetReadersOf(componentId);
                    writers = tracker.GetWritersOf(componentId);
                }
                linker = new LiveInspectorLinker(world);
            }
            else
            {
                TrecsSchemaAccessInfo access = null;
                if (cacheSchema?.Access != null && cacheEntry != null)
                {
                    foreach (var a in cacheSchema.Access)
                    {
                        if (a.ComponentDisplayName == cacheEntry.DisplayName)
                        {
                            access = a;
                            break;
                        }
                    }
                }
                readers = access?.ReadBySystems;
                writers = access?.WrittenBySystems;
                linker = new CacheInspectorLinker(cacheSchema);
            }

            ApplyNameList(_readByFoldout, readers, ref _lastReaderHash, linker);
            ApplyNameList(_writtenByFoldout, writers, ref _lastWriterHash, linker);
        }

        static void ApplyNameList(
            Foldout foldout,
            IReadOnlyCollection<string> names,
            ref int lastHash,
            InspectorLinker linker
        )
        {
            int hash = HashOfNames(names);
            if (hash == lastHash)
            {
                return;
            }
            lastHash = hash;
            foldout.Clear();
            if (names == null || names.Count == 0)
            {
                foldout.Add(MakeMutedLine("(none recorded)"));
                return;
            }
            var sorted = new List<string>(names);
            sorted.Sort(StringComparer.OrdinalIgnoreCase);
            foreach (var n in sorted)
            {
                foldout.Add(linker.AccessorLink(n));
            }
        }

        static int HashOfNames(IReadOnlyCollection<string> names)
        {
            if (names == null || names.Count == 0)
            {
                return 0;
            }
            int h = names.Count;
            foreach (var n in names)
            {
                h ^= n == null ? 0 : n.GetHashCode();
            }
            return h;
        }

        void ShowStatus(string text)
        {
            _statusLabel.text = text;
            _statusLabel.style.display = DisplayStyle.Flex;
            _bodyContainer.style.display = DisplayStyle.None;
            _renderedEntryKey = null;
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

        // Two-column row for fields: name on the left, type on the right
        // (dimmer). Mirrors the Unity inspector's variable display style.
        static VisualElement MakeFieldRow(string name, string typeName)
        {
            var row = new VisualElement();
            row.style.flexDirection = FlexDirection.Row;
            row.style.paddingLeft = 4;
            row.style.paddingTop = 1;
            row.style.paddingBottom = 1;
            var n = new Label(name ?? "?");
            n.style.flexGrow = 1;
            row.Add(n);
            var t = new Label(typeName ?? "?");
            t.style.opacity = 0.55f;
            t.style.marginLeft = 8;
            row.Add(t);
            return row;
        }
    }
}
