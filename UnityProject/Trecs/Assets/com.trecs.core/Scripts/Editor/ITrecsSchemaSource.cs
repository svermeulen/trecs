using System;
using System.Collections.Generic;

namespace Trecs
{
    /// <summary>
    /// Unified data source for <see cref="TrecsHierarchyWindow"/>. The window
    /// renders against a single source at a time; live worlds and on-disk
    /// schema snapshots both implement this interface so the section
    /// builders, predicate matchers, and inspector linker can have a single
    /// code path. Live-only capabilities (entity iteration, system enable
    /// toggle, per-tick refresh) flow through capability gates so cache mode
    /// disables UI controls gracefully rather than hiding them.
    /// </summary>
    public interface ITrecsSchemaSource
    {
        string DisplayName { get; }
        bool IsLive { get; }

        IReadOnlyList<TemplateRef> Templates { get; }
        IReadOnlyList<ComponentTypeRef> ComponentTypes { get; }
        IReadOnlyList<SetRef> Sets { get; }
        IReadOnlyList<TagRef> Tags { get; }
        IReadOnlyList<AccessorPhaseRef> AccessorsByPhase { get; }

        bool SupportsEntityIteration { get; }
        bool SupportsSystemEnableToggle { get; }
        bool SupportsLiveRefresh { get; }

        // Live-only — return 0 / empty / false in cache mode.
        int CountEntitiesInGroup(GroupIndex group);
        IEnumerable<EntityHandle> EntitiesInGroup(GroupIndex group, int max);

        // Editor-channel state — this is what the inspector toggle reflects
        // and round-trips with SetSystemEnabled. Other channels (User /
        // Playback) and the deterministic paused flag are not folded in
        // here; consume TryGetSystemEffectivelyEnabled when you need the
        // combined "is this system actually running" answer.
        bool TryGetSystemEnabled(int systemIndex, out bool enabled);
        void SetSystemEnabled(int systemIndex, bool enabled);

        // Combined enable state across all channels (Editor / User /
        // Playback) plus the deterministic paused flag. Drives hierarchy
        // grayout so the tree row matches whether the system actually runs,
        // matching Unity's GameObject convention (inspector toggle =
        // activeSelf, hierarchy grayout = activeInHierarchy).
        bool TryGetSystemEffectivelyEnabled(int systemIndex, out bool enabled);

        IAccessTracker AccessTracker { get; }
    }

    /// <summary>
    /// Thin discriminated wrapper over a template entry. <see cref="LiveTemplate"/>
    /// + <see cref="LiveResolved"/> populated when the source is live; otherwise
    /// <see cref="CacheNative"/> populated. Most call sites consume only the
    /// projected name + flags; the typed native references stay accessible
    /// for the few sites that need them (inspector deep-dive).
    ///
    /// <see cref="AllTagNames"/>, <see cref="ComponentTypeNames"/>,
    /// <see cref="BaseTemplateNames"/>, and <see cref="DerivedTemplateNames"/>
    /// are pre-projected by the source so search predicates can read uniformly
    /// across modes — the cache copies them off the schema entry; the live
    /// source walks <c>ResolvedTemplate</c> + <c>WorldInfo</c> once at
    /// construction. Empty list (never null) when the underlying data has none.
    /// </summary>
    public sealed class TemplateRef
    {
        static readonly IReadOnlyList<string> EmptyNames = Array.Empty<string>();

        public string DebugName { get; }
        public bool IsResolved { get; }

        public Template LiveTemplate { get; }
        public ResolvedTemplate LiveResolved { get; }

        public TrecsSchemaTemplate CacheNative { get; }

        public IReadOnlyList<string> AllTagNames { get; }
        public IReadOnlyList<string> ComponentTypeNames { get; }
        public IReadOnlyList<string> BaseTemplateNames { get; }

        // Inverse of BaseTemplateNames — every other template that extends
        // this one. Live source fills this in a 2-pass walk after every
        // template's base list is known; cache reads it pre-computed off
        // the schema (TrecsSchemaCache builds it at save time).
        public IReadOnlyList<string> DerivedTemplateNames { get; internal set; }

