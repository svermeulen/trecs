using System;
using System.Collections.Generic;
using Trecs.Internal;
using UnityEngine.UIElements;

namespace Trecs
{
    /// <summary>
    /// Abstraction over "make a clickable link to another inspector entry".
    /// Inspector code is shared across live and cache mode and just calls
    /// the linker by name; the live/cache flavour resolves the name back
    /// to a live reference (Template, Type, Tag, …) or a cache schema
    /// entry and routes the click through the matching proxy. Falls back
    /// to a non-clickable muted line when the target isn't found.
    /// </summary>
    public abstract class InspectorLinker
    {
        public abstract VisualElement TemplateLink(string templateDebugName);
        public abstract VisualElement ComponentTypeLink(string componentDisplayName);
        public abstract VisualElement AccessorLink(string accessorDebugName);
        public abstract VisualElement SetLink(string setDebugName);
        public abstract VisualElement TagLink(string tagName);

        protected static VisualElement MakeMutedLine(string text)
        {
            var l = new Label(text);
            l.style.opacity = 0.5f;
            l.style.paddingLeft = 4;
            l.style.paddingTop = 1;
            l.style.paddingBottom = 1;
            l.style.whiteSpace = WhiteSpace.Normal;
            return l;
        }
    }

    /// <summary>
    /// Linker for live mode — resolves names by walking
    /// <see cref="WorldInfo"/>'s indexes and the live tracker, then routes
    /// clicks through the standard MakeXxxLink helpers (which keep their
    /// shift+hover preview behaviour intact).
    /// </summary>
    public sealed class LiveInspectorLinker : InspectorLinker
    {
        readonly World _world;

        public LiveInspectorLinker(World world)
        {
            _world = world;
        }

        public override VisualElement TemplateLink(string templateDebugName)
        {
            var template = LookupTemplate(templateDebugName);
            return template == null
                ? MakeMutedLine(templateDebugName)
                : TrecsInspectorLinks.MakeTemplateLink(_world, template, templateDebugName);
        }

        public override VisualElement ComponentTypeLink(string componentDisplayName)
        {
            var type = LookupComponentType(componentDisplayName);
            return type == null
                ? MakeMutedLine(componentDisplayName)
                : TrecsInspectorLinks.MakeComponentTypeLink(_world, type, componentDisplayName);
        }

        public override VisualElement AccessorLink(string accessorDebugName)
        {
            var id = LookupAccessorId(accessorDebugName);
            return id < 0
                ? MakeMutedLine(accessorDebugName)
                : TrecsInspectorLinks.MakeAccessorLink(
                    _world,
                    id,
                    accessorDebugName,
                    accessorDebugName
                );
        }

        public override VisualElement SetLink(string setDebugName)
        {
            var entitySet = LookupSet(setDebugName);
            return entitySet.Id.Id == 0
                ? MakeMutedLine(setDebugName)
                : TrecsInspectorLinks.MakeSetLink(_world, entitySet, setDebugName);
        }

        public override VisualElement TagLink(string tagName)
        {
            var tag = LookupTag(tagName);
            return tag.Guid == 0
                ? MakeMutedLine(tagName)
                : TrecsInspectorLinks.MakeTagLink(_world, tag, tagName);
        }

        Template LookupTemplate(string debugName)
        {
            if (_world == null || _world.IsDisposed || string.IsNullOrEmpty(debugName))
            {
                return null;
            }
            try
            {
                foreach (var t in _world.WorldInfo.AllTemplates)
                {
                    if (t.DebugName == debugName)
                    {
                        return t;
                    }
                }
            }
            catch (Exception) { }
            return null;
        }

        Type LookupComponentType(string displayName)
        {
            if (_world == null || _world.IsDisposed || string.IsNullOrEmpty(displayName))
            {
                return null;
            }
            try
            {
                foreach (var rt in _world.WorldInfo.ResolvedTemplates)
                {
                    foreach (var d in rt.ComponentDeclarations)
                    {
                        if (
                            d.ComponentType != null
                            && TrecsHierarchyWindow.ComponentTypeDisplayName(d.ComponentType)
                                == displayName
                        )
                        {
                            return d.ComponentType;
                        }
                    }
                }
            }
            catch (Exception) { }
            return null;
        }

        int LookupAccessorId(string debugName)
        {
            if (_world == null || _world.IsDisposed || string.IsNullOrEmpty(debugName))
            {
                return -1;
            }
            try
            {
                foreach (var entry in _world.GetAllAccessors())
                {
                    if (entry.Value?.DebugName == debugName)
                    {
                        return entry.Key;
                    }
                }
            }
            catch (Exception) { }
            return -1;
        }

        EntitySet LookupSet(string debugName)
        {
            if (_world == null || _world.IsDisposed || string.IsNullOrEmpty(debugName))
            {
                return default;
            }
            try
            {
                foreach (var s in _world.WorldInfo.AllSets)
                {
                    if (s.DebugName == debugName)
                    {
                        return s;
                    }
                }
            }
            catch (Exception) { }
            return default;
        }

