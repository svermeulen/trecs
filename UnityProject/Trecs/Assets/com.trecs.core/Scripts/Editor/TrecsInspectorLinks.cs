using System;
using UnityEditor;
using UnityEngine;
using UnityEngine.UIElements;

namespace Trecs
{
    /// <summary>
    /// Helper for the clickable cross-link rows in the Trecs hierarchy
    /// inspectors. Each link is an icon + text row with three behaviors:
    /// <list type="bullet">
    /// <item>hover → fires a preview event so the hierarchy window can
    /// scroll to and highlight the target row without changing selection,
    /// </item>
    /// <item>leave → clears the hierarchy preview highlight,</item>
    /// <item>click → routes <see cref="Selection.activeObject"/> through the
    /// matching <see cref="TrecsSelectionProxies"/> instance, same as a
    /// click on the corresponding hierarchy row.</item>
    /// </list>
    /// </summary>
    public static class TrecsInspectorLinks
    {
        static readonly Color _hoverColor = new(0.4f, 0.7f, 1.0f);

        // Fired while the user hovers an inspector link row — the hierarchy
        // window scrolls to the matching tree row and applies a transient
        // highlight. PreviewClearRequested removes the highlight when the
        // mouse leaves the link. None of these change Selection.activeObject;
        // selection only happens on click, via the SelectXxx helpers below.
        public static event Action<World, Template> PreviewTemplateRequested;
        public static event Action<World, Type> PreviewComponentTypeRequested;
        public static event Action<World, int> PreviewAccessorRequested;
        public static event Action<World, EntityHandle> PreviewEntityRequested;
        public static event Action<World, SetId> PreviewSetRequested;
        public static event Action<World, Tag> PreviewTagRequested;
        public static event Action PreviewClearRequested;

        // Cache-mode counterparts of the live preview events. Cache rows have
        // no World/Template/etc., so the payload is the schema entry that the
        // hierarchy uses to look up the matching tree row id.
        public static event Action<TrecsSchemaTemplate> PreviewTemplateCacheRequested;
        public static event Action<TrecsSchemaComponentType> PreviewComponentTypeCacheRequested;
        public static event Action<string> PreviewAccessorCacheRequested;
        public static event Action<TrecsSchemaSet> PreviewSetCacheRequested;
        public static event Action<TrecsSchemaTag> PreviewTagCacheRequested;

        static Texture _iconTemplate;
        static Texture _iconScript;
        static Texture _iconScriptable;
        static Texture _iconTag;

        /// <summary>
        /// Builds a plain text-only clickable Label that calls
        /// <paramref name="onClick"/> on MouseDown and stops the event from
        /// bubbling. Kept for callers that don't need an icon or hover
        /// preview — prefer <see cref="MakeTemplateLink"/> et al for the
        /// new icon + preview behavior.
        /// </summary>
        public static Label MakeLink(string text, Action onClick)
        {
            var l = new Label(text);
            l.style.opacity = 0.9f;
            l.style.paddingLeft = 4;
            l.style.paddingTop = 1;
            l.style.paddingBottom = 1;
            l.style.whiteSpace = WhiteSpace.Normal;
            l.RegisterCallback<MouseEnterEvent>(_ => l.style.color = _hoverColor);
            l.RegisterCallback<MouseLeaveEvent>(_ =>
                l.style.color = new StyleColor(StyleKeyword.Null)
            );
            l.RegisterCallback<MouseDownEvent>(evt =>
            {
                onClick?.Invoke();
                evt.StopPropagation();
            });
            return l;
        }

        public static VisualElement MakeTemplateLink(World world, Template template, string text)
        {
            return BuildLinkRow(
                IconForTemplate(),
                text,
                onMouseEnter: () => PreviewTemplateRequested?.Invoke(world, template),
                onMouseLeave: () => PreviewClearRequested?.Invoke(),
                onClick: () => SelectTemplate(world, template)
            );
        }

        public static VisualElement MakeComponentTypeLink(
            World world,
            Type componentType,
            string text
        )
        {
            return BuildLinkRow(
                IconForComponentType(),
                text,
                onMouseEnter: () => PreviewComponentTypeRequested?.Invoke(world, componentType),
                onMouseLeave: () => PreviewClearRequested?.Invoke(),
                onClick: () => SelectComponentType(world, componentType)
            );
        }

        public static VisualElement MakeAccessorLink(
            World world,
            int accessorId,
            string text,
            string displayName = null
        )
        {
            return BuildLinkRow(
                IconForAccessor(),
                text,
                onMouseEnter: () => PreviewAccessorRequested?.Invoke(world, accessorId),
                onMouseLeave: () => PreviewClearRequested?.Invoke(),
                onClick: () => SelectAccessor(world, accessorId, displayName ?? text)
            );
        }

