using UnityEngine;
using UnityEngine.UIElements;

namespace Trecs
{
    /// <summary>
    /// Resolves inspector cross-link targets ("click this name to open
    /// that template / component / set / tag") against an
    /// <see cref="ITrecsSchemaSource"/>. Live and cache flavours used to
    /// be separate subclasses; the source abstraction collapses them. The
    /// linker walks <c>source.Templates</c> / <c>source.ComponentTypes</c>
    /// / etc. for name lookup, then dispatches through the matching
    /// identity-based <see cref="TrecsInspectorLinks"/> Make-Link factory
    /// (live and cache modes share the same factory now). Falls back to
    /// a non-clickable muted line when the target isn't found.
    /// </summary>
    public sealed class InspectorLinker
    {
        readonly ITrecsSchemaSource _source;

        public InspectorLinker(ITrecsSchemaSource source)
        {
            _source = source;
        }

        /// <summary>
        /// Convenience factory used by the per-kind inspectors: builds a
        /// fresh <see cref="LiveSchemaSource"/> when a live world is
        /// available, falling back to a <see cref="CacheSchemaSource"/>
        /// over the proxy's cached schema. Returns null when neither side
        /// has data, leaving the linker in fall-through mode (every
        /// link renders as a muted line).
        /// </summary>
        public static ITrecsSchemaSource SourceFor(
            World liveWorld,
            WorldAccessor liveAccessor,
            TrecsSchema cacheSchema
        )
        {
            if (liveWorld != null && !liveWorld.IsDisposed)
            {
                return new LiveSchemaSource(liveWorld, liveAccessor);
            }
            if (cacheSchema != null)
            {
                return new CacheSchemaSource(cacheSchema);
            }
            return null;
        }

        public VisualElement TemplateLink(string templateDebugName)
        {
            if (string.IsNullOrEmpty(templateDebugName) || _source == null)
                return MakeMutedLine(templateDebugName);
            foreach (var t in _source.Templates)
            {
                if (t.DebugName == templateDebugName)
                {
                    return TrecsInspectorLinks.MakeTemplateLink(
                        templateDebugName,
                        templateDebugName
                    );
                }
            }
            return MakeStaleLine(templateDebugName, "template");
        }

        public VisualElement ComponentTypeLink(string componentDisplayName)
        {
            if (string.IsNullOrEmpty(componentDisplayName) || _source == null)
                return MakeMutedLine(componentDisplayName);
            foreach (var c in _source.ComponentTypes)
            {
                if (c.DisplayName == componentDisplayName)
                {
                    return TrecsInspectorLinks.MakeComponentTypeLink(
                        componentDisplayName,
                        componentDisplayName
                    );
                }
            }
            return MakeStaleLine(componentDisplayName, "component");
        }

        public VisualElement AccessorLink(string accessorDebugName)
        {
            if (string.IsNullOrEmpty(accessorDebugName) || _source == null)
                return MakeMutedLine(accessorDebugName);
            foreach (var phase in _source.AccessorsByPhase)
            {
                foreach (var a in phase.Accessors)
                {
                    if (a.DebugName == accessorDebugName)
                    {
                        return TrecsInspectorLinks.MakeAccessorLink(
                            accessorDebugName,
                            accessorDebugName
                        );
                    }
                }
            }
            return MakeStaleLine(accessorDebugName, "accessor");
        }

        public VisualElement SetLink(string setDebugName)
        {
            if (string.IsNullOrEmpty(setDebugName) || _source == null)
                return MakeMutedLine(setDebugName);
            foreach (var s in _source.Sets)
            {
                if (s.DebugName == setDebugName)
                {
                    return TrecsInspectorLinks.MakeSetLink(setDebugName, setDebugName);
                }
            }
            return MakeStaleLine(setDebugName, "set");
        }

        public VisualElement TagLink(string tagName)
        {
            if (string.IsNullOrEmpty(tagName) || _source == null)
                return MakeMutedLine(tagName);
            foreach (var t in _source.Tags)
            {
                if (t.Name == tagName)
                {
                    return TrecsInspectorLinks.MakeTagLink(tagName, tagName);
                }
            }
            return MakeStaleLine(tagName, "tag");
        }

        static VisualElement MakeMutedLine(string text)
        {
            var l = new Label(text);
            l.style.opacity = 0.5f;
            l.style.paddingLeft = 4;
            l.style.paddingTop = 1;
            l.style.paddingBottom = 1;
            l.style.whiteSpace = WhiteSpace.Normal;
            return l;
        }

        // Used when a cross-reference name (e.g. an accessor in a Reads /
        // Writes / Dependents list) doesn't resolve in the current source.
        // Most often this is an entry the schema cache accumulated on a
        // prior run from a system or component that has since been
        // renamed or deleted — flagging it explicitly avoids the user
        // mistaking it for a current relationship that just isn't clickable.
        static VisualElement MakeStaleLine(string text, string kind)
        {
            var l = new Label($"{text}  (stale)");
            l.style.opacity = 0.5f;
            l.style.unityFontStyleAndWeight = FontStyle.Italic;
            l.style.paddingLeft = 4;
            l.style.paddingTop = 1;
            l.style.paddingBottom = 1;
            l.style.whiteSpace = WhiteSpace.Normal;
            l.tooltip =
                $"No {kind} named '{text}' exists in the current source. "
                + "Likely a leftover from a prior run after the underlying "
                + $"{kind} was renamed or removed. Use the hierarchy "
                + "window's Clear button to wipe accumulated stale entries.";
            return l;
        }
    }
}
