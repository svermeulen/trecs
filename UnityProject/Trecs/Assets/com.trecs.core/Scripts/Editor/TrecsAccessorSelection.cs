using System;
using System.Collections.Generic;
using System.IO;
using Trecs.Internal;
using UnityEditor;
using UnityEditorInternal;
using UnityEngine;
using UnityEngine.UIElements;

namespace Trecs
{
    /// <summary>
    /// ScriptableObject proxy used as <see cref="Selection.activeObject"/> when
    /// the user picks an accessor row in <see cref="TrecsHierarchyWindow"/>.
    /// Carries only a serialized <see cref="TrecsSelectionProxy.Identity"/>
    /// string (e.g. <c>"accessor:MoveSystem"</c>); the inspector resolves
    /// the live <see cref="AccessorRef"/> dynamically against
    /// <see cref="TrecsHierarchyWindow.ActiveSource"/> on every refresh.
    /// Same identity covers both system-owned and manually-created
    /// accessors; the source's projection decides which.
    /// </summary>
    public sealed class TrecsAccessorSelection : TrecsSelectionProxy { }

    [CustomEditor(typeof(TrecsAccessorSelection))]
    public sealed class TrecsAccessorSelectionInspector : Editor
    {
        [InitializeOnLoadMethod]
        static void RegisterEditorAccessorName() =>
            TrecsEditorAccessorNames.Register("TrecsAccessorSelectionInspector");

        WorldAccessor _windowAccessor;
        VisualElement _bodyContainer;
        Label _statusLabel;
        Label _nameValue;
        Label _kindValue;
        Label _roleValue;
        Label _typeValue;
        Label _namespaceValue;
        Label _phaseValue;
        Label _priorityValue;
        Foldout _dependsOnFoldout;
        Foldout _dependentsFoldout;
        Toggle _enabledToggle;
        Label _enabledOverrideLabel;
        VisualElement _systemOnlySection;
        VisualElement _manualOnlySection;
        Label _originValue;
        Foldout _readsFoldout;
        Foldout _writesFoldout;
        Foldout _tagsTouchedFoldout;
        Label _tagsCaveat;
        Foldout _addsFoldout;
        Foldout _removesFoldout;
        Foldout _movesFoldout;
        bool _suppressToggleEvents;

        // Composite render key — identity + source mode + source name.
        string _renderedKey;
        int _lastReadsHash;
        int _lastWritesHash;
        int _lastTagsTouchedHash;
        int _lastAddsHash;
        int _lastRemovesHash;
        int _lastMovesHash;

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
            TrecsInspectorLinks.WireHoverPreview(
                _nameValue,
                () => (target as TrecsAccessorSelection)?.Identity
            );

            _kindValue = AddRow(container, "Kind", "");

            // Role applies to *every* accessor (system-owned or manual) so it
            // sits above the system-only / manual-only blocks. Tooltip
            // explains the rule matrix in one line so the user doesn't need
            // to chase the AccessorRole docs to interpret the value.
            _roleValue = AddRow(container, "Role", "");
            _roleValue.tooltip =
                "Input — input ingestion + frame-scoped heap.\n"
                + "Fixed — deterministic simulation; structural changes; persistent heap.\n"
                + "Variable — display/render; read-only sim state.\n"
                + "Unrestricted — escape hatch; skips role rules. Use sparingly.";

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

            // Surfaces non-Editor blockers (Playback channel, User channel, or
            // deterministic Paused) when the toggle and the actual run state
            // disagree, so the inspector doesn't lie about why a system isn't
            // running. Hidden when the toggle accurately reflects state.
            _enabledOverrideLabel = new Label();
            _enabledOverrideLabel.style.marginLeft = 16;
            _enabledOverrideLabel.style.opacity = 0.75f;
            _enabledOverrideLabel.style.display = DisplayStyle.None;
            _systemOnlySection.Add(_enabledOverrideLabel);

            container.Add(_systemOnlySection);

            // Manual-only metadata: source-line origin. Click jumps to the
            // CreateAccessor callsite via the user's external script editor.
            _manualOnlySection = new VisualElement();
            _originValue = AddRow(_manualOnlySection, "Origin", "");
            _originValue.RegisterCallback<ClickEvent>(OnOriginClicked);
            container.Add(_manualOnlySection);

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