        // Small "go to component type inspector" button — renders as an
        // arrow glyph rather than a full row so it can sit inline next to
        // a Foldout title or PropertyField label without taking visual
        // ownership of the line.
        public static VisualElement MakeComponentTypeJumpButton(World world, Type componentType)
        {
            return BuildJumpGlyph(
                tooltip: "Inspect component type",
                onMouseEnter: () => PreviewComponentTypeRequested?.Invoke(world, componentType),
                onMouseLeave: () => PreviewClearRequested?.Invoke(),
                onClick: () => SelectComponentType(world, componentType)
            );
        }

        // Wires hover-only preview behavior onto an existing element (e.g.
        // an inspector's title Label). MouseEnter fires the matching
        // PreviewXxxRequested so the hierarchy window scrolls to and
        // highlights the row; MouseLeave clears the highlight. No click
        // handler — the element is presumably already showing the data
        // for the previewed item, so clicking it would be a no-op.
        //
        // The resolver-style signatures (Func<...>) let inspectors call
        // these once at build-time — the resolver is invoked on each
        // hover so the inspector can change the underlying selection
        // without re-registering callbacks.
        public static void WireHoverPreviewTemplate(
            VisualElement element,
            Func<(World, Template)> resolve
        )
        {
            element.RegisterCallback<MouseEnterEvent>(evt =>
            {
                if (!evt.shiftKey)
                    return;
                var (w, t) = resolve();
                if (w != null && t != null)
                {
                    PreviewTemplateRequested?.Invoke(w, t);
                }
            });
            element.RegisterCallback<MouseLeaveEvent>(_ => PreviewClearRequested?.Invoke());
        }

        public static void WireHoverPreviewComponentType(
            VisualElement element,
            Func<(World, Type)> resolve
        )
        {
            element.RegisterCallback<MouseEnterEvent>(evt =>
            {
                if (!evt.shiftKey)
                    return;
                var (w, t) = resolve();
                if (w != null && t != null)
                {
                    PreviewComponentTypeRequested?.Invoke(w, t);
                }
            });
            element.RegisterCallback<MouseLeaveEvent>(_ => PreviewClearRequested?.Invoke());
        }

        public static void WireHoverPreviewAccessor(
            VisualElement element,
            Func<(World, int)> resolve
        )
        {
            element.RegisterCallback<MouseEnterEvent>(evt =>
            {
                if (!evt.shiftKey)
                    return;
                var (w, id) = resolve();
                if (w != null && id >= 0)
                {
                    PreviewAccessorRequested?.Invoke(w, id);
                }
            });
            element.RegisterCallback<MouseLeaveEvent>(_ => PreviewClearRequested?.Invoke());
        }

        public static void WireHoverPreviewEntity(
            VisualElement element,
            Func<(World, EntityHandle)> resolve
        )
        {
            element.RegisterCallback<MouseEnterEvent>(evt =>
            {
                if (!evt.shiftKey)
                    return;
                var (w, h) = resolve();
                if (w != null)
                {
                    PreviewEntityRequested?.Invoke(w, h);
                }
            });
            element.RegisterCallback<MouseLeaveEvent>(_ => PreviewClearRequested?.Invoke());
        }

        public static void WireHoverPreviewSet(VisualElement element, Func<(World, SetId)> resolve)
        {
            element.RegisterCallback<MouseEnterEvent>(evt =>
            {
                if (!evt.shiftKey)
                    return;
                var (w, id) = resolve();
                if (w != null && id.Id != 0)
                {
                    PreviewSetRequested?.Invoke(w, id);
                }
            });
            element.RegisterCallback<MouseLeaveEvent>(_ => PreviewClearRequested?.Invoke());
        }

        public static void WireHoverPreviewTag(VisualElement element, Func<(World, Tag)> resolve)
        {
            element.RegisterCallback<MouseEnterEvent>(evt =>
            {
                if (!evt.shiftKey)
                    return;
                var (w, tag) = resolve();
                if (w != null && tag.Guid != 0)
                {
                    PreviewTagRequested?.Invoke(w, tag);
                }
            });
            element.RegisterCallback<MouseLeaveEvent>(_ => PreviewClearRequested?.Invoke());
        }

        public static VisualElement MakeTagLink(World world, Tag tag, string text)
        {
            return BuildLinkRow(
                IconForTag(),
                text,
                onMouseEnter: () => PreviewTagRequested?.Invoke(world, tag),
                onMouseLeave: () => PreviewClearRequested?.Invoke(),
                onClick: () => SelectTag(world, tag)
            );
        }

