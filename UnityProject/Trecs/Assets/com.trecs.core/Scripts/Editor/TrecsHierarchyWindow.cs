using System;
using System.Collections.Generic;
using System.Text;
using Trecs.Internal;
using UnityEditor;
using UnityEditor.UIElements;
using UnityEngine;
using UnityEngine.UIElements;
using Object = UnityEngine.Object;

namespace Trecs
{
    /// <summary>
    /// Cross-referenced view of the selected world's ECS schema. Rendered as
    /// a single Unity <see cref="TreeView"/> so hover, selection, and
    /// expand-on-arrow behaviors match Unity's own Hierarchy window. Three
    /// top-level branches: <b>Templates</b> (the resolved-template tree with
    /// entity rows under each), <b>Accessors</b> (systems + manual
    /// accessors, grouped by run phase), and <b>Components</b> (the union of
    /// all component types declared across the world's templates). Selecting
    /// a leaf routes through Unity's standard <see cref="Selection.activeObject"/>
    /// to a per-kind <see cref="ScriptableObject"/> proxy whose
    /// <see cref="Editor"/> renders the inspector body. Search filters all
    /// branches by name.
    ///
    /// Replaces <c>TrecsEntitiesWindow</c> and <c>TrecsSystemsWindow</c>.
    /// </summary>
    public class TrecsHierarchyWindow : EditorWindow, IHasCustomMenu
    {
        [InitializeOnLoadMethod]
        static void RegisterEditorAccessorName() =>
            TrecsEditorAccessorNames.Register("TrecsHierarchyWindow");

        // Persistent user toggles, surfaced via the EditorWindow ≡ menu.
        const string PrefShowEmptyTemplates = "Svkj.Trecs.Hierarchy.ShowEmptyTemplates";
        const string PrefShowAbstractTemplates = "Svkj.Trecs.Hierarchy.ShowAbstractTemplates";
        bool _showEmptyTemplates = true;
        bool _showAbstractTemplates = true;

        DropdownField _worldDropdown;
        ToolbarButton _clearCacheButton;
        ToolbarSearchField _searchField;
        ToolbarButton _searchHelpButton;
        VisualElement _searchHelpPanel;
        Label _emptyState;
        Label _cacheBanner;

        // Convenience accessors derived from _source. Branches across the
        // window read these instead of pattern-matching the source type
        // inline; one place to change if the source taxonomy grows.
        bool _cacheMode => _source != null && !_source.IsLive;
        TrecsSchema _cachedSchema => (_source as CacheSchemaSource)?.Schema;
        readonly List<TrecsSchema> _cachedSchemas = new();
        TreeView _tree;

        World _selectedWorld;
        WorldAccessor _selectedAccessor;

        // Unified data source over the current selection. Replaces the
        // _selectedWorld / _cacheMode / _cachedSchema reads as each section
        // gets ported off the live/cache divergence. Recreated whenever the
        // dropdown selection changes or a structural rebuild fires (so the
        // pre-projected lists in LiveSchemaSource pick up new templates,
        // entity counts, etc.). Null when nothing is selected.
        ITrecsSchemaSource _source;

        // Mirrors `_source` for inspector consumption. Identity-based
        // selection proxies (TrecsTemplateSelection etc.) resolve their
        // payload by walking ActiveSource.Templates / .ComponentTypes /
        // … on every Refresh — that's how a proxy survives Unity's
        // silent stop-play-mode restoration: identity is serialized,
        // data is recomputed each tick from whichever source is current.
        // Last writer wins across multiple hierarchy windows; in practice
        // only one is open at a time.
        public static ITrecsSchemaSource ActiveSource { get; private set; }

        void SetSource(ITrecsSchemaSource src)
        {
            _source = src;
            ActiveSource = src;
        }

        readonly List<World> _dropdownWorlds = new();
        string _searchText = string.Empty;
        readonly ParsedSearch _searchFilter = new();

        // Most-recent-first list of distinct search queries the user has
        // typed during this and prior sessions. Up/Down arrow in the
        // search field cycles through them. Persisted via EditorPrefs as
        // a newline-joined string (JsonUtility doesn't serialize List<string>
        // at the top level).
        readonly List<string> _searchHistory = new();

        // -1 means the user isn't navigating history. >= 0 indexes
        // into _searchHistory (0 = most recent).
        int _searchHistoryIndex = -1;

        // What the user was typing before they hit Up the first time.
        // Restored when they Down-arrow back past the newest history entry.
        string _searchHistoryDraft = string.Empty;

        // Suppresses history-record on programmatic value changes (history
        // recall + Esc-clear).
        bool _suppressSearchHistoryRecord;
        const int SearchHistoryMax = 20;
        const string SearchHistoryPref = "Svkj.TrecsHierarchy.SearchHistory";

        // Browser-style selection navigation. Each user-driven selection
        // append a new entry; Alt+Left/Right walks backward/forward. The
        // ring-buffer proxy pool ensures prior selections are distinct SO
        // instances, so jumping back actually restores the data — until
        // pool slots get recycled (>16 selections of the same kind).
        readonly List<Object> _selectionHistory = new();
        int _selectionHistoryIndex = -1;
        bool _navigatingSelectionHistory;
        const int SelectionHistoryMax = 32;

        // True while we mutate tree selection in response to an external
        // Selection.activeObject change, so the tree's own selectionChanged
        // event doesn't echo back and overwrite the proxy we just resolved.
        bool _suppressTreeSelectionFeedback;

        // Set by OnTreeSelectionChanged before it routes a click to a proxy.
        // The Selection.activeObject swap fires Unity's selectionChanged on
        // a different reference, which would otherwise call
        // UpdateRowSelectionFromUnity(scrollToItem:true) and centre the
        // already-visible row — feeling like the tree jumped under the
        // user's cursor. Cleared one editor tick later so it doesn't outlive
        // the click event.
        bool _suppressNextSelectionScroll;

        // Bumped by every ArmScrollSuppression. The delayCall safety net
        // clears the flag only when its captured token still matches —
        // stale delayCalls left over from earlier arms become no-ops, so
        // a rapid second click can't have its suppression cancelled by a
        // first click's leftover delayCall.
        int _suppressionToken;

        // Last tree-row id we've reflected in the tree. Used to skip
        // redundant SetSelectionById / ScrollToItemById calls when the
        // resolved selection hasn't actually changed — without this, every
        // refresh tick that does a structural rebuild was snapping the view
        // back to the selected row each time the user tried to scroll away.
        int _lastReflectedSelectionId = -1;

        // Tree-row id currently under "preview hover" via an inspector link.
        // -1 when no link is being hovered. Drives a transient background
        // overlay in BindTreeItem so the user can see where a linked row
        // lives without losing their current selection.
        int _previewHoverRowId = -1;
        static readonly Color _previewHoverColor = new(0.7f, 0.7f, 0.7f, 0.18f);

        // Cached lookup for the TreeView's underlying ScrollView (resolved
        // via UQuery once, since BaseListView.scrollView is internal).
        ScrollView _treeScrollViewCache;

        // Snapshot of expanded keys taken when the user starts typing in the
        // search field. While search is active we override the natural
        // expand state (collapse all templates, expand all accessor phases)
        // and we want clearing the search to restore the user's previous
        // shape rather than leave them with our search-time mutations.
        HashSet<string> _preSearchExpandedKeys;

        // First-rebuild gate for the root sections. After the initial rebuild
        // we let the persisted-expansion restore loop handle expand state,
        // so a user collapsing a section stays collapsed. Persisted via
        // SessionState so the gate survives domain reload — without that,
        // every play-mode entry would wipe C# state and re-open sections
        // the user had just collapsed.
        bool _initialSectionExpansionApplied;
        const string InitialSectionExpansionAppliedSessionKey =
            "Svkj.TrecsHierarchy.InitialSectionExpansionApplied";

        // Persistent set of stable string keys for rows the user has
        // expanded. Survives play-mode entry / domain reload via
        // SessionState (a [SerializeField] mirror was unreliable: the
        // EditorWindow round-trip during play-mode entry sometimes lost
        // the field, and the periodic RefreshTick wasn't fast enough to
        // capture user clicks made just before pressing Play). Saved on
        // every mutation and on the playModeStateChanged ExitingEditMode
        // edge so the user's edit-mode changes always reach storage.
        readonly HashSet<string> _expandedStableKeys = new();

        const string ExpandedKeysSessionKey = "Svkj.TrecsHierarchy.ExpandedKeys";
        const string ExpandedKeysSeparator = "\n";

        // Name-based identity of the currently-selected row, persisted to
        // SessionState so the selection survives world transitions
        // (entering/exiting play mode disposes the editor / play-mode
        // world, leaving the proxy bound to a stale World reference).
        // The corresponding row in the new world is found by matching
        // against this identity and a fresh proxy is bound.
        const string SelectedRowIdentitySessionKey = "Svkj.TrecsHierarchy.SelectedRowIdentity";

        // Parallel id → stable-string-key map. Populated alongside _idByKey
        // in GetOrAssignId. Used by CaptureExpandedKeys /
        // RestoreExpandedFromStableKeys to bridge between TreeView's int
        // ids and the persistent stable keys.
        readonly Dictionary<int, string> _stableKeyById = new();

        // Five fixed root-section ids; user-data ids start at 100 so they
        // never collide.
        const int SectionTemplatesId = 1;
        const int SectionAccessorsId = 2;
        const int SectionComponentsId = 3;
        const int SectionSetsId = 4;
        const int SectionTagsId = 5;

        int _nextId = 100;

        // Stable id assignment: tree rebuilds keep the same id for the same
        // logical key (Template, EntityHandle, Type, etc.), so TreeView's
        // internal expand state is preserved across SetRootItems calls.
        readonly Dictionary<object, int> _idByKey = new();
        readonly Dictionary<int, RowData> _dataById = new();
        readonly Dictionary<int, int> _parentById = new();

        // Entity rows aren't keyed by identity (no stable identity that
        // survives across worlds), so they need their own EntityHandle-
        // keyed map for the "select this entity" reverse lookup. All
        // other kinds resolve identity → row id via _idByKey directly.
        readonly Dictionary<EntityHandle, int> _entityIds = new();

        // Structural fingerprint used by RefreshTick to decide whether
        // SetRootItems is needed (slow path) or just RefreshItems for binding
        // updates (fast path). Per-group entity count changes count as
        // structural because adding/removing entities adds/removes tree
        // nodes.
        int _lastResolvedTemplateCount = -1;
        int _lastAbstractTemplateCount = -1;
        int _lastAccessorCount = -1;
        int _lastComponentTypeCount = -1;
        int _lastSetCount = -1;
        int _lastTagCount = -1;
        readonly Dictionary<GroupIndex, int> _lastEntityCountByGroup = new();

        // Reusable scratch collections for per-tick fingerprint checks.
        // NeedsStructuralRebuild fires every 250ms; allocating fresh
        // HashSets per call would generate steady GC pressure.
        readonly HashSet<Type> _scratchTypeSet = new();
        readonly HashSet<int> _scratchTagGuidSet = new();

        [MenuItem("Window/Trecs/Hierarchy")]
        public static void ShowWindow()
        {
            var window = GetWindow<TrecsHierarchyWindow>();
            window.titleContent = new GUIContent("Trecs Hierarchy");
            window.minSize = new Vector2(360, 400);
        }

        public void AddItemsToMenu(GenericMenu menu)
        {
            menu.AddItem(
                new GUIContent("Show Empty Templates"),
                _showEmptyTemplates,
                () =>
                {
                    _showEmptyTemplates = !_showEmptyTemplates;
                    EditorPrefs.SetBool(PrefShowEmptyTemplates, _showEmptyTemplates);
                    ForceFullRebuild();
                }
            );
            menu.AddItem(
                new GUIContent("Show Abstract Templates"),
                _showAbstractTemplates,
                () =>
                {
                    _showAbstractTemplates = !_showAbstractTemplates;
                    EditorPrefs.SetBool(PrefShowAbstractTemplates, _showAbstractTemplates);
                    ForceFullRebuild();
                }
            );
            menu.AddSeparator(string.Empty);
            menu.AddItem(new GUIContent("Help..."), false, ToggleSearchHelp);
            if (_searchHistory.Count > 0)
            {
                menu.AddItem(
                    new GUIContent("Clear Search History"),
                    false,
                    () =>
                    {
                        _searchHistory.Clear();
                        SaveSearchHistory();
                    }
                );
            }
            else
            {
                menu.AddDisabledItem(new GUIContent("Clear Search History"));
            }
        }

        void ToggleSearchHelp()
        {
            if (_searchHelpPanel == null)
                return;
            bool isVisible = _searchHelpPanel.style.display != DisplayStyle.None;
            _searchHelpPanel.style.display = isVisible ? DisplayStyle.None : DisplayStyle.Flex;
        }

        // Builds a collapsible help block with the supported search-field
        // syntax. Lives under the toolbar (above the tree) so it doesn't
        // steal focus from the search field — the user can keep typing
        // while reading.
        VisualElement BuildSearchHelpPanel()
        {
            var panel = new VisualElement();
            panel.AddToClassList("trecs-help-panel");

            var heading = new Label("Trecs Hierarchy — Help");
            heading.AddToClassList("trecs-help-panel__heading");
            panel.Add(heading);

            var body = new Label(
                "<b>Keyboard shortcuts</b>\n"
                    + "  Cmd/Ctrl+F   Focus the search field\n"
                    + "  Esc          Clear the search field\n"
                    + "  Up / Down    (search field) Recall recent queries\n"
                    + "  Alt+Left/Right  Walk back/forward through prior selections\n"
                    + "  Shift+hover (inspector link)  Preview-scroll to its row\n\n"
                    + "<b>Right-click a row</b>\n"
                    + "  Copy Name (always)\n"
                    + "  Templates    → Find Entities/Derived/Base\n"
                    + "  Components   → Find Anything With This / scoped variants /\n"
                    + "                 Accessors That Read or Write This\n"
                    + "  Tags         → Find Anything With This / scoped variants\n"
                    + "  Sets         → Find Sets With Same Name\n"
                    + "  Entities     → Copy Entity Id\n\n"
                    + "<b>Search syntax</b>\n"
                    + "Tokens are AND'd. Bare words substring-match the row's display name.\n"
                    + "Smart-case: a token with no uppercase chars matches case-insensitively;\n"
                    + "any uppercase char in the token flips it to case-sensitive.\n\n"
                    + "<b>Kind selector</b> (optional, restricts which kinds appear):\n"
                    + "  t:e    entities         t:s     sets\n"
                    + "  t:t    templates        t:tag   tags\n"
                    + "  t:c    components       t:a     accessors\n\n"
                    + "<b>Predicates</b> (key:value, AND'd with everything else):\n"
                    + "  tag:X       row associated with tag X\n"
                    + "  c:X         row has component X (also: component:X)\n"
                    + "  base:X      template has X in its base chain\n"
                    + "  derived:X   template is extended by X\n"
                    + "  template:X  entity belongs to template X\n"
                    + "  reads:X     accessor reads component X\n"
                    + "  writes:X    accessor writes component X\n"
                    + "  accesses:X  accessor reads OR writes component X\n\n"
                    + "<b>Modifiers</b>\n"
                    + "  -tok        negate (exclude rows that match)\n"
                    + "  \"a b c\"     quoted phrase — single substring with spaces\n\n"
                    + "<b>Examples</b>\n"
                    + "  player                     anything matching \"player\"\n"
                    + "  tag:player                 anything tagged \"player\" (cross-kind)\n"
                    + "  t:e tag:player             entities tagged \"player\"\n"
                    + "  t:e tag:enemy -c:Boss      enemies that don't have a Boss component\n"
                    + "  t:t c:Health               templates with a Health component\n"
                    + "  t:e tag:enemy c:Health     entities tagged enemy AND with Health\n"
                    + "  t:a reads:Health           accessors that read Health\n"
                    + "  \"My Long Name\"             substring including spaces\n"
                    + "  base:Enemy                 templates derived from Enemy"
            );
            body.enableRichText = true;
            body.AddToClassList("trecs-help-panel__body");
            panel.Add(body);

            var dismiss = new Button(ToggleSearchHelp) { text = "Close" };
            dismiss.AddToClassList("trecs-help-panel__dismiss");
            panel.Add(dismiss);

            return panel;
        }