        public TemplateRef(
            Template template,
            ResolvedTemplate resolved,
            IReadOnlyList<string> allTagNames,
            IReadOnlyList<string> componentTypeNames,
            IReadOnlyList<string> baseTemplateNames
        )
        {
            LiveTemplate = template;
            LiveResolved = resolved;
            IsResolved = resolved != null;
            DebugName = template?.DebugName ?? "(unnamed)";
            AllTagNames = allTagNames ?? EmptyNames;
            ComponentTypeNames = componentTypeNames ?? EmptyNames;
            BaseTemplateNames = baseTemplateNames ?? EmptyNames;
            DerivedTemplateNames = EmptyNames;
        }

        public TemplateRef(TrecsSchemaTemplate cache)
        {
            CacheNative = cache;
            IsResolved = cache?.IsResolved ?? false;
            DebugName = cache?.DebugName ?? "(unnamed)";
            AllTagNames = (IReadOnlyList<string>)cache?.AllTagNames ?? EmptyNames;
            ComponentTypeNames = ProjectComponentNames(cache);
            BaseTemplateNames = (IReadOnlyList<string>)cache?.BaseTemplateNames ?? EmptyNames;
            DerivedTemplateNames = (IReadOnlyList<string>)cache?.DerivedTemplateNames ?? EmptyNames;
        }

        // The schema's ComponentTypeNames is the union (kept for back-compat
        // with caches written before the direct/inherited split). When that
        // list is populated we use it directly; otherwise we synthesize the
        // union from the two newer lists.
        static IReadOnlyList<string> ProjectComponentNames(TrecsSchemaTemplate cache)
        {
            if (cache == null)
            {
                return EmptyNames;
            }
            if (cache.ComponentTypeNames != null && cache.ComponentTypeNames.Count > 0)
            {
                return cache.ComponentTypeNames;
            }
            var direct = cache.DirectComponentTypeNames;
            var inherited = cache.InheritedComponentTypeNames;
            int total = (direct?.Count ?? 0) + (inherited?.Count ?? 0);
            if (total == 0)
            {
                return EmptyNames;
            }
            var union = new List<string>(total);
            if (direct != null)
            {
                union.AddRange(direct);
            }
            if (inherited != null)
            {
                union.AddRange(inherited);
            }
            return union;
        }
    }

    public sealed class ComponentTypeRef
    {
        public string DisplayName { get; }
        public string FullName { get; }

        public Type LiveType { get; }
        public TrecsSchemaComponentType CacheNative { get; }

        public ComponentTypeRef(Type type, string displayName)
        {
            LiveType = type;
            DisplayName = displayName;
            FullName = type?.FullName ?? type?.Name ?? string.Empty;
        }

        public ComponentTypeRef(TrecsSchemaComponentType cache)
        {
            CacheNative = cache;
            DisplayName = cache?.DisplayName ?? "(unnamed)";
            FullName = cache?.FullName ?? string.Empty;
        }
    }

    public sealed class SetRef
    {
        static readonly IReadOnlyList<string> EmptyNames = Array.Empty<string>();

        public string DebugName { get; }

        public EntitySet LiveSet { get; }
        public bool HasLiveSet { get; }

        public TrecsSchemaSet CacheNative { get; }

        // Pre-projected scoped tags. Search predicates read from here so
        // they don't have to branch on live-vs-cache.
        public IReadOnlyList<string> TagNames { get; }

        public SetRef(EntitySet liveSet, IReadOnlyList<string> tagNames)
        {
            LiveSet = liveSet;
            HasLiveSet = true;
            DebugName = liveSet.DebugName ?? $"#{liveSet.Id.Id}";
            TagNames = tagNames ?? EmptyNames;
        }

        public SetRef(TrecsSchemaSet cache)
        {
            CacheNative = cache;
            DebugName = cache?.DebugName ?? "(unnamed)";
            TagNames = (IReadOnlyList<string>)cache?.TagNames ?? EmptyNames;
        }
    }

    public sealed class TagRef
    {
        public string Name { get; }

        public Tag LiveTag { get; }
        public bool HasLiveTag { get; }

        public TrecsSchemaTag CacheNative { get; }

