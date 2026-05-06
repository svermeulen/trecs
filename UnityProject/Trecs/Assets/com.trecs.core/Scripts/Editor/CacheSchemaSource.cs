using System;
using System.Collections.Generic;

namespace Trecs
{
    /// <summary>
    /// <see cref="ITrecsSchemaSource"/> backed by a deserialized
    /// <see cref="TrecsSchema"/> JSON snapshot. Capability gates report no
    /// entity iteration / system enable / live refresh — entities aren't
    /// captured in the schema, the system's enabled flag is runtime-only,
    /// and the snapshot is static. The window UI-disables interactive
    /// controls in cache mode so the user sees affordances exist but
    /// can't interact.
    /// </summary>
    public sealed class CacheSchemaSource : ITrecsSchemaSource
    {
        readonly TrecsSchema _schema;

        IReadOnlyList<TemplateRef> _templates;
        IReadOnlyList<ComponentTypeRef> _componentTypes;
        IReadOnlyList<SetRef> _sets;
        IReadOnlyList<TagRef> _tags;
        IReadOnlyList<AccessorPhaseRef> _accessorsByPhase;
        CacheAccessTrackerView _trackerView;

        public CacheSchemaSource(TrecsSchema schema)
        {
            _schema = schema;
        }

        public string DisplayName => _schema?.WorldName ?? "(unnamed)";
        public bool IsLive => false;
        public bool SupportsEntityIteration => false;
        public bool SupportsSystemEnableToggle => false;
        public bool SupportsLiveRefresh => false;

        public TrecsSchema Schema => _schema;

        public IReadOnlyList<TemplateRef> Templates => _templates ??= ProjectTemplates();

        public IReadOnlyList<ComponentTypeRef> ComponentTypes =>
            _componentTypes ??= ProjectComponentTypes();

        public IReadOnlyList<SetRef> Sets => _sets ??= ProjectSets();

        public IReadOnlyList<TagRef> Tags => _tags ??= ProjectTags();

        public IReadOnlyList<AccessorPhaseRef> AccessorsByPhase =>
            _accessorsByPhase ??= ProjectAccessors();

        public IAccessTracker AccessTracker => _trackerView ??= new CacheAccessTrackerView(_schema);

        public int CountEntitiesInGroup(GroupIndex group) => 0;

        public IEnumerable<EntityHandle> EntitiesInGroup(GroupIndex group, int max) =>
            Array.Empty<EntityHandle>();

        public bool TryGetSystemEnabled(int systemIndex, out bool enabled)
        {
            enabled = false;
            return false;
        }

        public void SetSystemEnabled(int systemIndex, bool enabled)
        {
            // No-op in cache mode.
        }

        public bool TryGetSystemEffectivelyEnabled(int systemIndex, out bool enabled)
        {
            enabled = false;
            return false;
        }

        IReadOnlyList<TemplateRef> ProjectTemplates()
        {
            var list = new List<TemplateRef>();
            if (_schema?.Templates == null)
                return list;
            foreach (var t in _schema.Templates)
            {
                list.Add(new TemplateRef(t));
            }
            return list;
        }

        IReadOnlyList<ComponentTypeRef> ProjectComponentTypes()
        {
            var list = new List<ComponentTypeRef>();
            if (_schema?.ComponentTypes == null)
                return list;
            foreach (var c in _schema.ComponentTypes)
            {
                list.Add(new ComponentTypeRef(c));
            }
            return list;
        }

        IReadOnlyList<SetRef> ProjectSets()
        {
            var list = new List<SetRef>();
            if (_schema?.Sets == null)
                return list;
            foreach (var s in _schema.Sets)
            {
                list.Add(new SetRef(s));
            }
            return list;
        }

        IReadOnlyList<TagRef> ProjectTags()
        {
            var list = new List<TagRef>();
            if (_schema?.Tags == null)
                return list;
            foreach (var t in _schema.Tags)
            {
                list.Add(new TagRef(t));
            }
            return list;
        }