        void OnEnable()
        {
            _showEmptyTemplates = EditorPrefs.GetBool(PrefShowEmptyTemplates, true);
            _showAbstractTemplates = EditorPrefs.GetBool(PrefShowAbstractTemplates, true);
            LoadSearchHistory();

            // Rehydrate persisted state from SessionState (survives the
            // domain reload that fires on play-mode entry).
            LoadExpandedStableKeys();
            _initialSectionExpansionApplied = SessionState.GetBool(
                InitialSectionExpansionAppliedSessionKey,
                false
            );

            EditorApplication.playModeStateChanged += OnPlayModeStateChangedForExpansion;

            WorldRegistry.WorldRegistered += OnWorldRegistered;
            WorldRegistry.WorldUnregistered += OnWorldUnregistered;
            TrecsEditorSelection.ActiveWorldChanged += OnSharedActiveWorldChanged;
            Selection.selectionChanged += OnUnitySelectionChanged;
            TrecsInspectorLinks.PreviewRequested += OnPreviewByIdentity;
            TrecsInspectorLinks.PreviewEntityRequested += OnPreviewEntity;
            TrecsInspectorLinks.PreviewClearRequested += OnPreviewClear;
            TrecsSchemaCache.SchemaSaved += OnSchemaSaved;
        }

        void OnDisable()
        {
            WorldRegistry.WorldRegistered -= OnWorldRegistered;
            WorldRegistry.WorldUnregistered -= OnWorldUnregistered;
            TrecsEditorSelection.ActiveWorldChanged -= OnSharedActiveWorldChanged;
            Selection.selectionChanged -= OnUnitySelectionChanged;
            TrecsInspectorLinks.PreviewRequested -= OnPreviewByIdentity;
            TrecsInspectorLinks.PreviewEntityRequested -= OnPreviewEntity;
            TrecsInspectorLinks.PreviewClearRequested -= OnPreviewClear;
            TrecsSchemaCache.SchemaSaved -= OnSchemaSaved;
            EditorApplication.playModeStateChanged -= OnPlayModeStateChangedForExpansion;
            _selectedAccessor = null;

            // Flush any in-flight expansion state into SessionState so a
            // window-close-then-reopen during the same editor session
            // preserves it. The live capture is unioned in so a row the
            // user expanded since the last RefreshTick isn't lost.
            if (_tree != null && _dataById.Count > 0)
            {
                _expandedStableKeys.UnionWith(CaptureExpandedKeys());
            }
            SaveExpandedStableKeys();

            // Don't clear Selection.activeObject here. Other Trecs editor
            // windows (Time Travel, Systems) drive the same proxy types,
            // so closing the hierarchy shouldn't kick the user's selection
            // out from under them. The proxies are session-scoped SOs;
            // they're safe to leave selected after this window closes.
        }

        const string StyleSheetPath =
            "Packages/com.trecs.core/Scripts/Editor/TrecsHierarchyWindow.uss";

        void CreateGUI()
        {
            var root = rootVisualElement;
            root.style.flexGrow = 1;

            var styleSheet = AssetDatabase.LoadAssetAtPath<StyleSheet>(StyleSheetPath);
            if (styleSheet != null)
            {
                root.styleSheets.Add(styleSheet);
            }

            var toolbar = new Toolbar();
            root.Add(toolbar);

            _worldDropdown = new DropdownField(new List<string>(), 0);
            _worldDropdown.style.minWidth = 160;
            _worldDropdown.RegisterValueChangedCallback(OnWorldDropdownChanged);
            toolbar.Add(_worldDropdown);

            // Cache-mode-only: drop the on-disk snapshot for the currently
            // shown world (left-click) or all worlds (right-click). Hidden
            // in live mode since there's nothing accumulated to clear.
            _clearCacheButton = new ToolbarButton(OnClearCacheButtonClicked) { text = "Clear" };
            _clearCacheButton.tooltip =
                "Delete the on-disk schema snapshot for this world. Right-click for 'Clear all'.";
            _clearCacheButton.style.minWidth = 44;
            _clearCacheButton.style.display = DisplayStyle.None;
            _clearCacheButton.RegisterCallback<ContextClickEvent>(evt =>
            {
                evt.StopPropagation();
                OnClearCacheButtonContextClicked();
            });
            toolbar.Add(_clearCacheButton);

            _searchField = new ToolbarSearchField();
            _searchField.style.flexGrow = 1;
            _searchField.style.marginLeft = 4;
            _searchField.tooltip =
                "Space-separated tokens AND together. 't:kind' restricts scope "
                + "(t:e, t:t, t:c, t:s, t:tag, t:a). 'key:value' adds a typed "
                + "predicate (tag:, c:, base:, derived:, template:, reads:, "
                + "writes:, accesses:). Prefix '-' to negate; wrap a phrase in \"...\" for "
                + "spaces. Up/Down arrow recalls recent queries. Esc clears. "
                + "Click the ? button for full help.";
            _searchField.RegisterValueChangedCallback(OnSearchChanged);
            // Up/Down arrow recalls history. Capture during TrickleDown so
            // the text field's caret-move handling doesn't swallow them
            // first. Only intercept when the field actually has focus —
            // otherwise tree-view keyboard navigation should still work.
            _searchField.RegisterCallback<KeyDownEvent>(
                OnSearchFieldKeyDown,
                TrickleDown.TrickleDown
            );
            toolbar.Add(_searchField);

            _searchHelpButton = new ToolbarButton(ToggleSearchHelp) { text = "?" };
            _searchHelpButton.tooltip =
                "Show keyboard shortcuts, search syntax, and context-menu help";
            _searchHelpButton.style.minWidth = 22;
            toolbar.Add(_searchHelpButton);

            _searchHelpPanel = BuildSearchHelpPanel();
            _searchHelpPanel.style.display = DisplayStyle.None;
            root.Add(_searchHelpPanel);

            _emptyState = new Label("No active worlds (enter Play mode)");
            _emptyState.style.marginTop = 8;
            _emptyState.style.marginLeft = 8;
            _emptyState.style.opacity = 0.7f;
            root.Add(_emptyState);

            // Banner shown when we're rendering from disk cache rather than a
            // live world. Sits above the tree and replaces the empty-state
            // label whenever a snapshot is available.
            _cacheBanner = new Label();
            _cacheBanner.style.marginTop = 4;
            _cacheBanner.style.marginLeft = 8;
            _cacheBanner.style.marginRight = 8;
            _cacheBanner.style.marginBottom = 4;
            _cacheBanner.style.paddingLeft = 6;
            _cacheBanner.style.paddingRight = 6;
            _cacheBanner.style.paddingTop = 3;
            _cacheBanner.style.paddingBottom = 3;
            _cacheBanner.style.borderTopLeftRadius = 3;
            _cacheBanner.style.borderTopRightRadius = 3;
            _cacheBanner.style.borderBottomLeftRadius = 3;
            _cacheBanner.style.borderBottomRightRadius = 3;
            _cacheBanner.style.backgroundColor = new Color(0.25f, 0.20f, 0.10f, 0.6f);
            _cacheBanner.style.opacity = 0.9f;
            _cacheBanner.style.whiteSpace = WhiteSpace.Normal;
            _cacheBanner.style.display = DisplayStyle.None;
            root.Add(_cacheBanner);

            _tree = new TreeView();
            _tree.style.flexGrow = 1;
            _tree.fixedItemHeight = 18;
            _tree.makeItem = MakeTreeItem;
            _tree.bindItem = BindTreeItem;
            // Multiple lets the user ctrl+click to deselect the only selected
            // row (Single mode silently no-ops on ctrl-click of the selected
            // row). The handler picks the first selected row for the
            // inspector — multiselect is supported visually, but the
            // inspector body still shows one item at a time.
            _tree.selectionType = SelectionType.Multiple;
            _tree.selectionChanged += OnTreeSelectionChanged;
            root.Add(_tree);

            // Cmd/Ctrl+F focuses the search field — matches Unity's standard
            // panel keybinding. TrickleDown ensures we see the key before
            // child elements consume it (e.g. the TreeView's own keyboard
            // navigation). evt.actionKey is Cmd on macOS, Ctrl elsewhere.
            root.RegisterCallback<KeyDownEvent>(OnRootKeyDown, TrickleDown.TrickleDown);

            RebuildDropdown();
            root.schedule.Execute(RefreshTick)
                .Every(TrecsDebugWindowSettings.Get().RefreshIntervalMs);
        }

        void OnSearchFieldKeyDown(KeyDownEvent evt)
        {
            if (evt.keyCode == KeyCode.UpArrow)
            {
                RecallSearchHistory(+1);
                evt.StopPropagation();
                return;
            }
            if (evt.keyCode == KeyCode.DownArrow)
            {
                RecallSearchHistory(-1);
                evt.StopPropagation();
                return;
            }
        }

        void OnRootKeyDown(KeyDownEvent evt)
        {
            if (evt.actionKey && evt.keyCode == KeyCode.F && _searchField != null)
            {
                _searchField.Focus();
                evt.StopPropagation();
                return;
            }
            // Esc clears the search when the field is focused (or the
            // user has just typed something). Standard editor convention
            // — Unity's Project / Hierarchy windows do the same.
            if (
                evt.keyCode == KeyCode.Escape
                && _searchField != null
                && !string.IsNullOrEmpty(_searchText)
            )
            {
                _searchField.value = string.Empty;
                evt.StopPropagation();
                return;
            }
            // Alt+Left / Alt+Right walk the selection history. Skip when
            // the search field is focused so its native word-jump still
            // works while typing.
            if (
                evt.altKey
                && (evt.keyCode == KeyCode.LeftArrow || evt.keyCode == KeyCode.RightArrow)
                && !IsSearchFieldFocused(evt)
            )
            {
                int delta = evt.keyCode == KeyCode.LeftArrow ? -1 : +1;
                if (NavigateSelectionHistory(delta))
                {
                    evt.StopPropagation();
                }
                return;
            }
        }

        bool IsSearchFieldFocused(KeyDownEvent evt)
        {
            if (_searchField == null || evt.target is not VisualElement target)
            {
                return false;
            }
            return target == _searchField || _searchField.Contains(target);
        }

        void OnWorldRegistered(World w) => RebuildDropdown();

        void OnWorldUnregistered(World w) => RebuildDropdown();

        // Capture-and-flush at the precise moment the user clicks Play
        // (or clicks Stop). The periodic RefreshTick can lag behind
        // user clicks by up to RefreshIntervalMs, so a row expanded
        // immediately before pressing Play wouldn't otherwise reach
        // SessionState before the domain reload kicked in.
        void OnPlayModeStateChangedForExpansion(PlayModeStateChange change)
        {
            if (
                change == PlayModeStateChange.ExitingEditMode
                || change == PlayModeStateChange.ExitingPlayMode
            )
            {
                if (_tree != null && _dataById.Count > 0)
                {
                    _expandedStableKeys.UnionWith(CaptureExpandedKeys());
                }
                SaveExpandedStableKeys();
            }
        }

        void OnSchemaSaved()
        {
            // Only re-render if we're already showing the cache; live mode
            // is authoritative regardless of disk state.
            if (_cacheMode)
            {
                RebuildDropdown();
            }
        }

        void OnSharedActiveWorldChanged(World world)
        {
            if (world != _selectedWorld && _dropdownWorlds.Contains(world))
            {
                SelectWorld(world);
                var idx = _dropdownWorlds.IndexOf(world);
                if (idx >= 0 && idx < _worldDropdown.choices.Count)
                {
                    _worldDropdown.SetValueWithoutNotify(_worldDropdown.choices[idx]);
                }
            }
        }

        void OnSearchChanged(ChangeEvent<string> evt)
        {
            var newSearch = (evt.newValue ?? string.Empty).Trim();
            // Entering search mode: capture pre-search expand state so we
            // can restore it when the user clears the field. While search
            // is active we collapse all templates / expand all phases for
            // a cleaner result view, but those mutations shouldn't outlast
            // the search.
            if (string.IsNullOrEmpty(_searchText) && !string.IsNullOrEmpty(newSearch))
            {
                _preSearchExpandedKeys = CaptureExpandedKeys();
            }
            // Manual edit (vs Up/Down recall) — exit history navigation
            // and record the new query in history. Programmatic recall
            // sets _suppressSearchHistoryRecord to skip both.
            if (!_suppressSearchHistoryRecord)
            {
                _searchHistoryIndex = -1;
                RecordSearchHistory(newSearch);
            }
            _searchText = newSearch;
            ParseSearch(newSearch);
            ForceFullRebuild();
        }

        // Adds the query at the front of the history list. Dedupe rules:
        //   - Empty queries skipped.
        //   - Identical to the current head → no-op (avoids spamming on
        //     refocus).
        //   - Current head is a strict prefix of the new query → replace
        //     the head (so typing "tag:e" → "tag:en" → "tag:enemy" leaves
        //     a single "tag:enemy" entry, not three partial ones).
        //   - Otherwise prepend.
        // Capped at SearchHistoryMax entries.
        void RecordSearchHistory(string query)
        {
            if (string.IsNullOrEmpty(query))
                return;
            if (_searchHistory.Count > 0)
            {
                var head = _searchHistory[0];
                if (head == query)
                    return;
                if (query.StartsWith(head, StringComparison.Ordinal))
                {
                    _searchHistory[0] = query;
                    SaveSearchHistory();
                    return;
                }
                // Promote an existing identical entry to the front rather
                // than duplicating it (lets the user revisit a query
                // without churning recent history).
                int existing = _searchHistory.IndexOf(query);
                if (existing > 0)
                {
                    _searchHistory.RemoveAt(existing);
                    _searchHistory.Insert(0, query);
                    SaveSearchHistory();
                    return;
                }
            }
            _searchHistory.Insert(0, query);
            if (_searchHistory.Count > SearchHistoryMax)
            {
                _searchHistory.RemoveRange(
                    SearchHistoryMax,
                    _searchHistory.Count - SearchHistoryMax
                );
            }
            SaveSearchHistory();
        }

        void LoadSearchHistory()
        {
            _searchHistory.Clear();
            var raw = EditorPrefs.GetString(SearchHistoryPref, string.Empty);
            if (string.IsNullOrEmpty(raw))
                return;
            foreach (var line in raw.Split('\n'))
            {
                if (!string.IsNullOrEmpty(line))
                    _searchHistory.Add(line);
            }
        }

        void SaveSearchHistory()
        {
            EditorPrefs.SetString(SearchHistoryPref, string.Join("\n", _searchHistory));
        }

        void RecallSearchHistory(int delta)
        {
            if (_searchHistory.Count == 0)
                return;
            int newIndex;
            if (_searchHistoryIndex < 0)
            {
                if (delta < 0)
                    return; // Down with no nav in progress: no-op.
                _searchHistoryDraft = _searchText ?? string.Empty;
                newIndex = 0;
            }
            else
            {
                newIndex = _searchHistoryIndex + delta;
            }
            if (newIndex >= _searchHistory.Count)
                return; // Past oldest — clamp.
            _suppressSearchHistoryRecord = true;
            try
            {
                if (newIndex < 0)
                {
                    // Stepped forward past the newest entry — restore the
                    // user's draft and exit nav.
                    _searchHistoryIndex = -1;
                    _searchField.value = _searchHistoryDraft;
                }
                else
                {
                    _searchHistoryIndex = newIndex;
                    _searchField.value = _searchHistory[newIndex];
                }
            }
            finally
            {
                _suppressSearchHistoryRecord = false;
            }
        }

        // Tokenizes the input on whitespace, respecting double-quoted
        // phrases as single tokens, and bins each one of:
        //   - "t:value" — kind selector
        //   - "key:value" with a recognized predicate key — typed predicate
        //   - bare word OR "key:value" with an unrecognized key — bare
        //     substring (so accidental colons don't silently disappear)
        //
        // A leading '-' on a token negates it: -tag:player excludes rows
        // tagged player; -foo excludes rows whose display name contains
        // foo. The kind selector ("t:") doesn't negate — "-t:e" falls
        // through to bare substring.
        void ParseSearch(string raw)
        {
            _searchFilter.Reset();
            raw ??= string.Empty;
            foreach (var rawTok in TokenizeSearch(raw))
            {
                if (rawTok.Length == 0)
                    continue;
                bool negate = false;
                var tok = rawTok;
                if (tok.Length > 1 && tok[0] == '-')
                {
                    negate = true;
                    tok = tok.Substring(1);
                }
                int colon = tok.IndexOf(':');
                if (colon <= 0)
                {
                    _searchFilter.BareSubstrings.Add((tok, negate));
                    continue;
                }
                var key = tok.Substring(0, colon).ToLowerInvariant();
                var value = tok.Substring(colon + 1);
                if (key == "t" && !negate)
                {
                    var kind = ScopeFromKindValue(value);
                    if (kind != 0)
                    {
                        _searchFilter.ExplicitKind = kind;
                        _searchFilter.HasExplicitKind = true;
                    }
                    else
                    {
                        _searchFilter.BareSubstrings.Add((tok, false));
                    }
                    continue;
                }
                if (IsKnownPredicate(key))
                {
                    _searchFilter.Predicates.Add((key, value, negate));
                }
                else
                {
                    _searchFilter.BareSubstrings.Add((tok, negate));
                }
            }
        }