        public TagRef(Tag liveTag)
        {
            LiveTag = liveTag;
            HasLiveTag = true;
            Name = liveTag.ToString() ?? $"#{liveTag.Guid}";
        }

        public TagRef(TrecsSchemaTag cache)
        {
            CacheNative = cache;
            Name = cache?.Name ?? "(unnamed)";
        }
    }

    /// <summary>
    /// One run-phase bucket of accessors (e.g. "Fixed", "Presentation",
    /// "Other" for manual / phaseless accessors). Live mode emits buckets
    /// in topological execution order; cache mode uses the same per-phase
    /// grouping recorded at snapshot time.
    /// </summary>
    public sealed class AccessorPhaseRef
    {
        public string PhaseName { get; }
        public IReadOnlyList<AccessorRef> Accessors { get; }

        public AccessorPhaseRef(string phaseName, IReadOnlyList<AccessorRef> accessors)
        {
            PhaseName = phaseName;
            Accessors = accessors;
        }
    }

    public sealed class AccessorRef
    {
        public string DebugName { get; }

        // Live-only.
        public int AccessorId { get; }
        public int SystemIndex { get; }
        public int? ExecutionPriority { get; }
        public bool IsManual { get; }

        // For manual accessors: the file:line where world.CreateAccessor
        // was called (captured via CallerFilePath/CallerLineNumber). Empty
        // string + 0 when unavailable (release builds, system-owned
        // accessors). Surfaced by the accessor inspector so the user can
        // jump back to where a manually-created accessor lives.
        public string CreatedAtFile { get; }
        public int CreatedAtLine { get; }

        public TrecsSchemaSystem CacheNativeSystem { get; }
        public TrecsSchemaAccessor CacheNativeManual { get; }

        public AccessorRef(
            string debugName,
            int accessorId,
            int systemIndex,
            int? executionPriority,
            bool isManual,
            string createdAtFile = "",
            int createdAtLine = 0
        )
        {
            DebugName = debugName;
            AccessorId = accessorId;
            SystemIndex = systemIndex;
            ExecutionPriority = executionPriority;
            IsManual = isManual;
            CreatedAtFile = createdAtFile ?? string.Empty;
            CreatedAtLine = createdAtLine;
        }

        public AccessorRef(TrecsSchemaSystem cache)
        {
            DebugName = cache?.DebugName ?? "(unnamed)";
            AccessorId = -1;
            SystemIndex = -1;
            ExecutionPriority = (cache?.HasPriority ?? false) ? cache.Priority : (int?)null;
            IsManual = false;
            CreatedAtFile = string.Empty;
            CreatedAtLine = 0;
            CacheNativeSystem = cache;
        }

        public AccessorRef(TrecsSchemaAccessor cache)
        {
            DebugName = cache?.DebugName ?? "(unnamed)";
            AccessorId = -1;
            SystemIndex = -1;
            ExecutionPriority = null;
            IsManual = true;
            CreatedAtFile = cache?.CreatedAtFile ?? string.Empty;
            CreatedAtLine = cache?.CreatedAtLine ?? 0;
            CacheNativeManual = cache;
        }
    }

    /// <summary>
    /// Identity-string format constants. An identity is a stable, mode-
    /// agnostic string stamped onto each selection proxy and persisted
    /// via SessionState — both writers (the section builders in
    /// <see cref="TrecsHierarchyWindow"/>, the link factories in
    /// <see cref="TrecsInspectorLinks"/>) and readers
    /// (<see cref="TrecsSchemaSourceExtensions"/>'s Resolve* helpers)
    /// use these prefixes so writer/reader can't drift.
    /// </summary>
    public static class TrecsRowIdentity
    {
        public const string TemplatePrefix = "template:";
        public const string ComponentPrefix = "component:";
        public const string SetPrefix = "set:";
        public const string TagPrefix = "tag:";
        public const string AccessorPrefix = "accessor:";
        public const string EntityPrefix = "entity:";
    }

