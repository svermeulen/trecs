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
        // highlight. The identity string (e.g. "template:Foo") is computed
        // at the link factory and matches the row's stable key, so the
        // window resolves it via its single _idByKey map regardless of
        // live vs cache mode. PreviewClearRequested removes the highlight
        // when the mouse leaves the link. None of these change
        // Selection.activeObject; selection only happens on click, via the
        // SelectXxx helpers below.
        public static event Action<string> PreviewRequested;
        public static event Action<World, EntityHandle> PreviewEntityRequested;
        public static event Action PreviewClearRequested;

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

        // Identity-based link factories. The publishers — link click, hover
        // preview, click select — all key off the row's stable identity
        // string ("template:Foo", "tag:Player", etc.), so live and cache
        // mode share a single set of factories. The typed-payload public
        // overloads below are thin wrappers for callers that have a
        // Template / Type / EntitySet etc. in scope (e.g. the entity
        // inspector); InspectorLinker calls the by-name versions directly.

        public static VisualElement MakeTemplateLink(string name, string text)
        {
            return MakeIdentityLinkRow(
                IconForTemplate(),
                text,
                TrecsRowIdentity.TemplatePrefix + (name ?? string.Empty),
                name ?? string.Empty,
                TrecsSelectionProxies.NextTemplate
            );
        }

        public static VisualElement MakeComponentTypeLink(string displayName, string text)
        {
            return MakeIdentityLinkRow(
                IconForComponentType(),
                text,
                TrecsRowIdentity.ComponentPrefix + (displayName ?? string.Empty),
                displayName ?? string.Empty,
                TrecsSelectionProxies.NextComponentType
            );
        }

        public static VisualElement MakeAccessorLink(string displayName, string text)
        {
            var name = displayName ?? string.Empty;
            return MakeIdentityLinkRow(
                IconForAccessor(),
                text,
                TrecsRowIdentity.AccessorPrefix + name,
                name,
                TrecsSelectionProxies.NextAccessor
            );
        }

        public static VisualElement MakeSetLink(string name, string text)
        {
            return MakeIdentityLinkRow(
                IconForFolder(),
                text,
                TrecsRowIdentity.SetPrefix + (name ?? string.Empty),
                name ?? string.Empty,
                TrecsSelectionProxies.NextSet
            );
        }

        public static VisualElement MakeTagLink(string name, string text)
        {
            return MakeIdentityLinkRow(
                IconForTag(),
                text,
                TrecsRowIdentity.TagPrefix + (name ?? string.Empty),
                name ?? string.Empty,
                TrecsSelectionProxies.NextTag
            );
        }

        // Live typed overloads — preserved for callers that have a
        // Template/Type/EntitySet/Tag in scope (entity inspector). Each
        // extracts the name and delegates to the identity-based factory.
        public static VisualElement MakeTemplateLink(World world, Template template, string text) =>
            MakeTemplateLink(template?.DebugName, text);

        public static VisualElement MakeComponentTypeLink(
            World world,
            Type componentType,
            string text
        ) =>
            MakeComponentTypeLink(
                componentType == null
                    ? null
                    : TrecsHierarchyWindow.ComponentTypeDisplayName(componentType),
                text
            );

        public static VisualElement MakeAccessorLink(
            World world,
            int accessorId,
            string text,
            string displayName = null
        ) => MakeAccessorLink(displayName ?? text, text);

        public static VisualElement MakeTagLink(World world, Tag tag, string text) =>
            MakeTagLink(tag.Guid == 0 ? null : (tag.ToString() ?? $"#{tag.Guid}"), text);

        public static VisualElement MakeSetLink(World world, EntitySet entitySet, string text) =>
            MakeSetLink(entitySet.DebugName ?? $"#{entitySet.Id.Id}", text);

        // Small "go to component type inspector" button — renders as an
        // arrow glyph rather than a full row so it can sit inline next to
        // a Foldout title or PropertyField label without taking visual
        // ownership of the line.
        public static VisualElement MakeComponentTypeJumpButton(World world, Type componentType)
        {
            var name =
                componentType == null
                    ? string.Empty
                    : TrecsHierarchyWindow.ComponentTypeDisplayName(componentType);
            var identity = TrecsRowIdentity.ComponentPrefix + name;
            return BuildJumpGlyph(
                tooltip: "Inspect component type",
                onMouseEnter: () => PreviewRequested?.Invoke(identity),
                onMouseLeave: () => PreviewClearRequested?.Invoke(),
                onClick: () =>
                    SelectByIdentity(TrecsSelectionProxies.NextComponentType, identity, name)
            );
        }

        // Shared identity-link factory. Identity is the stable-key string
        // the hierarchy uses to resolve a row; the proxy factory mints a
        // fresh ring-pool slot of the right kind on click so external
        // selection-history tools see distinct references.
        static VisualElement MakeIdentityLinkRow(
            Texture icon,
            string text,
            string identity,
            string displayName,
            Func<TrecsSelectionProxy> proxyFactory
        )
        {
            return BuildLinkRow(
                icon,
                text,
                onMouseEnter: () => PreviewRequested?.Invoke(identity),
                onMouseLeave: () => PreviewClearRequested?.Invoke(),
                onClick: () => SelectByIdentity(proxyFactory, identity, displayName)
            );
        }

        static void SelectByIdentity(
            Func<TrecsSelectionProxy> proxyFactory,
            string identity,
            string displayName
        )
        {
            var p = proxyFactory();
            p.SetIdentity(identity, displayName);
            Selection.activeObject = p;
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
        // Single identity-based hover-preview wire for all non-entity kinds.
        // Inspectors pass a resolver that returns the active proxy's
        // identity string (e.g. "template:Foo"); shift+hover fires the
        // unified PreviewRequested event so the hierarchy window scrolls
        // to the matching row. resolve() returning null/empty leaves the
        // preview untouched.
        public static void WireHoverPreview(VisualElement element, Func<string> resolveIdentity)
        {
            element.RegisterCallback<MouseEnterEvent>(evt =>
            {
                if (!evt.shiftKey)
                    return;
                var identity = resolveIdentity();
                if (!string.IsNullOrEmpty(identity))
                {
                    PreviewRequested?.Invoke(identity);
                }
            });
            element.RegisterCallback<MouseLeaveEvent>(_ => PreviewClearRequested?.Invoke());
        }

        // Entity preview stays on its own typed event — entity row ids
        // are tracked separately (_entityIds is keyed by EntityHandle, not
        // by identity string) and have no meaningful identity that
        // survives world transitions.
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

        // Live typed Select* — kept for the entity inspector's
        // "select this entity's template" click handler. Internally
        // delegates to the identity-based path.
        public static void SelectTemplate(World world, Template template)
        {
            if (template == null)
            {
                return;
            }
            var name = template.DebugName ?? string.Empty;
            SelectByIdentity(
                TrecsSelectionProxies.NextTemplate,
                TrecsRowIdentity.TemplatePrefix + name,
                name
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