        // Whitespace-tokenizer that treats "..." as a single token (the
        // quotes are stripped). Used so the user can search for substrings
        // containing spaces, e.g. "Player Spawner" or "id:42 ".
        static List<string> TokenizeSearch(string raw)
        {
            var tokens = new List<string>();
            var cur = new StringBuilder();
            bool inQuote = false;
            for (int i = 0; i < raw.Length; i++)
            {
                char c = raw[i];
                if (c == '"')
                {
                    inQuote = !inQuote;
                    continue;
                }
                if (!inQuote && (c == ' ' || c == '\t'))
                {
                    if (cur.Length > 0)
                    {
                        tokens.Add(cur.ToString());
                        cur.Clear();
                    }
                    continue;
                }
                cur.Append(c);
            }
            if (cur.Length > 0)
                tokens.Add(cur.ToString());
            return tokens;
        }

        // Smart-case: a bare substring with no uppercase characters
        // matches case-insensitively (the typical fast-typing case),
        // while introducing any uppercase character flips the token to
        // case-sensitive — same convention as ripgrep, vim, ag, etc.
        // Picked per token, so `play COLL` requires a literal "COLL"
        // even though `play` still matches loosely.
        static StringComparison ComparisonForToken(string tok)
        {
            if (string.IsNullOrEmpty(tok))
                return StringComparison.OrdinalIgnoreCase;
            for (int i = 0; i < tok.Length; i++)
            {
                if (char.IsUpper(tok[i]))
                    return StringComparison.Ordinal;
            }
            return StringComparison.OrdinalIgnoreCase;
        }

        static SearchScope ScopeFromKindValue(string v) =>
            (v ?? "").ToLowerInvariant() switch
            {
                "e" or "entity" or "entities" => SearchScope.Entities,
                "t" or "template" or "templates" => SearchScope.Templates,
                "c" or "component" or "components" => SearchScope.Components,
                "s" or "set" or "sets" => SearchScope.Sets,
                "tag" or "tags" => SearchScope.Tags,
                "a" or "accessor" or "accessors" => SearchScope.Accessors,
                _ => 0,
            };

        static bool IsKnownPredicate(string key) =>
            key switch
            {
                "tag"
                or "c"
                or "component"
                or "base"
                or "derived"
                or "template"
                or "reads"
                or "writes"
                or "accesses" => true,
                _ => false,
            };

        // True iff the user has typed something — either text, a kind
        // selector, or a predicate. Filter sites short-circuit when this
        // is false to keep the no-filter rebuild path cheap.
        bool SearchActive => !_searchFilter.IsEmpty;

        // Predicate-aware row match. Returns true when the row satisfies
        // every part of the active filter:
        //   - Kind: row's scope must overlap the explicit t: selector
        //     (or no selector → any kind ok).
        //   - Bare substrings: each must appear in displayName.
        //   - Predicates: each key must be defined for this kind, and at
        //     least one of the kind's resolved values for that key must
        //     contain the user's value as a substring.
        // PredicateData is passed by ref so callers can stamp only the
        // fields relevant to the current row's kind.
        bool MatchesSearch(
            SearchScope rowScope,
            string displayName,
            string altName,
            in PredicateData ctx
        )
        {
            var f = _searchFilter;
            if (f.IsEmpty)
                return true;
            if (f.HasExplicitKind && (f.ExplicitKind & rowScope) == 0)
                return false;
            for (int i = 0; i < f.BareSubstrings.Count; i++)
            {
                var (sub, negate) = f.BareSubstrings[i];
                var cmp = ComparisonForToken(sub);
                bool inDisplay = displayName != null && displayName.IndexOf(sub, cmp) >= 0;
                bool inAlt = altName != null && altName.IndexOf(sub, cmp) >= 0;
                bool match = inDisplay || inAlt;
                if (negate ? match : !match)
                    return false;
            }
            for (int i = 0; i < f.Predicates.Count; i++)
            {
                var (key, value, negate) = f.Predicates[i];
                bool match = MatchesPredicate(rowScope, key, value, in ctx);
                if (negate ? match : !match)
                    return false;
            }
            return true;
        }

        bool MatchesSearch(SearchScope rowScope, string displayName, in PredicateData ctx) =>
            MatchesSearch(rowScope, displayName, null, in ctx);

        // Substring-only sites (sections, partition rows, phase headings).
        // A query that includes predicates filters these out — predicate
        // dispatch returns false for kinds it doesn't handle, which is the
        // desired behavior (predicates need to apply somewhere, and
        // partitions etc. aren't a meaningful target).
        bool MatchesSearch(SearchScope rowScope, string displayName) =>
            MatchesSearch(rowScope, displayName, null, in PredicateData.Empty);

        void OnWorldDropdownChanged(ChangeEvent<string> evt)
        {
            var index = _worldDropdown.index;
            if (index < 0)
            {
                return;
            }
            if (index < _dropdownWorlds.Count)
            {
                SelectWorld(_dropdownWorlds[index]);
                return;
            }
            // Past the live-worlds prefix, the rest of the dropdown is
            // cached snapshots — switch into cache mode for that one.
            int cacheIdx = index - _dropdownWorlds.Count;
            if (cacheIdx >= 0 && cacheIdx < _cachedSchemas.Count)
            {
                SelectWorld(null);
                EnterCacheModeForSchema(_cachedSchemas[cacheIdx]);
            }
        }

        static int ChooseDefaultCachedIndex(List<TrecsSchema> schemas)
        {
            // Prefer the most-recently-saved snapshot so reopening the
            // hierarchy lands on the world the user was last looking at.
            int best = 0;
            DateTime bestStamp = DateTime.MinValue;
            for (int i = 0; i < schemas.Count; i++)
            {
                if (DateTime.TryParse(schemas[i].SavedAtIso, out var stamp) && stamp > bestStamp)
                {
                    bestStamp = stamp;
                    best = i;
                }
            }
            return best;
        }

        void EnterCacheModeForSchema(TrecsSchema schema)
        {
            SetSource(new CacheSchemaSource(schema));
            ShowCacheBanner(schema);
            RebuildTree();
            // No proxy rebind needed: every selection-proxy kind is now
            // identity-based, and SetSource above updates ActiveSource so
            // each inspector's next Refresh resolves against the new
            // schema automatically.
        }

        void RebuildDropdown()
        {
            if (_worldDropdown == null)
            {
                return;
            }

            _dropdownWorlds.Clear();
            _cachedSchemas.Clear();
            var labels = new List<string>();
            // Sort by debug name so dropdown indices stay stable across
            // sessions where worlds register at different rates (e.g.
            // Host registers before Client mid-frame). Without this,
            // muscle memory ("the second entry is Client") breaks
            // whenever registration order shifts.
            var active = new List<World>(WorldRegistry.ActiveWorlds);
            active.Sort(
                (a, b) =>
                    string.Compare(
                        a.DebugName ?? "",
                        b.DebugName ?? "",
                        StringComparison.OrdinalIgnoreCase
                    )
            );
            for (int i = 0; i < active.Count; i++)
            {
                _dropdownWorlds.Add(active[i]);
                labels.Add(active[i].DebugName ?? $"World #{i}");
            }

            if (_dropdownWorlds.Count == 0)
            {
                // No live worlds — fall back to cached snapshots. Each
                // entry becomes a dropdown row so the user can switch
                // between snapshots just like live worlds.
                if (TrecsSchemaCache.TryLoadAll(out var schemas) && schemas.Count > 0)
                {
                    schemas.Sort(
                        (a, b) =>
                            string.Compare(
                                a.WorldName ?? "",
                                b.WorldName ?? "",
                                StringComparison.OrdinalIgnoreCase
                            )
                    );
                    _cachedSchemas.AddRange(schemas);
                    foreach (var s in schemas)
                    {
                        labels.Add(((s.WorldName ?? "(unnamed)") + " (cached)"));
                    }
                    _worldDropdown.choices = labels;
                    _worldDropdown.style.display = DisplayStyle.Flex;
                    _searchField.style.display = DisplayStyle.Flex;
                    _emptyState.style.display = DisplayStyle.None;
                    _tree.style.display = DisplayStyle.Flex;
                    SelectWorld(null);
                    // Stick with the currently-shown cached world if it
                    // survived the reload (e.g. SchemaSaved fired); fall
                    // back to most-recent otherwise.
                    int targetIndex = -1;
                    if (_cachedSchema != null)
                    {
                        for (int i = 0; i < _cachedSchemas.Count; i++)
                        {
                            if (_cachedSchemas[i].WorldName == _cachedSchema.WorldName)
                            {
                                targetIndex = i;
                                break;
                            }
                        }
                    }
                    if (targetIndex < 0)
                    {
                        targetIndex = ChooseDefaultCachedIndex(_cachedSchemas);
                    }
                    EnterCacheModeForSchema(_cachedSchemas[targetIndex]);
                    _worldDropdown.SetValueWithoutNotify(
                        labels[_dropdownWorlds.Count + targetIndex]
                    );
                    return;
                }

                _worldDropdown.choices = labels;
                _worldDropdown.style.display = DisplayStyle.None;
                _searchField.style.display = DisplayStyle.None;
                _emptyState.style.display = DisplayStyle.Flex;
                _tree.style.display = DisplayStyle.None;
                HideCacheBanner();
                SetSource(null);
                SelectWorld(null);
                return;
            }

            _worldDropdown.choices = labels;
            SetSource(null);
            HideCacheBanner();
            _worldDropdown.style.display = DisplayStyle.Flex;
            _searchField.style.display = DisplayStyle.Flex;
            _emptyState.style.display = DisplayStyle.None;
            _tree.style.display = DisplayStyle.Flex;

            var selectedIndex =
                _selectedWorld == null ? -1 : _dropdownWorlds.IndexOf(_selectedWorld);
            if (selectedIndex < 0)
            {
                var shared = TrecsEditorSelection.ActiveWorld;
                if (shared != null)
                {
                    selectedIndex = _dropdownWorlds.IndexOf(shared);
                }
                if (selectedIndex < 0)
                {
                    selectedIndex = 0;
                }
                SelectWorld(_dropdownWorlds[selectedIndex]);
            }
            _worldDropdown.SetValueWithoutNotify(labels[selectedIndex]);
        }

        void SelectWorld(World world)
        {
            if (_selectedWorld == world)
            {
                return;
            }
            _selectedWorld = world;
            _selectedAccessor = null;
            // Live source needs both world + accessor. Accessor is created
            // lazily by TryGetAccessor on the next refresh tick — we'll
            // (re)build the live source then, inside RefreshTick.
            SetSource(null);

            // Drop TreeView's selection before we wipe the id maps below.
            // Without this, the next rebuild's SetRootItems would auto-
            // fire selectionChanged on whichever new row happens to land
            // at the previously-selected int id (the cross-world id
            // namespaces don't agree on what id 105 means).
            if (_tree != null)
            {
                _suppressTreeSelectionFeedback = true;
                try
                {
                    _tree.ClearSelection();
                }
                finally
                {
                    _suppressTreeSelectionFeedback = false;
                }
            }

            // Different worlds use different ids. No need to share state.
            _idByKey.Clear();
            _dataById.Clear();
            _parentById.Clear();
            _entityIds.Clear();
            _lastEntityCountByGroup.Clear();
            _lastResolvedTemplateCount = -1;
            _lastAbstractTemplateCount = -1;
            _lastAccessorCount = -1;
            _lastComponentTypeCount = -1;
            _lastSetCount = -1;
            _lastTagCount = -1;
            _nextId = 100;

            if (world != null)
            {
                TrecsEditorSelection.ActiveWorld = world;
            }
            ForceFullRebuild();
        }

        int CountNonEditorAccessors()
        {
            if (_selectedWorld == null)
            {
                return 0;
            }
            int n = 0;
            foreach (var entry in _selectedWorld.GetAllAccessors())
            {
                var dbg = entry.Value?.DebugName;
                if (dbg == null || !TrecsEditorAccessorNames.Contains(dbg))
                {
                    n++;
                }
            }
            return n;
        }

        bool TryGetAccessor(out WorldAccessor accessor)
        {
            accessor = null;
            if (_selectedWorld == null || _selectedWorld.IsDisposed)
            {
                return false;
            }
            try
            {
                _selectedAccessor ??= _selectedWorld.CreateAccessor(
                    AccessorRole.Unrestricted,
                    "TrecsHierarchyWindow"
                );
                accessor = _selectedAccessor;
                return accessor != null;
            }
            catch (Exception)
            {
                return false;
            }
        }

        // ----- Cache fallback mode --------------------------------------------

        void ShowCacheBanner(TrecsSchema schema)
        {
            string ts = schema.SavedAtIso;
            if (DateTime.TryParse(schema.SavedAtIso, out var savedAt))
            {
                var delta = DateTime.UtcNow - savedAt.ToUniversalTime();
                ts = FormatRelativeTime(delta) + " ago";
            }
            var text = $"Schema cache (no live world) — world '{schema.WorldName}', saved {ts}.";
            // If we have multiple cached snapshots, point the user at the
            // dropdown. Otherwise the dropdown looks decorative when there's
            // nothing to switch to.
            if (_cachedSchemas.Count > 1)
            {
                text += " Use the world dropdown above to switch snapshots.";
            }
            _cacheBanner.text = text;
            _cacheBanner.style.display = DisplayStyle.Flex;
            if (_clearCacheButton != null)
            {
                _clearCacheButton.style.display = DisplayStyle.Flex;
            }
        }

        void HideCacheBanner()
        {
            _cacheBanner.style.display = DisplayStyle.None;
            if (_clearCacheButton != null)
            {
                _clearCacheButton.style.display = DisplayStyle.None;
            }
        }

        void OnClearCacheButtonClicked()
        {
            var schema = _cachedSchema;
            if (schema == null)
            {
                return;
            }
            var name = schema.WorldName ?? "(unnamed)";
            if (
                !EditorUtility.DisplayDialog(
                    "Clear cached schema",
                    $"Delete the on-disk schema snapshot for world '{name}'?\n\n"
                        + "Accumulated runtime access data (reads/writes/tags-touched) "
                        + "for this world will be lost.",
                    "Clear",
                    "Cancel"
                )
            )
            {
                return;
            }
            TrecsSchemaCache.Clear(name);
            RebuildDropdown();
        }

        void OnClearCacheButtonContextClicked()
        {
            var menu = new GenericMenu();
            menu.AddItem(new GUIContent("Clear this world"), false, OnClearCacheButtonClicked);
            menu.AddItem(
                new GUIContent("Clear all cached schemas"),
                false,
                () =>
                {
                    if (
                        EditorUtility.DisplayDialog(
                            "Clear all cached schemas",
                            "Delete every on-disk schema snapshot under "
                                + "Library/com.trecs/inspector_schema/?\n\nAccumulated "
                                + "runtime access data for every world will be lost.",
                            "Clear all",
                            "Cancel"
                        )
                    )
                    {
                        TrecsSchemaCache.ClearAll();
                        RebuildDropdown();
                    }
                }
            );
            menu.ShowAsContext();
        }

        static string FormatRelativeTime(TimeSpan delta)
        {
            if (delta.TotalSeconds < 60)
                return $"{(int)delta.TotalSeconds}s";
            if (delta.TotalMinutes < 60)
                return $"{(int)delta.TotalMinutes}m";
            if (delta.TotalHours < 48)
                return $"{(int)delta.TotalHours}h";
            return $"{(int)delta.TotalDays}d";
        }

        // ----- Refresh tick ---------------------------------------------------

        void ForceFullRebuild()
        {
            _lastResolvedTemplateCount = -1;
            _lastAbstractTemplateCount = -1;
            _lastAccessorCount = -1;
            _lastComponentTypeCount = -1;
            _lastEntityCountByGroup.Clear();
            if (_source != null && !_source.SupportsLiveRefresh)
            {
                RebuildTree();
                return;
            }
            RefreshTick();
        }

