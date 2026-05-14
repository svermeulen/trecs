using System;
using System.Collections.Generic;
using Trecs.Internal;

namespace Trecs
{
    /// <summary>
    /// <see cref="ITrecsSchemaSource"/> backed by a running <see cref="World"/>
    /// + its <see cref="WorldAccessor"/> + <see cref="WorldInfo"/>. Pre-projects
    /// the structural sections (templates, components, sets, tags, accessors)
    /// at construction so per-tick reads are cheap; dynamic data (entity
    /// counts, system enable state) flows through the live methods rather
    /// than the cached refs. The window discards and rebuilds the source on
    /// any structural change, matching the existing
    /// <c>NeedsStructuralRebuild</c> cadence.
    /// </summary>
    public sealed class LiveSchemaSource : ITrecsSchemaSource
    {
        readonly World _world;
        readonly WorldAccessor _accessor;
        readonly WorldInfo _info;

        IReadOnlyList<TemplateRef> _templates;
        IReadOnlyList<ComponentTypeRef> _componentTypes;
        IReadOnlyList<SetRef> _sets;
        IReadOnlyList<TagRef> _tags;
        IReadOnlyList<AccessorPhaseRef> _accessorsByPhase;
        LiveAccessTrackerView _trackerView;

        public LiveSchemaSource(World world, WorldAccessor accessor)
        {
            _world = world;
            _accessor = accessor;
            _info = world?.WorldInfo;
        }

        public string DisplayName => _world?.DebugName ?? "(unnamed)";
        public bool IsLive => true;
        public bool SupportsEntityIteration => true;
        public bool SupportsSystemEnableToggle => true;
        public bool SupportsLiveRefresh => true;

        public World World => _world;
        public WorldAccessor Accessor => _accessor;
        public WorldInfo Info => _info;

        public IReadOnlyList<TemplateRef> Templates => _templates ??= ProjectTemplates();

        public IReadOnlyList<ComponentTypeRef> ComponentTypes =>
            _componentTypes ??= ProjectComponentTypes();

        public IReadOnlyList<SetRef> Sets => _sets ??= ProjectSets();

        public IReadOnlyList<TagRef> Tags => _tags ??= ProjectTags();

        public IReadOnlyList<AccessorPhaseRef> AccessorsByPhase =>
            _accessorsByPhase ??= ProjectAccessors();

        public IAccessTracker AccessTracker => _trackerView ??= new LiveAccessTrackerView(_world);

        public int CountEntitiesInGroup(GroupIndex group)
        {
            if (_accessor == null)
                return 0;
            try
            {
                return _accessor.CountEntitiesInGroup(group);
            }
            catch (Exception)
            {
                return 0;
            }
        }

        public IEnumerable<EntityHandle> EntitiesInGroup(GroupIndex group, int max)
        {
            if (_accessor == null || max <= 0)
                yield break;
            int count;
            try
            {
                count = _accessor.CountEntitiesInGroup(group);
            }
            catch (Exception)
            {
                yield break;
            }
            int shown = count < max ? count : max;
            for (int i = 0; i < shown; i++)
            {
                EntityHandle handle;
                try
                {
                    handle = new EntityIndex(i, group).ToHandle(_accessor);
                }
                catch
                {
                    continue;
                }
                yield return handle;
            }
        }

        public bool TryGetSystemEnabled(int systemIndex, out bool enabled)
        {
            enabled = false;
            if (_world == null || systemIndex < 0)
                return false;
            try
            {
                // Hierarchy display reflects the Editor channel — same channel
                // the inspector toggle drives. Other channels (Playback / User)
                // and the deterministic Paused flag are surfaced separately by
                // the inspector status label.
                enabled = _accessor.IsSystemEnabled(systemIndex, EnableChannel.Editor);
                return true;
            }
            catch (Exception)
            {
                return false;
            }
        }

        public void SetSystemEnabled(int systemIndex, bool enabled)
        {
            if (_world == null || systemIndex < 0)
                return;
            try
            {
                _accessor.SetSystemEnabled(systemIndex, EnableChannel.Editor, enabled);
            }
            catch (Exception)
            {
                // Best-effort.
            }
        }

