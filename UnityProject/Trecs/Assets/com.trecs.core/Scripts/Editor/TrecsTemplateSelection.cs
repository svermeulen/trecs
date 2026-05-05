using System;
using System.Collections.Generic;
using System.Linq;
using UnityEditor;
using UnityEngine;
using UnityEngine.UIElements;

namespace Trecs
{
    /// <summary>
    /// ScriptableObject proxy used as <see cref="Selection.activeObject"/> when
    /// the user picks a template row in <see cref="TrecsHierarchyWindow"/>.
    /// Carries only a serialized <see cref="TrecsSelectionProxy.Identity"/>
    /// string (e.g. <c>"template:MealEntity"</c>); the inspector resolves
    /// the live <see cref="TemplateRef"/> dynamically against
    /// <see cref="TrecsHierarchyWindow.ActiveSource"/> on every refresh.
    /// </summary>
    public class TrecsTemplateSelection : TrecsSelectionProxy { }

    [CustomEditor(typeof(TrecsTemplateSelection))]
    public class TrecsTemplateSelectionInspector : Editor
    {
        [InitializeOnLoadMethod]
        static void RegisterEditorAccessorName() =>
            TrecsEditorAccessorNames.Register("TrecsTemplateSelectionInspector");

        VisualElement _root;
        Label _statusLabel;
        VisualElement _bodyContainer;
        Label _headerLabel;
        Label _worldLabel;
        Label _entityCountLabel;
        Foldout _tagsFoldout;
        Foldout _baseTemplatesFoldout;
        Foldout _derivedTemplatesFoldout;
        Foldout _componentsFoldout;
        Foldout _inheritedComponentsFoldout;
        Foldout _partitionsFoldout;

        // Composite key of (identity, source-mode-and-name) — the static
        // section gets re-rendered when EITHER the user picks a different
        // proxy OR the active source flips between live and cache. The
        // proxy itself only carries identity; the source is the
        // window-side context that decides which native handle backs it.
        string _renderedKey;

        // Cache the accessor so UpdateEntityCount doesn't allocate a new
        // one on every refresh tick. Without this, the world's accessor
        // count grows by one every 250ms while a template is selected,
        // which kicks the hierarchy window into a structural rebuild
        // every tick — and that rebuild was what fought the user's
        // manual scroll position.
        WorldAccessor _accessor;
        World _accessorWorld;

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
            // template's tree row. Window resolves the identity against
            // its row map regardless of live/cache mode.
            TrecsInspectorLinks.WireHoverPreview(
                _headerLabel,
                () => (target as TrecsTemplateSelection)?.Identity
            );

            _worldLabel = new Label();
            _worldLabel.style.opacity = 0.6f;
            _bodyContainer.Add(_worldLabel);

            _entityCountLabel = new Label();
            _entityCountLabel.style.opacity = 0.85f;
            _bodyContainer.Add(_entityCountLabel);

            _tagsFoldout = new Foldout { text = "Tags", value = true };
            _tagsFoldout.style.marginTop = 6;
            _bodyContainer.Add(_tagsFoldout);

            _baseTemplatesFoldout = new Foldout { text = "Base Templates", value = true };
            _baseTemplatesFoldout.style.marginTop = 6;
            _bodyContainer.Add(_baseTemplatesFoldout);

            _derivedTemplatesFoldout = new Foldout { text = "Derived Templates", value = true };
            _derivedTemplatesFoldout.style.marginTop = 6;
            _bodyContainer.Add(_derivedTemplatesFoldout);

            _componentsFoldout = new Foldout { text = "Components", value = true };
            _componentsFoldout.style.marginTop = 6;
            _bodyContainer.Add(_componentsFoldout);

            _inheritedComponentsFoldout = new Foldout
            {
                text = "Inherited Components",
                value = true,
            };
            _inheritedComponentsFoldout.style.marginTop = 6;
            _bodyContainer.Add(_inheritedComponentsFoldout);

            _partitionsFoldout = new Foldout { text = "Partitions", value = true };
            _partitionsFoldout.style.marginTop = 6;
            _bodyContainer.Add(_partitionsFoldout);

            _root.Add(_bodyContainer);

            Refresh();
            var refreshMs = TrecsDebugWindowSettings.Get().RefreshIntervalMs;
            _root.schedule.Execute(Refresh).Every(refreshMs);

            return _root;
        }