            // Structural-change tracking — which templates this accessor
            // adds/removes/moves entities on. Move flags both source and
            // destination of each operation.
            _addsFoldout = new Foldout { text = "Adds to templates", value = true };
            _addsFoldout.style.marginTop = 6;
            container.Add(_addsFoldout);

            _removesFoldout = new Foldout { text = "Removes from templates", value = true };
            _removesFoldout.style.marginTop = 6;
            container.Add(_removesFoldout);

            _movesFoldout = new Foldout { text = "Moves on templates", value = true };
            _movesFoldout.style.marginTop = 6;
            container.Add(_movesFoldout);
        }

        void RenderManualOriginSection(AccessorEntry entry)
        {
            var path = entry.CreatedAtFile;
            if (string.IsNullOrEmpty(path))
            {
                _manualOnlySection.style.display = DisplayStyle.None;
                _originValue.userData = null;
                return;
            }
            _manualOnlySection.style.display = DisplayStyle.Flex;
            // Show the file name + line in the visible label so the inspector
            // doesn't get pushed wide by long absolute paths; full path goes
            // in the tooltip and on click. Click handler reads CreatedAtFile
            // from the proxy via _originValue.userData so we don't need to
            // capture entry across closures.
            var fileName = Path.GetFileName(path);
            _originValue.text =
                entry.CreatedAtLine > 0 ? $"{fileName}:{entry.CreatedAtLine}" : fileName;
            // Tooltip flags the cross-machine case: paths captured by
            // CallerFilePath are absolute on the recorder's machine, so a
            // snapshot from another developer's checkout (or one whose
            // working tree has moved) won't open here.
            var pathWithLine = entry.CreatedAtLine > 0 ? $"{path}:{entry.CreatedAtLine}" : path;
            _originValue.tooltip =
                $"{pathWithLine}\n\nClick to open in your script editor "
                + "(no-ops if the file isn't on this machine).";
            _originValue.style.unityFontStyleAndWeight = FontStyle.Italic;
            _originValue.userData = (path, entry.CreatedAtLine);
        }