        void RefreshTick()
        {
            if (!TryGetAccessor(out var accessor))
            {
                // No live world. The cache mode tree (if any) is static and
                // doesn't need per-tick refreshes — only wipe if we aren't
                // in cache mode. Either way, sync the user's
                // expand/collapse clicks into SessionState so they survive
                // a play-mode entry from this state.
                SyncExpandedStableKeysFromTree();
                if (!_cacheMode && _tree != null)
                {
                    // Capture expansion before wiping so play-mode entry
                    // (which fires WorldUnregistered → this branch) doesn't
                    // collapse every folder. Without this, when the world
                    // re-registers after play mode boots, the rebuild's
                    // CaptureExpandedKeys call would snapshot the empty
                    // tree and lose the user's prior layout.
                    _expandedStableKeys.UnionWith(CaptureExpandedKeys());
                    SaveExpandedStableKeys();
                    // Clear TreeView's selectedIds before wiping rows.
                    // Without this, TreeView preserves the int id across
                    // SetRootItems calls, and a later cache rebuild that
                    // happens to assign that id to a different row would
                    // auto-fire selectionChanged on it (re-binding
                    // Selection.activeObject to the wrong proxy).
                    _suppressTreeSelectionFeedback = true;
                    try
                    {
                        _tree.ClearSelection();
                    }
                    finally
                    {
                        _suppressTreeSelectionFeedback = false;
                    }
                    _tree.SetRootItems(new List<TreeViewItemData<RowData>>());
                    _tree.Rebuild();

                    // Drop the stale Trecs proxy so the inspector doesn't
                    // sit on "world unavailable" once the world goes away
                    // (Stop play mode is the canonical case). The
                    // selection identity is already in SessionState, so
                    // TryRestoreSelectionFromIdentity will rebind it
                    // when a fresh world registers and the tree rebuilds.
                    if (IsTrecsSelection(Selection.activeObject))
                    {
                        Selection.activeObject = null;
                        _lastReflectedSelectionId = -1;
                    }
                }
                return;
            }

            var info = _selectedWorld.WorldInfo;

            if (NeedsStructuralRebuild(accessor, info))
            {
                // Recreate the live source so its pre-projected lists pick
                // up any new templates / components / etc. Cheap (single
                // WorldInfo walk) and fired only on structural change.
                SetSource(new LiveSchemaSource(_selectedWorld, accessor));
                RebuildTree();
            }
            else if (UpdateMutableData(accessor))
            {
                // Only RefreshItems when something actually changed —
                // unconditional rebinds reset the row's :hover state every
                // tick, so the hover highlight only paints while the mouse is
                // moving.
                _tree.RefreshItems();
            }

            SyncExpandedStableKeysFromTree();
        }

        // Per-row sync: for each row currently in the tree, mirror its
        // expand state into _expandedStableKeys. Keys for off-screen rows
        // (in the persistent set but not in _stableKeyById right now) are
        // left untouched — when the matching row reappears, the rebuild
        // path will restore its expansion. Without this method the user's
        // expand/collapse clicks wouldn't persist until the next
        // structural rebuild.
        void SyncExpandedStableKeysFromTree()
        {
            if (_tree == null || _dataById.Count == 0)
            {
                return;
            }
            bool changed = false;
            foreach (var kv in _stableKeyById)
            {
                if (!_dataById.ContainsKey(kv.Key))
                {
                    continue;
                }
                bool nowExpanded;
                try
                {
                    nowExpanded = _tree.IsExpanded(kv.Key);
                }
                catch
                {
                    continue;
                }
                bool wasExpanded = _expandedStableKeys.Contains(kv.Value);
                if (nowExpanded && !wasExpanded)
                {
                    _expandedStableKeys.Add(kv.Value);
                    changed = true;
                }
                else if (!nowExpanded && wasExpanded)
                {
                    _expandedStableKeys.Remove(kv.Value);
                    changed = true;
                }
            }
            if (changed)
            {
                SaveExpandedStableKeys();
            }
        }

        bool NeedsStructuralRebuild(WorldAccessor accessor, WorldInfo info)
        {
            int resolvedCount = info.ResolvedTemplates.Count;
            int abstractCount = 0;
            foreach (var t in info.AllTemplates)
            {
                if (!info.IsResolvedTemplate(t))
                {
                    abstractCount++;
                }
            }
            if (resolvedCount != _lastResolvedTemplateCount)
            {
                return true;
            }
            if (abstractCount != _lastAbstractTemplateCount)
            {
                return true;
            }

            // Count only non-editor accessors. Some inspectors (notably
            // TrecsTemplateSelectionInspector.UpdateEntityCount) call
            // world.CreateAccessor on every refresh tick, which would
            // make the total-count fingerprint mismatch every tick and
            // trigger a structural rebuild — that rebuild was what kept
            // snapping the user's manual scroll back into frame.
            int accessorCount;
            try
            {
                accessorCount = CountNonEditorAccessors();
            }
            catch
            {
                return true;
            }
            if (accessorCount != _lastAccessorCount)
            {
                return true;
            }

            _scratchTypeSet.Clear();
            foreach (var rt in info.ResolvedTemplates)
            {
                foreach (var d in rt.ComponentDeclarations)
                {
                    if (d.ComponentType != null)
                    {
                        _scratchTypeSet.Add(d.ComponentType);
                    }
                }
            }
            if (_scratchTypeSet.Count != _lastComponentTypeCount)
            {
                return true;
            }

            if (info.AllSets.Count != _lastSetCount)
            {
                return true;
            }

            int tagCount = CountUniqueTagGuids(info, _scratchTagGuidSet);
            if (tagCount != _lastTagCount)
            {
                return true;
            }

            // Per-group entity count change → entities under that group are
            // tree leaves, so a count delta requires a structural rebuild.
            foreach (var rt in info.ResolvedTemplates)
            {
                foreach (var g in rt.Groups)
                {
                    var c = accessor.CountEntitiesInGroup(g);
                    if (!_lastEntityCountByGroup.TryGetValue(g, out var lc) || lc != c)
                    {
                        return true;
                    }
                }
            }
            return false;
        }

        bool UpdateMutableData(WorldAccessor accessor)
        {
            // Refresh the only data that mutates without changing tree
            // structure: per-system enabled state. Counts already trigger a
            // structural rebuild via NeedsStructuralRebuild. Returns true if
            // any visible row's data actually changed so the caller can
            // decide whether to RefreshItems.
            // Single _dataById walk handles both per-tick mutables that
            // don't trigger a structural rebuild: per-accessor system-
            // enabled state and per-set entity-count badges. Total row
            // count is small (low hundreds even on big worlds), so the
            // walk is cheap.
            bool changed = false;
            try
            {
                var world = accessor.GetWorld();
                foreach (var kvp in _dataById)
                {
                    var data = kvp.Value;
                    switch (data.Kind)
                    {
                        case RowKind.Accessor:
                            if (data.SystemIndex >= 0 && data.SystemIndex < world.SystemCount)
                            {
                                // Effective state — combines all enable
                                // channels with the deterministic paused
                                // flag — so the row grays out whenever the
                                // system isn't actually running, even if
                                // the inspector's Editor toggle is checked.
                                var enabled = world.IsSystemEffectivelyEnabled(data.SystemIndex);
                                if (enabled != data.SystemEffectivelyEnabled)
                                {
                                    data.SystemEffectivelyEnabled = enabled;
                                    changed = true;
                                }
                            }
                            break;
                        case RowKind.SetItem:
                            var newCount = accessor.CountEntitiesInSet(data.EntitySet.Id);
                            if (newCount != data.Count)
                            {
                                data.Count = newCount;
                                changed = true;
                            }
                            break;
                    }
                }
            }
            catch
            {
                // World may have transitioned; next tick will resync.
            }

            return changed;
        }

        // ----- Tree structure build ------------------------------------------

        // Single section-emission path used by both live (re-entered on
        // structural change) and cache (re-entered on schema swap). Live-
        // only state — fingerprint update, post-rebuild Selection sync,
        // PruneIdByKey — runs only when the source supports live refresh,
        // gated through src.SupportsLiveRefresh.
        void RebuildTree()
        {
            if (_source == null || _tree == null)
            {
                return;
            }

            var src = _source;
            var live = src as LiveSchemaSource;
            var info = live?.Info;

            // Merge the tree's currently-expanded rows into the persistent
            // set so the rebuild can restore them. Stable string keys (vs.
            // int ids) survive play-mode entry and domain reloads.
            _expandedStableKeys.UnionWith(CaptureExpandedKeys());

            // Reset structural state. _idByKey and _stableKeyById are
            // intentionally preserved so ids stay stable for keys we
            // already saw — PruneIdByKey at the end drops entries whose
            // ids didn't make it into the new tree.
            _dataById.Clear();
            _parentById.Clear();
            _entityIds.Clear();
            _lastEntityCountByGroup.Clear();
            SeedSectionStableKeys();

            var rootItems = new List<TreeViewItemData<RowData>>();

            // Section counts come from the world snapshot, not the children
            // list — the children list reflects the current search filter,
            // but the heading should always show "this is what exists in
            // the world", same as how the per-template count ignores
            // partition-level expansion. Live mode walks WorldInfo (matches
            // the structural-rebuild fingerprint inputs); cache mode reads
            // the source's projected list lengths.
            int totalTemplateCount;
            int resolvedTemplateCount = 0;
            int abstractTemplateCount = 0;
            int totalComponentTypeCount;
            int totalAccessorCount;
            int totalSetCount;
            int totalTagCount = src.Tags.Count;
            if (info != null)
            {
                totalTemplateCount = info.AllTemplates.Count;
                resolvedTemplateCount = info.ResolvedTemplates.Count;
                foreach (var t in info.AllTemplates)
                {
                    if (!info.IsResolvedTemplate(t))
                        abstractTemplateCount++;
                }
                _scratchTypeSet.Clear();
                foreach (var rt in info.ResolvedTemplates)
                {
                    foreach (var d in rt.ComponentDeclarations)
                    {
                        if (d.ComponentType != null)
                        {
                            _scratchTypeSet.Add(d.ComponentType);
                        }
                    }
                }
                totalComponentTypeCount = _scratchTypeSet.Count;
                try
                {
                    totalAccessorCount = CountNonEditorAccessors();
                }
                catch
                {
                    totalAccessorCount = -1;
                }
                totalSetCount = info.AllSets.Count;
            }
            else
            {
                totalTemplateCount = src.Templates.Count;
                totalComponentTypeCount = src.ComponentTypes.Count;
                totalSetCount = src.Sets.Count;
                int accessorCount = 0;
                foreach (var p in src.AccessorsByPhase)
                {
                    accessorCount += p.Accessors.Count;
                }
                totalAccessorCount = accessorCount;
            }

            // While search is active the tree morphs into a flat list of
            // matching content rows (see HarvestFlatLeaves). Otherwise we
            // emit the standard 5-section hierarchy. The per-section build
            // calls run unconditionally so reverse-lookup tables and stable
            // ids stay populated either way.
            bool searchActive = !string.IsNullOrEmpty(_searchText);

            EmitSection(
                SectionTemplatesId,
                "Templates",
                totalTemplateCount,
                BuildTemplatesChildren(_source, SectionTemplatesId),
                rootItems,
                searchActive
            );
            EmitSection(
                SectionAccessorsId,
                "Accessors",
                totalAccessorCount,
                BuildAccessorsChildren(_source, SectionAccessorsId),
                rootItems,
                searchActive
            );
            EmitSection(
                SectionComponentsId,
                "Components",
                totalComponentTypeCount,
                BuildComponentsChildren(_source, SectionComponentsId),
                rootItems,
                searchActive
            );
            EmitSection(
                SectionSetsId,
                "Sets",
                totalSetCount,
                BuildSetsChildren(_source, SectionSetsId),
                rootItems,
                searchActive
            );
            EmitSection(
                SectionTagsId,
                "Tags",
                totalTagCount,
                BuildTagsChildren(_source, SectionTagsId),
                rootItems,
                searchActive
            );

            if (searchActive)
            {
                rootItems.Sort(
                    (a, b) =>
                        string.Compare(
                            a.data.DisplayName ?? string.Empty,
                            b.data.DisplayName ?? string.Empty,
                            StringComparison.OrdinalIgnoreCase
                        )
                );
            }

            _tree.SetRootItems(rootItems);
            _tree.Rebuild();

            // First non-search rebuild: open the five root sections so the
            // tree doesn't appear collapsed-from-empty. Subsequent rebuilds
            // rely on the persisted-key restore below — that way the user
            // can collapse a section and have it stay collapsed across
            // ticks (and across play-mode entry). The gate is
            // [SerializeField], so it survives domain reload — without
            // that, every play-mode entry would force-reopen sections
            // the user had just collapsed.
            if (!searchActive && !_initialSectionExpansionApplied)
            {
                _expandedStableKeys.Add("section:Templates");
                _expandedStableKeys.Add("section:Accessors");
                _expandedStableKeys.Add("section:Components");
                _expandedStableKeys.Add("section:Sets");
                _expandedStableKeys.Add("section:Tags");
                SetInitialSectionExpansionApplied(true);
            }

            bool leavingSearch = !searchActive && _preSearchExpandedKeys != null;

            if (leavingSearch)
            {
                // Search just got cleared. Restore the expand state the
                // user had before they started typing — the flat-mode
                // tree had nothing to capture so _expandedStableKeys may
                // already be partial, and pre-search is the source of
                // truth.
                _expandedStableKeys.UnionWith(_preSearchExpandedKeys);
                _preSearchExpandedKeys = null;
            }

            RestoreExpandedFromStableKeys();
            SaveExpandedStableKeys();
            TryRestoreSelectionFromIdentity();

            // Drop _idByKey entries whose ids didn't make it into the new
            // tree (entities destroyed, accessors disposed, templates pruned
            // by toggles, etc.). Without this the map grows monotonically
            // for the editor session.
            PruneIdByKey();

            // Update structural fingerprint with the counts captured at the
            // top of this method — no need to re-walk the world. Cache mode
            // doesn't run NeedsStructuralRebuild so the fingerprint update
            // is wasted; skip.
            if (src.SupportsLiveRefresh)
            {
                _lastResolvedTemplateCount = resolvedTemplateCount;
                _lastAbstractTemplateCount = abstractTemplateCount;
                _lastAccessorCount = totalAccessorCount;
                _lastComponentTypeCount = totalComponentTypeCount;
                _lastSetCount = totalSetCount;
                _lastTagCount = totalTagCount;
            }

            // Reapply tree highlight from the current Selection.activeObject —
            // the prior tree's selected row id is gone, but the proxy may
            // still point to a row that exists in the new tree. Don't
            // scroll for routine rebuilds (count tick): the user may be
            // scrolled elsewhere on purpose. Exception: when the user just
            // cleared the search field, the flat results view collapses
            // back to the section tree and the selection often lands
            // off-screen — scroll it into view so they regain context.
            UpdateRowSelectionFromUnity(scrollToItem: leavingSearch);
        }

        // While search is active we render a flat list of matching leaves
        // instead of the section hierarchy — see header comment on
        // _preSearchExpandedKeys. Walks the per-section subtree and pulls
        // out every content row, dropping headers (Section, AccessorPhase,
        // Group, MorePlaceholder). Each harvested leaf is reconstructed
        // without children so it sits at the root of the flat tree.
        //
        // The explicit-kind filter (e.g. `t:e`) gets re-applied here:
        // TryBuildTemplateNode keeps a template node when any of
        // its entity children match, since hierarchy-mode rendering needs
        // the template as the structural parent of those children. In
        // flat mode there's no structural reason to keep it, so a `t:e`
        // search would otherwise show both the template row and its
        // matching entities. Re-checking the kind mask drops the
        // template.
        void HarvestFlatLeaves(
            List<TreeViewItemData<RowData>> source,
            List<TreeViewItemData<RowData>> sink
        )
        {
            var kindMask = _searchFilter.HasExplicitKind
                ? _searchFilter.ExplicitKind
                : SearchScope.All;
            HarvestFlatLeavesInner(source, sink, kindMask);
        }

        static void HarvestFlatLeavesInner(
            List<TreeViewItemData<RowData>> source,
            List<TreeViewItemData<RowData>> sink,
            SearchScope kindMask
        )
        {
            foreach (var item in source)
            {
                var kind = item.data.Kind;
                var rowScope = ScopeForRowKind(kind);
                bool isLeafContent = rowScope != 0;
                if (isLeafContent && (rowScope & kindMask) != 0)
                {
                    sink.Add(new TreeViewItemData<RowData>(item.id, item.data));
                }
                if (item.hasChildren)
                {
                    HarvestFlatLeavesInner(
                        (List<TreeViewItemData<RowData>>)item.children,
                        sink,
                        kindMask
                    );
                }
            }
        }