        IReadOnlyList<AccessorPhaseRef> ProjectAccessors()
        {
            var phases = new List<AccessorPhaseRef>();
            if (_schema == null)
                return phases;

            // Bucket systems by phase. Insertion order in schema.Systems is
            // already topologically sorted (TrecsSchemaCache does this at
            // save time) — preserved by walking the list once, splitting
            // into phase buckets in encounter order.
            var byPhase = new Dictionary<string, List<TrecsSchemaSystem>>();
            var phaseOrder = new List<string>();
            foreach (var s in _schema.Systems)
            {
                var phaseKey = s.Phase ?? string.Empty;
                if (!byPhase.TryGetValue(phaseKey, out var bucket))
                {
                    bucket = new List<TrecsSchemaSystem>();
                    byPhase[phaseKey] = bucket;
                    phaseOrder.Add(phaseKey);
                }
                bucket.Add(s);
            }

            // Match the live tree's phase-title order regardless of
            // schema.Systems order (defensive — TrecsSchemaCache already
            // sorts, but a hand-edited cache shouldn't reorder phases).
            var titleOrder = new List<string>
            {
                "Early Presentation",
                "Input",
                "Fixed",
                "Presentation",
                "Late Presentation",
            };
            foreach (var p in phaseOrder)
            {
                if (!titleOrder.Contains(p) && !string.IsNullOrEmpty(p))
                {
                    titleOrder.Add(p);
                }
            }
            if (byPhase.ContainsKey(string.Empty))
            {
                titleOrder.Add(string.Empty);
            }

            foreach (var phaseKey in titleOrder)
            {
                if (!byPhase.TryGetValue(phaseKey, out var systemsInPhase))
                    continue;
                var phaseTitle = string.IsNullOrEmpty(phaseKey) ? "(no phase)" : phaseKey;
                var bucket = new List<AccessorRef>(systemsInPhase.Count);
                foreach (var s in systemsInPhase)
                {
                    bucket.Add(new AccessorRef(s));
                }
                phases.Add(new AccessorPhaseRef(phaseTitle, bucket));
            }

            if (_schema.ManualAccessors != null && _schema.ManualAccessors.Count > 0)
            {
                var manual = new List<AccessorRef>(_schema.ManualAccessors.Count);
                var sorted = new List<TrecsSchemaAccessor>(_schema.ManualAccessors);
                sorted.Sort(
                    (a, b) =>
                        string.Compare(
                            a.DebugName ?? string.Empty,
                            b.DebugName ?? string.Empty,
                            StringComparison.OrdinalIgnoreCase
                        )
                );
                foreach (var m in sorted)
                {
                    manual.Add(new AccessorRef(m));
                }
                phases.Add(new AccessorPhaseRef("Other", manual));
            }

            return phases;
        }
    }

    /// <summary>
    /// <see cref="IAccessTracker"/> backed by <see cref="TrecsSchema.Access"/>.
    /// Builds two name→names dictionaries on construction so per-inspector
    /// queries are O(1) — schema sizes are small (hundreds of entries) so
    /// the up-front cost is negligible.
    /// </summary>
    sealed class CacheAccessTrackerView : IAccessTracker
    {
        readonly Dictionary<string, IReadOnlyCollection<string>> _readersByComponent;
        readonly Dictionary<string, IReadOnlyCollection<string>> _writersByComponent;
        readonly Dictionary<string, HashSet<string>> _readsBySystem;
        readonly Dictionary<string, HashSet<string>> _writesBySystem;
        readonly Dictionary<string, IReadOnlyCollection<string>> _tagsTouchedByAccessor;
        readonly Dictionary<string, IReadOnlyCollection<string>> _addsByAccessor;
        readonly Dictionary<string, IReadOnlyCollection<string>> _removesByAccessor;
        readonly Dictionary<string, IReadOnlyCollection<string>> _movesByAccessor;
        readonly Dictionary<string, HashSet<string>> _addersByTemplate;
        readonly Dictionary<string, HashSet<string>> _removersByTemplate;
        readonly Dictionary<string, HashSet<string>> _moversByTemplate;

        public CacheAccessTrackerView(TrecsSchema schema)
        {
            _readersByComponent = new();
            _writersByComponent = new();
            _readsBySystem = new();
            _writesBySystem = new();
            _tagsTouchedByAccessor = new();
            _addsByAccessor = new();
            _removesByAccessor = new();
            _movesByAccessor = new();
            _addersByTemplate = new();
            _removersByTemplate = new();
            _moversByTemplate = new();

            if (schema == null)
                return;

            if (schema.Access != null)
            {
                foreach (var entry in schema.Access)
                {
                    var compName = entry.ComponentDisplayName ?? string.Empty;
                    if (entry.ReadBySystems != null && entry.ReadBySystems.Count > 0)
                    {
                        _readersByComponent[compName] = entry.ReadBySystems.ToArray();
                        foreach (var sys in entry.ReadBySystems)
                        {
                            AddTo(_readsBySystem, sys, compName);
                        }
                    }
                    if (entry.WrittenBySystems != null && entry.WrittenBySystems.Count > 0)
                    {
                        _writersByComponent[compName] = entry.WrittenBySystems.ToArray();
                        foreach (var sys in entry.WrittenBySystems)
                        {
                            AddTo(_writesBySystem, sys, compName);
                        }
                    }
                }
            }

            if (schema.TagsTouched != null)
            {
                foreach (var entry in schema.TagsTouched)
                {
                    if (
                        entry?.AccessorDebugName == null
                        || entry.TagNames == null
                        || entry.TagNames.Count == 0
                    )
                    {
                        continue;
                    }
                    _tagsTouchedByAccessor[entry.AccessorDebugName] = entry.TagNames.ToArray();
                }
            }

            if (schema.Structural != null)
            {
                foreach (var entry in schema.Structural)
                {
                    if (entry?.AccessorDebugName == null)
                    {
                        continue;
                    }
                    var sys = entry.AccessorDebugName;
                    IndexStructural(
                        sys,
                        entry.AddedTemplateNames,
                        _addsByAccessor,
                        _addersByTemplate
                    );
                    IndexStructural(
                        sys,
                        entry.RemovedTemplateNames,
                        _removesByAccessor,
                        _removersByTemplate
                    );
                    IndexStructural(
                        sys,
                        entry.MovedTemplateNames,
                        _movesByAccessor,
                        _moversByTemplate
                    );
                }
            }
        }

