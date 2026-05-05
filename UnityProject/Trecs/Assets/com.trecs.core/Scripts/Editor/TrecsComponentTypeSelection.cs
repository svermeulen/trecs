using System;
using System.Collections.Generic;
using UnityEditor;
using UnityEngine;
using UnityEngine.UIElements;

namespace Trecs
{
    /// <summary>
    /// ScriptableObject proxy used as <see cref="Selection.activeObject"/> when
    /// the user picks a component type row in <see cref="TrecsHierarchyWindow"/>.
    /// Carries only a serialized <see cref="TrecsSelectionProxy.Identity"/>
    /// string (e.g. <c>"component:HealthData"</c>); the inspector resolves
    /// the live <see cref="ComponentTypeRef"/> dynamically against
    /// <see cref="TrecsHierarchyWindow.ActiveSource"/> on every refresh.
    /// </summary>
    public class TrecsComponentTypeSelection : TrecsSelectionProxy { }

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

        // Composite render key — identity + source mode + source name.
        // Static section re-renders when any of these change.
        string _renderedKey;
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
            TrecsInspectorLinks.WireHoverPreview(
                _headerLabel,
                () => (target as TrecsComponentTypeSelection)?.Identity
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

            var src = TrecsHierarchyWindow.ActiveSource;
            if (src == null || string.IsNullOrEmpty(selection.Identity))
            {
                ShowStatus("No component type selected.");
                return;
            }

            var cref = src.ResolveComponentType(selection.Identity);
            if (cref == null)
            {
                ShowStatus(
                    $"Component '{selection.name}' not found in {(src.IsLive ? "live world" : "cached schema")} '{src.DisplayName}'."
                );
                return;
            }

            _statusLabel.style.display = DisplayStyle.None;
            _bodyContainer.style.display = DisplayStyle.Flex;

            var renderKey = src.RenderKey(selection.Identity);
            if (renderKey != _renderedKey)
            {
                _renderedKey = renderKey;
                _lastReaderHash = 0;
                _lastWriterHash = 0;
                var entry = BuildEntryFromRef(cref);
                var linker = new InspectorLinker(src);
                var attributes = cref.LiveType != null ? CollectAttributes(cref.LiveType) : null;
                var owningTemplates = ResolveOwningTemplates(src, cref.DisplayName);
                RenderStatic(entry, linker, attributes, owningTemplates);
            }

            UpdateRuntimeFields(src, cref);
        }

        static TrecsSchemaComponentType BuildEntryFromRef(ComponentTypeRef cref)
        {
            if (cref.CacheNative != null)
            {
                return cref.CacheNative;
            }
            return BuildEntryFromLive(cref.LiveType);
        }

        // Folds a live System.Type into the cache schema shape so the
        // RenderStatic path stays mode-agnostic.
        static TrecsSchemaComponentType BuildEntryFromLive(Type t)
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

        // Walks the source's templates to gather every template that
        // owns this component. Live and cache produce the same answer
        // because LiveSchemaSource projects ComponentTypeNames identically
        // to the cache writer; the inspector reads from whichever native
        // form the ref carries.
        static List<string> ResolveOwningTemplates(ITrecsSchemaSource src, string componentName)
        {
            var names = new List<string>();
            foreach (var tref in src.Templates)
            {
                if (tref.CacheNative != null)
                {
                    if (
                        tref.CacheNative.ComponentTypeNames != null
                        && tref.CacheNative.ComponentTypeNames.Contains(componentName)
                    )
                    {
                        names.Add(tref.DebugName ?? "(unnamed)");
                    }
                    continue;
                }
                var rt = tref.LiveResolved;
                if (rt == null)
                {
                    continue;
                }
                foreach (var d in rt.ComponentDeclarations)
                {
                    if (
                        d.ComponentType != null
                        && TrecsHierarchyWindow.ComponentTypeDisplayName(d.ComponentType)
                            == componentName
                    )
                    {
                        names.Add(tref.DebugName ?? "(unnamed)");
                        break;
                    }
                }
            }
            names.Sort(StringComparer.OrdinalIgnoreCase);
            return names;
        }

        // Per-tick refresh of read-by / written-by lists. Both modes go
        // through src.AccessTracker — live wraps the live runtime tracker,
        // cache wraps schema.Access.
        void UpdateRuntimeFields(ITrecsSchemaSource src, ComponentTypeRef cref)
        {
            IReadOnlyCollection<string> readers = null;
            IReadOnlyCollection<string> writers = null;
            var linker = new InspectorLinker(src);

            if (!string.IsNullOrEmpty(cref.DisplayName))
            {
                readers = src.AccessTracker.GetReadersOfComponent(cref.DisplayName);
                writers = src.AccessTracker.GetWritersOfComponent(cref.DisplayName);
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
            _renderedKey = null;
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