        public bool TryGetSystemEffectivelyEnabled(int systemIndex, out bool enabled)
        {
            enabled = false;
            if (_world == null || systemIndex < 0)
                return false;
            try
            {
                // Combines all enable channels with the deterministic paused
                // flag — the hierarchy grayout uses this so a system disabled
                // via code (User / Playback channel, or paused) shows as
                // grayed even when the inspector's Editor-channel toggle is
                // still checked.
                enabled = _world.IsSystemEffectivelyEnabled(systemIndex);
                return true;
            }
            catch (Exception)
            {
                return false;
            }
        }

        IReadOnlyList<TemplateRef> ProjectTemplates()
        {
            var list = new List<TemplateRef>();
            if (_info == null)
                return list;
            // Pass 1: build refs with their per-template projections (tags,
            // components, base names). DerivedTemplateNames is filled in
            // pass 2 once every template's base list is known.
            var byTemplate = new Dictionary<Template, TemplateRef>();
            foreach (var t in _info.AllTemplates)
            {
                ResolvedTemplate resolved = null;
                if (_info.IsResolvedTemplate(t))
                {
                    foreach (var rt in _info.ResolvedTemplates)
                    {
                        if (rt.Template == t)
                        {
                            resolved = rt;
                            break;
                        }
                    }
                }

                var allTagNames = ProjectAllTagNames(resolved);
                var componentNames = ProjectComponentNames(resolved);
                var baseNames = ProjectBaseTemplateNames(resolved);

                var tref = new TemplateRef(t, resolved, allTagNames, componentNames, baseNames);
                list.Add(tref);
                byTemplate[t] = tref;
            }
            // Pass 2: invert base relationships into derived lists. Each
            // template B that declares A as a base contributes B's name to
            // A's DerivedTemplateNames.
            var derivedAccumulator = new Dictionary<Template, List<string>>();
            foreach (var rt in _info.ResolvedTemplates)
            {
                if (rt?.AllBaseTemplates == null)
                    continue;
                var derivedName = rt.DebugName;
                if (string.IsNullOrEmpty(derivedName))
                    continue;
                foreach (var baseTemplate in rt.AllBaseTemplates)
                {
                    if (baseTemplate == null || baseTemplate == rt.Template)
                        continue;
                    if (!derivedAccumulator.TryGetValue(baseTemplate, out var sink))
                    {
                        sink = new List<string>();
                        derivedAccumulator[baseTemplate] = sink;
                    }
                    sink.Add(derivedName);
                }
            }
            foreach (var kv in derivedAccumulator)
            {
                if (byTemplate.TryGetValue(kv.Key, out var tref))
                {
                    kv.Value.Sort(StringComparer.OrdinalIgnoreCase);
                    tref.DerivedTemplateNames = kv.Value;
                }
            }
            return list;
        }

        static List<string> ProjectAllTagNames(ResolvedTemplate rt)
        {
            if (rt == null || rt.AllTags.IsNull)
                return null;
            var result = new List<string>();
            foreach (var t in rt.AllTags.Tags)
            {
                var n = t.ToString();
                if (!string.IsNullOrEmpty(n))
                    result.Add(n);
            }
            return result;
        }

        static List<string> ProjectComponentNames(ResolvedTemplate rt)
        {
            if (rt?.ComponentDeclarations == null)
                return null;
            var result = new List<string>();
            foreach (var d in rt.ComponentDeclarations)
            {
                if (d.ComponentType == null)
                    continue;
                var n = TrecsHierarchyWindow.ComponentTypeDisplayName(d.ComponentType);
                if (!string.IsNullOrEmpty(n))
                    result.Add(n);
            }
            return result;
        }

        static List<string> ProjectBaseTemplateNames(ResolvedTemplate rt)
        {
            if (rt?.AllBaseTemplates == null)
                return null;
            var result = new List<string>();
            foreach (var b in rt.AllBaseTemplates)
            {
                var n = b?.DebugName;
                if (!string.IsNullOrEmpty(n))
                    result.Add(n);
            }
            return result;
        }

        IReadOnlyList<ComponentTypeRef> ProjectComponentTypes()
        {
            var seen = new HashSet<Type>();
            var list = new List<ComponentTypeRef>();
            if (_info == null)
                return list;
            foreach (var rt in _info.ResolvedTemplates)
            {
                foreach (var d in rt.ComponentDeclarations)
                {
                    if (d.ComponentType == null || !seen.Add(d.ComponentType))
                        continue;
                    list.Add(
                        new ComponentTypeRef(
                            d.ComponentType,
                            TrecsHierarchyWindow.ComponentTypeDisplayName(d.ComponentType)
                        )
                    );
                }
            }
            return list;
        }