        static void IndexStructural(
            string accessorDebugName,
            List<string> templateNames,
            Dictionary<string, IReadOnlyCollection<string>> bySystem,
            Dictionary<string, HashSet<string>> byTemplate
        )
        {
            if (templateNames == null || templateNames.Count == 0)
            {
                return;
            }
            bySystem[accessorDebugName] = templateNames.ToArray();
            foreach (var template in templateNames)
            {
                if (string.IsNullOrEmpty(template))
                {
                    continue;
                }
                AddTo(byTemplate, template, accessorDebugName);
            }
        }

        public IReadOnlyCollection<string> GetReadersOfComponent(string componentDisplayName) =>
            _readersByComponent.TryGetValue(componentDisplayName ?? string.Empty, out var v)
                ? v
                : Array.Empty<string>();

        public IReadOnlyCollection<string> GetWritersOfComponent(string componentDisplayName) =>
            _writersByComponent.TryGetValue(componentDisplayName ?? string.Empty, out var v)
                ? v
                : Array.Empty<string>();

        public IReadOnlyCollection<string> GetComponentsReadBy(string systemName) =>
            _readsBySystem.TryGetValue(systemName ?? string.Empty, out var v)
                ? v
                : (IReadOnlyCollection<string>)Array.Empty<string>();

        public IReadOnlyCollection<string> GetComponentsWrittenBy(string systemName) =>
            _writesBySystem.TryGetValue(systemName ?? string.Empty, out var v)
                ? v
                : (IReadOnlyCollection<string>)Array.Empty<string>();

        public IReadOnlyCollection<string> GetTagNamesTouchedBy(string accessorDebugName) =>
            _tagsTouchedByAccessor.TryGetValue(accessorDebugName ?? string.Empty, out var v)
                ? v
                : Array.Empty<string>();

        public IReadOnlyCollection<string> GetTemplateNamesAddedBy(string accessorDebugName) =>
            _addsByAccessor.TryGetValue(accessorDebugName ?? string.Empty, out var v)
                ? v
                : Array.Empty<string>();

        public IReadOnlyCollection<string> GetTemplateNamesRemovedBy(string accessorDebugName) =>
            _removesByAccessor.TryGetValue(accessorDebugName ?? string.Empty, out var v)
                ? v
                : Array.Empty<string>();

        public IReadOnlyCollection<string> GetTemplateNamesMovedBy(string accessorDebugName) =>
            _movesByAccessor.TryGetValue(accessorDebugName ?? string.Empty, out var v)
                ? v
                : Array.Empty<string>();

        public IReadOnlyCollection<string> GetSystemsAddingTo(string templateDebugName) =>
            _addersByTemplate.TryGetValue(templateDebugName ?? string.Empty, out var v)
                ? v
                : (IReadOnlyCollection<string>)Array.Empty<string>();

        public IReadOnlyCollection<string> GetSystemsRemovingFrom(string templateDebugName) =>
            _removersByTemplate.TryGetValue(templateDebugName ?? string.Empty, out var v)
                ? v
                : (IReadOnlyCollection<string>)Array.Empty<string>();

        public IReadOnlyCollection<string> GetSystemsMovingOn(string templateDebugName) =>
            _moversByTemplate.TryGetValue(templateDebugName ?? string.Empty, out var v)
                ? v
                : (IReadOnlyCollection<string>)Array.Empty<string>();

        static void AddTo(Dictionary<string, HashSet<string>> map, string key, string value)
        {
            if (!map.TryGetValue(key, out var set))
            {
                set = new HashSet<string>();
                map[key] = set;
            }
            set.Add(value);
        }
    }
}
