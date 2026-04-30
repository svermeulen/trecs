using System;
using System.Collections.Generic;
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
    /// the user picks an accessor row in <see cref="TrecsHierarchyWindow"/>.
    /// Identifies the accessor by <see cref="WorldAccessor.Id"/>, which is
    /// stable for the world's lifetime and works for both system-owned and
    /// manually-created (<see cref="World.CreateAccessor"/>) accessors. The
    /// inspector resolves the id back to a live <see cref="WorldAccessor"/>
    /// each refresh, then optionally maps it to its owning system to surface
    /// system-only metadata (phase, dependencies, enable toggle).
    /// </summary>
    public class TrecsAccessorSelection : ScriptableObject
    {
        [NonSerialized]
        WeakReference<World> _worldRef;

        [NonSerialized]
        public int AccessorId = -1;

        [NonSerialized]
        public TrecsSchema CacheSchema;

        // Cache mode identifies an accessor by its debug name (the same
        // string the schema saved). Could be a system or a manual accessor.
        [NonSerialized]
        public string CacheAccessorName;

        public World GetWorld()
        {
            if (_worldRef == null)
            {
                return null;
            }
            return _worldRef.TryGetTarget(out var w) ? w : null;
        }

        public void Set(World world, int accessorId, string displayName)
        {
            _worldRef = world == null ? null : new WeakReference<World>(world);
            AccessorId = accessorId;
            CacheSchema = null;
            CacheAccessorName = null;
            name = displayName;
        }

        public void SetCache(TrecsSchema schema, string accessorName)
        {
            _worldRef = null;
            AccessorId = -1;
            CacheSchema = schema;
            CacheAccessorName = accessorName;
            name = accessorName ?? "Accessor";
        }
    }

    [CustomEditor(typeof(TrecsAccessorSelection))]
    public class TrecsAccessorSelectionInspector : Editor
    {
        [InitializeOnLoadMethod]
        static void RegisterEditorAccessorName() =>
            TrecsEditorAccessorNames.Register("TrecsAccessorSelectionInspector");

        WorldAccessor _windowAccessor;
        VisualElement _bodyContainer;
        Label _statusLabel;
        Label _nameValue;
        Label _kindValue;
        Label _typeValue;
        Label _namespaceValue;
        Label _phaseValue;
        Label _priorityValue;
        Foldout _dependsOnFoldout;
        Foldout _dependentsFoldout;
        Toggle _enabledToggle;
        VisualElement _systemOnlySection;
        Foldout _readsFoldout;
        Foldout _writesFoldout;
        Foldout _tagsTouchedFoldout;
        Label _tagsCaveat;
        bool _suppressToggleEvents;

        // Identity of the currently-rendered accessor: boxed int for live
        // (AccessorId), string for cache (CacheAccessorName). RenderStatic
        // only fires when this changes.
        object _renderedEntryKey;
        int _lastReadsHash;
        int _lastWritesHash;
        int _lastTagsTouchedHash;

        public override VisualElement CreateInspectorGUI()
        {
            var root = new VisualElement();
            root.style.paddingLeft = 8;
            root.style.paddingRight = 8;
            root.style.paddingTop = 8;
            root.style.paddingBottom = 8;

            _statusLabel = new Label();
            _statusLabel.style.opacity = 0.7f;
            _statusLabel.style.whiteSpace = WhiteSpace.Normal;
            _statusLabel.style.display = DisplayStyle.None;
            root.Add(_statusLabel);

            _bodyContainer = new VisualElement();
            BuildStaticBody(_bodyContainer);
            root.Add(_bodyContainer);

            Refresh();

            var refreshMs = TrecsDebugWindowSettings.Get().RefreshIntervalMs;
            root.schedule.Execute(Refresh).Every(refreshMs);

            return root;
        }

        void BuildStaticBody(VisualElement container)
        {
            container.Clear();

            _nameValue = AddRow(container, "Name", "");
            _nameValue.style.unityFontStyleAndWeight = FontStyle.Bold;

            // Shift+hover the heading → hierarchy scrolls back to this
            // accessor's tree row.
            TrecsInspectorLinks.WireHoverPreviewAccessor(
                _nameValue,
                () =>
                {
                    var sel = target as TrecsAccessorSelection;
                    return (sel?.GetWorld(), sel?.AccessorId ?? -1);
                }
            );

            _kindValue = AddRow(container, "Kind", "");

            // System-only metadata: kept in its own VisualElement so the whole
            // block hides cleanly for non-system (manual) accessors.
            _systemOnlySection = new VisualElement();
            _typeValue = AddRow(_systemOnlySection, "Type", "");
            _namespaceValue = AddRow(_systemOnlySection, "Namespace", "");
            _phaseValue = AddRow(_systemOnlySection, "Phase", "");
            _priorityValue = AddRow(_systemOnlySection, "Priority", "");

            _dependsOnFoldout = new Foldout { text = "Depends on", value = true };
            _dependsOnFoldout.style.marginTop = 6;
            _systemOnlySection.Add(_dependsOnFoldout);

            _dependentsFoldout = new Foldout { text = "Dependents", value = true };
            _dependentsFoldout.style.marginTop = 6;
            _systemOnlySection.Add(_dependentsFoldout);

            _enabledToggle = new Toggle("Enabled");
            _enabledToggle.style.marginTop = 8;
            _enabledToggle.RegisterValueChangedCallback(OnEnabledToggleChanged);
            _systemOnlySection.Add(_enabledToggle);

            container.Add(_systemOnlySection);

            // Component access tracking (populated per-tick via tracker, so
            // manual accessors get covered too — not just systems).
            _readsFoldout = new Foldout { text = "Reads", value = true };
            _readsFoldout.style.marginTop = 6;
            container.Add(_readsFoldout);

            _writesFoldout = new Foldout { text = "Writes", value = true };
            _writesFoldout.style.marginTop = 6;
            container.Add(_writesFoldout);

            _tagsTouchedFoldout = new Foldout { text = "Tags touched", value = true };
            _tagsTouchedFoldout.style.marginTop = 6;
            container.Add(_tagsTouchedFoldout);

            _tagsCaveat = new Label(
                "Approximate — derived from groups touched at runtime. A tag listed here "
                    + "may sit on a group the system happens to read for some other component."
            );
            _tagsCaveat.style.opacity = 0.55f;
            _tagsCaveat.style.fontSize = 10;
            _tagsCaveat.style.whiteSpace = WhiteSpace.Normal;
            _tagsCaveat.style.marginTop = 2;
            _tagsCaveat.style.marginLeft = 4;
            _tagsCaveat.style.marginRight = 4;
            _tagsTouchedFoldout.Add(_tagsCaveat);
        }

        static Label AddRow(VisualElement container, string label, string initial)
        {
            var row = new VisualElement();
            row.style.flexDirection = FlexDirection.Row;
            row.style.marginTop = 2;

            var name = new Label(label);
            name.style.minWidth = 120;
            name.style.opacity = 0.7f;
            row.Add(name);

            var value = new Label(initial);
            value.style.flexGrow = 1;
            row.Add(value);

            container.Add(row);
            return value;
        }

        void OnEnabledToggleChanged(ChangeEvent<bool> evt)
        {
            if (_suppressToggleEvents)
            {
                return;
            }
            var selection = (TrecsAccessorSelection)target;
            var world = selection.GetWorld();
            if (world == null || world.IsDisposed || _windowAccessor == null)
            {
                return;
            }
            int systemIndex = -1;
            try
            {
                var systems = _windowAccessor.GetSystems();
                for (int i = 0; i < systems.Count; i++)
                {
                    var s = systems[i];
                    if (
                        s.Metadata.Accessor != null
                        && s.Metadata.Accessor.Id == selection.AccessorId
                    )
                    {
                        systemIndex = i;
                        break;
                    }
                }
            }
            catch (Exception)
            {
                return;
            }
            if (systemIndex < 0)
            {
                return;
            }
            try
            {
                _windowAccessor.GetSystemRunner().SetSystemEnabled(systemIndex, evt.newValue);
            }
            catch (Exception)
            {
                // World may have transitioned; next refresh will resync.
            }
        }

        void Refresh()
        {
            var selection = (TrecsAccessorSelection)target;
            if (selection == null)
            {
                return;
            }

            ResolveContext(
                selection,
                out var liveWorld,
                out var liveAccessor,
                out var liveSystem,
                out var liveSystems,
                out var liveRunner,
                out var liveSystemIndex,
                out var identity,
                out var error
            );

            if (error != null)
            {
                ShowStatus(error);
                return;
            }
            if (identity == null)
            {
                var w = selection.GetWorld();
                ShowStatus(
                    w == null || w.IsDisposed
                        ? "No accessor selected — world unavailable."
                        : "No accessor selected."
                );
                return;
            }

            _statusLabel.style.display = DisplayStyle.None;
            _bodyContainer.style.display = DisplayStyle.Flex;

            if (!Equals(identity, _renderedEntryKey))
            {
                _renderedEntryKey = identity;
                _lastReadsHash = 0;
                _lastWritesHash = 0;
                _lastTagsTouchedHash = 0;
                var entry =
                    liveWorld != null
                        ? BuildEntryFromLive(liveAccessor, liveSystem, liveSystems, liveSystemIndex)
                        : BuildEntryFromCache(selection.CacheSchema, selection.CacheAccessorName);
                var linker =
                    liveWorld != null
                        ? (InspectorLinker)new LiveInspectorLinker(liveWorld)
                        : new CacheInspectorLinker(selection.CacheSchema);
                bool isCache = liveWorld == null;
                RenderStatic(entry, linker, isCache);
            }

            UpdateRuntimeFields(
                selection,
                liveWorld,
                liveAccessor,
                liveRunner,
                liveSystem,
                liveSystemIndex
            );
        }

        // Resolve the live/cache context. Identity is boxed-int (live
        // AccessorId) or string (cache name). Error is populated when we
        // know the user picked something but it's no longer valid (e.g.
        // accessor unregistered) so we can show a specific status.
        void ResolveContext(
            TrecsAccessorSelection selection,
            out World liveWorld,
            out WorldAccessor liveAccessor,
            out ExecutableSystemInfo liveSystem,
            out IReadOnlyList<ExecutableSystemInfo> liveSystems,
            out SystemRunner liveRunner,
            out int liveSystemIndex,
            out object identity,
            out string error
        )
        {
            liveWorld = null;
            liveAccessor = null;
            liveSystem = null;
            liveSystems = null;
            liveRunner = null;
            liveSystemIndex = -1;
            identity = null;
            error = null;

            var world = selection.GetWorld();
            if (world != null && !world.IsDisposed && selection.AccessorId >= 0)
            {
                try
                {
                    _windowAccessor ??= world.CreateAccessor("TrecsAccessorSelectionInspector");
                }
                catch (Exception e)
                {
                    error = $"Failed to create world accessor: {e.Message}";
                    return;
                }
                try
                {
                    if (
                        !world.GetAllAccessors().TryGetValue(selection.AccessorId, out liveAccessor)
                    )
                    {
                        error = "Accessor is no longer registered.";
                        return;
                    }
                }
                catch (Exception e)
                {
                    error = $"Failed to read accessors: {e.Message}";
                    return;
                }
                try
                {
                    liveSystems = _windowAccessor.GetSystems();
                    liveRunner = _windowAccessor.GetSystemRunner();
                    for (int i = 0; i < liveSystems.Count; i++)
                    {
                        var s = liveSystems[i];
                        if (
                            s.Metadata.Accessor != null
                            && s.Metadata.Accessor.Id == selection.AccessorId
                        )
                        {
                            liveSystem = s;
                            liveSystemIndex = i;
                            break;
                        }
                    }
                }
                catch (Exception)
                {
                    // System list unavailable — treat as manual accessor.
                }
                liveWorld = world;
                identity = selection.AccessorId;
                return;
            }
            if (!string.IsNullOrEmpty(selection.CacheAccessorName))
            {
                identity = selection.CacheAccessorName;
            }
        }

        // Internal view type: same shape regardless of live or cache source.
        // Manual accessors leave system fields null/false and the renderer
        // hides the system-only block.
        sealed class AccessorEntry
        {
            public string DebugName;
            public bool IsSystem;
            public string TypeName;
            public string TypeNamespace;
            public string Phase;
            public bool HasPriority;
            public int Priority;
            public List<string> DependsOnSystemDebugNames = new();
            public List<string> DependentSystemDebugNames = new();
        }

        static AccessorEntry BuildEntryFromLive(
            WorldAccessor accessor,
            ExecutableSystemInfo systemInfo,
            IReadOnlyList<ExecutableSystemInfo> allSystems,
            int systemIndex
        )
        {
            var entry = new AccessorEntry { DebugName = accessor.DebugName ?? $"#{accessor.Id}" };
            if (systemInfo == null)
            {
                return entry;
            }
            var sysType = systemInfo.System.GetType();
            entry.IsSystem = true;
            entry.TypeName = sysType.Name;
            entry.TypeNamespace = sysType.Namespace;
            entry.Phase = systemInfo.Metadata.Phase.ToString();
            entry.HasPriority = systemInfo.Metadata.ExecutionPriority.HasValue;
            entry.Priority = systemInfo.Metadata.ExecutionPriority ?? 0;
            var deps = systemInfo.Metadata.SystemDependencies;
            if (deps != null && allSystems != null)
            {
                foreach (var depIdx in deps)
                {
                    if (depIdx < 0 || depIdx >= allSystems.Count)
                    {
                        entry.DependsOnSystemDebugNames.Add($"#{depIdx}");
                        continue;
                    }
                    var depInfo = allSystems[depIdx];
                    entry.DependsOnSystemDebugNames.Add(
                        depInfo.Metadata.DebugName ?? depInfo.System.GetType().Name
                    );
                }
            }
            // Dependents: scan every other system's deps for an index
            // pointing at us. Cheap (systems list is small) and keeps the
            // entry self-contained. systemIndex is what ResolveContext
            // already produced — avoid re-scanning to find ourselves.
            if (allSystems != null && systemIndex >= 0)
            {
                for (int i = 0; i < allSystems.Count; i++)
                {
                    var other = allSystems[i];
                    var otherDeps = other.Metadata.SystemDependencies;
                    if (otherDeps == null)
                        continue;
                    foreach (var idx in otherDeps)
                    {
                        if (idx == systemIndex)
                        {
                            entry.DependentSystemDebugNames.Add(
                                other.Metadata.DebugName ?? other.System.GetType().Name
                            );
                            break;
                        }
                    }
                }
                entry.DependentSystemDebugNames.Sort(StringComparer.OrdinalIgnoreCase);
            }
            return entry;
        }

        static AccessorEntry BuildEntryFromCache(TrecsSchema schema, string accessorName)
        {
            var entry = new AccessorEntry { DebugName = accessorName ?? "(unnamed)" };
            if (schema == null)
                return entry;
            foreach (var sys in schema.Systems)
            {
                if (sys.DebugName != accessorName)
                    continue;
                entry.IsSystem = true;
                entry.TypeName = sys.TypeName;
                entry.TypeNamespace = sys.TypeNamespace;
                entry.Phase = sys.Phase;
                entry.HasPriority = sys.HasPriority;
                entry.Priority = sys.Priority;
                if (sys.DependsOnSystemDebugNames != null)
                {
                    foreach (var d in sys.DependsOnSystemDebugNames)
                    {
                        entry.DependsOnSystemDebugNames.Add(d);
                    }
                }
                if (sys.DependentSystemDebugNames != null)
                {
                    foreach (var d in sys.DependentSystemDebugNames)
                    {
                        entry.DependentSystemDebugNames.Add(d);
                    }
                }
                return entry;
            }
            // Not in Systems — treat as manual; debug name is enough.
            return entry;
        }

        void RenderStatic(AccessorEntry entry, InspectorLinker linker, bool isCache)
        {
            _nameValue.text = entry.DebugName ?? "(unnamed)";
            if (!entry.IsSystem)
            {
                _kindValue.text = isCache ? "Manual accessor (cached)" : "Manual accessor";
                _systemOnlySection.style.display = DisplayStyle.None;
                return;
            }
            _kindValue.text = isCache ? "System (cached)" : "System";
            _systemOnlySection.style.display = DisplayStyle.Flex;
            _typeValue.text = entry.TypeName ?? string.Empty;
            _namespaceValue.text = string.IsNullOrEmpty(entry.TypeNamespace)
                ? "(none)"
                : entry.TypeNamespace;
            _phaseValue.text = entry.Phase ?? string.Empty;
            _priorityValue.text = entry.HasPriority ? entry.Priority.ToString() : "(none)";

            FillSystemFoldout(_dependsOnFoldout, entry.DependsOnSystemDebugNames, linker);
            FillSystemFoldout(_dependentsFoldout, entry.DependentSystemDebugNames, linker);
            // Cache mode: lock the toggle on (accessor stale; can't drive
            // runtime). Live mode hands control to UpdateRuntimeFields.
            if (isCache)
            {
                _enabledToggle.SetValueWithoutNotify(true);
                _enabledToggle.SetEnabled(false);
            }
            else
            {
                _enabledToggle.SetEnabled(true);
            }
        }

        void UpdateRuntimeFields(
            TrecsAccessorSelection selection,
            World world,
            WorldAccessor accessor,
            SystemRunner runner,
            ExecutableSystemInfo systemInfo,
            int systemIndex
        )
        {
            // Enabled toggle reflects current runner state when live.
            if (world != null && !world.IsDisposed && systemInfo != null && runner != null)
            {
                _suppressToggleEvents = true;
                try
                {
                    _enabledToggle.SetValueWithoutNotify(runner.IsSystemEnabled(systemIndex));
                }
                finally
                {
                    _suppressToggleEvents = false;
                }
            }

            // Reads/Writes — live tracker (per-tick) or schema.Access (frozen).
            IReadOnlyCollection<string> reads = null;
            IReadOnlyCollection<string> writes = null;
            InspectorLinker linker;
            if (world != null && !world.IsDisposed)
            {
                var tracker = TrecsAccessRegistry.GetTracker(world);
                if (tracker != null && accessor != null)
                {
                    var rIds = tracker.GetReadsBy(accessor.DebugName);
                    var wIds = tracker.GetWritesBy(accessor.DebugName);
                    reads = TranslateComponentIds(rIds);
                    writes = TranslateComponentIds(wIds);
                }
                linker = new LiveInspectorLinker(world);
            }
            else
            {
                var (r, w) = ExtractCacheReadsWrites(
                    selection.CacheSchema,
                    selection.CacheAccessorName
                );
                reads = r;
                writes = w;
                linker = new CacheInspectorLinker(selection.CacheSchema);
            }
            ApplyNameList(_readsFoldout, reads, ref _lastReadsHash, linker.ComponentTypeLink);
            ApplyNameList(_writesFoldout, writes, ref _lastWritesHash, linker.ComponentTypeLink);

            // Tags touched — live derives from tracker groups; cache mode
            // doesn't capture this so it shows a marker.
            UpdateTagsTouched(world, accessor, linker);
        }

        // Tracker stores ComponentId; the foldout renders display names. We
        // translate once per refresh and feed the same name → linker path
        // both modes use.
        static IReadOnlyCollection<string> TranslateComponentIds(
            IReadOnlyCollection<ComponentId> ids
        )
        {
            if (ids == null || ids.Count == 0)
            {
                return Array.Empty<string>();
            }
            var names = new List<string>(ids.Count);
            foreach (var id in ids)
            {
                Type type = null;
                try
                {
                    type = TypeIdProvider.GetTypeFromId(id.Value);
                }
                catch (Exception) { }
                names.Add(
                    type != null
                        ? TrecsHierarchyWindow.ComponentTypeDisplayName(type)
                        : $"#{id.Value}"
                );
            }
            return names;
        }

        static (List<string> reads, List<string> writes) ExtractCacheReadsWrites(
            TrecsSchema schema,
            string accessorName
        )
        {
            var reads = new List<string>();
            var writes = new List<string>();
            if (schema?.Access == null)
                return (reads, writes);
            foreach (var a in schema.Access)
            {
                if (a.ReadBySystems != null && a.ReadBySystems.Contains(accessorName))
                {
                    reads.Add(a.ComponentDisplayName ?? "?");
                }
                if (a.WrittenBySystems != null && a.WrittenBySystems.Contains(accessorName))
                {
                    writes.Add(a.ComponentDisplayName ?? "?");
                }
            }
            return (reads, writes);
        }

        void UpdateTagsTouched(World world, WorldAccessor accessor, InspectorLinker linker)
        {
            if (world == null || world.IsDisposed)
            {
                if (_lastTagsTouchedHash != -1)
                {
                    _lastTagsTouchedHash = -1;
                    StripTagsTouchedRows();
                    _tagsTouchedFoldout.Add(
                        MakeMutedLine("(cached — runtime tracker data not available)")
                    );
                }
                return;
            }

            var tags = new Dictionary<int, Tag>();
            try
            {
                var tracker = TrecsAccessRegistry.GetTracker(world);
                if (tracker != null && accessor != null)
                {
                    var info = world.WorldInfo;
                    foreach (var g in tracker.GetGroupsTouchedBy(accessor.DebugName))
                    {
                        foreach (var t in info.GetGroupTags(g))
                        {
                            if (t.Guid != 0 && !tags.ContainsKey(t.Guid))
                            {
                                tags[t.Guid] = t;
                            }
                        }
                    }
                }
            }
            catch (Exception)
            {
                return;
            }

            int hash = tags.Count;
            foreach (var kv in tags)
            {
                hash ^= kv.Key;
            }
            if (hash == _lastTagsTouchedHash)
                return;
            _lastTagsTouchedHash = hash;

            StripTagsTouchedRows();
            if (tags.Count == 0)
            {
                _tagsTouchedFoldout.Add(MakeMutedLine("(none recorded)"));
                return;
            }
            var sorted = new List<Tag>(tags.Values);
            sorted.Sort(
                (a, b) =>
                    string.Compare(
                        a.ToString() ?? "",
                        b.ToString() ?? "",
                        StringComparison.OrdinalIgnoreCase
                    )
            );
            foreach (var tag in sorted)
            {
                _tagsTouchedFoldout.Add(linker.TagLink(tag.ToString() ?? $"#{tag.Guid}"));
            }
        }

        void StripTagsTouchedRows()
        {
            for (int i = _tagsTouchedFoldout.childCount - 1; i >= 0; i--)
            {
                if (_tagsTouchedFoldout[i] != _tagsCaveat)
                {
                    _tagsTouchedFoldout.RemoveAt(i);
                }
            }
        }

        static void ApplyNameList(
            Foldout foldout,
            IReadOnlyCollection<string> names,
            ref int lastHash,
            Func<string, VisualElement> linker
        )
        {
            int hash = HashOfNames(names);
            if (hash == lastHash)
                return;
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
                foldout.Add(linker(n));
            }
        }

        static int HashOfNames(IReadOnlyCollection<string> names)
        {
            if (names == null || names.Count == 0)
                return 0;
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

        // Renders accessor link rows; hides the foldout when the list is
        // empty — an absent section communicates the same thing as a
        // "(none)" line without occupying screen space.
        static void FillSystemFoldout(Foldout foldout, List<string> names, InspectorLinker linker)
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
                foldout.Add(linker.AccessorLink(n));
            }
        }
    }
}