        IReadOnlyList<SetRef> ProjectSets()
        {
            var list = new List<SetRef>();
            if (_info?.AllSets == null)
                return list;
            foreach (var s in _info.AllSets)
            {
                List<string> tagNames = null;
                if (!s.Tags.IsNull)
                {
                    tagNames = new List<string>();
                    foreach (var t in s.Tags.Tags)
                    {
                        var n = t.ToString();
                        if (!string.IsNullOrEmpty(n))
                            tagNames.Add(n);
                    }
                }
                list.Add(new SetRef(s, tagNames));
            }
            return list;
        }

        IReadOnlyList<TagRef> ProjectTags()
        {
            var list = new List<TagRef>();
            var seen = new HashSet<int>();
            if (_info == null)
                return list;
            foreach (var rt in _info.ResolvedTemplates)
            {
                AccumulateTags(rt.AllTags, seen, list);
                foreach (var p in rt.Partitions)
                {
                    AccumulateTags(p, seen, list);
                }
            }
            foreach (var s in _info.AllSets)
            {
                AccumulateTags(s.Tags, seen, list);
            }
            return list;
        }

        static void AccumulateTags(TagSet ts, HashSet<int> seen, List<TagRef> sink)
        {
            if (ts.IsNull)
                return;
            foreach (var t in ts.Tags)
            {
                if (t.Guid == 0 || !seen.Add(t.Guid))
                    continue;
                sink.Add(new TagRef(t));
            }
        }

        IReadOnlyList<AccessorPhaseRef> ProjectAccessors()
        {
            var phases = new List<AccessorPhaseRef>();
            if (_accessor == null || _world == null)
                return phases;

            IReadOnlyList<SystemMetadata> systems;
            IReadOnlyList<int> sortedEarly;
            IReadOnlyList<int> sortedInput;
            IReadOnlyList<int> sortedFixed;
            IReadOnlyList<int> sortedPresentation;
            IReadOnlyList<int> sortedLate;
            try
            {
                systems = _accessor.GetSystems();
                sortedEarly = _accessor.GetSortedEarlyPresentationSystems();
                sortedInput = _accessor.GetSortedInputSystems();
                sortedFixed = _accessor.GetSortedFixedSystems();
                sortedPresentation = _accessor.GetSortedPresentationSystems();
                sortedLate = _accessor.GetSortedLatePresentationSystems();
            }
            catch (Exception)
            {
                return phases;
            }

            var systemAccessorIds = new HashSet<int>();
            for (int i = 0; i < systems.Count; i++)
            {
                var acc = systems[i].Accessor;
                if (acc != null)
                    systemAccessorIds.Add(acc.Id);
            }

            var validAccessorIds = new HashSet<int>();
            var manual = new List<AccessorRef>();
            try
            {
                foreach (var entry in _world.GetAccessorsById())
                {
                    var id = entry.Key;
                    var acc = entry.Value;
                    if (acc == null)
                        continue;
                    if (TrecsEditorAccessorNames.Contains(acc.DebugName))
                        continue;
                    validAccessorIds.Add(id);
                    if (!systemAccessorIds.Contains(id))
                    {
                        manual.Add(
                            new AccessorRef(
                                acc.DebugName ?? $"#{id}",
                                accessorId: id,
                                systemIndex: -1,
                                executionPriority: null,
                                isManual: true,
                                role: acc.Role,
                                createdAtFile: acc.CreatedAtFile ?? string.Empty,
                                createdAtLine: acc.CreatedAtLine
                            )
                        );
                    }
                }
            }
            catch (Exception)
            {
                return phases;
            }

            void AddPhase(string title, IReadOnlyList<int> sortedGlobalIndices)
            {
                if (sortedGlobalIndices == null || sortedGlobalIndices.Count == 0)
                    return;
                var bucket = new List<AccessorRef>(sortedGlobalIndices.Count);
                foreach (var sysIdx in sortedGlobalIndices)
                {
                    var info = systems[sysIdx];
                    var acc = info.Accessor;
                    if (acc == null || !validAccessorIds.Contains(acc.Id))
                        continue;
                    bucket.Add(
                        new AccessorRef(
                            info.DebugName ?? acc.DebugName ?? $"#{acc.Id}",
                            accessorId: acc.Id,
                            systemIndex: sysIdx,
                            executionPriority: info.ExecutionPriority,
                            isManual: false,
                            // System-owned accessors carry the role
                            // derived from their phase — surface it on the
                            // ref so the inspector / hierarchy badge can
                            // show it without re-walking metadata.
                            role: acc.Role
                        )
                    );
                }
                if (bucket.Count > 0)
                {
                    phases.Add(new AccessorPhaseRef(title, bucket));
                }
            }

            AddPhase("Early Presentation", sortedEarly);
            AddPhase("Input", sortedInput);
            AddPhase("Fixed", sortedFixed);
            AddPhase("Presentation", sortedPresentation);
            AddPhase("Late Presentation", sortedLate);

            if (manual.Count > 0)
            {
                manual.Sort(
                    (a, b) =>
                        string.Compare(a.DebugName, b.DebugName, StringComparison.OrdinalIgnoreCase)
                );
                phases.Add(new AccessorPhaseRef("Other", manual));
            }

            return phases;
        }
    }