        void Refresh()
        {
            var selection = (TrecsTemplateSelection)target;
            if (selection == null)
            {
                return;
            }

            var src = TrecsHierarchyWindow.ActiveSource;
            if (src == null || string.IsNullOrEmpty(selection.Identity))
            {
                ShowStatus("No template selected.");
                return;
            }

            var tref = src.ResolveTemplate(selection.Identity);
            if (tref == null)
            {
                ShowStatus(
                    $"Template '{selection.name}' not found in {(src.IsLive ? "live world" : "cached schema")} '{src.DisplayName}'."
                );
                return;
            }

            _statusLabel.style.display = DisplayStyle.None;
            _bodyContainer.style.display = DisplayStyle.Flex;

            // Re-render the static section when identity OR source-mode
            // changes (live → cache transitions need a fresh render even
            // for the same template).
            var renderKey = src.RenderKey(selection.Identity);
            if (renderKey != _renderedKey)
            {
                _renderedKey = renderKey;
                var entry = BuildEntryFromRef(src, tref);
                var linker = new InspectorLinker(src);
                RenderStatic(entry, linker, src.DisplayName, !src.IsLive);
            }

            UpdateRuntimeFields(src, tref);
        }

        // Folds the source's TemplateRef into the cache schema shape so
        // RenderStatic stays mode-agnostic. Live mode also walks the
        // world for derived-template lookup; cache mode reads the
        // pre-computed list off TrecsSchemaTemplate.
        static TrecsSchemaTemplate BuildEntryFromRef(ITrecsSchemaSource src, TemplateRef tref)
        {
            if (tref.CacheNative != null)
            {
                return tref.CacheNative;
            }
            return BuildEntryFromLive(
                (src as LiveSchemaSource)?.World,
                tref.LiveTemplate,
                tref.LiveResolved
            );
        }

        // Folds live Template + ResolvedTemplate into the same shape the
        // cache stores. RenderStatic then reads from the entry — same code
        // path as cache mode. World ref needed so we can compute the
        // derived-templates list (every other template that lists this one
        // as a base).
        static TrecsSchemaTemplate BuildEntryFromLive(
            World world,
            Template template,
            ResolvedTemplate rt
        )
        {
            var entry = new TrecsSchemaTemplate
            {
                DebugName = template.DebugName,
                IsResolved = rt != null,
            };
            if (rt != null)
            {
                if (!rt.AllTags.IsNull)
                {
                    foreach (var tag in rt.AllTags.Tags)
                    {
                        entry.AllTagNames.Add(tag.ToString());
                    }
                }
                foreach (
                    var b in rt.AllBaseTemplates.OrderBy(
                        t => t.DebugName,
                        StringComparer.OrdinalIgnoreCase
                    )
                )
                {
                    entry.BaseTemplateNames.Add(b.DebugName ?? "(unnamed)");
                }
                // Split components into direct (declared on this template)
                // and inherited (pulled in from base templates). Use the
                // same display-name policy as the hierarchy and schema
                // cache so the linker's name → Type lookup matches and
                // the rows are clickable.
                var directTypes = new HashSet<Type>();
                foreach (var ld in template.LocalComponentDeclarations)
                {
                    if (ld.ComponentType != null)
                        directTypes.Add(ld.ComponentType);
                }
                foreach (var d in rt.ComponentDeclarations)
                {
                    if (d.ComponentType == null)
                        continue;
                    var n = TrecsHierarchyWindow.ComponentTypeDisplayName(d.ComponentType);
                    entry.ComponentTypeNames.Add(n);
                    if (directTypes.Contains(d.ComponentType))
                    {
                        entry.DirectComponentTypeNames.Add(n);
                    }
                    else
                    {
                        entry.InheritedComponentTypeNames.Add(n);
                    }
                }
                entry.ComponentTypeNames.Sort(StringComparer.OrdinalIgnoreCase);
                entry.DirectComponentTypeNames.Sort(StringComparer.OrdinalIgnoreCase);
                entry.InheritedComponentTypeNames.Sort(StringComparer.OrdinalIgnoreCase);
                foreach (var p in rt.Partitions)
                {
                    var sp = new TrecsSchemaPartition();
                    if (!p.IsNull)
                    {
                        foreach (var t in p.Tags)
                        {
                            sp.TagNames.Add(t.ToString());
                        }
                    }
                    entry.Partitions.Add(sp);
                }
            }
            // Derived templates: every other resolved template whose base
            // chain includes this one. Live mode walks the world; the
            // schema cache writer pre-computes the same list.
            try
            {
                foreach (var other in world.WorldInfo.ResolvedTemplates)
                {
                    if (other.Template == template)
                        continue;
                    foreach (var b in other.AllBaseTemplates)
                    {
                        if (b == template)
                        {
                            entry.DerivedTemplateNames.Add(other.DebugName ?? "(unnamed)");
                            break;
                        }
                    }
                }
                entry.DerivedTemplateNames.Sort(StringComparer.OrdinalIgnoreCase);
            }
            catch (Exception) { }
            return entry;
        }