        // Maps a row's RowKind to the SearchScope it represents. Header
        // kinds (Section / AccessorPhase / Group / MorePlaceholder) return
        // 0 since they aren't user-searchable content.
        static SearchScope ScopeForRowKind(RowKind kind) =>
            kind switch
            {
                RowKind.Template or RowKind.AbstractTemplate => SearchScope.Templates,
                RowKind.Entity => SearchScope.Entities,
                RowKind.Accessor => SearchScope.Accessors,
                RowKind.ComponentType => SearchScope.Components,
                RowKind.SetItem => SearchScope.Sets,
                RowKind.TagItem => SearchScope.Tags,
                _ => 0,
            };

        // Walks live tree rows, looks up each id's stable string key, and
        // emits the keys for rows that are currently expanded. Rows
        // without a registered stable key (anonymous "more not shown"
        // placeholders) are skipped.
        HashSet<string> CaptureExpandedKeys()
        {
            var s = new HashSet<string>();
            if (_tree == null)
            {
                return s;
            }
            foreach (var id in _dataById.Keys)
            {
                if (!_stableKeyById.TryGetValue(id, out var key))
                {
                    continue;
                }
                try
                {
                    if (_tree.IsExpanded(id))
                    {
                        s.Add(key);
                    }
                }
                catch
                {
                    // id may not exist in the controller anymore.
                }
            }
            return s;
        }

        // Re-expands every row whose stable key is in _expandedStableKeys.
        // Called after each Rebuild() to translate persisted keys back
        // into the new tree's int ids.
        void RestoreExpandedFromStableKeys()
        {
            if (_tree == null)
            {
                return;
            }
            foreach (var kv in _stableKeyById)
            {
                if (!_dataById.ContainsKey(kv.Key))
                {
                    continue;
                }
                if (_expandedStableKeys.Contains(kv.Value))
                {
                    _tree.ExpandItem(kv.Key);
                }
            }
        }

        // Persist _expandedStableKeys to SessionState. Called on every
        // mutation and at the play-mode-entry edge so the user's
        // edit-mode expansions survive the domain reload.
        void SaveExpandedStableKeys()
        {
            SessionState.SetString(
                ExpandedKeysSessionKey,
                string.Join(ExpandedKeysSeparator, _expandedStableKeys)
            );
        }

        void LoadExpandedStableKeys()
        {
            _expandedStableKeys.Clear();
            var raw = SessionState.GetString(ExpandedKeysSessionKey, string.Empty);
            if (string.IsNullOrEmpty(raw))
            {
                return;
            }
            foreach (var k in raw.Split(ExpandedKeysSeparator))
            {
                if (!string.IsNullOrEmpty(k))
                {
                    _expandedStableKeys.Add(k);
                }
            }
        }

        void SetInitialSectionExpansionApplied(bool value)
        {
            _initialSectionExpansionApplied = value;
            SessionState.SetBool(InitialSectionExpansionAppliedSessionKey, value);
        }

        // ---- Templates subtree ----

        List<TreeViewItemData<RowData>> BuildTemplatesChildren(ITrecsSchemaSource src, int parentId)
        {
            var children = new List<TreeViewItemData<RowData>>();
            if (src == null)
            {
                return children;
            }

            // Alpha sort across both modes, with concrete templates ahead
            // of abstract on ties so the "(abstract)" badge rows naturally
            // bunch at the bottom. Live mode loses its old "populated
            // templates float up" affordance — the win is that row order
            // is now identical between live and the same world's cached
            // snapshot, so transitioning between the two doesn't shuffle
            // the visible list.
            var sorted = new List<TemplateRef>(src.Templates);
            sorted.Sort(
                (a, b) =>
                {
                    if (a.IsResolved != b.IsResolved)
                    {
                        return a.IsResolved ? -1 : 1;
                    }
                    return string.Compare(
                        a.DebugName ?? string.Empty,
                        b.DebugName ?? string.Empty,
                        StringComparison.OrdinalIgnoreCase
                    );
                }
            );

            // Disambiguate duplicate names. Different Template instances are
            // allowed to share a DebugName, but each row needs a unique
            // stable key (and hence a unique int id) — otherwise two rows
            // collapse onto the same id and TreeView selects both at once.
            // The disambiguator is the 0-based occurrence index among
            // same-named entries; left at -1 (no suffix) for unique names.
            var disambig = new int[sorted.Count];
            var nameSeen = new Dictionary<string, int>();
            var nameTotal = new Dictionary<string, int>();
            for (int i = 0; i < sorted.Count; i++)
            {
                var name = sorted[i].DebugName ?? string.Empty;
                nameTotal.TryGetValue(name, out int total);
                nameTotal[name] = total + 1;
            }
            for (int i = 0; i < sorted.Count; i++)
            {
                var name = sorted[i].DebugName ?? string.Empty;
                if (nameTotal[name] <= 1)
                {
                    disambig[i] = -1;
                    continue;
                }
                nameSeen.TryGetValue(name, out int idx);
                disambig[i] = idx;
                nameSeen[name] = idx + 1;
            }

            for (int i = 0; i < sorted.Count; i++)
            {
                var tref = sorted[i];
                if (!_showAbstractTemplates && !tref.IsResolved)
                {
                    continue;
                }
                if (TryBuildTemplateNode(src, tref, disambig[i], parentId, out var item))
                {
                    children.Add(item);
                }
            }
            return children;
        }

        bool TryBuildTemplateNode(
            ITrecsSchemaSource src,
            TemplateRef tref,
            int disambiguator,
            int parentId,
            out TreeViewItemData<RowData> result
        )
        {
            result = default;
            var displayName = tref.DebugName ?? "(unnamed)";

            // Children — only when the source supports entity iteration
            // (live mode + resolved template). Cache rows are leaves; the
            // TreeView's expand arrow won't render for them.
            List<TreeViewItemData<RowData>> tplChildren = null;
            int totalCount = 0;
            bool canIterateEntities =
                src.SupportsEntityIteration && tref.IsResolved && tref.LiveResolved != null;
            if (canIterateEntities)
            {
                var rt = tref.LiveResolved;
                foreach (var g in rt.Groups)
                {
                    int n = src.CountEntitiesInGroup(g);
                    totalCount += n;
                    _lastEntityCountByGroup[g] = n;
                }
                if (!_showEmptyTemplates && totalCount == 0)
                {
                    return false;
                }
                if (totalCount > 0 && _selectedAccessor != null)
                {
                    tplChildren = new List<TreeViewItemData<RowData>>();
                    var hasPartitions = rt.Partitions.Count > 0;
                    for (int i = 0; i < rt.Groups.Count; i++)
                    {
                        var group = rt.Groups[i];
                        if (hasPartitions)
                        {
                            var tags = i < rt.Partitions.Count ? rt.Partitions[i] : default;
                            if (
                                TryBuildPartitionNode(
                                    _selectedAccessor,
                                    tref,
                                    rt,
                                    group,
                                    tags,
                                    out var pitem
                                )
                            )
                            {
                                tplChildren.Add(pitem);
                            }
                        }
                        else
                        {
                            AppendEntityChildren(
                                _selectedAccessor,
                                group,
                                displayName,
                                tref,
                                tplChildren
                            );
                        }
                    }
                }
            }

            // Search predicate context. Refs carry pre-projected name lists
            // (tags, components, base/derived templates) so the matchers
            // read uniformly across live and cache modes.
            var ctx = new PredicateData { Template = tref };
            bool selfMatches = MatchesSearch(
                SearchScope.Templates,
                displayName,
                tref.DebugName ?? string.Empty,
                in ctx
            );
            bool anyChildMatches = tplChildren != null && tplChildren.Count > 0;
            if (SearchActive && !selfMatches && !anyChildMatches)
            {
                return false;
            }

            // Name-keyed id allocation — survives the live ↔ cache
            // transition, so the same template row keeps its id and
            // expansion state across mode switches. The disambiguator
            // suffix (#N) is appended only when multiple templates in
            // this rebuild share a debug name; ordinary unique-name rows
            // stay on the bare "template:Name" key.
            var bareName = tref.DebugName ?? string.Empty;
            string stableKey =
                disambiguator < 0
                    ? TrecsRowIdentity.TemplatePrefix + bareName
                    : TrecsRowIdentity.TemplatePrefix + bareName + "#" + disambiguator;
            int treeId = GetOrAssignId(stableKey);
            _parentById[treeId] = parentId;

            var data = new RowData
            {
                Kind = tref.IsResolved ? RowKind.Template : RowKind.AbstractTemplate,
                DisplayName = displayName,
                StableKey = stableKey,
                Template = tref.LiveTemplate,
                ResolvedTemplate = tref.LiveResolved,
                Count = totalCount,
                ShowCount = canIterateEntities,
            };
            _dataById[treeId] = data;

            if (tplChildren != null)
            {
                foreach (var c in tplChildren)
                {
                    _parentById[c.id] = treeId;
                }
            }

            result = new TreeViewItemData<RowData>(treeId, data, tplChildren);
            return true;
        }

        bool TryBuildPartitionNode(
            WorldAccessor accessor,
            TemplateRef parentTref,
            ResolvedTemplate rt,
            GroupIndex group,
            TagSet partitionTags,
            out TreeViewItemData<RowData> result
        )
        {
            result = default;
            int count = accessor.CountEntitiesInGroup(group);
            _lastEntityCountByGroup[group] = count;

            var displayName = FormatPartitionLabel(group, partitionTags);
            var entityChildren = new List<TreeViewItemData<RowData>>();
            AppendEntityChildren(
                accessor,
                group,
                rt.DebugName ?? string.Empty,
                parentTref,
                entityChildren
            );

            bool selfMatches = MatchesSearch(SearchScope.Partitions, displayName);
            bool anyChildMatches = entityChildren.Count > 0;
            if (SearchActive && !selfMatches && !anyChildMatches)
            {
                return false;
            }

            int treeId = GetOrAssignId(MakeGroupKey(rt, group));
            var data = new RowData
            {
                Kind = RowKind.Group,
                DisplayName = displayName,
                ResolvedTemplate = rt,
                Group = group,
                PartitionTags = partitionTags,
                Count = count,
                ShowCount = true,
            };
            _dataById[treeId] = data;
            foreach (var c in entityChildren)
            {
                _parentById[c.id] = treeId;
            }
            result = new TreeViewItemData<RowData>(treeId, data, entityChildren);
            return true;
        }

        void AppendEntityChildren(
            WorldAccessor accessor,
            GroupIndex group,
            string templateDisplay,
            TemplateRef parentTref,
            List<TreeViewItemData<RowData>> sink
        )
        {
            int count = accessor.CountEntitiesInGroup(group);
            _lastEntityCountByGroup[group] = count;

            var maxPerGroup = TrecsDebugWindowSettings.Get().MaxEntitiesPerGroup;
            int shown = Mathf.Min(count, maxPerGroup);
            for (int i = 0; i < shown; i++)
            {
                EntityHandle handle;
                try
                {
                    handle = accessor.GetEntityHandle(new EntityIndex(i, group));
                }
                catch
                {
                    continue;
                }
                var displayName = string.IsNullOrEmpty(templateDisplay)
                    ? $"#{handle.UniqueId}"
                    : $"{templateDisplay} #{handle.UniqueId}";

                if (SearchActive)
                {
                    var hay = $"{displayName} id:{handle.UniqueId}";
                    var ctx = new PredicateData { Template = parentTref };
                    if (!MatchesSearch(SearchScope.Entities, hay, in ctx))
                    {
                        continue;
                    }
                }

                int eid = GetOrAssignId(handle);
                _entityIds[handle] = eid;
                var data = new RowData
                {
                    Kind = RowKind.Entity,
                    DisplayName = displayName,
                    EntityHandle = handle,
                };
                _dataById[eid] = data;
                sink.Add(new TreeViewItemData<RowData>(eid, data));
            }

            // The "N more not shown" placeholder is informational only —
            // suppress it when a search filter is active so it doesn't
            // count as a "child matched" (which was making any template
            // with more entities than MaxEntitiesPerGroup pass the filter
            // unconditionally — Cave Bounds, the largest, always survived
            // every search). The placeholder also wouldn't reflect search
            // hits among entities beyond `shown` anyway.
            if (count > shown && string.IsNullOrEmpty(_searchText))
            {
                int phid = AllocateAnonId();
                var data = new RowData
                {
                    Kind = RowKind.MorePlaceholder,
                    DisplayName = $"… {count - shown} more not shown",
                };
                _dataById[phid] = data;
                sink.Add(new TreeViewItemData<RowData>(phid, data));
            }
        }

        // ---- Accessors subtree ----

        List<TreeViewItemData<RowData>> BuildAccessorsChildren(ITrecsSchemaSource src, int parentId)
        {
            var result = new List<TreeViewItemData<RowData>>();
            if (src == null)
            {
                return result;
            }
            foreach (var phase in src.AccessorsByPhase)
            {
                AddPhase(src, result, phase, parentId);
            }
            return result;
        }

        void AddPhase(
            ITrecsSchemaSource src,
            List<TreeViewItemData<RowData>> sink,
            AccessorPhaseRef phase,
            int parentId
        )
        {
            var phaseChildren = new List<TreeViewItemData<RowData>>();
            foreach (var aref in phase.Accessors)
            {
                var ctx = new PredicateData { AccessorDebugName = aref.DebugName };
                if (!MatchesSearch(SearchScope.Accessors, aref.DebugName, in ctx))
                {
                    continue;
                }
                // Live effective enable state — falls through to true (the
                // pre-rebuild snapshot) for cache rows, manual accessors,
                // and any live row whose system index isn't currently
                // resolvable. UpdateMutableData flips it on the next tick
                // for resolvable live system rows.
                bool enabled = true;
                if (
                    src.SupportsSystemEnableToggle
                    && aref.SystemIndex >= 0
                    && src.TryGetSystemEffectivelyEnabled(aref.SystemIndex, out var live)
                )
                {
                    enabled = live;
                }
                var stableKey = TrecsRowIdentity.AccessorPrefix + aref.DebugName;
                int aid = GetOrAssignId(stableKey);
                var data = new RowData
                {
                    Kind = RowKind.Accessor,
                    DisplayName = aref.DebugName,
                    StableKey = stableKey,
                    AccessorId = aref.AccessorId,
                    SystemIndex = aref.SystemIndex,
                    ExecutionPriority = aref.ExecutionPriority,
                    AccessorRole = aref.Role,
                    SystemEffectivelyEnabled = enabled,
                };
                _dataById[aid] = data;
                phaseChildren.Add(new TreeViewItemData<RowData>(aid, data));
            }

            bool phaseTitleMatches = MatchesSearch(SearchScope.Accessors, phase.PhaseName);
            if (SearchActive && !phaseTitleMatches && phaseChildren.Count == 0)
            {
                return;
            }

            int phid = GetOrAssignId("phase:" + phase.PhaseName);
            _parentById[phid] = parentId;
            var pdata = new RowData
            {
                Kind = RowKind.AccessorPhase,
                DisplayName = phase.PhaseName,
                Count = phaseChildren.Count,
                ShowCount = true,
            };
            _dataById[phid] = pdata;
            foreach (var c in phaseChildren)
            {
                _parentById[c.id] = phid;
            }

            sink.Add(new TreeViewItemData<RowData>(phid, pdata, phaseChildren));
        }

        // ---- Components subtree ----

        List<TreeViewItemData<RowData>> BuildComponentsChildren(
            ITrecsSchemaSource src,
            int parentId
        )
        {
            var children = new List<TreeViewItemData<RowData>>();
            if (src == null)
            {
                return children;
            }
            var sorted = new List<ComponentTypeRef>(src.ComponentTypes);
            sorted.Sort(
                (a, b) =>
                    string.Compare(
                        a.DisplayName ?? string.Empty,
                        b.DisplayName ?? string.Empty,
                        StringComparison.OrdinalIgnoreCase
                    )
            );
            foreach (var cref in sorted)
            {
                var display = cref.DisplayName ?? "(unnamed)";
                var ctx = new PredicateData { ComponentType = cref };
                if (!MatchesSearch(SearchScope.Components, display, cref.LiveType?.Name, in ctx))
                {
                    continue;
                }
                var stableKey = TrecsRowIdentity.ComponentPrefix + display;
                int cid = GetOrAssignId(stableKey);
                _parentById[cid] = parentId;
                var data = new RowData
                {
                    Kind = RowKind.ComponentType,
                    DisplayName = display,
                    StableKey = stableKey,
                    ComponentType = cref.LiveType,
                };
                _dataById[cid] = data;
                children.Add(new TreeViewItemData<RowData>(cid, data));
            }
            return children;
        }