        public static VisualElement MakeSetLink(World world, EntitySet entitySet, string text)
        {
            return BuildLinkRow(
                IconForFolder(),
                text,
                onMouseEnter: () => PreviewSetRequested?.Invoke(world, entitySet.Id),
                onMouseLeave: () => PreviewClearRequested?.Invoke(),
                onClick: () => SelectSet(world, entitySet)
            );
        }

        static VisualElement BuildJumpGlyph(
            string tooltip,
            Action onMouseEnter,
            Action onMouseLeave,
            Action onClick
        )
        {
            // "↗" reads as "open / jump to" in most icon vocabularies and
            // doesn't depend on a Unity built-in icon name being available.
            var glyph = new Label("↗");
            glyph.tooltip = tooltip;
            glyph.style.fontSize = 12;
            glyph.style.opacity = 0.45f;
            glyph.style.unityFontStyleAndWeight = FontStyle.Bold;
            glyph.style.marginLeft = 6;
            glyph.style.marginTop = 1;
            glyph.style.flexShrink = 0;

            glyph.RegisterCallback<MouseEnterEvent>(evt =>
            {
                glyph.style.opacity = 1f;
                glyph.style.color = _hoverColor;
                // Preview-scroll only on shift+hover so plain hover doesn't
                // jank the hierarchy around. Click navigates regardless.
                if (evt.shiftKey)
                {
                    onMouseEnter?.Invoke();
                }
            });
            glyph.RegisterCallback<MouseLeaveEvent>(_ =>
            {
                glyph.style.opacity = 0.45f;
                glyph.style.color = new StyleColor(StyleKeyword.Null);
                onMouseLeave?.Invoke();
            });
            glyph.RegisterCallback<MouseDownEvent>(evt =>
            {
                onClick?.Invoke();
                evt.StopPropagation();
            });
            return glyph;
        }

        static VisualElement BuildLinkRow(
            Texture icon,
            string text,
            Action onMouseEnter,
            Action onMouseLeave,
            Action onClick
        )
        {
            var row = new VisualElement();
            row.style.flexDirection = FlexDirection.Row;
            row.style.alignItems = Align.Center;
            row.style.paddingLeft = 4;
            row.style.paddingTop = 1;
            row.style.paddingBottom = 1;

            if (icon != null)
            {
                var img = new Image { image = icon, scaleMode = ScaleMode.ScaleToFit };
                img.style.width = 14;
                img.style.height = 14;
                img.style.marginRight = 4;
                img.style.flexShrink = 0;
                row.Add(img);
            }

            var label = new Label(text);
            label.style.opacity = 0.9f;
            label.style.whiteSpace = WhiteSpace.Normal;
            label.style.flexGrow = 1;
            row.Add(label);

            row.RegisterCallback<MouseEnterEvent>(evt =>
            {
                label.style.color = _hoverColor;
                // Preview-scroll only on shift+hover so plain hover doesn't
                // jank the hierarchy around. Click navigates regardless.
                if (evt.shiftKey)
                {
                    onMouseEnter?.Invoke();
                }
            });
            row.RegisterCallback<MouseLeaveEvent>(_ =>
            {
                label.style.color = new StyleColor(StyleKeyword.Null);
                onMouseLeave?.Invoke();
            });
            row.RegisterCallback<MouseDownEvent>(evt =>
            {
                onClick?.Invoke();
                evt.StopPropagation();
            });
            return row;
        }

        public static void SelectTemplate(World world, Template template)
        {
            if (world == null || template == null)
            {
                return;
            }
            var p = TrecsSelectionProxies.NextTemplate();
            p.Set(world, template);
            Selection.activeObject = p;
        }

        public static void SelectComponentType(World world, Type componentType)
        {
            if (world == null || componentType == null)
            {
                return;
            }
            var p = TrecsSelectionProxies.NextComponentType();
            p.Set(world, componentType);
            Selection.activeObject = p;
        }

        public static void SelectAccessor(World world, int accessorId, string displayName)
        {
            if (world == null || accessorId < 0)
            {
                return;
            }
            var p = TrecsSelectionProxies.NextAccessor();
            p.Set(world, accessorId, displayName);
            Selection.activeObject = p;
        }

        public static void SelectSet(World world, EntitySet entitySet)
        {
            if (world == null || entitySet.Id.Id == 0)
            {
                return;
            }
            var p = TrecsSelectionProxies.NextSet();
            p.Set(world, entitySet);
            Selection.activeObject = p;
        }

        public static void SelectTag(World world, Tag tag)
        {
            if (world == null || tag.Guid == 0)
            {
                return;
            }
            var p = TrecsSelectionProxies.NextTag();
            p.Set(world, tag);
            Selection.activeObject = p;
        }