        Tag LookupTag(string tagName)
        {
            if (_world == null || _world.IsDisposed || string.IsNullOrEmpty(tagName))
            {
                return default;
            }
            try
            {
                // Tag.ToString resolves through GroupTagNames which takes
                // the guid as the key. Walk every TagSet we can see and
                // return the first matching one — tag names are unique.
                foreach (var rt in _world.WorldInfo.ResolvedTemplates)
                {
                    if (TryFindInTagSet(rt.AllTags, tagName, out var t))
                        return t;
                    foreach (var p in rt.Partitions)
                    {
                        if (TryFindInTagSet(p, tagName, out var pt))
                            return pt;
                    }
                }
                foreach (var s in _world.WorldInfo.AllSets)
                {
                    if (TryFindInTagSet(s.Tags, tagName, out var st))
                        return st;
                }
            }
            catch (Exception) { }
            return default;
        }

        static bool TryFindInTagSet(TagSet ts, string tagName, out Tag found)
        {
            found = default;
            if (ts.IsNull)
                return false;
            foreach (var t in ts.Tags)
            {
                if (t.ToString() == tagName)
                {
                    found = t;
                    return true;
                }
            }
            return false;
        }
    }

    /// <summary>
    /// Linker for cache mode — looks up names against a
    /// <see cref="TrecsSchema"/> snapshot and routes clicks through the
    /// SetCache(...) entry point on each proxy. Manual accessors are
    /// represented just by their debug-name string, so that link path
    /// doesn't need a schema entry lookup.
    /// </summary>
    public sealed class CacheInspectorLinker : InspectorLinker
    {
        readonly TrecsSchema _schema;
        readonly Dictionary<string, TrecsSchemaTemplate> _templatesByName;
        readonly Dictionary<string, TrecsSchemaComponentType> _componentsByName;
        readonly Dictionary<string, TrecsSchemaSet> _setsByName;
        readonly Dictionary<string, TrecsSchemaTag> _tagsByName;
        readonly HashSet<string> _accessorNames;

        public CacheInspectorLinker(TrecsSchema schema)
        {
            _schema = schema;
            _templatesByName = new();
            _componentsByName = new();
            _setsByName = new();
            _tagsByName = new();
            _accessorNames = new();
            if (schema == null)
                return;
            foreach (var t in schema.Templates)
            {
                if (!string.IsNullOrEmpty(t.DebugName))
                {
                    _templatesByName[t.DebugName] = t;
                }
            }
            foreach (var c in schema.ComponentTypes)
            {
                if (!string.IsNullOrEmpty(c.DisplayName))
                {
                    _componentsByName[c.DisplayName] = c;
                }
            }
            foreach (var s in schema.Sets)
            {
                if (!string.IsNullOrEmpty(s.DebugName))
                {
                    _setsByName[s.DebugName] = s;
                }
            }
            foreach (var t in schema.Tags)
            {
                if (!string.IsNullOrEmpty(t.Name))
                {
                    _tagsByName[t.Name] = t;
                }
            }
            foreach (var s in schema.Systems)
            {
                if (!string.IsNullOrEmpty(s.DebugName))
                {
                    _accessorNames.Add(s.DebugName);
                }
            }
            foreach (var m in schema.ManualAccessors)
            {
                if (!string.IsNullOrEmpty(m.DebugName))
                {
                    _accessorNames.Add(m.DebugName);
                }
            }
        }

        public override VisualElement TemplateLink(string templateDebugName)
        {
            return _templatesByName.TryGetValue(templateDebugName ?? string.Empty, out var entry)
                ? TrecsInspectorLinks.MakeTemplateLinkCache(_schema, entry, templateDebugName)
                : MakeMutedLine(templateDebugName);
        }

        public override VisualElement ComponentTypeLink(string componentDisplayName)
        {
            return _componentsByName.TryGetValue(
                componentDisplayName ?? string.Empty,
                out var entry
            )
                ? TrecsInspectorLinks.MakeComponentTypeLinkCache(
                    _schema,
                    entry,
                    componentDisplayName
                )
                : MakeMutedLine(componentDisplayName);
        }

        public override VisualElement AccessorLink(string accessorDebugName)
        {
            return _accessorNames.Contains(accessorDebugName ?? string.Empty)
                ? TrecsInspectorLinks.MakeAccessorLinkCache(
                    _schema,
                    accessorDebugName,
                    accessorDebugName
                )
                : MakeMutedLine(accessorDebugName);
        }

        public override VisualElement SetLink(string setDebugName)
        {
            return _setsByName.TryGetValue(setDebugName ?? string.Empty, out var entry)
                ? TrecsInspectorLinks.MakeSetLinkCache(_schema, entry, setDebugName)
                : MakeMutedLine(setDebugName);
        }

        public override VisualElement TagLink(string tagName)
        {
            return _tagsByName.TryGetValue(tagName ?? string.Empty, out var entry)
                ? TrecsInspectorLinks.MakeTagLinkCache(_schema, entry, tagName)
                : MakeMutedLine(tagName);
        }
    }
}