        List<TreeViewItemData<RowData>> BuildSetsChildren(ITrecsSchemaSource src, int parentId)
        {
            var children = new List<TreeViewItemData<RowData>>();
            if (src == null)
            {
                return children;
            }
            var sorted = new List<SetRef>(src.Sets);
            sorted.Sort(
                (a, b) =>
                    string.Compare(
                        a.DebugName ?? string.Empty,
                        b.DebugName ?? string.Empty,
                        StringComparison.OrdinalIgnoreCase
                    )
            );
            foreach (var sref in sorted)
            {
                var display = sref.DebugName ?? "(unnamed)";
                var ctx = new PredicateData { Set = sref };
                if (!MatchesSearch(SearchScope.Sets, display, in ctx))
                {
                    continue;
                }

                // Live mode shows the entity count (mutates each tick);
                // cache rows have no count (entities aren't snapshotted).
                int count = 0;
                bool showCount = false;
                if (src.SupportsEntityIteration && sref.HasLiveSet && _selectedAccessor != null)
                {
                    try
                    {
                        count = _selectedAccessor.CountEntitiesInSet(sref.LiveSet.Id);
                        showCount = true;
                    }
                    catch
                    {
                        count = 0;
                    }
                }

                var stableKey = TrecsRowIdentity.SetPrefix + display;
                int sid = GetOrAssignId(stableKey);
                _parentById[sid] = parentId;
                var data = new RowData
                {
                    Kind = RowKind.SetItem,
                    DisplayName = display,
                    StableKey = stableKey,
                    EntitySet = sref.HasLiveSet ? sref.LiveSet : default,
                    Count = count,
                    ShowCount = showCount,
                };
                _dataById[sid] = data;
                children.Add(new TreeViewItemData<RowData>(sid, data));
            }
            return children;
        }

        // Counts unique tag guids without building a Dictionary<int, Tag>.
        // Used by the per-tick fingerprint check where the actual Tag
        // values aren't needed — only the count is compared. The caller
        // owns the HashSet so it can be reused across ticks.
        static int CountUniqueTagGuids(WorldInfo info, HashSet<int> scratch)
        {
            scratch.Clear();
            foreach (var rt in info.ResolvedTemplates)
            {
                if (!rt.AllTags.IsNull)
                {
                    foreach (var t in rt.AllTags.Tags)
                    {
                        if (t.Guid != 0)
                            scratch.Add(t.Guid);
                    }
                }
                foreach (var p in rt.Partitions)
                {
                    if (p.IsNull)
                        continue;
                    foreach (var t in p.Tags)
                    {
                        if (t.Guid != 0)
                            scratch.Add(t.Guid);
                    }
                }
            }
            foreach (var entitySet in info.AllSets)
            {
                if (entitySet.Tags.IsNull)
                    continue;
                foreach (var t in entitySet.Tags.Tags)
                {
                    if (t.Guid != 0)
                        scratch.Add(t.Guid);
                }
            }
            return scratch.Count;
        }

        List<TreeViewItemData<RowData>> BuildTagsChildren(ITrecsSchemaSource src, int parentId)
        {
            var children = new List<TreeViewItemData<RowData>>();
            if (src == null)
            {
                return children;
            }
            var sorted = new List<TagRef>(src.Tags);
            sorted.Sort(
                (a, b) =>
                    string.Compare(
                        a.Name ?? string.Empty,
                        b.Name ?? string.Empty,
                        StringComparison.OrdinalIgnoreCase
                    )
            );
            foreach (var tref in sorted)
            {
                var display = tref.Name ?? "(unnamed)";
                var ctx = new PredicateData { Tag = tref };
                if (!MatchesSearch(SearchScope.Tags, display, in ctx))
                {
                    continue;
                }
                var stableKey = TrecsRowIdentity.TagPrefix + display;
                int tid = GetOrAssignId(stableKey);
                _parentById[tid] = parentId;
                var data = new RowData
                {
                    Kind = RowKind.TagItem,
                    DisplayName = display,
                    StableKey = stableKey,
                    Tag = tref.HasLiveTag ? tref.LiveTag : default,
                };
                _dataById[tid] = data;
                children.Add(new TreeViewItemData<RowData>(tid, data));
            }
            return children;
        }

        // Stamps the section header into _dataById and either harvests its
        // already-built children as flat rootItems (search active) or emits
        // them as a collapsible section node (default). Live and cache
        // rebuilds drive five of these per pass; the search-active sort runs
        // once after all five.
        void EmitSection(
            int sectionId,
            string title,
            int count,
            List<TreeViewItemData<RowData>> children,
            List<TreeViewItemData<RowData>> rootItems,
            bool searchActive
        )
        {
            var sectionData = new RowData
            {
                Kind = RowKind.Section,
                DisplayName = title,
                Count = count,
                ShowCount = true,
            };
            _dataById[sectionId] = sectionData;
            if (searchActive)
            {
                HarvestFlatLeaves(children, rootItems);
            }
            else
            {
                rootItems.Add(new TreeViewItemData<RowData>(sectionId, sectionData, children));
            }
        }

        // ----- Tree make / bind ----------------------------------------------

        VisualElement MakeTreeItem()
        {
            var row = new VisualElement();
            row.style.flexDirection = FlexDirection.Row;
            row.style.alignItems = Align.Center;
            row.style.flexGrow = 1;
            // Right-click menu — UI Toolkit triggers this on Win/Linux
            // right-click and macOS Ctrl+click. Per-row data is looked up
            // fresh from _dataById via the id stamped on the element by
            // BindTreeItem.
            row.AddManipulator(new ContextualMenuManipulator(BuildRowContextMenu));

            var icon = new Image { name = "trecs-icon", scaleMode = ScaleMode.ScaleToFit };
            icon.style.width = 16;
            icon.style.height = 16;
            icon.style.marginRight = 4;
            icon.style.flexShrink = 0;
            row.Add(icon);

            var label = new Label { name = "trecs-label" };
            label.style.flexGrow = 1;
            label.style.overflow = Overflow.Hidden;
            label.style.textOverflow = TextOverflow.Ellipsis;
            row.Add(label);

            var badge = new Label { name = "trecs-badge" };
            badge.style.opacity = 0.5f;
            badge.style.marginLeft = 4;
            row.Add(badge);
            return row;
        }

        void BindTreeItem(VisualElement element, int index)
        {
            var id = _tree.GetIdForIndex(index);
            // Stash the id so the contextual-menu handler can resolve the
            // row's RowData fresh from _dataById (the data ref itself
            // could be stale if a structural rebuild raced with the menu
            // opening).
            element.userData = id;
            if (!_dataById.TryGetValue(id, out var data))
            {
                return;
            }

            BindRowIcon(element.Q<Image>("trecs-icon"), data);
            BindRowLabel(element.Q<Label>("trecs-label"), data);
            BindRowBadge(element.Q<Label>("trecs-badge"), data);
            BindRowPreviewOverlay(element, id);
        }

        static void BindRowIcon(Image icon, RowData data)
        {
            icon.image = TrecsRowIcons.For(data.Kind);
            icon.style.opacity =
                (data.Kind == RowKind.AbstractTemplate || data.Kind == RowKind.MorePlaceholder)
                    ? 0.5f
                    : 1f;
            icon.style.display = icon.image == null ? DisplayStyle.None : DisplayStyle.Flex;
        }

        void BindRowLabel(Label label, RowData data)
        {
            ApplyLabelTextWithSearchHighlight(label, data.DisplayName);
            switch (data.Kind)
            {
                case RowKind.Section:
                    label.style.unityFontStyleAndWeight = FontStyle.Bold;
                    label.style.opacity = data.ShowCount && data.Count == 0 ? 0.45f : 1f;
                    break;
                case RowKind.AbstractTemplate:
                    label.style.unityFontStyleAndWeight = FontStyle.Italic;
                    label.style.opacity = 0.6f;
                    break;
                case RowKind.MorePlaceholder:
                    label.style.unityFontStyleAndWeight = FontStyle.Italic;
                    label.style.opacity = 0.5f;
                    break;
                case RowKind.Accessor:
                    label.style.unityFontStyleAndWeight = FontStyle.Normal;
                    label.style.opacity = data.SystemEffectivelyEnabled ? 1f : 0.45f;
                    break;
                case RowKind.Template:
                    label.style.unityFontStyleAndWeight = FontStyle.Normal;
                    label.style.opacity = data.Count == 0 ? 0.55f : 1f;
                    break;
                default:
                    label.style.unityFontStyleAndWeight = FontStyle.Normal;
                    label.style.opacity = 1f;
                    break;
            }
        }

        static void BindRowBadge(Label badge, RowData data)
        {
            string badgeText = null;
            switch (data.Kind)
            {
                case RowKind.Section:
                case RowKind.Template:
                case RowKind.Group:
                case RowKind.AccessorPhase:
                case RowKind.SetItem:
                    if (data.ShowCount)
                    {
                        badgeText = $"({data.Count})";
                    }
                    break;
                case RowKind.Accessor:
                    // Show role + (optional) priority. Role is always
                    // present on live rows and on cache snapshots written
                    // after the role field landed; legacy snapshots
                    // gracefully fall back to "prio N" / nothing.
                    string roleText = data.AccessorRole.HasValue
                        ? data.AccessorRole.Value.ToString()
                        : null;
                    string prioText = data.ExecutionPriority.HasValue
                        ? $"prio {data.ExecutionPriority.Value}"
                        : null;
                    if (roleText != null && prioText != null)
                    {
                        badgeText = $"{roleText} · {prioText}";
                    }
                    else
                    {
                        badgeText = roleText ?? prioText;
                    }
                    break;
                case RowKind.AbstractTemplate:
                    badgeText = "(abstract)";
                    break;
            }
            if (badgeText != null)
            {
                badge.text = badgeText;
                badge.tooltip = string.Empty;
                badge.style.display = DisplayStyle.Flex;
            }
            else
            {
                badge.tooltip = string.Empty;
                badge.style.display = DisplayStyle.None;
            }
        }

        // Preview-hover overlay driven by inspector links. Apply on the
        // custom row element only — TreeView's own selection / :hover
        // visuals live on the wrapper above us, so we don't interfere
        // with them. Use StyleKeyword.Null to release the override
        // when this row isn't the previewed one.
        void BindRowPreviewOverlay(VisualElement element, int id)
        {
            element.style.backgroundColor =
                id == _previewHoverRowId
                    ? new StyleColor(_previewHoverColor)
                    : new StyleColor(StyleKeyword.Null);
        }

        // ----- Row context menu ---------------------------------------------

        // Wraps a value with double quotes if it contains whitespace so
        // it round-trips through TokenizeSearch as a single token. Also
        // handles empty-string defensively.
        static string QuoteForSearch(string s)
        {
            if (string.IsNullOrEmpty(s))
                return "\"\"";
            for (int i = 0; i < s.Length; i++)
            {
                if (s[i] == ' ' || s[i] == '\t')
                {
                    return "\"" + s + "\"";
                }
            }
            return s;
        }

        // Sets the search field via the regular value-change path so
        // OnSearchChanged records the query in history and triggers a
        // rebuild. Used by every "Filter to X" / "Find usages" menu item.
        void SetSearchFromMenu(string query)
        {
            if (_searchField == null)
                return;
            _searchField.value = query;
        }

        void BuildRowContextMenu(ContextualMenuPopulateEvent evt)
        {
            if (evt.target is not VisualElement el)
                return;
            if (el.userData is not int rowId)
                return;
            if (!_dataById.TryGetValue(rowId, out var data))
                return;
            var name = data.DisplayName ?? string.Empty;
            var quoted = QuoteForSearch(name);

            evt.menu.AppendAction(
                "Copy Name",
                _ =>
                {
                    EditorGUIUtility.systemCopyBuffer = name;
                }
            );

            switch (data.Kind)
            {
                case RowKind.Template:
                case RowKind.AbstractTemplate:
                    evt.menu.AppendSeparator();
                    evt.menu.AppendAction(
                        "Find Entities Of This Template",
                        _ => SetSearchFromMenu($"t:e template:{quoted}")
                    );
                    evt.menu.AppendAction(
                        "Find Templates Derived From This",
                        _ => SetSearchFromMenu($"t:t base:{quoted}")
                    );
                    evt.menu.AppendAction(
                        "Find Templates This Derives From",
                        _ => SetSearchFromMenu($"t:t derived:{quoted}")
                    );
                    break;

                case RowKind.ComponentType:
                    evt.menu.AppendSeparator();
                    evt.menu.AppendAction(
                        "Find Anything With This Component",
                        _ => SetSearchFromMenu($"c:{quoted}")
                    );
                    evt.menu.AppendAction(
                        "Find Templates With This Component",
                        _ => SetSearchFromMenu($"t:t c:{quoted}")
                    );
                    evt.menu.AppendAction(
                        "Find Entities With This Component",
                        _ => SetSearchFromMenu($"t:e c:{quoted}")
                    );
                    evt.menu.AppendAction(
                        "Find Accessors That Read or Write This",
                        _ => SetSearchFromMenu($"t:a accesses:{quoted}")
                    );
                    evt.menu.AppendAction(
                        "Find Accessors That Read This",
                        _ => SetSearchFromMenu($"t:a reads:{quoted}")
                    );
                    evt.menu.AppendAction(
                        "Find Accessors That Write This",
                        _ => SetSearchFromMenu($"t:a writes:{quoted}")
                    );
                    break;

                case RowKind.TagItem:
                    evt.menu.AppendSeparator();
                    evt.menu.AppendAction(
                        "Find Anything With This Tag",
                        _ => SetSearchFromMenu($"tag:{quoted}")
                    );
                    evt.menu.AppendAction(
                        "Find Templates With This Tag",
                        _ => SetSearchFromMenu($"t:t tag:{quoted}")
                    );
                    evt.menu.AppendAction(
                        "Find Entities With This Tag",
                        _ => SetSearchFromMenu($"t:e tag:{quoted}")
                    );
                    evt.menu.AppendAction(
                        "Find Sets With This Tag",
                        _ => SetSearchFromMenu($"t:s tag:{quoted}")
                    );
                    break;

                case RowKind.SetItem:
                    evt.menu.AppendSeparator();
                    evt.menu.AppendAction(
                        "Find Sets With Same Name",
                        _ => SetSearchFromMenu($"t:s {quoted}")
                    );
                    break;

                case RowKind.Entity:
                    evt.menu.AppendSeparator();
                    evt.menu.AppendAction(
                        "Copy Entity Id",
                        _ =>
                            EditorGUIUtility.systemCopyBuffer =
                                data.EntityHandle.UniqueId.ToString()
                    );
                    break;

                case RowKind.Accessor:
                    // No useful filter we can derive from just the row name
                    // — accessor cross-links live in the inspector.
                    break;
            }
        }

        // ----- Selection -----------------------------------------------------

        void OnTreeSelectionChanged(IEnumerable<object> selected)
        {
            if (_suppressTreeSelectionFeedback)
            {
                return;
            }

            // In cache mode rows aren't backed by a live World, but each
            // proxy now has a SetCache path that drives the inspector from
            // the schema. Route the click through there instead of the
            // live setters below.
            if (_cacheMode)
            {
                RouteCacheModeSelection(selected);
                return;
            }

            // Count what we got. Multi-select routes to a separate proxy
            // whose inspector explains we don't yet support multi-edit;
            // empty selection leaves Selection.activeObject alone so the
            // user doesn't lose unrelated context.
            var rowDatas = new List<RowData>();
            foreach (var obj in selected)
            {
                if (obj is RowData rd)
                {
                    rowDatas.Add(rd);
                }
            }

            if (rowDatas.Count == 0)
            {
                // User cleared the tree selection (e.g. ctrl+click on the
                // single selected row). If Selection.activeObject is still
                // one of our proxies, the next refresh tick would resolve
                // it back to a tree id and re-apply the highlight. Clear it
                // so deselection sticks.
                if (IsTrecsSelection(Selection.activeObject))
                {
                    Selection.activeObject = null;
                }
                _lastReflectedSelectionId = -1;
                SessionState.SetString(SelectedRowIdentitySessionKey, string.Empty);
                return;
            }

            // The user just clicked a row in the tree, so the row is already
            // where they want it. Suppress the auto-scroll that
            // OnUnitySelectionChanged would otherwise apply when
            // Selection.activeObject changes to the new proxy instance.
            ArmScrollSuppression();

            if (rowDatas.Count > 1)
            {
                var p = TrecsSelectionProxies.MultiSelect;
                p.Count = rowDatas.Count;
                Selection.activeObject = p;
                return;
            }

            var data = rowDatas[0];
            var proxy = TrecsSelectionProxies.CreateProxy(_selectedWorld, data);
            if (proxy != null)
            {
                Selection.activeObject = proxy;
            }
            // Section / Group / AccessorPhase / MorePlaceholder return null
            // (no proxy). Identity still gets saved so the next RebuildTree
            // can restore the row's selection.
            SaveSelectionIdentity(data);
        }