        // ----- Cache-mode select / link helpers ------------------------------
        // Mirror the live versions but route through SetCache(...) on each
        // proxy. Used by InspectorLinker.Cache so unified inspector code can
        // make clickable cross-links without knowing whether it's running
        // against a live world or a schema snapshot.

        public static void SelectTemplateCache(TrecsSchema schema, TrecsSchemaTemplate entry)
        {
            if (entry == null)
                return;
            var p = TrecsSelectionProxies.NextTemplate();
            p.SetCache(schema, entry);
            Selection.activeObject = p;
        }

        public static void SelectComponentTypeCache(
            TrecsSchema schema,
            TrecsSchemaComponentType entry
        )
        {
            if (entry == null)
                return;
            var p = TrecsSelectionProxies.NextComponentType();
            p.SetCache(schema, entry);
            Selection.activeObject = p;
        }

        public static void SelectAccessorCache(TrecsSchema schema, string accessorName)
        {
            if (string.IsNullOrEmpty(accessorName))
                return;
            var p = TrecsSelectionProxies.NextAccessor();
            p.SetCache(schema, accessorName);
            Selection.activeObject = p;
        }

        public static void SelectSetCache(TrecsSchema schema, TrecsSchemaSet entry)
        {
            if (entry == null)
                return;
            var p = TrecsSelectionProxies.NextSet();
            p.SetCache(schema, entry);
            Selection.activeObject = p;
        }

        public static void SelectTagCache(TrecsSchema schema, TrecsSchemaTag entry)
        {
            if (entry == null)
                return;
            var p = TrecsSelectionProxies.NextTag();
            p.SetCache(schema, entry);
            Selection.activeObject = p;
        }

        public static VisualElement MakeTemplateLinkCache(
            TrecsSchema schema,
            TrecsSchemaTemplate entry,
            string text
        )
        {
            return BuildLinkRow(
                IconForTemplate(),
                text,
                onMouseEnter: () => PreviewTemplateCacheRequested?.Invoke(entry),
                onMouseLeave: () => PreviewClearRequested?.Invoke(),
                onClick: () => SelectTemplateCache(schema, entry)
            );
        }

        public static VisualElement MakeComponentTypeLinkCache(
            TrecsSchema schema,
            TrecsSchemaComponentType entry,
            string text
        )
        {
            return BuildLinkRow(
                IconForComponentType(),
                text,
                onMouseEnter: () => PreviewComponentTypeCacheRequested?.Invoke(entry),
                onMouseLeave: () => PreviewClearRequested?.Invoke(),
                onClick: () => SelectComponentTypeCache(schema, entry)
            );
        }

        public static VisualElement MakeAccessorLinkCache(
            TrecsSchema schema,
            string accessorName,
            string text
        )
        {
            return BuildLinkRow(
                IconForAccessor(),
                text,
                onMouseEnter: () => PreviewAccessorCacheRequested?.Invoke(accessorName),
                onMouseLeave: () => PreviewClearRequested?.Invoke(),
                onClick: () => SelectAccessorCache(schema, accessorName)
            );
        }

        public static VisualElement MakeSetLinkCache(
            TrecsSchema schema,
            TrecsSchemaSet entry,
            string text
        )
        {
            return BuildLinkRow(
                IconForFolder(),
                text,
                onMouseEnter: () => PreviewSetCacheRequested?.Invoke(entry),
                onMouseLeave: () => PreviewClearRequested?.Invoke(),
                onClick: () => SelectSetCache(schema, entry)
            );
        }

        public static VisualElement MakeTagLinkCache(
            TrecsSchema schema,
            TrecsSchemaTag entry,
            string text
        )
        {
            return BuildLinkRow(
                IconForTag(),
                text,
                onMouseEnter: () => PreviewTagCacheRequested?.Invoke(entry),
                onMouseLeave: () => PreviewClearRequested?.Invoke(),
                onClick: () => SelectTagCache(schema, entry)
            );
        }

        static Texture _iconFolder;

        static Texture IconForFolder() =>
            _iconFolder ??= EditorGUIUtility.IconContent("Folder Icon").image;

        // Icons mirror the kinds rendered in TrecsHierarchyWindow's tree so
        // a link in an inspector and the corresponding row in the hierarchy
        // share the same icon.
        static Texture IconForTemplate() =>
            _iconTemplate ??= EditorGUIUtility.IconContent("Prefab Icon").image;

        static Texture IconForComponentType() =>
            _iconScriptable ??= EditorGUIUtility.IconContent("ScriptableObject Icon").image;

        static Texture IconForAccessor() =>
            _iconScript ??= EditorGUIUtility.IconContent("cs Script Icon").image;

        static Texture IconForTag() =>
            _iconTag ??= EditorGUIUtility.IconContent("FilterByLabel").image;
    }
}