        void RenderStatic(
            TrecsSchemaTemplate entry,
            InspectorLinker linker,
            string worldName,
            bool isCache
        )
        {
            _headerLabel.text = ObjectNames.NicifyVariableName(entry.DebugName ?? "(unnamed)");
            _worldLabel.text = isCache
                ? $"World: {worldName ?? "(unknown)"} (cached)"
                : $"World: {worldName ?? "(unnamed)"}";

            FillFoldout(_tagsFoldout, entry.AllTagNames, linker.TagLink);
            FillFoldout(_baseTemplatesFoldout, entry.BaseTemplateNames, linker.TemplateLink);
            FillFoldout(_derivedTemplatesFoldout, entry.DerivedTemplateNames, linker.TemplateLink);

            // Direct components — fall back to the legacy combined list
            // when reading a cache written before the direct/inherited
            // split (DirectComponentTypeNames stays empty for old caches).
            var directNames =
                entry.DirectComponentTypeNames != null && entry.DirectComponentTypeNames.Count > 0
                    ? entry.DirectComponentTypeNames
                    : (
                        entry.InheritedComponentTypeNames != null
                        && entry.InheritedComponentTypeNames.Count > 0
                            ? new List<string>()
                            : entry.ComponentTypeNames
                    );
            FillFoldout(_componentsFoldout, directNames, linker.ComponentTypeLink);
            FillFoldout(
                _inheritedComponentsFoldout,
                entry.InheritedComponentTypeNames,
                linker.ComponentTypeLink
            );

            // Partitions are bracketed rows with a tag link per element.
            // Reuse the linker for the per-tag clickable pieces.
            _partitionsFoldout.Clear();
            if (entry.Partitions == null || entry.Partitions.Count == 0)
            {
                _partitionsFoldout.style.display = DisplayStyle.None;
            }
            else
            {
                _partitionsFoldout.style.display = DisplayStyle.Flex;
                foreach (var p in entry.Partitions)
                {
                    _partitionsFoldout.Add(BuildPartitionRow(p, linker));
                }
            }
        }

        static VisualElement BuildPartitionRow(
            TrecsSchemaPartition partition,
            InspectorLinker linker
        )
        {
            var row = new VisualElement();
            row.style.flexDirection = FlexDirection.Row;
            row.style.flexWrap = Wrap.Wrap;
            row.style.alignItems = Align.Center;
            row.style.paddingLeft = 4;
            row.style.paddingTop = 1;
            row.style.paddingBottom = 1;
            var open = new Label("[");
            open.style.opacity = 0.6f;
            open.style.alignSelf = Align.Center;
            row.Add(open);
            if (partition.TagNames != null)
            {
                var first = true;
                foreach (var n in partition.TagNames)
                {
                    if (!first)
                    {
                        var sep = new Label(", ");
                        sep.style.opacity = 0.6f;
                        sep.style.alignSelf = Align.Center;
                        row.Add(sep);
                    }
                    first = false;
                    row.Add(linker.TagLink(n));
                }
            }
            var close = new Label("]");
            close.style.opacity = 0.6f;
            close.style.alignSelf = Align.Center;
            row.Add(close);
            return row;
        }

        // Renders names as link rows; hides the foldout entirely when the
        // list is empty (no need to show "(none)" — an absent section
        // communicates the same thing without taking up screen space).
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

        void UpdateRuntimeFields(ITrecsSchemaSource src, TemplateRef tref)
        {
            if (!src.IsLive)
            {
                _entityCountLabel.text = tref.IsResolved
                    ? "Entities: (cached — N/A)"
                    : "Entities: (abstract template — none)";
                return;
            }
            var world = (src as LiveSchemaSource)?.World;
            if (world == null || world.IsDisposed)
            {
                _entityCountLabel.text = "Entities: (world unavailable)";
                return;
            }
            if (tref.LiveResolved == null)
            {
                _entityCountLabel.text =
                    "Entities: (template not registered as a resolved template)";
                return;
            }
            try
            {
                if (_accessor == null || _accessorWorld != world)
                {
                    _accessor = world.CreateAccessor("TrecsTemplateSelectionInspector");
                    _accessorWorld = world;
                }
                int total = 0;
                foreach (var g in tref.LiveResolved.Groups)
                {
                    total += _accessor.CountEntitiesInGroup(g);
                }
                _entityCountLabel.text = $"Entities: {total}";
            }
            catch
            {
                _entityCountLabel.text = "Entities: (unavailable)";
            }
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
    }
}