        void RouteCacheModeSelection(IEnumerable<object> selected)
        {
            RowData first = null;
            foreach (var obj in selected)
            {
                if (obj is RowData rd)
                {
                    first = rd;
                    break;
                }
            }
            if (first == null)
            {
                return;
            }
            ArmScrollSuppression();
            var proxy = TrecsSelectionProxies.CreateProxy(world: null, first);
            if (proxy != null)
            {
                Selection.activeObject = proxy;
            }
            SaveSelectionIdentity(first);
        }

        // True when the active selection is one of the Trecs hierarchy proxy
        // kinds, regardless of which pool slot. Used to scope behaviors that
        // should fire only while the user is "inside" the hierarchy (e.g.
        // clearing selection on window close).
        static bool IsTrecsSelection(Object obj) =>
            obj is TrecsSelectionProxy || obj is TrecsMultiSelection;

        // ----- Preview hover (driven by inspector link MouseEnter/Leave) ----

        // Single identity-based preview handler. The link factories and
        // the inspector-title hover wires now all fire PreviewRequested
        // with the row's stable-key identity (e.g. "tag:Player"); a row
        // built in this rebuild has the same key in _idByKey.
        void OnPreviewByIdentity(string identity)
        {
            if (string.IsNullOrEmpty(identity))
            {
                SetPreviewRowId(-1);
                return;
            }
            if (_idByKey.TryGetValue(identity, out var id))
            {
                SetPreviewRowId(id);
            }
        }

        void OnPreviewEntity(World world, EntityHandle handle)
        {
            if (world != _selectedWorld)
            {
                SetPreviewRowId(-1);
                return;
            }
            if (_entityIds.TryGetValue(handle, out var id))
            {
                SetPreviewRowId(id);
            }
        }

        void OnPreviewClear() => SetPreviewRowId(-1);

        void SetPreviewRowId(int id)
        {
            if (_tree == null)
            {
                return;
            }

            // If the user is hovering an inspector header for the row that
            // is already the active selection, scroll to it but skip the
            // overlay: TreeView's selection-blue is more prominent than
            // our preview-gray and combining them looks muddy. Scroll
            // alone is exactly the "take me back to my selection" affordance
            // the user asked for.
            bool sameAsSelection = id >= 0 && IsTreeSelectionExactly(id);
            if (sameAsSelection)
            {
                ExpandAncestors(id);
                var capturedSel = id;
                _tree.schedule.Execute(() => ScrollToItemCentered(capturedSel));
                return;
            }

            if (_previewHoverRowId == id)
            {
                return;
            }
            int oldId = _previewHoverRowId;
            _previewHoverRowId = id;

            // Re-bind both the row losing the highlight and the row gaining
            // it. RefreshItem(int) is a single-row rebind so other rows'
            // :hover state isn't disturbed.
            if (oldId >= 0 && _dataById.ContainsKey(oldId))
            {
                try
                {
                    _tree.RefreshItem(oldId);
                }
                catch
                { /* id may not be visible/known */
                }
            }
            if (id >= 0 && _dataById.ContainsKey(id))
            {
                ExpandAncestors(id);
                // Defer scroll one tick so any expand we just did has
                // completed layout — same pattern as UpdateRowSelectionFromUnity.
                var captured = id;
                _tree.schedule.Execute(() =>
                {
                    ScrollToItemCentered(captured);
                    try
                    {
                        _tree.RefreshItem(captured);
                    }
                    catch
                    { /* ignore */
                    }
                });
            }
        }

        void OnUnitySelectionChanged()
        {
            // If the selection change came from our own tree-row click, the
            // row is already where the user clicked; centring it here would
            // jump the scroll. The flag is one-shot.
            bool consumed = _suppressNextSelectionScroll;
            _suppressNextSelectionScroll = false;
            RecordSelectionHistory();
            UpdateRowSelectionFromUnity(scrollToItem: !consumed);
        }

        // Appends Selection.activeObject to the history if it's a Trecs
        // proxy and not already at the current cursor position. Browser-
        // style truncation: making a fresh selection while sitting on an
        // older history entry drops the forward portion.
        void RecordSelectionHistory()
        {
            if (_navigatingSelectionHistory)
                return;
            var sel = Selection.activeObject;
            if (sel == null || !IsTrecsSelection(sel))
                return;
            if (
                _selectionHistoryIndex >= 0
                && _selectionHistoryIndex < _selectionHistory.Count
                && _selectionHistory[_selectionHistoryIndex] == sel
            )
            {
                return;
            }
            // Drop forward history (anything after the current cursor) —
            // the user has branched off in a new direction.
            int forwardStart = _selectionHistoryIndex + 1;
            if (forwardStart < _selectionHistory.Count)
            {
                _selectionHistory.RemoveRange(forwardStart, _selectionHistory.Count - forwardStart);
            }
            _selectionHistory.Add(sel);
            _selectionHistoryIndex = _selectionHistory.Count - 1;
            // Cap from the front; shift the cursor to compensate so it
            // still points at the same logical entry.
            if (_selectionHistory.Count > SelectionHistoryMax)
            {
                int excess = _selectionHistory.Count - SelectionHistoryMax;
                _selectionHistory.RemoveRange(0, excess);
                _selectionHistoryIndex -= excess;
            }
        }

        // Returns true if navigation actually moved (so the key handler
        // can stop the event). Skips entries whose target SO has been
        // destroyed (Unity's `==` overload returns true for those).
        bool NavigateSelectionHistory(int delta)
        {
            int idx = _selectionHistoryIndex + delta;
            while (idx >= 0 && idx < _selectionHistory.Count && _selectionHistory[idx] == null)
            {
                idx += delta;
            }
            if (idx < 0 || idx >= _selectionHistory.Count)
                return false;
            var target = _selectionHistory[idx];
            _navigatingSelectionHistory = true;
            try
            {
                _selectionHistoryIndex = idx;
                Selection.activeObject = target;
            }
            finally
            {
                _navigatingSelectionHistory = false;
            }
            return true;
        }

        void ArmScrollSuppression()
        {
            _suppressNextSelectionScroll = true;
            // Safety net: drop the flag after the current event cycle so
            // it can't leak into a later external selection change. Each
            // call captures its own token so a stale delayCall (from an
            // earlier arm whose selectionChanged already consumed the
            // flag) doesn't clear the flag set by a subsequent arm.
            int myToken = ++_suppressionToken;
            EditorApplication.delayCall += () =>
            {
                if (myToken == _suppressionToken)
                {
                    _suppressNextSelectionScroll = false;
                }
            };
        }

        void UpdateRowSelectionFromUnity(bool scrollToItem)
        {
            if (_tree == null)
            {
                return;
            }
            var sel = Selection.activeObject;
            int id = -1;

            if (_cacheMode)
            {
                id = ResolveCacheModeRowId(sel);
                if (id <= 0)
                {
                    _suppressTreeSelectionFeedback = true;
                    try
                    {
                        _tree.ClearSelection();
                    }
                    finally
                    {
                        _suppressTreeSelectionFeedback = false;
                    }
                    _lastReflectedSelectionId = -1;
                    return;
                }
            }
            else if (sel is TrecsEntitySelection es && es.GetWorld() == _selectedWorld)
            {
                _entityIds.TryGetValue(es.Handle, out id);
            }
            else if (sel is TrecsTemplateSelection ts && !string.IsNullOrEmpty(ts.Identity))
            {
                if (_idByKey.TryGetValue(ts.Identity, out var resolved))
                {
                    id = resolved;
                }
            }
            else if (sel is TrecsAccessorSelection acs && !string.IsNullOrEmpty(acs.Identity))
            {
                if (_idByKey.TryGetValue(acs.Identity, out var resolved))
                {
                    id = resolved;
                }
            }
            else if (sel is TrecsComponentTypeSelection cs && !string.IsNullOrEmpty(cs.Identity))
            {
                if (_idByKey.TryGetValue(cs.Identity, out var resolved))
                {
                    id = resolved;
                }
            }
            else if (sel is TrecsSetSelection ss && !string.IsNullOrEmpty(ss.Identity))
            {
                if (_idByKey.TryGetValue(ss.Identity, out var resolved))
                {
                    id = resolved;
                }
            }
            else if (sel is TrecsTagSelection tgs && !string.IsNullOrEmpty(tgs.Identity))
            {
                if (_idByKey.TryGetValue(tgs.Identity, out var resolved))
                {
                    id = resolved;
                }
            }
            else
            {
                _suppressTreeSelectionFeedback = true;
                try
                {
                    _tree.ClearSelection();
                }
                finally
                {
                    _suppressTreeSelectionFeedback = false;
                }
                _lastReflectedSelectionId = -1;
                return;
            }

            if (id <= 0 || !_dataById.ContainsKey(id))
            {
                return;
            }

            // Bookkeeping: remember the resolved id so duplicate
            // selectionChanged fires don't re-scroll on the user-action path.
            _lastReflectedSelectionId = id;

            // The rebuild path (scrollToItem == false) just refreshes
            // bookkeeping. TreeView preserves selectedIds across
            // SetRootItems when our stable ids match, so re-asserting the
            // selection here is unnecessary and was triggering the
            // tree's auto-keep-selected-visible behavior — which fought
            // the user's manual scroll position every refresh tick.
            // On the user-action path we still ensure selection + scroll.
            if (!scrollToItem)
            {
                return;
            }

            ExpandAncestors(id);
            if (!IsTreeSelectionExactly(id))
            {
                _suppressTreeSelectionFeedback = true;
                try
                {
                    _tree.SetSelectionById(new[] { id });
                }
                finally
                {
                    _suppressTreeSelectionFeedback = false;
                }
            }

            // Defer one tick so any foldout we just expanded has gone
            // through a layout pass; otherwise the row index → Y mapping
            // computes against pre-expand geometry.
            var capturedId = id;
            _tree.schedule.Execute(() => ScrollToItemCentered(capturedId));
        }

        // Resolves the active selection proxy back to a tree row id while
        // we're rendering from a TrecsSchema rather than a live world. The
        // proxies' live fields (Template, ComponentType, etc.) are null in
        // cache mode — only the CacheXxx fields are populated — so this
        // mirrors UpdateRowSelectionFromUnity's switch but reads
        // .DebugName / .DisplayName off the cache entry to look up the
        // unified name-keyed dictionaries.
        int ResolveCacheModeRowId(Object sel)
        {
            int id = -1;
            if (sel is TrecsTemplateSelection ts && !string.IsNullOrEmpty(ts.Identity))
            {
                if (_idByKey.TryGetValue(ts.Identity, out var resolved))
                {
                    id = resolved;
                }
            }
            else if (sel is TrecsComponentTypeSelection cs && !string.IsNullOrEmpty(cs.Identity))
            {
                if (_idByKey.TryGetValue(cs.Identity, out var resolved))
                {
                    id = resolved;
                }
            }
            else if (sel is TrecsAccessorSelection acs && !string.IsNullOrEmpty(acs.Identity))
            {
                if (_idByKey.TryGetValue(acs.Identity, out var resolved))
                {
                    id = resolved;
                }
            }
            else if (sel is TrecsSetSelection ss && !string.IsNullOrEmpty(ss.Identity))
            {
                if (_idByKey.TryGetValue(ss.Identity, out var resolved))
                {
                    id = resolved;
                }
            }
            else if (sel is TrecsTagSelection tgs && !string.IsNullOrEmpty(tgs.Identity))
            {
                if (_idByKey.TryGetValue(tgs.Identity, out var resolved))
                {
                    id = resolved;
                }
            }
            return id;
        }

        // Scroll the row vertically centered in the viewport rather than
        // just barely-visible (which is what TreeView's built-in
        // ScrollToItemById does — it aligns to the nearest edge). With a
        // fixed item height we can compute the offset directly from the
        // row's flat-tree index; clamps to the valid scroll range so rows
        // near the top/bottom degrade gracefully.
        // Renders the row text with every non-negated bare search
        // substring wrapped in a bold yellow rich-text span. Bypasses
        // rich text for generic-type display names (which contain
        // literal "<>") so the parser doesn't mangle them; those rows
        // still show, just without a highlight band.
        void ApplyLabelTextWithSearchHighlight(Label label, string displayName)
        {
            // Fast path: no positive bare tokens → just stamp the text.
            // BindTreeItem calls this for every visible row on every
            // refresh, so skipping the IndexOf and rich-text toggle when
            // there's nothing to highlight is worth a branch.
            var bare = _searchFilter.BareSubstrings;
            bool anyPositive = false;
            for (int i = 0; i < bare.Count; i++)
            {
                if (!bare[i].Negate && !string.IsNullOrEmpty(bare[i].Substring))
                {
                    anyPositive = true;
                    break;
                }
            }
            if (!anyPositive)
            {
                label.text = displayName;
                return;
            }
            // Generic-type display names contain literal "<>" — bypass
            // rich text so Unity's parser doesn't mangle them. Those
            // rows still render, just without a highlight band.
            if (displayName.IndexOf('<') >= 0)
            {
                label.enableRichText = false;
                label.text = displayName;
                return;
            }

            // Collect every match span across all positive substrings,
            // then merge overlapping/adjacent spans so the rich-text
            // wrapping doesn't nest <color> tags or split the same
            // character across multiple spans.
            var spans = new List<(int start, int end)>();
            for (int i = 0; i < bare.Count; i++)
            {
                var (sub, neg) = bare[i];
                if (neg || string.IsNullOrEmpty(sub))
                    continue;
                var cmp = ComparisonForToken(sub);
                int from = 0;
                while (from <= displayName.Length - sub.Length)
                {
                    int idx = displayName.IndexOf(sub, from, cmp);
                    if (idx < 0)
                        break;
                    spans.Add((idx, idx + sub.Length));
                    from = idx + sub.Length;
                }
            }
            if (spans.Count == 0)
            {
                label.enableRichText = false;
                label.text = displayName;
                return;
            }
            spans.Sort((a, b) => a.start.CompareTo(b.start));
            var merged = new List<(int start, int end)> { spans[0] };
            for (int i = 1; i < spans.Count; i++)
            {
                var top = merged[merged.Count - 1];
                var s = spans[i];
                if (s.start <= top.end)
                {
                    merged[merged.Count - 1] = (top.start, Math.Max(top.end, s.end));
                }
                else
                {
                    merged.Add(s);
                }
            }

            label.enableRichText = true;
            var sb = new StringBuilder(displayName.Length + merged.Count * 24);
            int cursor = 0;
            foreach (var (start, end) in merged)
            {
                if (start > cursor)
                    sb.Append(displayName, cursor, start - cursor);
                sb.Append("<color=#FFD24A><b>");
                sb.Append(displayName, start, end - start);
                sb.Append("</b></color>");
                cursor = end;
            }
            if (cursor < displayName.Length)
                sb.Append(displayName, cursor, displayName.Length - cursor);
            label.text = sb.ToString();
        }

        void ScrollToItemCentered(int id)
        {
            // BaseListView.scrollView is internal in this Unity version, so
            // grab the underlying ScrollView via UQuery once and cache it.
            var sv = _treeScrollViewCache ??= _tree?.Q<ScrollView>();
            if (sv == null)
            {
                return;
            }
            int index;
            try
            {
                index = _tree.viewController.GetIndexForId(id);
            }
            catch
            {
                return;
            }
            if (index < 0)
            {
                return;
            }
            float itemHeight = _tree.fixedItemHeight;
            if (itemHeight <= 0)
            {
                itemHeight = 18;
            }
            float rowCenter = (index + 0.5f) * itemHeight;
            float viewportHeight = sv.contentViewport.resolvedStyle.height;
            float contentHeight = sv.contentContainer.resolvedStyle.height;
            float maxY = Mathf.Max(0f, contentHeight - viewportHeight);
            float targetY = Mathf.Clamp(rowCenter - viewportHeight * 0.5f, 0f, maxY);
            sv.scrollOffset = new Vector2(sv.scrollOffset.x, targetY);
        }