    /// <summary>
    /// Adapts <see cref="TrecsAccessTracker"/>'s ComponentId-keyed surface to
    /// the display-name-keyed <see cref="IAccessTracker"/> shape so live and
    /// cache mode answer the same questions.
    /// </summary>
    sealed class LiveAccessTrackerView : IAccessTracker
    {
        readonly World _world;

        public LiveAccessTrackerView(World world)
        {
            _world = world;
        }

        public IReadOnlyCollection<string> GetReadersOfComponent(string componentDisplayName)
        {
            var tracker = TrecsAccessRegistry.GetTracker(_world);
            if (tracker == null)
                return Array.Empty<string>();
            if (!TryResolveComponentId(componentDisplayName, out var id))
                return Array.Empty<string>();
            return tracker.GetReadersOf(id);
        }

        public IReadOnlyCollection<string> GetWritersOfComponent(string componentDisplayName)
        {
            var tracker = TrecsAccessRegistry.GetTracker(_world);
            if (tracker == null)
                return Array.Empty<string>();
            if (!TryResolveComponentId(componentDisplayName, out var id))
                return Array.Empty<string>();
            return tracker.GetWritersOf(id);
        }

        public IReadOnlyCollection<string> GetComponentsReadBy(string systemName)
        {
            var tracker = TrecsAccessRegistry.GetTracker(_world);
            if (tracker == null)
                return Array.Empty<string>();
            return ProjectIdsToNames(tracker.GetReadsBy(systemName));
        }

        public IReadOnlyCollection<string> GetComponentsWrittenBy(string systemName)
        {
            var tracker = TrecsAccessRegistry.GetTracker(_world);
            if (tracker == null)
                return Array.Empty<string>();
            return ProjectIdsToNames(tracker.GetWritesBy(systemName));
        }

        public IReadOnlyCollection<string> GetTagNamesTouchedBy(string accessorDebugName)
        {
            if (_world == null || _world.IsDisposed || string.IsNullOrEmpty(accessorDebugName))
                return Array.Empty<string>();
            var tracker = TrecsAccessRegistry.GetTracker(_world);
            if (tracker == null)
                return Array.Empty<string>();
            var groups = tracker.GetGroupsTouchedBy(accessorDebugName);
            if (groups == null || groups.Count == 0)
                return Array.Empty<string>();
            HashSet<string> seen = null;
            try
            {
                var info = _world.WorldInfo;
                foreach (var g in groups)
                {
                    foreach (var t in info.GetGroupTags(g))
                    {
                        var name = t.ToString();
                        if (string.IsNullOrEmpty(name))
                            continue;
                        seen ??= new HashSet<string>();
                        seen.Add(name);
                    }
                }
            }
            catch (Exception)
            {
                return Array.Empty<string>();
            }
            if (seen == null || seen.Count == 0)
                return Array.Empty<string>();
            return seen;
        }

        public IReadOnlyCollection<string> GetTemplateNamesAddedBy(string accessorDebugName) =>
            CollectTemplateNames(accessorDebugName, t => t.GetGroupsAddedBy(accessorDebugName));

        public IReadOnlyCollection<string> GetTemplateNamesRemovedBy(string accessorDebugName) =>
            CollectTemplateNames(accessorDebugName, t => t.GetGroupsRemovedBy(accessorDebugName));

        public IReadOnlyCollection<string> GetTemplateNamesMovedBy(string accessorDebugName) =>
            CollectTemplateNames(accessorDebugName, t => t.GetGroupsMovedBy(accessorDebugName));