    /// <summary>
    /// Identity → Ref lookup helpers used by the per-kind inspector
    /// proxies. Identity strings (e.g. <c>"template:Foo"</c>,
    /// <c>"tag:Player"</c>) are persisted on the proxy and resolved
    /// against the active source on every Refresh — so a proxy survives
    /// stop-play-mode silent restoration and live↔cache transitions.
    /// Returns null on prefix mismatch, missing source, or no match.
    /// </summary>
    public static class TrecsSchemaSourceExtensions
    {
        public static TemplateRef ResolveTemplate(this ITrecsSchemaSource src, string identity)
        {
            if (!TrySplitIdentity(identity, TrecsRowIdentity.TemplatePrefix, out var rest))
            {
                return null;
            }
            if (src == null)
            {
                return null;
            }
            // Templates may share names; identities for duplicates carry a
            // "#N" occurrence-index suffix (0-based, in source iteration
            // order). Strip it, then count matches on the way through.
            string name;
            int dup = 0;
            int hashIdx = rest.IndexOf('#');
            if (hashIdx >= 0)
            {
                name = rest.Substring(0, hashIdx);
                int.TryParse(rest.Substring(hashIdx + 1), out dup);
            }
            else
            {
                name = rest;
            }
            int seen = 0;
            foreach (var t in src.Templates)
            {
                if (!string.Equals(t.DebugName ?? string.Empty, name, StringComparison.Ordinal))
                {
                    continue;
                }
                if (seen == dup)
                {
                    return t;
                }
                seen++;
            }
            return null;
        }

        public static ComponentTypeRef ResolveComponentType(
            this ITrecsSchemaSource src,
            string identity
        )
        {
            if (
                !TrySplitIdentity(identity, TrecsRowIdentity.ComponentPrefix, out var name)
                || src == null
            )
            {
                return null;
            }
            foreach (var c in src.ComponentTypes)
            {
                if (string.Equals(c.DisplayName ?? string.Empty, name, StringComparison.Ordinal))
                {
                    return c;
                }
            }
            return null;
        }

        public static SetRef ResolveSet(this ITrecsSchemaSource src, string identity)
        {
            if (
                !TrySplitIdentity(identity, TrecsRowIdentity.SetPrefix, out var name)
                || src == null
            )
            {
                return null;
            }
            foreach (var s in src.Sets)
            {
                if (string.Equals(s.DebugName ?? string.Empty, name, StringComparison.Ordinal))
                {
                    return s;
                }
            }
            return null;
        }

        public static TagRef ResolveTag(this ITrecsSchemaSource src, string identity)
        {
            if (
                !TrySplitIdentity(identity, TrecsRowIdentity.TagPrefix, out var name)
                || src == null
            )
            {
                return null;
            }
            foreach (var t in src.Tags)
            {
                if (string.Equals(t.Name ?? string.Empty, name, StringComparison.Ordinal))
                {
                    return t;
                }
            }
            return null;
        }

        /// <summary>
        /// Composite "render key" used by per-kind inspectors to decide
        /// whether their cached static section needs rebuilding. Captures
        /// source mode (live vs cache), source identity (which world or
        /// snapshot), and the proxy's own identity — change any of those
        /// and the inspector re-runs RenderStatic.
        /// </summary>
        public static string RenderKey(this ITrecsSchemaSource src, string identity)
        {
            if (src == null)
            {
                return identity;
            }
            return (src.IsLive ? "L|" : "C|") + src.DisplayName + "|" + identity;
        }

        public static AccessorRef ResolveAccessor(this ITrecsSchemaSource src, string identity)
        {
            if (
                !TrySplitIdentity(identity, TrecsRowIdentity.AccessorPrefix, out var name)
                || src == null
            )
            {
                return null;
            }
            foreach (var phase in src.AccessorsByPhase)
            {
                foreach (var a in phase.Accessors)
                {
                    if (string.Equals(a.DebugName ?? string.Empty, name, StringComparison.Ordinal))
                    {
                        return a;
                    }
                }
            }
            return null;
        }

        static bool TrySplitIdentity(string identity, string prefix, out string rest)
        {
            if (
                string.IsNullOrEmpty(identity)
                || !identity.StartsWith(prefix, StringComparison.Ordinal)
            )
            {
                rest = null;
                return false;
            }
            rest = identity.Substring(prefix.Length);
            return true;
        }
    }
}