        void OnOriginClicked(ClickEvent _)
        {
            if (_originValue.userData is not ValueTuple<string, int> origin)
            {
                return;
            }
            var (path, line) = origin;
            if (string.IsNullOrEmpty(path))
            {
                return;
            }
            // Same path Unity uses for stack-frame links in the console.
            // Silently no-ops when the file doesn't exist on this machine
            // (e.g. a snapshot taken on another developer's checkout).
            InternalEditorUtility.OpenFileAtLineExternal(path, line);
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

        // Hides the Role row entirely when the source can't supply one
        // (legacy cache snapshot). Toggling DisplayStyle on the parent
        // collapses the whole row including the label so the inspector
        // doesn't show "Role: " with an empty value.
        void UpdateRoleRow(AccessorRole? role)
        {
            var rowContainer = _roleValue.parent;
            if (!role.HasValue)
            {
                if (rowContainer != null)
                {
                    rowContainer.style.display = DisplayStyle.None;
                }
                _roleValue.text = string.Empty;
                return;
            }
            if (rowContainer != null)
            {
                rowContainer.style.display = DisplayStyle.Flex;
            }
            _roleValue.text = role.Value.ToString();
        }

        void OnEnabledToggleChanged(ChangeEvent<bool> evt)
        {
            if (_suppressToggleEvents)
            {
                return;
            }
            var selection = (TrecsAccessorSelection)target;
            if (
                TrecsHierarchyWindow.ActiveSource is not LiveSchemaSource live
                || live.World == null
                || live.World.IsDisposed
            )
            {
                return;
            }
            var aref = live.ResolveAccessor(selection.Identity);
            if (aref == null || aref.SystemIndex < 0)
            {
                return;
            }
            live.SetSystemEnabled(aref.SystemIndex, evt.newValue);
        }

        void UpdateEnabledOverrideLabel(ITrecsSchemaSource src, int systemIndex)
        {
            // Only the live source carries channel / paused state to surface.
            var live = src as LiveSchemaSource;
            if (live == null || live.World == null || live.World.IsDisposed)
            {
                _enabledOverrideLabel.style.display = DisplayStyle.None;
                return;
            }
            var world = live.World;
            var accessor = live.Accessor;
            if (accessor == null)
            {
                _enabledOverrideLabel.style.display = DisplayStyle.None;
                return;
            }

            // Cheap early-out: if the only thing affecting the system is the
            // Editor channel (which the toggle already reflects), skip the
            // per-blocker enumeration entirely.
            bool editorEnabled = accessor.IsSystemEnabled(systemIndex, EnableChannel.Editor);
            if (world.IsSystemEffectivelyEnabled(systemIndex) == editorEnabled)
            {
                _enabledOverrideLabel.style.display = DisplayStyle.None;
                return;
            }

            // Something other than Editor is in play. Enumerate which channels
            // are blocking, then note the deterministic paused flag separately.
            string blockers = null;
            if (!accessor.IsSystemEnabled(systemIndex, EnableChannel.Playback))
            {
                blockers = "Playback";
            }
            if (!accessor.IsSystemEnabled(systemIndex, EnableChannel.User))
            {
                blockers = blockers == null ? "User" : blockers + ", User";
            }

            bool paused = live.Accessor != null && live.Accessor.IsSystemPaused(systemIndex);

            if (blockers == null && !paused)
            {
                _enabledOverrideLabel.style.display = DisplayStyle.None;
                return;
            }

            string text;
            if (blockers != null && paused)
            {
                text = $"Disabled by: {blockers}. Paused (deterministic).";
            }
            else if (blockers != null)
            {
                text = $"Disabled by: {blockers}";
            }
            else
            {
                text = "Paused (deterministic)";
            }

            _enabledOverrideLabel.text = text;
            _enabledOverrideLabel.style.display = DisplayStyle.Flex;
        }

        void Refresh()
        {
            var selection = (TrecsAccessorSelection)target;
            if (selection == null)
            {
                return;
            }

            var src = TrecsHierarchyWindow.ActiveSource;
            if (src == null || string.IsNullOrEmpty(selection.Identity))
            {
                ShowStatus("No accessor selected.");
                return;
            }

            var aref = src.ResolveAccessor(selection.Identity);
            if (aref == null)
            {
                ShowStatus(
                    $"Accessor '{selection.name}' not found in {(src.IsLive ? "live world" : "cached schema")} '{src.DisplayName}'."
                );
                return;
            }

            // Live mode also needs the runtime ExecutableSystemInfo to
            // render system metadata (type, dependencies). Resolve it via
            // the live world's system list.
            ExecutableSystemInfo liveSystem = null;
            IReadOnlyList<ExecutableSystemInfo> liveSystems = null;
            SystemRunner liveRunner = null;
            World liveWorld = null;
            if (src is LiveSchemaSource live)
            {
                liveWorld = live.World;
                if (liveWorld != null && !liveWorld.IsDisposed)
                {
                    try
                    {
                        _windowAccessor ??= liveWorld.CreateAccessor(
                            AccessorRole.Unrestricted,
                            "TrecsAccessorSelectionInspector"
                        );
                        liveSystems = _windowAccessor.GetSystems();
                        liveRunner = _windowAccessor.GetSystemRunner();
                        if (aref.SystemIndex >= 0 && aref.SystemIndex < liveSystems.Count)
                        {
                            liveSystem = liveSystems[aref.SystemIndex];
                        }
                    }
                    catch (Exception) { }
                }
            }

            _statusLabel.style.display = DisplayStyle.None;
            _bodyContainer.style.display = DisplayStyle.Flex;

            var renderKey = src.RenderKey(selection.Identity);
            if (renderKey != _renderedKey)
            {
                _renderedKey = renderKey;
                _lastReadsHash = 0;
                _lastWritesHash = 0;
                _lastTagsTouchedHash = 0;
                _lastAddsHash = 0;
                _lastRemovesHash = 0;
                _lastMovesHash = 0;
                var entry = BuildEntryFromRef(aref, liveSystem, liveSystems);
                var linker = new InspectorLinker(src);
                RenderStatic(entry, linker, !src.IsLive);
            }

            UpdateRuntimeFields(src, aref, liveSystem, liveRunner);
        }

        static AccessorEntry BuildEntryFromRef(
            AccessorRef aref,
            ExecutableSystemInfo liveSystem,
            IReadOnlyList<ExecutableSystemInfo> liveSystems
        )
        {
            AccessorEntry entry;
            if (aref.CacheNativeSystem != null)
            {
                entry = BuildEntryFromCacheSystem(aref.CacheNativeSystem);
            }
            else if (aref.CacheNativeManual != null)
            {
                entry = new AccessorEntry
                {
                    DebugName = aref.DebugName ?? "(unnamed)",
                    CreatedAtFile = aref.CreatedAtFile,
                    CreatedAtLine = aref.CreatedAtLine,
                };
            }
            else
            {
                entry = BuildEntryFromLive(
                    aref.DebugName,
                    liveSystem,
                    liveSystems,
                    aref.SystemIndex
                );
                // Live manual accessors carry their origin on the
                // AccessorRef itself (LiveSchemaSource pulls it from
                // WorldAccessor.CreatedAt*). Systems leave it empty —
                // they have richer metadata anyway.
                if (!entry.IsSystem)
                {
                    entry.CreatedAtFile = aref.CreatedAtFile;
                    entry.CreatedAtLine = aref.CreatedAtLine;
                }
            }
            // Role lives on the AccessorRef regardless of source mode.
            // Always pull from there so we don't have to thread it
            // through every per-source builder.
            entry.Role = aref.Role;
            return entry;
        }

        // Internal view type: same shape regardless of live or cache source.
        // Manual accessors leave system fields null/false and the renderer
        // hides the system-only block.
        sealed class AccessorEntry
        {
            public string DebugName;
            public bool IsSystem;

            // Set on every accessor (system or manual) when the source
            // can supply it. Null on legacy cache snapshots saved before
            // role was recorded — RenderStatic hides the row in that case.
            public AccessorRole? Role;
            public string TypeName;
            public string TypeNamespace;
            public string Phase;
            public bool HasPriority;
            public int Priority;
            public List<string> DependsOnSystemDebugNames = new();
            public List<string> DependentSystemDebugNames = new();

            // Manual accessors only — system entries leave these unset.
            public string CreatedAtFile;
            public int CreatedAtLine;
        }

        static AccessorEntry BuildEntryFromLive(
            string debugName,
            ExecutableSystemInfo systemInfo,
            IReadOnlyList<ExecutableSystemInfo> allSystems,
            int systemIndex
        )
        {
            var entry = new AccessorEntry { DebugName = debugName ?? "(unnamed)" };
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

        static AccessorEntry BuildEntryFromCacheSystem(TrecsSchemaSystem sys)
        {
            var entry = new AccessorEntry
            {
                DebugName = sys.DebugName ?? "(unnamed)",
                IsSystem = true,
                TypeName = sys.TypeName,
                TypeNamespace = sys.TypeNamespace,
                Phase = sys.Phase,
                HasPriority = sys.HasPriority,
                Priority = sys.Priority,
            };
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

        void RenderStatic(AccessorEntry entry, InspectorLinker linker, bool isCache)
        {
            _nameValue.text = entry.DebugName ?? "(unnamed)";
            // Role row is shared across system + manual entries. Hidden
            // entirely when null (legacy cache snapshot) so the user sees
            // "no info" rather than a misleading default value.
            UpdateRoleRow(entry.Role);
            if (!entry.IsSystem)
            {
                _kindValue.text = isCache ? "Manual accessor (cached)" : "Manual accessor";
                _systemOnlySection.style.display = DisplayStyle.None;
                RenderManualOriginSection(entry);
                return;
            }
            _kindValue.text = isCache ? "System (cached)" : "System";
            _systemOnlySection.style.display = DisplayStyle.Flex;
            _manualOnlySection.style.display = DisplayStyle.None;
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
                _enabledToggle.tooltip =
                    "Cached schema — system enable state isn't editable from a snapshot.";
            }
            else
            {
                _enabledToggle.SetEnabled(true);
                _enabledToggle.tooltip = string.Empty;
            }
        }

        void UpdateRuntimeFields(
            ITrecsSchemaSource src,
            AccessorRef aref,
            ExecutableSystemInfo liveSystem,
            SystemRunner liveRunner
        )
        {
            // Enabled toggle controls (and reflects) the Editor disable channel
            // via the schema source. Other blockers (Playback / User channels,
            // deterministic Paused) are surfaced via _enabledOverrideLabel below.
            if (src.IsLive && liveSystem != null && liveRunner != null && aref.SystemIndex >= 0)
            {
                _suppressToggleEvents = true;
                try
                {
                    if (src.TryGetSystemEnabled(aref.SystemIndex, out var enabled))
                    {
                        _enabledToggle.SetValueWithoutNotify(enabled);
                    }
                }
                finally
                {
                    _suppressToggleEvents = false;
                }

                UpdateEnabledOverrideLabel(src, aref.SystemIndex);
            }
            else
            {
                _enabledOverrideLabel.style.display = DisplayStyle.None;
            }

            // Reads/Writes — both modes go through src.AccessTracker.
            IReadOnlyCollection<string> reads = null;
            IReadOnlyCollection<string> writes = null;
            var linker = new InspectorLinker(src);
            if (!string.IsNullOrEmpty(aref.DebugName))
            {
                reads = src.AccessTracker.GetComponentsReadBy(aref.DebugName);
                writes = src.AccessTracker.GetComponentsWrittenBy(aref.DebugName);
            }
            ApplyNameList(_readsFoldout, reads, ref _lastReadsHash, linker.ComponentTypeLink);
            ApplyNameList(_writesFoldout, writes, ref _lastWritesHash, linker.ComponentTypeLink);

            // Tags touched — live derives from tracker groups; cache mode
            // doesn't capture this so it shows a marker.
            UpdateTagsTouched(src, aref, linker);

            // Structural ops — both modes go through the IAccessTracker
            // surface; cache reads from schema.Structural (persisted at save
            // time so offline browsing shows real data).
            IReadOnlyCollection<string> adds = null;
            IReadOnlyCollection<string> removes = null;
            IReadOnlyCollection<string> moves = null;
            if (!string.IsNullOrEmpty(aref.DebugName))
            {
                adds = src.AccessTracker.GetTemplateNamesAddedBy(aref.DebugName);
                removes = src.AccessTracker.GetTemplateNamesRemovedBy(aref.DebugName);
                moves = src.AccessTracker.GetTemplateNamesMovedBy(aref.DebugName);
            }
            ApplyNameList(_addsFoldout, adds, ref _lastAddsHash, linker.TemplateLink);
            ApplyNameList(_removesFoldout, removes, ref _lastRemovesHash, linker.TemplateLink);
            ApplyNameList(_movesFoldout, moves, ref _lastMovesHash, linker.TemplateLink);
        }

        void UpdateTagsTouched(ITrecsSchemaSource src, AccessorRef aref, InspectorLinker linker)
        {
            // Both modes go through src.AccessTracker.GetTagNamesTouchedBy.
            // Live derives from the runtime tracker + WorldInfo on demand;
            // cache reads from schema.TagsTouched (persisted at save time so
            // offline browsing shows real data).
            IReadOnlyCollection<string> tagNames = string.IsNullOrEmpty(aref.DebugName)
                ? Array.Empty<string>()
                : src.AccessTracker.GetTagNamesTouchedBy(aref.DebugName);

            int hash = HashOfNames(tagNames);
            if (hash == _lastTagsTouchedHash)
                return;
            _lastTagsTouchedHash = hash;

            StripTagsTouchedRows();
            if (tagNames == null || tagNames.Count == 0)
            {
                _tagsTouchedFoldout.Add(MakeMutedLine("(none recorded)"));
                return;
            }
            var sorted = new List<string>(tagNames);
            sorted.Sort(StringComparer.OrdinalIgnoreCase);
            foreach (var name in sorted)
            {
                _tagsTouchedFoldout.Add(linker.TagLink(name));
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