        public IReadOnlyCollection<string> GetSystemsAddingTo(string templateDebugName) =>
            CollectSystemsForTemplate(templateDebugName, (t, g) => t.GetSystemsAddingTo(g));

        public IReadOnlyCollection<string> GetSystemsRemovingFrom(string templateDebugName) =>
            CollectSystemsForTemplate(templateDebugName, (t, g) => t.GetSystemsRemovingFrom(g));

        public IReadOnlyCollection<string> GetSystemsMovingOn(string templateDebugName) =>
            CollectSystemsForTemplate(templateDebugName, (t, g) => t.GetSystemsMovingOn(g));

        IReadOnlyCollection<string> CollectTemplateNames(
            string accessorDebugName,
            Func<TrecsAccessTracker, IReadOnlyCollection<GroupIndex>> getGroups
        )
        {
            if (_world == null || _world.IsDisposed || string.IsNullOrEmpty(accessorDebugName))
                return Array.Empty<string>();
            var tracker = TrecsAccessRegistry.GetTracker(_world);
            if (tracker == null)
                return Array.Empty<string>();
            var groups = getGroups(tracker);
            if (groups == null || groups.Count == 0)
                return Array.Empty<string>();
            HashSet<string> seen = null;
            try
            {
                var info = _world.WorldInfo;
                foreach (var g in groups)
                {
                    var template = info.GetResolvedTemplateForGroup(g);
                    var name = template?.DebugName;
                    if (string.IsNullOrEmpty(name))
                        continue;
                    seen ??= new HashSet<string>();
                    seen.Add(name);
                }
            }
            catch (Exception)
            {
                return Array.Empty<string>();
            }
            return seen ?? (IReadOnlyCollection<string>)Array.Empty<string>();
        }

        IReadOnlyCollection<string> CollectSystemsForTemplate(
            string templateDebugName,
            Func<TrecsAccessTracker, GroupIndex, IReadOnlyCollection<string>> getSystems
        )
        {
            if (_world == null || _world.IsDisposed || string.IsNullOrEmpty(templateDebugName))
                return Array.Empty<string>();
            var tracker = TrecsAccessRegistry.GetTracker(_world);
            if (tracker == null)
                return Array.Empty<string>();
            HashSet<string> seen = null;
            try
            {
                foreach (var rt in _world.WorldInfo.ResolvedTemplates)
                {
                    if (rt?.DebugName != templateDebugName)
                        continue;
                    var groups = rt.Groups;
                    if (groups == null)
                        continue;
                    foreach (var g in groups)
                    {
                        var systems = getSystems(tracker, g);
                        if (systems == null)
                            continue;
                        foreach (var s in systems)
                        {
                            if (string.IsNullOrEmpty(s))
                                continue;
                            seen ??= new HashSet<string>();
                            seen.Add(s);
                        }
                    }
                }
            }
            catch (Exception)
            {
                return Array.Empty<string>();
            }
            return seen ?? (IReadOnlyCollection<string>)Array.Empty<string>();
        }

        bool TryResolveComponentId(string displayName, out ComponentId id)
        {
            id = default;
            if (_world == null || _world.IsDisposed || string.IsNullOrEmpty(displayName))
                return false;
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
                            id = new ComponentId(TypeIdProvider.GetTypeId(d.ComponentType));
                            return true;
                        }
                    }
                }
            }
            catch (Exception) { }
            return false;
        }

        IReadOnlyCollection<string> ProjectIdsToNames(IReadOnlyCollection<ComponentId> ids)
        {
            if (ids == null || ids.Count == 0 || _world == null || _world.IsDisposed)
                return Array.Empty<string>();
            var idSet = new HashSet<ComponentId>(ids);
            var names = new HashSet<string>();
            try
            {
                foreach (var rt in _world.WorldInfo.ResolvedTemplates)
                {
                    foreach (var d in rt.ComponentDeclarations)
                    {
                        if (d.ComponentType == null)
                            continue;
                        var cid = new ComponentId(TypeIdProvider.GetTypeId(d.ComponentType));
                        if (idSet.Contains(cid))
                        {
                            names.Add(
                                TrecsHierarchyWindow.ComponentTypeDisplayName(d.ComponentType)
                            );
                        }
                    }
                }
            }
            catch (Exception) { }
            return names;
        }
    }
}