        bool IsTreeSelectionExactly(int id)
        {
            int count = 0;
            foreach (var sid in _tree.selectedIds)
            {
                if (sid != id)
                {
                    return false;
                }
                count++;
            }
            return count == 1;
        }

        void ExpandAncestors(int id)
        {
            while (_parentById.TryGetValue(id, out var parentId))
            {
                _tree.ExpandItem(parentId);
                id = parentId;
            }
        }

        // ----- Helpers --------------------------------------------------------

        int GetOrAssignId(object key)
        {
            if (_idByKey.TryGetValue(key, out var id))
            {
                return id;
            }
            id = _nextId++;
            _idByKey[key] = id;
            var sk = TryGetStableKey(key);
            if (sk != null)
            {
                _stableKeyById[id] = sk;
            }
            return id;
        }

        // The "more not shown" placeholder rows aren't keyed (they're not
        // selectable and we never need to find them later), so allocate a
        // fresh unkeyed id each rebuild.
        int AllocateAnonId() => _nextId++;

        // Cache-mode rows bypass GetOrAssignId (they use raw _nextId++ +
        // _idByKey.Clear() each rebuild) but we still want their expand
        // state to survive domain reloads. Pair the fresh id with a
        // hand-built stable key from the schema-entry fields.
        int AllocateIdWithStableKey(string stableKey)
        {
            int id = _nextId++;
            if (stableKey != null)
            {
                _stableKeyById[id] = stableKey;
            }
            return id;
        }

        // Maps an _idByKey key into a deterministic short string that
        // survives domain reload (TreeView ids do not). Returns null for
        // shapes we don't recognize — those rows simply won't have their
        // expansion persisted, which is the same behavior as before this
        // tracking existed.
        string TryGetStableKey(object key)
        {
            switch (key)
            {
                case string s:
                    // Pre-built stable key (templates, after the live/cache
                    // unification that name-keys both modes through the
                    // same _idByKey entry).
                    return s;
                case Template t:
                    return TrecsRowIdentity.TemplatePrefix + (t.DebugName ?? "");
                case EntityHandle eh:
                    return TrecsRowIdentity.EntityPrefix + eh.UniqueId + ":" + eh.Version;
                case ValueTuple<Template, GroupIndex> tg:
                    return "group:"
                        + (tg.Item1?.DebugName ?? "")
                        + ":"
                        + (tg.Item2.IsNull ? -1 : tg.Item2.Index);
                default:
                    return null;
            }
        }

        // The five hardcoded section ids never flow through GetOrAssignId,
        // so they need to be seeded into _stableKeyById manually after each
        // clear. Called from RebuildTree.
        void SeedSectionStableKeys()
        {
            _stableKeyById[SectionTemplatesId] = "section:Templates";
            _stableKeyById[SectionAccessorsId] = "section:Accessors";
            _stableKeyById[SectionComponentsId] = "section:Components";
            _stableKeyById[SectionSetsId] = "section:Sets";
            _stableKeyById[SectionTagsId] = "section:Tags";
        }

        // Builds a name-based identity for a selectable row. Distinct from
        // _stableKeyById (which uses live-world ids for accessors and
        // entity handles), because selection needs to survive world
        // transitions and the new world will have different ids. Returns
        // null for header rows (Section / AccessorPhase / Group /
        // MorePlaceholder) and unselectable kinds.
        string TryGetSelectionIdentity(RowData data)
        {
            if (data == null)
            {
                return null;
            }
            switch (data.Kind)
            {
                case RowKind.Template:
                case RowKind.AbstractTemplate:
                    return data.StableKey;
                case RowKind.Entity:
                    // Entity selection across world transitions is
                    // best-effort: UniqueId+Version are stable within a
                    // world but not across instances. The match will
                    // typically miss after play-mode entry, which is the
                    // honest answer (the editor-mode entity isn't in the
                    // play-mode world).
                    if (data.EntityHandle.UniqueId != 0)
                    {
                        return TrecsRowIdentity.EntityPrefix
                            + data.EntityHandle.UniqueId
                            + ":"
                            + data.EntityHandle.Version;
                    }
                    return null;
                case RowKind.Accessor:
                case RowKind.ComponentType:
                case RowKind.SetItem:
                case RowKind.TagItem:
                    return data.StableKey;
                default:
                    return null;
            }
        }

        // Persists the currently-selected row's identity (or clears it
        // when there is no Trecs selection). Called after every
        // user-initiated selection change so the most recent intent is
        // always in SessionState.
        void SaveSelectionIdentity(RowData data)
        {
            var ident = TryGetSelectionIdentity(data);
            SessionState.SetString(SelectedRowIdentitySessionKey, ident ?? string.Empty);
        }

        // After a tree rebuild (live or cache), if Selection.activeObject
        // doesn't match a row in the current tree, attempt to restore by
        // looking up the persisted identity and binding a fresh proxy to
        // the new world. No-op when the existing selection is already
        // valid for the current world (i.e. the user navigated within
        // the same world).
        void TryRestoreSelectionFromIdentity()
        {
            if (_tree == null || _dataById.Count == 0)
            {
                return;
            }
            if (IsCurrentSelectionStillValid())
            {
                return;
            }

            var key = SessionState.GetString(SelectedRowIdentitySessionKey, string.Empty);
            if (string.IsNullOrEmpty(key))
            {
                return;
            }

            foreach (var kv in _dataById)
            {
                var ident = TryGetSelectionIdentity(kv.Value);
                if (ident == null || ident != key)
                {
                    continue;
                }
                ApplySelectionFromRow(kv.Key, kv.Value, scrollToItem: false);
                return;
            }
        }

        // True when Selection.activeObject is one of our proxies bound
        // to the current world / cache schema. Used to short-circuit
        // restoration so we don't repeatedly create fresh proxy
        // instances on every routine refresh. Identity is the proxy's
        // serialized payload — non-empty means the proxy survived
        // domain reload and points at a row we can render against the
        // current source. Empty means the proxy is gutted and the
        // identity-restore path needs to mint a fresh one.
        bool IsCurrentSelectionStillValid()
        {
            var sel = Selection.activeObject;
            if (sel == null)
            {
                return false;
            }
            if (_cacheMode)
            {
                return sel switch
                {
                    // Identity-based: a non-empty Identity is enough; the
                    // inspector resolves dynamically against ActiveSource
                    // and shows a "not found" status when stale.
                    TrecsTemplateSelection ts => !string.IsNullOrEmpty(ts.Identity),
                    TrecsComponentTypeSelection cs => !string.IsNullOrEmpty(cs.Identity),
                    TrecsAccessorSelection acs => !string.IsNullOrEmpty(acs.Identity),
                    TrecsSetSelection ss => !string.IsNullOrEmpty(ss.Identity),
                    TrecsTagSelection tgs => !string.IsNullOrEmpty(tgs.Identity),
                    TrecsMultiSelection => true,
                    _ => false,
                };
            }
            if (_selectedWorld == null)
            {
                return false;
            }
            return sel switch
            {
                TrecsTemplateSelection ts => !string.IsNullOrEmpty(ts.Identity),
                TrecsEntitySelection es => es.GetWorld() == _selectedWorld
                    && es.Handle.UniqueId != 0,
                TrecsAccessorSelection acs => !string.IsNullOrEmpty(acs.Identity),
                TrecsComponentTypeSelection cs => !string.IsNullOrEmpty(cs.Identity),
                TrecsSetSelection ss => !string.IsNullOrEmpty(ss.Identity),
                TrecsTagSelection tgs => !string.IsNullOrEmpty(tgs.Identity),
                TrecsMultiSelection => true,
                _ => false,
            };
        }

        // Mirrors OnTreeSelectionChanged's single-row switch but works
        // for both live and cache modes, and is callable from the
        // selection-restore path (where no UI click occurred). Sets
        // both the tree's selection highlight and Selection.activeObject
        // to a fresh proxy bound to the current world / schema.
        void ApplySelectionFromRow(int rowId, RowData data, bool scrollToItem)
        {
            if (data == null)
            {
                return;
            }
            _suppressTreeSelectionFeedback = true;
            try
            {
                _tree.SetSelectionById(new[] { rowId });
            }
            finally
            {
                _suppressTreeSelectionFeedback = false;
            }
            _lastReflectedSelectionId = rowId;

            var proxy = TrecsSelectionProxies.CreateProxy(_cacheMode ? null : _selectedWorld, data);
            if (proxy != null)
            {
                Selection.activeObject = proxy;
            }

            if (scrollToItem)
            {
                ExpandAncestors(rowId);
                var captured = rowId;
                _tree.schedule.Execute(() => ScrollToItemCentered(captured));
            }
        }

        // Drop key→id entries whose id no longer appears in _dataById. Called
        // at the end of each structural rebuild. Without it, every entity /
        // template / accessor we've ever seen sticks around in _idByKey for
        // the editor session. _stableKeyById is pruned in lockstep so the
        // two maps stay consistent.
        void PruneIdByKey()
        {
            List<object> toRemove = null;
            foreach (var kv in _idByKey)
            {
                if (!_dataById.ContainsKey(kv.Value))
                {
                    (toRemove ??= new List<object>()).Add(kv.Key);
                }
            }
            if (toRemove != null)
            {
                foreach (var k in toRemove)
                {
                    var staleId = _idByKey[k];
                    _idByKey.Remove(k);
                    _stableKeyById.Remove(staleId);
                }
            }

            List<int> staleStableIds = null;
            foreach (var kv in _stableKeyById)
            {
                if (!_dataById.ContainsKey(kv.Key))
                {
                    (staleStableIds ??= new List<int>()).Add(kv.Key);
                }
            }
            if (staleStableIds != null)
            {
                foreach (var id in staleStableIds)
                {
                    _stableKeyById.Remove(id);
                }
            }
            // Re-seed sections in case a prune dropped them (they're not
            // in _idByKey, so the loop above wouldn't have removed them,
            // but the second loop will if _dataById is currently empty).
            if (_dataById.ContainsKey(SectionTemplatesId))
            {
                SeedSectionStableKeys();
            }
        }

        static object MakeGroupKey(ResolvedTemplate rt, GroupIndex group) => (rt.Template, group);

        static string FormatPartitionLabel(GroupIndex group, TagSet partitionTags)
        {
            if (partitionTags.Tags == null || partitionTags.Tags.Count == 0)
            {
                return $"Group {group}";
            }
            var sb = new StringBuilder();
            sb.Append("Partition [");
            var first = true;
            foreach (var tag in partitionTags.Tags)
            {
                if (!first)
                {
                    sb.Append(", ");
                }
                first = false;
                sb.Append(tag.ToString());
            }
            sb.Append("]");
            return sb.ToString();
        }

        // For the Components section we render the raw C# type name so it's
        // unambiguous that the row is a *type* (not a property). For nested
        // types we prepend the declaring type chain so e.g.
        // SharedTemplates.WorldUiTasks shows as "SharedTemplates.WorldUiTasks".
        // Namespace is intentionally elided — the inspector body shows the
        // FullName below the header. Public so the component-type inspector
        // can match this header to what was clicked in the hierarchy.
        public static string ComponentTypeDisplayName(Type t)
        {
            if (t.DeclaringType == null)
            {
                return FormatTypeName(t);
            }
            var sb = new StringBuilder();
            var declaring = t.DeclaringType;
            while (declaring != null)
            {
                sb.Insert(0, FormatTypeName(declaring) + ".");
                declaring = declaring.DeclaringType;
            }
            sb.Append(FormatTypeName(t));
            return sb.ToString();
        }

        // Renders a single type's name with generic arguments expanded —
        // e.g. Interpolated<CFoo> instead of Unity's raw "Interpolated`1".
        // Recursive so nested generics like Pair<Foo, Bar<Baz>> render
        // correctly. Strips the namespace from each component to keep the
        // hierarchy / inspector headers compact.
        static string FormatTypeName(Type t)
        {
            if (!t.IsGenericType)
            {
                return t.Name;
            }
            var name = t.Name;
            var backtick = name.IndexOf('`');
            if (backtick >= 0)
            {
                name = name.Substring(0, backtick);
            }
            var args = t.GetGenericArguments();
            var sb = new StringBuilder();
            sb.Append(name);
            sb.Append('<');
            for (int i = 0; i < args.Length; i++)
            {
                if (i > 0)
                {
                    sb.Append(", ");
                }
                sb.Append(FormatTypeName(args[i]));
            }
            sb.Append('>');
            return sb.ToString();
        }

        // Dispatch table: returns true if at least one of the row's
        // resolved values for `key` contains `value` as a substring. False
        // if the predicate isn't defined for this kind (so the row gets
        // filtered) or no value matches.
        bool MatchesPredicate(SearchScope rowScope, string key, string value, in PredicateData ctx)
        {
            switch (rowScope)
            {
                case SearchScope.Templates:
                    return MatchesTemplatePredicate(key, value, in ctx);
                case SearchScope.Entities:
                    return MatchesEntityPredicate(key, value, in ctx);
                case SearchScope.Components:
                    return MatchesComponentPredicate(key, value, in ctx);
                case SearchScope.Sets:
                    return MatchesSetPredicate(key, value, in ctx);
                case SearchScope.Tags:
                    return MatchesTagPredicate(key, value, in ctx);
                case SearchScope.Accessors:
                    return MatchesAccessorPredicate(key, value, in ctx);
                default:
                    return false;
            }
        }

        bool MatchesTemplatePredicate(string key, string value, in PredicateData ctx)
        {
            var t = ctx.Template;
            if (t == null)
                return false;
            return key switch
            {
                "tag" => AnyContains(t.AllTagNames, value),
                "c" or "component" => AnyContains(t.ComponentTypeNames, value),
                "base" => AnyContains(t.BaseTemplateNames, value),
                "derived" => AnyContains(t.DerivedTemplateNames, value),
                _ => false,
            };
        }

        bool MatchesEntityPredicate(string key, string value, in PredicateData ctx)
        {
            // Entity rows reuse their parent template's data for tag/component
            // queries — entities don't carry per-instance tags or components,
            // they inherit from the resolved template they were spawned from.
            switch (key)
            {
                case "tag":
                case "c":
                case "component":
                    return MatchesTemplatePredicate(key, value, in ctx);
                case "template":
                    var n = ctx.Template?.DebugName;
                    return n != null && n.IndexOf(value, StringComparison.OrdinalIgnoreCase) >= 0;
                default:
                    return false;
            }
        }

        bool MatchesComponentPredicate(string key, string value, in PredicateData ctx)
        {
            // Components only answer "c:X" — matches when the row's own
            // display name contains X. Other predicates filter the row out
            // (a component isn't itself tagged, doesn't have a base, etc.).
            if (key != "c" && key != "component")
                return false;
            var name = ctx.ComponentType?.DisplayName;
            return name != null && name.IndexOf(value, StringComparison.OrdinalIgnoreCase) >= 0;
        }

        bool MatchesSetPredicate(string key, string value, in PredicateData ctx)
        {
            if (key != "tag" || ctx.Set == null)
                return false;
            return AnyContains(ctx.Set.TagNames, value);
        }

        bool MatchesTagPredicate(string key, string value, in PredicateData ctx)
        {
            // A tag "has" itself — so tag:player matches the tag named
            // player. Other predicates filter out (tags don't have
            // components/templates/etc.).
            if (key != "tag")
                return false;
            var name = ctx.Tag?.Name;
            return name != null && name.IndexOf(value, StringComparison.OrdinalIgnoreCase) >= 0;
        }

        bool MatchesAccessorPredicate(string key, string value, in PredicateData ctx)
        {
            if (string.IsNullOrEmpty(ctx.AccessorDebugName) || _source == null)
            {
                return false;
            }
            var tracker = _source.AccessTracker;
            if (tracker == null)
            {
                return false;
            }
            switch (key)
            {
                case "reads":
                    return AnyContains(tracker.GetComponentsReadBy(ctx.AccessorDebugName), value);
                case "writes":
                    return AnyContains(
                        tracker.GetComponentsWrittenBy(ctx.AccessorDebugName),
                        value
                    );
                case "accesses":
                    return AnyContains(tracker.GetComponentsReadBy(ctx.AccessorDebugName), value)
                        || AnyContains(
                            tracker.GetComponentsWrittenBy(ctx.AccessorDebugName),
                            value
                        );
                default:
                    return false;
            }
        }

        static bool AnyContains(IReadOnlyCollection<string> names, string value)
        {
            if (names == null || names.Count == 0)
                return false;
            foreach (var n in names)
            {
                if (n != null && n.IndexOf(value, StringComparison.OrdinalIgnoreCase) >= 0)
                {
                    return true;
                }
            }
            return false;
        }
    }
}
