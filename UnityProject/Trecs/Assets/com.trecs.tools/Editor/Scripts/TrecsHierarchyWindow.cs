using System;
using System.Collections.Generic;
using System.Linq;
using System.Text;
using Trecs;
using Trecs.Collections;
using Trecs.Internal;
using UnityEditor;
using UnityEditor.UIElements;
using UnityEngine;
using UnityEngine.UIElements;

namespace Trecs.Tools
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
        ToolbarSearchField _searchField;
        ToolbarButton _searchHelpButton;
        VisualElement _searchHelpPanel;
        Label _emptyState;
        Label _cacheBanner;
        bool _cacheMode;
        TrecsSchema _cachedSchema;
        readonly List<TrecsSchema> _cachedSchemas = new();
        TreeView _tree;

        World _selectedWorld;
        WorldAccessor _selectedAccessor;
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
        readonly List<UnityEngine.Object> _selectionHistory = new();
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

        // Snapshot of expanded ids taken when the user starts typing in the
        // search field. While search is active we override the natural
        // expand state (collapse all templates, expand all accessor phases)
        // and we want clearing the search to restore the user's previous
        // shape rather than leave them with our search-time mutations.
        HashSet<int> _preSearchExpandedIds;

        // First-rebuild gate for the root sections. After the initial rebuild
        // we let the prevExpanded restore loop handle expand state, so a user
        // collapsing a section stays collapsed.
        bool _initialSectionExpansionApplied;

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

        // Per-kind reverse lookup tables for cross-link reveal: jump from a
        // selection proxy back to the corresponding tree row id.
        readonly Dictionary<Template, int> _templateIds = new();
        readonly Dictionary<EntityHandle, int> _entityIds = new();
        readonly Dictionary<int, int> _accessorTreeIds = new();
        readonly Dictionary<Type, int> _componentTypeIds = new();
        readonly Dictionary<SetId, int> _setIds = new();
        readonly Dictionary<int, int> _tagIds = new();

        // Cache-mode counterparts. Keyed by the schema entry that the
        // selection proxy holds (or accessor display name for accessors,
        // since cache-mode accessors have no stable id).
        readonly Dictionary<TrecsSchemaTemplate, int> _templateCacheIds = new();
        readonly Dictionary<TrecsSchemaComponentType, int> _componentTypeCacheIds = new();
        readonly Dictionary<string, int> _accessorCacheIds = new();
        readonly Dictionary<TrecsSchemaSet, int> _setCacheIds = new();
        readonly Dictionary<TrecsSchemaTag, int> _tagCacheIds = new();

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

        static Texture _iconTemplate;
        static Texture _iconEntity;
        static Texture _iconFolder;
        static Texture _iconScript;
        static Texture _iconScriptable;

        [MenuItem("Svkj/Trecs/Windows/Hierarchy")]
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
            panel.style.marginTop = 4;
            panel.style.marginLeft = 8;
            panel.style.marginRight = 8;
            panel.style.marginBottom = 4;
            panel.style.paddingLeft = 8;
            panel.style.paddingRight = 8;
            panel.style.paddingTop = 6;
            panel.style.paddingBottom = 6;
            panel.style.borderTopLeftRadius = 3;
            panel.style.borderTopRightRadius = 3;
            panel.style.borderBottomLeftRadius = 3;
            panel.style.borderBottomRightRadius = 3;
            panel.style.backgroundColor = new Color(0.18f, 0.22f, 0.30f, 0.55f);

            var heading = new Label("Trecs Hierarchy — Help");
            heading.style.unityFontStyleAndWeight = FontStyle.Bold;
            heading.style.marginBottom = 4;
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
                    + "  writes:X    accessor writes component X\n\n"
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
            body.style.whiteSpace = WhiteSpace.Normal;
            body.style.fontSize = 11;
            body.style.opacity = 0.9f;
            panel.Add(body);

            var dismiss = new Button(ToggleSearchHelp) { text = "Close" };
            dismiss.style.alignSelf = Align.FlexEnd;
            dismiss.style.marginTop = 4;
            panel.Add(dismiss);

            return panel;
        }

        void OnEnable()
        {
            _showEmptyTemplates = EditorPrefs.GetBool(PrefShowEmptyTemplates, true);
            _showAbstractTemplates = EditorPrefs.GetBool(PrefShowAbstractTemplates, true);
            LoadSearchHistory();

            WorldRegistry.WorldRegistered += OnWorldRegistered;
            WorldRegistry.WorldUnregistered += OnWorldUnregistered;
            TrecsEditorSelection.ActiveWorldChanged += OnSharedActiveWorldChanged;
            Selection.selectionChanged += OnUnitySelectionChanged;
            TrecsInspectorLinks.PreviewTemplateRequested += OnPreviewTemplate;
            TrecsInspectorLinks.PreviewComponentTypeRequested += OnPreviewComponentType;
            TrecsInspectorLinks.PreviewAccessorRequested += OnPreviewAccessor;
            TrecsInspectorLinks.PreviewEntityRequested += OnPreviewEntity;
            TrecsInspectorLinks.PreviewSetRequested += OnPreviewSet;
            TrecsInspectorLinks.PreviewTagRequested += OnPreviewTag;
            TrecsInspectorLinks.PreviewClearRequested += OnPreviewClear;
            TrecsInspectorLinks.PreviewTemplateCacheRequested += OnPreviewTemplateCache;
            TrecsInspectorLinks.PreviewComponentTypeCacheRequested += OnPreviewComponentTypeCache;
            TrecsInspectorLinks.PreviewAccessorCacheRequested += OnPreviewAccessorCache;
            TrecsInspectorLinks.PreviewSetCacheRequested += OnPreviewSetCache;
            TrecsInspectorLinks.PreviewTagCacheRequested += OnPreviewTagCache;
            TrecsSchemaCache.SchemaSaved += OnSchemaSaved;
        }

        void OnDisable()
        {
            WorldRegistry.WorldRegistered -= OnWorldRegistered;
            WorldRegistry.WorldUnregistered -= OnWorldUnregistered;
            TrecsEditorSelection.ActiveWorldChanged -= OnSharedActiveWorldChanged;
            Selection.selectionChanged -= OnUnitySelectionChanged;
            TrecsInspectorLinks.PreviewTemplateRequested -= OnPreviewTemplate;
            TrecsInspectorLinks.PreviewComponentTypeRequested -= OnPreviewComponentType;
            TrecsInspectorLinks.PreviewAccessorRequested -= OnPreviewAccessor;
            TrecsInspectorLinks.PreviewEntityRequested -= OnPreviewEntity;
            TrecsInspectorLinks.PreviewSetRequested -= OnPreviewSet;
            TrecsInspectorLinks.PreviewTagRequested -= OnPreviewTag;
            TrecsInspectorLinks.PreviewClearRequested -= OnPreviewClear;
            TrecsInspectorLinks.PreviewTemplateCacheRequested -= OnPreviewTemplateCache;
            TrecsInspectorLinks.PreviewComponentTypeCacheRequested -= OnPreviewComponentTypeCache;
            TrecsInspectorLinks.PreviewAccessorCacheRequested -= OnPreviewAccessorCache;
            TrecsInspectorLinks.PreviewSetCacheRequested -= OnPreviewSetCache;
            TrecsInspectorLinks.PreviewTagCacheRequested -= OnPreviewTagCache;
            TrecsSchemaCache.SchemaSaved -= OnSchemaSaved;
            _selectedAccessor = null;

            // Don't clear Selection.activeObject here. Other Trecs editor
            // windows (Time Travel, Systems) drive the same proxy types,
            // so closing the hierarchy shouldn't kick the user's selection
            // out from under them. The proxies are session-scoped SOs;
            // they're safe to leave selected after this window closes.
        }

        void CreateGUI()
        {
            var root = rootVisualElement;
            root.style.flexGrow = 1;

            var toolbar = new Toolbar();
            root.Add(toolbar);

            _worldDropdown = new DropdownField(new List<string>(), 0);
            _worldDropdown.style.minWidth = 160;
            _worldDropdown.RegisterValueChangedCallback(OnWorldDropdownChanged);
            toolbar.Add(_worldDropdown);

            _searchField = new ToolbarSearchField();
            _searchField.style.flexGrow = 1;
            _searchField.style.marginLeft = 4;
            _searchField.tooltip =
                "Space-separated tokens AND together. 't:kind' restricts scope "
                + "(t:e, t:t, t:c, t:s, t:tag, t:a). 'key:value' adds a typed "
                + "predicate (tag:, c:, base:, derived:, template:, reads:, "
                + "writes:). Prefix '-' to negate; wrap a phrase in \"...\" for "
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
                _preSearchExpandedIds = CaptureExpandedIds();
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
                or "writes" => true,
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
            _cacheMode = true;
            _cachedSchema = schema;
            ShowCacheBanner(schema);
            RebuildTreeFromCache(schema);
            // The active selection proxy may still be holding refs to the
            // previous schema's entries (e.g. SchemaSaved fired while the
            // user was inspecting a cached row). Rebind it so the inspector
            // sees the fresh data without the user having to re-click.
            RebindActiveCacheProxy(schema);
        }

        // Walks the active Selection.activeObject and, if it's one of our
        // cache proxies, looks up its identity in the new schema by name
        // and re-issues SetCache. The inspector's identity check then sees
        // the new ref and rebuilds its rendering on the next refresh tick.
        static void RebindActiveCacheProxy(TrecsSchema schema)
        {
            var sel = Selection.activeObject;
            if (sel == null || schema == null)
                return;

            switch (sel)
            {
                case TrecsTemplateSelection tplSel when tplSel.CacheTemplate != null:
                {
                    var name = tplSel.CacheTemplate.DebugName;
                    foreach (var t in schema.Templates)
                    {
                        if (t.DebugName == name)
                        {
                            tplSel.SetCache(schema, t);
                            return;
                        }
                    }
                    break;
                }
                case TrecsComponentTypeSelection cmpSel when cmpSel.CacheComponent != null:
                {
                    var name = cmpSel.CacheComponent.DisplayName;
                    foreach (var c in schema.ComponentTypes)
                    {
                        if (c.DisplayName == name)
                        {
                            cmpSel.SetCache(schema, c);
                            return;
                        }
                    }
                    break;
                }
                case TrecsAccessorSelection accSel
                    when !string.IsNullOrEmpty(accSel.CacheAccessorName):
                {
                    accSel.SetCache(schema, accSel.CacheAccessorName);
                    return;
                }
                case TrecsSetSelection setSel when setSel.CacheSet != null:
                {
                    var name = setSel.CacheSet.DebugName;
                    foreach (var s in schema.Sets)
                    {
                        if (s.DebugName == name)
                        {
                            setSel.SetCache(schema, s);
                            return;
                        }
                    }
                    break;
                }
                case TrecsTagSelection tagSel when tagSel.CacheTag != null:
                {
                    var name = tagSel.CacheTag.Name;
                    foreach (var t in schema.Tags)
                    {
                        if (t.Name == name)
                        {
                            tagSel.SetCache(schema, t);
                            return;
                        }
                    }
                    break;
                }
            }
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
                _cacheBanner.style.display = DisplayStyle.None;
                _cacheMode = false;
                _cachedSchema = null;
                SelectWorld(null);
                return;
            }

            _worldDropdown.choices = labels;
            _cacheMode = false;
            _cachedSchema = null;
            _cacheBanner.style.display = DisplayStyle.None;
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

            // Different worlds use different ids. No need to share state.
            _idByKey.Clear();
            _dataById.Clear();
            _parentById.Clear();
            _templateIds.Clear();
            _entityIds.Clear();
            _accessorTreeIds.Clear();
            _componentTypeIds.Clear();
            _setIds.Clear();
            _tagIds.Clear();
            _templateCacheIds.Clear();
            _componentTypeCacheIds.Clear();
            _accessorCacheIds.Clear();
            _setCacheIds.Clear();
            _tagCacheIds.Clear();
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
                _selectedAccessor ??= _selectedWorld.CreateAccessor("TrecsHierarchyWindow");
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

        void RebuildTreeFromCache(TrecsSchema schema)
        {
            // Reset structural state. Cache rows use synthetic ids — no
            // overlap with the live-world id space (which starts at 100).
            _dataById.Clear();
            _parentById.Clear();
            _templateIds.Clear();
            _entityIds.Clear();
            _accessorTreeIds.Clear();
            _componentTypeIds.Clear();
            _setIds.Clear();
            _tagIds.Clear();
            _templateCacheIds.Clear();
            _componentTypeCacheIds.Clear();
            _accessorCacheIds.Clear();
            _setCacheIds.Clear();
            _tagCacheIds.Clear();
            _idByKey.Clear();
            _nextId = 100;

            var rootItems = new List<TreeViewItemData<RowData>>();

            // While search is active the tree morphs into a flat list of
            // matching content rows (mirrors RebuildTreeStructure). Per-
            // section build code runs unchanged so reverse-lookup tables
            // still get populated; only the root assembly differs.
            bool searchActive = !string.IsNullOrEmpty(_searchText);

            // Templates (resolved + abstract). No per-template entity counts
            // in cache mode; sort alphabetically.
            var tplData = new RowData
            {
                Kind = RowKind.Section,
                DisplayName = "Templates",
                Count = schema.Templates.Count,
                ShowCount = true,
            };
            _dataById[SectionTemplatesId] = tplData;
            var tplChildren = new List<TreeViewItemData<RowData>>();
            var sortedTpl = new List<TrecsSchemaTemplate>(schema.Templates);
            // Concrete (resolved) templates first, then abstract; ties broken
            // by name. Mirrors how the live tree groups them visually.
            sortedTpl.Sort(
                (a, b) =>
                {
                    if (a.IsResolved != b.IsResolved)
                    {
                        return a.IsResolved ? -1 : 1;
                    }
                    return string.Compare(
                        a.DebugName ?? "",
                        b.DebugName ?? "",
                        StringComparison.OrdinalIgnoreCase
                    );
                }
            );
            foreach (var st in sortedTpl)
            {
                var name = st.DebugName ?? "(unnamed)";
                var ctx = new PredicateData { CacheTemplate = st };
                if (!MatchesSearch(SearchScope.Templates, name, in ctx))
                {
                    continue;
                }
                int id = _nextId++;
                var data = new RowData
                {
                    Kind = st.IsResolved ? RowKind.Template : RowKind.AbstractTemplate,
                    DisplayName = name,
                    Count = 0,
                    ShowCount = false,
                    CacheTemplate = st,
                };
                _dataById[id] = data;
                _templateCacheIds[st] = id;
                tplChildren.Add(new TreeViewItemData<RowData>(id, data));
            }
            if (searchActive)
            {
                HarvestFlatLeaves(tplChildren, rootItems);
            }
            else
            {
                rootItems.Add(
                    new TreeViewItemData<RowData>(SectionTemplatesId, tplData, tplChildren)
                );
            }

            // Accessors / Systems by phase. Each system row carries its
            // priority for the phase-bracket badge in the live tree; in
            // cache mode we just show the name + phase grouping.
            var accData = new RowData
            {
                Kind = RowKind.Section,
                DisplayName = "Accessors",
                Count = schema.Systems.Count + schema.ManualAccessors.Count,
                ShowCount = true,
            };
            _dataById[SectionAccessorsId] = accData;
            var accChildren = new List<TreeViewItemData<RowData>>();
            var byPhase = new Dictionary<string, List<TrecsSchemaSystem>>();
            foreach (var s in schema.Systems)
            {
                if (!byPhase.TryGetValue(s.Phase ?? "", out var list))
                {
                    list = new List<TrecsSchemaSystem>();
                    byPhase[s.Phase ?? ""] = list;
                }
                list.Add(s);
            }
            foreach (var kv in byPhase)
            {
                int phaseId = _nextId++;
                var phaseRows = new List<TreeViewItemData<RowData>>();
                foreach (var s in kv.Value)
                {
                    var name = s.DebugName ?? s.TypeName ?? "(unnamed)";
                    var ctx = new PredicateData
                    {
                        AccessorDebugName = name,
                        CacheSchemaForAccess = schema,
                    };
                    if (!MatchesSearch(SearchScope.Accessors, name, in ctx))
                    {
                        continue;
                    }
                    int sysId = _nextId++;
                    var data = new RowData
                    {
                        Kind = RowKind.Accessor,
                        DisplayName = name,
                        SystemEnabled = true,
                        ExecutionPriority = s.HasPriority ? s.Priority : (int?)null,
                    };
                    _dataById[sysId] = data;
                    _accessorCacheIds[name] = sysId;
                    phaseRows.Add(new TreeViewItemData<RowData>(sysId, data));
                }
                if (phaseRows.Count == 0)
                {
                    continue;
                }
                var phaseData = new RowData
                {
                    Kind = RowKind.AccessorPhase,
                    DisplayName = string.IsNullOrEmpty(kv.Key) ? "(no phase)" : kv.Key,
                    Count = phaseRows.Count,
                    ShowCount = true,
                };
                _dataById[phaseId] = phaseData;
                accChildren.Add(new TreeViewItemData<RowData>(phaseId, phaseData, phaseRows));
            }
            // Manual accessors get their own "Other" phase folder so the
            // structure matches the live tree (where AddPhaseFromSpecs adds
            // an "Other" folder for accessors with no system phase).
            if (schema.ManualAccessors.Count > 0)
            {
                var manualRows = new List<TreeViewItemData<RowData>>();
                var sortedManual = new List<TrecsSchemaAccessor>(schema.ManualAccessors);
                sortedManual.Sort(
                    (a, b) =>
                        string.Compare(
                            a.DebugName ?? "",
                            b.DebugName ?? "",
                            StringComparison.OrdinalIgnoreCase
                        )
                );
                foreach (var m in sortedManual)
                {
                    var name = m.DebugName ?? "(unnamed)";
                    var ctx = new PredicateData
                    {
                        AccessorDebugName = name,
                        CacheSchemaForAccess = schema,
                    };
                    if (!MatchesSearch(SearchScope.Accessors, name, in ctx))
                    {
                        continue;
                    }
                    int mid = _nextId++;
                    var data = new RowData
                    {
                        Kind = RowKind.Accessor,
                        DisplayName = name,
                        SystemEnabled = true,
                    };
                    _dataById[mid] = data;
                    _accessorCacheIds[name] = mid;
                    manualRows.Add(new TreeViewItemData<RowData>(mid, data));
                }
                if (manualRows.Count > 0)
                {
                    int otherId = _nextId++;
                    var otherData = new RowData
                    {
                        Kind = RowKind.AccessorPhase,
                        DisplayName = "Other",
                        Count = manualRows.Count,
                        ShowCount = true,
                    };
                    _dataById[otherId] = otherData;
                    accChildren.Add(new TreeViewItemData<RowData>(otherId, otherData, manualRows));
                }
            }
            if (searchActive)
            {
                HarvestFlatLeaves(accChildren, rootItems);
            }
            else
            {
                rootItems.Add(
                    new TreeViewItemData<RowData>(SectionAccessorsId, accData, accChildren)
                );
            }

            // Components.
            var cmpData = new RowData
            {
                Kind = RowKind.Section,
                DisplayName = "Components",
                Count = schema.ComponentTypes.Count,
                ShowCount = true,
            };
            _dataById[SectionComponentsId] = cmpData;
            var cmpChildren = new List<TreeViewItemData<RowData>>();
            var sortedCmp = new List<TrecsSchemaComponentType>(schema.ComponentTypes);
            sortedCmp.Sort(
                (a, b) =>
                    string.Compare(
                        a.DisplayName ?? "",
                        b.DisplayName ?? "",
                        StringComparison.OrdinalIgnoreCase
                    )
            );
            // Index access info by display name for O(1) lookup per row.
            var accessByName = new Dictionary<string, TrecsSchemaAccessInfo>(schema.Access.Count);
            foreach (var a in schema.Access)
            {
                if (!string.IsNullOrEmpty(a.ComponentDisplayName))
                {
                    accessByName[a.ComponentDisplayName] = a;
                }
            }
            foreach (var c in sortedCmp)
            {
                var name = c.DisplayName ?? "(unnamed)";
                var ctx = new PredicateData { CacheComponent = c };
                if (!MatchesSearch(SearchScope.Components, name, in ctx))
                {
                    continue;
                }
                int id = _nextId++;
                var data = new RowData
                {
                    Kind = RowKind.ComponentType,
                    DisplayName = name,
                    CacheComponent = c,
                };
                if (accessByName.TryGetValue(name, out var access))
                {
                    data.ReadCount = access.ReadBySystems?.Count ?? 0;
                    data.WriteCount = access.WrittenBySystems?.Count ?? 0;
                    data.AccessTooltip = BuildAccessTooltip(access);
                }
                _dataById[id] = data;
                _componentTypeCacheIds[c] = id;
                cmpChildren.Add(new TreeViewItemData<RowData>(id, data));
            }
            if (searchActive)
            {
                HarvestFlatLeaves(cmpChildren, rootItems);
            }
            else
            {
                rootItems.Add(
                    new TreeViewItemData<RowData>(SectionComponentsId, cmpData, cmpChildren)
                );
            }

            // Sets.
            var setsData = new RowData
            {
                Kind = RowKind.Section,
                DisplayName = "Sets",
                Count = schema.Sets.Count,
                ShowCount = true,
            };
            _dataById[SectionSetsId] = setsData;
            var setsChildren = new List<TreeViewItemData<RowData>>();
            var sortedSets = new List<TrecsSchemaSet>(schema.Sets);
            sortedSets.Sort(
                (a, b) =>
                    string.Compare(
                        a.DebugName ?? "",
                        b.DebugName ?? "",
                        StringComparison.OrdinalIgnoreCase
                    )
            );
            foreach (var s in sortedSets)
            {
                var name = s.DebugName ?? "(unnamed)";
                var ctx = new PredicateData { CacheSet = s };
                if (!MatchesSearch(SearchScope.Sets, name, in ctx))
                {
                    continue;
                }
                int id = _nextId++;
                var data = new RowData
                {
                    Kind = RowKind.SetItem,
                    DisplayName = name,
                    ShowCount = false,
                    CacheSet = s,
                };
                _dataById[id] = data;
                _setCacheIds[s] = id;
                setsChildren.Add(new TreeViewItemData<RowData>(id, data));
            }
            if (searchActive)
            {
                HarvestFlatLeaves(setsChildren, rootItems);
            }
            else
            {
                rootItems.Add(new TreeViewItemData<RowData>(SectionSetsId, setsData, setsChildren));
            }

            // Tags.
            var tagsData = new RowData
            {
                Kind = RowKind.Section,
                DisplayName = "Tags",
                Count = schema.Tags.Count,
                ShowCount = true,
            };
            _dataById[SectionTagsId] = tagsData;
            var tagsChildren = new List<TreeViewItemData<RowData>>();
            var sortedTags = new List<TrecsSchemaTag>(schema.Tags);
            sortedTags.Sort(
                (a, b) =>
                    string.Compare(a.Name ?? "", b.Name ?? "", StringComparison.OrdinalIgnoreCase)
            );
            foreach (var t in sortedTags)
            {
                var name = t.Name ?? "(unnamed)";
                var ctx = new PredicateData { CacheTag = t };
                if (!MatchesSearch(SearchScope.Tags, name, in ctx))
                {
                    continue;
                }
                int id = _nextId++;
                var data = new RowData
                {
                    Kind = RowKind.TagItem,
                    DisplayName = name,
                    CacheTag = t,
                };
                _dataById[id] = data;
                _tagCacheIds[t] = id;
                tagsChildren.Add(new TreeViewItemData<RowData>(id, data));
            }
            if (searchActive)
            {
                HarvestFlatLeaves(tagsChildren, rootItems);
                rootItems.Sort(
                    (a, b) =>
                        string.Compare(
                            a.data.DisplayName ?? string.Empty,
                            b.data.DisplayName ?? string.Empty,
                            StringComparison.OrdinalIgnoreCase
                        )
                );
            }
            else
            {
                rootItems.Add(new TreeViewItemData<RowData>(SectionTagsId, tagsData, tagsChildren));
            }

            _tree.SetRootItems(rootItems);
            _tree.Rebuild();
            if (!searchActive)
            {
                _tree.ExpandItem(SectionTemplatesId);
                _tree.ExpandItem(SectionAccessorsId);
                _tree.ExpandItem(SectionComponentsId);
                _tree.ExpandItem(SectionSetsId);
                _tree.ExpandItem(SectionTagsId);
            }
        }

        // ----- Refresh tick ---------------------------------------------------

        void ForceFullRebuild()
        {
            _lastResolvedTemplateCount = -1;
            _lastAbstractTemplateCount = -1;
            _lastAccessorCount = -1;
            _lastComponentTypeCount = -1;
            _lastEntityCountByGroup.Clear();
            if (_cacheMode && _cachedSchema != null)
            {
                RebuildTreeFromCache(_cachedSchema);
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
                // in cache mode.
                if (!_cacheMode && _tree != null)
                {
                    _tree.SetRootItems(new List<TreeViewItemData<RowData>>());
                    _tree.Rebuild();
                }
                return;
            }

            var info = _selectedWorld.WorldInfo;

            if (NeedsStructuralRebuild(accessor, info))
            {
                RebuildTreeStructure(accessor, info);
            }
            else if (UpdateMutableData(accessor))
            {
                // Only RefreshItems when something actually changed —
                // unconditional rebinds reset the row's :hover state every
                // tick, so the hover highlight only paints while the mouse is
                // moving.
                _tree.RefreshItems();
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
            bool changed = false;
            try
            {
                var systems = accessor.GetSystems();
                var runner = accessor.GetSystemRunner();
                foreach (var kvp in _accessorTreeIds)
                {
                    if (
                        !_dataById.TryGetValue(kvp.Value, out var data)
                        || data.Kind != RowKind.Accessor
                    )
                    {
                        continue;
                    }
                    if (data.SystemIndex >= 0 && data.SystemIndex < systems.Count)
                    {
                        var enabled = runner.IsSystemEnabled(data.SystemIndex);
                        if (enabled != data.SystemEnabled)
                        {
                            data.SystemEnabled = enabled;
                            changed = true;
                        }
                    }
                }
            }
            catch
            {
                // World may have transitioned; next tick will resync.
            }

            // Set entity counts mutate without changing tree structure (set
            // rows are leaves), so refresh the count badge on each tick.
            try
            {
                foreach (var kvp in _setIds)
                {
                    if (
                        !_dataById.TryGetValue(kvp.Value, out var data)
                        || data.Kind != RowKind.SetItem
                    )
                    {
                        continue;
                    }
                    var newCount = accessor.CountEntitiesInSet(data.SetDef.Id);
                    if (newCount != data.Count)
                    {
                        data.Count = newCount;
                        changed = true;
                    }
                }
            }
            catch
            {
                // World may have transitioned; next tick will resync.
            }

            // Component-type access counts grow over time as systems run.
            // Refresh from the live tracker — when present — so the badge
            // numbers stay current without forcing a structural rebuild.
            try
            {
                var tracker =
                    _selectedWorld != null ? TrecsAccessRegistry.GetTracker(_selectedWorld) : null;
                if (tracker != null)
                {
                    foreach (var kvp in _componentTypeIds)
                    {
                        if (
                            !_dataById.TryGetValue(kvp.Value, out var data)
                            || data.Kind != RowKind.ComponentType
                        )
                        {
                            continue;
                        }
                        ComponentId cid;
                        try
                        {
                            cid = new ComponentId(TypeIdProvider.GetTypeId(kvp.Key));
                        }
                        catch (Exception)
                        {
                            continue;
                        }
                        var readers = tracker.GetReadersOf(cid);
                        var writers = tracker.GetWritersOf(cid);
                        int newRead = readers?.Count ?? 0;
                        int newWrite = writers?.Count ?? 0;
                        if (newRead != data.ReadCount || newWrite != data.WriteCount)
                        {
                            data.ReadCount = newRead;
                            data.WriteCount = newWrite;
                            data.AccessTooltip = BuildAccessTooltip(readers, writers);
                            changed = true;
                        }
                    }
                }
            }
            catch
            {
                // World transitioned; next tick will resync.
            }
            return changed;
        }

        // ----- Tree structure build ------------------------------------------

        void RebuildTreeStructure(WorldAccessor accessor, WorldInfo info)
        {
            // Capture expand state by stable id so we can re-apply after
            // SetRootItems. TreeView preserves expand state by id internally,
            // but explicit re-expand makes the behavior independent of that
            // implementation detail.
            var prevExpanded = CaptureExpandedIds();

            // Reset structural state. _idByKey is intentionally preserved so
            // ids stay stable for keys we already saw.
            _dataById.Clear();
            _parentById.Clear();
            _templateIds.Clear();
            _entityIds.Clear();
            _accessorTreeIds.Clear();
            _componentTypeIds.Clear();
            _setIds.Clear();
            _tagIds.Clear();
            _templateCacheIds.Clear();
            _componentTypeCacheIds.Clear();
            _accessorCacheIds.Clear();
            _setCacheIds.Clear();
            _tagCacheIds.Clear();
            _lastEntityCountByGroup.Clear();

            var rootItems = new List<TreeViewItemData<RowData>>();

            // Section counts come from the world snapshot, not the children
            // list — the children list reflects the current search filter,
            // but the heading should always show "this is what exists in
            // the world", same as how the per-template count ignores
            // partition-level expansion.
            //
            // Compute every count once here and reuse below for both the
            // section header rows and the _lastXxx fingerprint update at
            // the end of this method.
            int totalTemplateCount = info.AllTemplates.Count;
            int resolvedTemplateCount = info.ResolvedTemplates.Count;
            int abstractTemplateCount = 0;
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
            int totalComponentTypeCount = _scratchTypeSet.Count;
            int totalAccessorCount;
            try
            {
                totalAccessorCount = CountNonEditorAccessors();
            }
            catch
            {
                totalAccessorCount = -1;
            }
            int totalSetCount = info.AllSets.Count;
            var allTags = CollectAllTags(info);
            int totalTagCount = allTags.Count;

            // While search is active the tree morphs into a flat list of
            // matching content rows (see HarvestFlatLeaves). Otherwise we
            // emit the standard 5-section hierarchy. The per-section build
            // calls run unconditionally so reverse-lookup tables and stable
            // ids stay populated either way.
            bool searchActive = !string.IsNullOrEmpty(_searchText);

            var templatesData = new RowData
            {
                Kind = RowKind.Section,
                DisplayName = "Templates",
                Count = totalTemplateCount,
                ShowCount = true,
            };
            _dataById[SectionTemplatesId] = templatesData;
            var templatesChildren = BuildTemplatesChildren(accessor, info, SectionTemplatesId);
            if (searchActive)
            {
                HarvestFlatLeaves(templatesChildren, rootItems);
            }
            else
            {
                rootItems.Add(
                    new TreeViewItemData<RowData>(
                        SectionTemplatesId,
                        templatesData,
                        templatesChildren
                    )
                );
            }

            var accessorsData = new RowData
            {
                Kind = RowKind.Section,
                DisplayName = "Accessors",
                Count = totalAccessorCount,
                ShowCount = true,
            };
            _dataById[SectionAccessorsId] = accessorsData;
            var accessorsChildren = BuildAccessorsChildren(accessor, SectionAccessorsId);
            if (searchActive)
            {
                HarvestFlatLeaves(accessorsChildren, rootItems);
            }
            else
            {
                rootItems.Add(
                    new TreeViewItemData<RowData>(
                        SectionAccessorsId,
                        accessorsData,
                        accessorsChildren
                    )
                );
            }

            var componentsData = new RowData
            {
                Kind = RowKind.Section,
                DisplayName = "Components",
                Count = totalComponentTypeCount,
                ShowCount = true,
            };
            _dataById[SectionComponentsId] = componentsData;
            var componentsChildren = BuildComponentsChildren(info, SectionComponentsId);
            if (searchActive)
            {
                HarvestFlatLeaves(componentsChildren, rootItems);
            }
            else
            {
                rootItems.Add(
                    new TreeViewItemData<RowData>(
                        SectionComponentsId,
                        componentsData,
                        componentsChildren
                    )
                );
            }

            var setsData = new RowData
            {
                Kind = RowKind.Section,
                DisplayName = "Sets",
                Count = totalSetCount,
                ShowCount = true,
            };
            _dataById[SectionSetsId] = setsData;
            var setsChildren = BuildSetsChildren(accessor, info, SectionSetsId);
            if (searchActive)
            {
                HarvestFlatLeaves(setsChildren, rootItems);
            }
            else
            {
                rootItems.Add(new TreeViewItemData<RowData>(SectionSetsId, setsData, setsChildren));
            }

            var tagsData = new RowData
            {
                Kind = RowKind.Section,
                DisplayName = "Tags",
                Count = allTags.Count,
                ShowCount = true,
            };
            _dataById[SectionTagsId] = tagsData;
            var tagsChildren = BuildTagsChildren(allTags, SectionTagsId);
            if (searchActive)
            {
                HarvestFlatLeaves(tagsChildren, rootItems);
            }
            else
            {
                rootItems.Add(new TreeViewItemData<RowData>(SectionTagsId, tagsData, tagsChildren));
            }

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
            // rely on the prevExpanded restore loop below — that way the
            // user can collapse a section and have it stay collapsed
            // across ticks. Skipped when the very first rebuild happens
            // with search active so the initial expansion runs against
            // the non-flat tree on first clear.
            if (!searchActive && !_initialSectionExpansionApplied)
            {
                _tree.ExpandItem(SectionTemplatesId);
                _tree.ExpandItem(SectionAccessorsId);
                _tree.ExpandItem(SectionComponentsId);
                _tree.ExpandItem(SectionSetsId);
                _tree.ExpandItem(SectionTagsId);
                _initialSectionExpansionApplied = true;
            }

            bool leavingSearch = !searchActive && _preSearchExpandedIds != null;

            if (leavingSearch)
            {
                // Search just got cleared. Restore the expand state the
                // user had before they started typing — the flat-mode
                // tree had nothing to capture so prevExpanded is empty,
                // and pre-search is the source of truth.
                var preSearch = _preSearchExpandedIds;
                _preSearchExpandedIds = null;
                foreach (var id in prevExpanded)
                {
                    if (_dataById.ContainsKey(id))
                    {
                        _tree.ExpandItem(id);
                    }
                }
                foreach (var id in preSearch)
                {
                    if (_dataById.ContainsKey(id))
                    {
                        _tree.ExpandItem(id);
                    }
                }
            }
            else
            {
                foreach (var id in prevExpanded)
                {
                    if (_dataById.ContainsKey(id))
                    {
                        _tree.ExpandItem(id);
                    }
                }
            }

            // Drop _idByKey entries whose ids didn't make it into the new
            // tree (entities destroyed, accessors disposed, templates pruned
            // by toggles, etc.). Without this the map grows monotonically
            // for the editor session.
            PruneIdByKey();

            // Update structural fingerprint with the counts captured at the
            // top of this method — no need to re-walk the world.
            _lastResolvedTemplateCount = resolvedTemplateCount;
            _lastAbstractTemplateCount = abstractTemplateCount;
            _lastAccessorCount = totalAccessorCount;
            _lastComponentTypeCount = totalComponentTypeCount;
            _lastSetCount = totalSetCount;
            _lastTagCount = totalTagCount;

            // Reapply tree highlight from the current Selection.activeObject —
            // the prior tree's selected row id is gone, but the proxy may
            // still point to a row that exists in the new tree. Don't
            // scroll: the rebuild fired because of a count tick, and the
            // user may be scrolled elsewhere on purpose.
            UpdateRowSelectionFromUnity(scrollToItem: false);
        }

        // While search is active we render a flat list of matching leaves
        // instead of the section hierarchy — see header comment on
        // _preSearchExpandedIds. Walks the per-section subtree and pulls
        // out every content row, dropping headers (Section, AccessorPhase,
        // Group, MorePlaceholder). Each harvested leaf is reconstructed
        // without children so it sits at the root of the flat tree.
        //
        // The explicit-kind filter (e.g. `t:e`) gets re-applied here:
        // TryBuildConcreteTemplateNode keeps a template node when any of
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

        HashSet<int> CaptureExpandedIds()
        {
            var s = new HashSet<int>();
            if (_tree == null)
            {
                return s;
            }
            foreach (var id in _dataById.Keys)
            {
                try
                {
                    if (_tree.IsExpanded(id))
                    {
                        s.Add(id);
                    }
                }
                catch
                {
                    // id may not exist in the controller anymore.
                }
            }
            return s;
        }

        // ---- Templates subtree ----

        List<TreeViewItemData<RowData>> BuildTemplatesChildren(
            WorldAccessor accessor,
            WorldInfo info,
            int parentId
        )
        {
            var children = new List<TreeViewItemData<RowData>>();

            // Concrete (resolved) templates first, sorted by entity count
            // (desc) so populated templates float up.
            var concrete = new List<(ResolvedTemplate rt, int count)>();
            foreach (var rt in info.ResolvedTemplates)
            {
                int c = 0;
                foreach (var g in rt.Groups)
                {
                    var n = accessor.CountEntitiesInGroup(g);
                    c += n;
                    _lastEntityCountByGroup[g] = n;
                }
                concrete.Add((rt, c));
            }
            concrete.Sort(
                (a, b) =>
                {
                    if (a.count != b.count)
                    {
                        return b.count.CompareTo(a.count);
                    }
                    return string.Compare(
                        a.rt.DebugName ?? string.Empty,
                        b.rt.DebugName ?? string.Empty,
                        StringComparison.OrdinalIgnoreCase
                    );
                }
            );
            foreach (var (rt, count) in concrete)
            {
                // User toggle: hide templates with no entities.
                if (!_showEmptyTemplates && count == 0)
                {
                    continue;
                }
                if (TryBuildConcreteTemplateNode(accessor, rt, count, parentId, out var item))
                {
                    children.Add(item);
                }
            }

            // Abstract / base templates listed below in alpha order — flat
            // (non-expandable) leaves with the "(abstract)" badge.
            if (_showAbstractTemplates)
            {
                var abstracts = new List<Template>();
                foreach (var t in info.AllTemplates)
                {
                    if (!info.IsResolvedTemplate(t))
                    {
                        abstracts.Add(t);
                    }
                }
                abstracts.Sort(
                    (a, b) =>
                        string.Compare(
                            a.DebugName ?? string.Empty,
                            b.DebugName ?? string.Empty,
                            StringComparison.OrdinalIgnoreCase
                        )
                );
                foreach (var t in abstracts)
                {
                    if (TryBuildAbstractTemplateNode(t, parentId, out var item))
                    {
                        children.Add(item);
                    }
                }
            }
            return children;
        }

        bool TryBuildConcreteTemplateNode(
            WorldAccessor accessor,
            ResolvedTemplate rt,
            int totalCount,
            int parentId,
            out TreeViewItemData<RowData> result
        )
        {
            result = default;
            var displayName = ObjectNames.NicifyVariableName(rt.DebugName ?? "(unnamed)");

            // Build entity / partition children first so we can decide
            // whether the subtree as a whole satisfies the search filter.
            List<TreeViewItemData<RowData>> tplChildren = null;
            if (totalCount > 0)
            {
                tplChildren = new List<TreeViewItemData<RowData>>();
                var hasPartitions = rt.Partitions.Count > 0;
                for (int i = 0; i < rt.Groups.Count; i++)
                {
                    var group = rt.Groups[i];
                    if (hasPartitions)
                    {
                        var tags = i < rt.Partitions.Count ? rt.Partitions[i] : default;
                        if (TryBuildPartitionNode(accessor, rt, group, tags, out var pitem))
                        {
                            tplChildren.Add(pitem);
                        }
                    }
                    else
                    {
                        AppendEntityChildren(accessor, group, displayName, rt, tplChildren);
                    }
                }
            }

            var ctx = new PredicateData
            {
                ResolvedTemplate = rt,
                WorldInfo = _selectedWorld?.WorldInfo,
            };
            bool selfMatches = MatchesSearch(
                SearchScope.Templates,
                displayName,
                rt.DebugName ?? string.Empty,
                in ctx
            );
            bool anyChildMatches = tplChildren != null && tplChildren.Count > 0;
            if (SearchActive && !selfMatches && !anyChildMatches)
            {
                return false;
            }

            int treeId = GetOrAssignId(rt.Template);
            _templateIds[rt.Template] = treeId;
            _parentById[treeId] = parentId;

            var data = new RowData
            {
                Kind = RowKind.Template,
                DisplayName = displayName,
                Template = rt.Template,
                ResolvedTemplate = rt,
                Count = totalCount,
                ShowCount = true,
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

        bool TryBuildAbstractTemplateNode(
            Template t,
            int parentId,
            out TreeViewItemData<RowData> result
        )
        {
            result = default;
            var displayName = ObjectNames.NicifyVariableName(t.DebugName ?? "(unnamed)");
            // Abstract templates skip predicate dispatch — they don't have
            // a ResolvedTemplate, so tag/c/base/derived predicates have no
            // resolution path and would always fail. Substring + kind only.
            var ctx = new PredicateData { AbstractTemplate = t };
            if (
                SearchActive
                && !MatchesSearch(
                    SearchScope.Templates,
                    displayName,
                    t.DebugName ?? string.Empty,
                    in ctx
                )
            )
            {
                return false;
            }
            int treeId = GetOrAssignId(t);
            _templateIds[t] = treeId;
            _parentById[treeId] = parentId;
            var data = new RowData
            {
                Kind = RowKind.AbstractTemplate,
                DisplayName = displayName,
                Template = t,
            };
            _dataById[treeId] = data;
            result = new TreeViewItemData<RowData>(treeId, data);
            return true;
        }

        bool TryBuildPartitionNode(
            WorldAccessor accessor,
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
                ObjectNames.NicifyVariableName(rt.DebugName ?? string.Empty),
                rt,
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
            ResolvedTemplate parentRt,
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
                    var ctx = new PredicateData { ResolvedTemplate = parentRt };
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

        List<TreeViewItemData<RowData>> BuildAccessorsChildren(
            WorldAccessor windowAccessor,
            int parentId
        )
        {
            var result = new List<TreeViewItemData<RowData>>();

            IReadOnlyList<ExecutableSystemInfo> systems;
            SystemRunner runner;
            try
            {
                systems = windowAccessor.GetSystems();
                runner = windowAccessor.GetSystemRunner();
            }
            catch
            {
                return result;
            }

            // accessor.Id → system index (so each system row carries the index
            // for enable-toggle bookkeeping in the accessor inspector).
            var accessorIdToSystemIndex = new Dictionary<int, int>();
            for (int i = 0; i < systems.Count; i++)
            {
                var acc = systems[i].Metadata.Accessor;
                if (acc != null)
                {
                    accessorIdToSystemIndex[acc.Id] = i;
                }
            }

            var byPhase = new Dictionary<SystemPhase, List<AccessorRowSpec>>();
            var manual = new List<AccessorRowSpec>();

            try
            {
                foreach (var entry in _selectedWorld.GetAllAccessors())
                {
                    var id = entry.Key;
                    var accessor = entry.Value;
                    if (accessor == null)
                    {
                        continue;
                    }
                    if (TrecsEditorAccessorNames.Contains(accessor.DebugName))
                    {
                        continue;
                    }

                    if (accessorIdToSystemIndex.TryGetValue(id, out var sysIdx))
                    {
                        var info = systems[sysIdx];
                        if (!byPhase.TryGetValue(info.Metadata.Phase, out var list))
                        {
                            list = new List<AccessorRowSpec>();
                            byPhase[info.Metadata.Phase] = list;
                        }
                        list.Add(
                            new AccessorRowSpec
                            {
                                AccessorId = id,
                                DisplayName =
                                    info.Metadata.DebugName ?? accessor.DebugName ?? $"#{id}",
                                SystemIndex = sysIdx,
                                ExecutionPriority = info.Metadata.ExecutionPriority,
                                SystemEnabled = runner.IsSystemEnabled(sysIdx),
                            }
                        );
                    }
                    else
                    {
                        manual.Add(
                            new AccessorRowSpec
                            {
                                AccessorId = id,
                                DisplayName = accessor.DebugName ?? $"#{id}",
                                SystemIndex = -1,
                                ExecutionPriority = null,
                                SystemEnabled = true,
                            }
                        );
                    }
                }
            }
            catch
            {
                return result;
            }

            AddPhase(
                result,
                "Early Presentation",
                byPhase,
                SystemPhase.EarlyPresentation,
                parentId
            );
            AddPhase(result, "Input", byPhase, SystemPhase.Input, parentId);
            AddPhase(result, "Fixed", byPhase, SystemPhase.Fixed, parentId);
            AddPhase(result, "Presentation", byPhase, SystemPhase.Presentation, parentId);
            AddPhase(result, "Late Presentation", byPhase, SystemPhase.LatePresentation, parentId);

            if (manual.Count > 0)
            {
                manual.Sort(
                    (a, b) =>
                        string.Compare(
                            a.DisplayName,
                            b.DisplayName,
                            StringComparison.OrdinalIgnoreCase
                        )
                );
                AddPhaseFromSpecs(result, "Other", manual, parentId);
            }

            return result;
        }

        void AddPhase(
            List<TreeViewItemData<RowData>> sink,
            string title,
            Dictionary<SystemPhase, List<AccessorRowSpec>> grouped,
            SystemPhase phase,
            int parentId
        )
        {
            if (!grouped.TryGetValue(phase, out var list) || list.Count == 0)
            {
                return;
            }
            // Lower priority first preserves intended execution order; ties
            // broken alphabetically.
            list.Sort(
                (a, b) =>
                {
                    var ap = a.ExecutionPriority ?? int.MaxValue;
                    var bp = b.ExecutionPriority ?? int.MaxValue;
                    if (ap != bp)
                    {
                        return ap.CompareTo(bp);
                    }
                    return string.Compare(
                        a.DisplayName,
                        b.DisplayName,
                        StringComparison.OrdinalIgnoreCase
                    );
                }
            );
            AddPhaseFromSpecs(sink, title, list, parentId);
        }

        void AddPhaseFromSpecs(
            List<TreeViewItemData<RowData>> sink,
            string title,
            List<AccessorRowSpec> specs,
            int parentId
        )
        {
            var phaseChildren = new List<TreeViewItemData<RowData>>();
            foreach (var spec in specs)
            {
                var ctx = new PredicateData { AccessorDebugName = spec.DisplayName };
                if (!MatchesSearch(SearchScope.Accessors, spec.DisplayName, in ctx))
                {
                    continue;
                }
                int aid = GetOrAssignId(BoxedAccessorIdKey(spec.AccessorId));
                _accessorTreeIds[spec.AccessorId] = aid;
                var data = new RowData
                {
                    Kind = RowKind.Accessor,
                    DisplayName = spec.DisplayName,
                    AccessorId = spec.AccessorId,
                    SystemIndex = spec.SystemIndex,
                    ExecutionPriority = spec.ExecutionPriority,
                    SystemEnabled = spec.SystemEnabled,
                };
                _dataById[aid] = data;
                phaseChildren.Add(new TreeViewItemData<RowData>(aid, data));
            }

            bool phaseTitleMatches = MatchesSearch(SearchScope.Accessors, title);
            if (SearchActive && !phaseTitleMatches && phaseChildren.Count == 0)
            {
                return;
            }

            int phid = GetOrAssignId(BoxedPhaseKey(title));
            _parentById[phid] = parentId;
            var pdata = new RowData
            {
                Kind = RowKind.AccessorPhase,
                DisplayName = title,
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

        List<TreeViewItemData<RowData>> BuildComponentsChildren(WorldInfo info, int parentId)
        {
            // Union of all component types declared across resolved templates.
            // Same type may appear on multiple templates — one row per type.
            var seen = new HashSet<Type>();
            var rows = new List<(Type t, string display)>();
            foreach (var rt in info.ResolvedTemplates)
            {
                foreach (var dec in rt.ComponentDeclarations)
                {
                    var t = dec.ComponentType;
                    if (t == null || !seen.Add(t))
                    {
                        continue;
                    }
                    rows.Add((t, ComponentTypeDisplayName(t)));
                }
            }
            rows.Sort(
                (a, b) => string.Compare(a.display, b.display, StringComparison.OrdinalIgnoreCase)
            );

            var children = new List<TreeViewItemData<RowData>>();
            foreach (var (t, display) in rows)
            {
                var ctx = new PredicateData { ComponentType = t };
                if (!MatchesSearch(SearchScope.Components, display, t.Name, in ctx))
                {
                    continue;
                }
                int cid = GetOrAssignId(t);
                _componentTypeIds[t] = cid;
                _parentById[cid] = parentId;
                var data = new RowData
                {
                    Kind = RowKind.ComponentType,
                    DisplayName = display,
                    ComponentType = t,
                };
                _dataById[cid] = data;
                children.Add(new TreeViewItemData<RowData>(cid, data));
            }
            return children;
        }

        List<TreeViewItemData<RowData>> BuildSetsChildren(
            WorldAccessor accessor,
            WorldInfo info,
            int parentId
        )
        {
            var rows = new List<SetDef>(info.AllSets);
            rows.Sort(
                (a, b) =>
                    string.Compare(
                        a.DebugName ?? "",
                        b.DebugName ?? "",
                        StringComparison.OrdinalIgnoreCase
                    )
            );

            var children = new List<TreeViewItemData<RowData>>();
            foreach (var setDef in rows)
            {
                var display = setDef.DebugName ?? $"#{setDef.Id.Id}";
                var ctx = new PredicateData { Set = setDef };
                if (!MatchesSearch(SearchScope.Sets, display, in ctx))
                {
                    continue;
                }
                int count;
                try
                {
                    count = accessor.CountEntitiesInSet(setDef.Id);
                }
                catch
                {
                    count = 0;
                }
                int sid = GetOrAssignId(setDef.Id);
                _setIds[setDef.Id] = sid;
                _parentById[sid] = parentId;
                var data = new RowData
                {
                    Kind = RowKind.SetItem,
                    DisplayName = display,
                    SetDef = setDef,
                    Count = count,
                    ShowCount = true,
                };
                _dataById[sid] = data;
                children.Add(new TreeViewItemData<RowData>(sid, data));
            }
            return children;
        }

        // Walks templates (AllTags + every partition) and sets to gather
        // every tag referenced in this world. Returns deduped by Tag.Guid,
        // keyed by Guid so we can share with downstream consumers without
        // recomputing.
        static Dictionary<int, Tag> CollectAllTags(WorldInfo info)
        {
            var tags = new Dictionary<int, Tag>();
            foreach (var rt in info.ResolvedTemplates)
            {
                if (!rt.AllTags.IsNull)
                {
                    foreach (var t in rt.AllTags.Tags)
                    {
                        if (t.Guid != 0 && !tags.ContainsKey(t.Guid))
                        {
                            tags[t.Guid] = t;
                        }
                    }
                }
                foreach (var p in rt.Partitions)
                {
                    if (p.IsNull)
                    {
                        continue;
                    }
                    foreach (var t in p.Tags)
                    {
                        if (t.Guid != 0 && !tags.ContainsKey(t.Guid))
                        {
                            tags[t.Guid] = t;
                        }
                    }
                }
            }
            foreach (var setDef in info.AllSets)
            {
                if (setDef.Tags.IsNull)
                {
                    continue;
                }
                foreach (var t in setDef.Tags.Tags)
                {
                    if (t.Guid != 0 && !tags.ContainsKey(t.Guid))
                    {
                        tags[t.Guid] = t;
                    }
                }
            }
            return tags;
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
            foreach (var setDef in info.AllSets)
            {
                if (setDef.Tags.IsNull)
                    continue;
                foreach (var t in setDef.Tags.Tags)
                {
                    if (t.Guid != 0)
                        scratch.Add(t.Guid);
                }
            }
            return scratch.Count;
        }

        List<TreeViewItemData<RowData>> BuildTagsChildren(Dictionary<int, Tag> tags, int parentId)
        {
            var rows = new List<Tag>(tags.Values);
            rows.Sort(
                (a, b) =>
                    string.Compare(
                        a.ToString() ?? "",
                        b.ToString() ?? "",
                        StringComparison.OrdinalIgnoreCase
                    )
            );

            var children = new List<TreeViewItemData<RowData>>();
            foreach (var tag in rows)
            {
                var display = tag.ToString() ?? $"#{tag.Guid}";
                var ctx = new PredicateData { Tag = tag };
                if (!MatchesSearch(SearchScope.Tags, display, in ctx))
                {
                    continue;
                }
                int tid = GetOrAssignId(("tag", tag.Guid));
                _tagIds[tag.Guid] = tid;
                _parentById[tid] = parentId;
                var data = new RowData
                {
                    Kind = RowKind.TagItem,
                    DisplayName = display,
                    Tag = tag,
                };
                _dataById[tid] = data;
                children.Add(new TreeViewItemData<RowData>(tid, data));
            }
            return children;
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

            var icon = element.Q<Image>("trecs-icon");
            var label = element.Q<Label>("trecs-label");
            var badge = element.Q<Label>("trecs-badge");

            icon.image = IconForKind(data.Kind);
            icon.style.opacity =
                (data.Kind == RowKind.AbstractTemplate || data.Kind == RowKind.MorePlaceholder)
                    ? 0.5f
                    : 1f;
            icon.style.display = icon.image == null ? DisplayStyle.None : DisplayStyle.Flex;

            ApplyLabelTextWithSearchHighlight(label, data.DisplayName);
            switch (data.Kind)
            {
                case RowKind.Section:
                    label.style.unityFontStyleAndWeight = FontStyle.Bold;
                    label.style.opacity = 1f;
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
                    label.style.opacity = data.SystemEnabled ? 1f : 0.45f;
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

            string badgeText = null;
            string badgeTooltip = null;
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
                    if (data.ExecutionPriority.HasValue)
                    {
                        badgeText = $"prio {data.ExecutionPriority.Value}";
                    }
                    break;
                case RowKind.AbstractTemplate:
                    badgeText = "(abstract)";
                    break;
                case RowKind.ComponentType:
                    if (data.ReadCount > 0 || data.WriteCount > 0)
                    {
                        badgeText = $"R {data.ReadCount} · W {data.WriteCount}";
                        badgeTooltip = data.AccessTooltip;
                    }
                    break;
            }
            if (badgeText != null)
            {
                badge.text = badgeText;
                badge.tooltip = badgeTooltip ?? string.Empty;
                badge.style.display = DisplayStyle.Flex;
            }
            else
            {
                badge.tooltip = string.Empty;
                badge.style.display = DisplayStyle.None;
            }

            // Preview-hover overlay driven by inspector links. Apply on the
            // custom row element only — TreeView's own selection / :hover
            // visuals live on the wrapper above us, so we don't interfere
            // with them. Use StyleKeyword.Null to release the override
            // when this row isn't the previewed one.
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
            switch (data.Kind)
            {
                case RowKind.Template:
                case RowKind.AbstractTemplate:
                {
                    var p = TrecsSelectionProxies.NextTemplate();
                    p.Set(_selectedWorld, data.Template);
                    Selection.activeObject = p;
                    break;
                }
                case RowKind.Entity:
                {
                    var p = TrecsSelectionProxies.NextEntity();
                    p.Set(_selectedWorld, data.EntityHandle);
                    Selection.activeObject = p;
                    break;
                }
                case RowKind.Accessor:
                {
                    var p = TrecsSelectionProxies.NextAccessor();
                    p.Set(_selectedWorld, data.AccessorId, data.DisplayName);
                    Selection.activeObject = p;
                    break;
                }
                case RowKind.ComponentType:
                {
                    var p = TrecsSelectionProxies.NextComponentType();
                    p.Set(_selectedWorld, data.ComponentType);
                    Selection.activeObject = p;
                    break;
                }
                case RowKind.SetItem:
                {
                    var p = TrecsSelectionProxies.NextSet();
                    p.Set(_selectedWorld, data.SetDef);
                    Selection.activeObject = p;
                    break;
                }
                case RowKind.TagItem:
                {
                    var p = TrecsSelectionProxies.NextTag();
                    p.Set(_selectedWorld, data.Tag);
                    Selection.activeObject = p;
                    break;
                }
                // Section / Group / AccessorPhase / MorePlaceholder: no proxy.
            }
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
            switch (first.Kind)
            {
                case RowKind.Template:
                case RowKind.AbstractTemplate:
                {
                    if (first.CacheTemplate == null)
                        return;
                    var p = TrecsSelectionProxies.NextTemplate();
                    p.SetCache(_cachedSchema, first.CacheTemplate);
                    Selection.activeObject = p;
                    break;
                }
                case RowKind.ComponentType:
                {
                    if (first.CacheComponent == null)
                        return;
                    var p = TrecsSelectionProxies.NextComponentType();
                    p.SetCache(_cachedSchema, first.CacheComponent);
                    Selection.activeObject = p;
                    break;
                }
                case RowKind.Accessor:
                {
                    if (string.IsNullOrEmpty(first.DisplayName))
                        return;
                    var p = TrecsSelectionProxies.NextAccessor();
                    p.SetCache(_cachedSchema, first.DisplayName);
                    Selection.activeObject = p;
                    break;
                }
                case RowKind.SetItem:
                {
                    if (first.CacheSet == null)
                        return;
                    var p = TrecsSelectionProxies.NextSet();
                    p.SetCache(_cachedSchema, first.CacheSet);
                    Selection.activeObject = p;
                    break;
                }
                case RowKind.TagItem:
                {
                    if (first.CacheTag == null)
                        return;
                    var p = TrecsSelectionProxies.NextTag();
                    p.SetCache(_cachedSchema, first.CacheTag);
                    Selection.activeObject = p;
                    break;
                }
                // Sections, phases, groups: no proxy.
            }
        }

        // True when the active selection is one of the Trecs hierarchy proxy
        // kinds, regardless of which pool slot. Used to scope behaviors that
        // should fire only while the user is "inside" the hierarchy (e.g.
        // clearing selection on window close).
        static bool IsTrecsSelection(UnityEngine.Object obj) =>
            obj is TrecsEntitySelection
            || obj is TrecsTemplateSelection
            || obj is TrecsComponentTypeSelection
            || obj is TrecsAccessorSelection
            || obj is TrecsSetSelection
            || obj is TrecsTagSelection
            || obj is TrecsMultiSelection;

        // ----- Preview hover (driven by inspector link MouseEnter/Leave) ----

        void OnPreviewTemplate(World world, Template template)
        {
            if (world != _selectedWorld || template == null)
            {
                SetPreviewRowId(-1);
                return;
            }
            if (_templateIds.TryGetValue(template, out var id))
            {
                SetPreviewRowId(id);
            }
        }

        void OnPreviewComponentType(World world, Type componentType)
        {
            if (world != _selectedWorld || componentType == null)
            {
                SetPreviewRowId(-1);
                return;
            }
            if (_componentTypeIds.TryGetValue(componentType, out var id))
            {
                SetPreviewRowId(id);
            }
        }

        void OnPreviewAccessor(World world, int accessorId)
        {
            if (world != _selectedWorld || accessorId < 0)
            {
                SetPreviewRowId(-1);
                return;
            }
            if (_accessorTreeIds.TryGetValue(accessorId, out var id))
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

        void OnPreviewSet(World world, SetId setId)
        {
            if (world != _selectedWorld || setId.Id == 0)
            {
                SetPreviewRowId(-1);
                return;
            }
            if (_setIds.TryGetValue(setId, out var id))
            {
                SetPreviewRowId(id);
            }
        }

        void OnPreviewTag(World world, Tag tag)
        {
            if (world != _selectedWorld || tag.Guid == 0)
            {
                SetPreviewRowId(-1);
                return;
            }
            if (_tagIds.TryGetValue(tag.Guid, out var id))
            {
                SetPreviewRowId(id);
            }
        }

        void OnPreviewTemplateCache(TrecsSchemaTemplate entry)
        {
            if (!_cacheMode || entry == null)
            {
                SetPreviewRowId(-1);
                return;
            }
            if (_templateCacheIds.TryGetValue(entry, out var id))
            {
                SetPreviewRowId(id);
            }
        }

        void OnPreviewComponentTypeCache(TrecsSchemaComponentType entry)
        {
            if (!_cacheMode || entry == null)
            {
                SetPreviewRowId(-1);
                return;
            }
            if (_componentTypeCacheIds.TryGetValue(entry, out var id))
            {
                SetPreviewRowId(id);
            }
        }

        void OnPreviewAccessorCache(string accessorName)
        {
            if (!_cacheMode || string.IsNullOrEmpty(accessorName))
            {
                SetPreviewRowId(-1);
                return;
            }
            if (_accessorCacheIds.TryGetValue(accessorName, out var id))
            {
                SetPreviewRowId(id);
            }
        }

        void OnPreviewSetCache(TrecsSchemaSet entry)
        {
            if (!_cacheMode || entry == null)
            {
                SetPreviewRowId(-1);
                return;
            }
            if (_setCacheIds.TryGetValue(entry, out var id))
            {
                SetPreviewRowId(id);
            }
        }

        void OnPreviewTagCache(TrecsSchemaTag entry)
        {
            if (!_cacheMode || entry == null)
            {
                SetPreviewRowId(-1);
                return;
            }
            if (_tagCacheIds.TryGetValue(entry, out var id))
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
            else if (sel is TrecsTemplateSelection ts && ts.GetWorld() == _selectedWorld)
            {
                if (ts.Template != null)
                {
                    _templateIds.TryGetValue(ts.Template, out id);
                }
            }
            else if (sel is TrecsAccessorSelection acs && acs.GetWorld() == _selectedWorld)
            {
                _accessorTreeIds.TryGetValue(acs.AccessorId, out id);
            }
            else if (sel is TrecsComponentTypeSelection cs && cs.GetWorld() == _selectedWorld)
            {
                if (cs.ComponentType != null)
                {
                    _componentTypeIds.TryGetValue(cs.ComponentType, out id);
                }
            }
            else if (sel is TrecsSetSelection ss && ss.GetWorld() == _selectedWorld)
            {
                if (ss.SetDef.Id.Id != 0)
                {
                    _setIds.TryGetValue(ss.SetDef.Id, out id);
                }
            }
            else if (sel is TrecsTagSelection tgs && tgs.GetWorld() == _selectedWorld)
            {
                if (tgs.Tag.Guid != 0)
                {
                    _tagIds.TryGetValue(tgs.Tag.Guid, out id);
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
        // mirrors UpdateRowSelectionFromUnity's switch but goes through the
        // schema-entry-keyed dictionaries populated in RebuildTreeFromCache.
        int ResolveCacheModeRowId(UnityEngine.Object sel)
        {
            int id = -1;
            if (sel is TrecsTemplateSelection ts && ts.CacheTemplate != null)
            {
                _templateCacheIds.TryGetValue(ts.CacheTemplate, out id);
            }
            else if (sel is TrecsComponentTypeSelection cs && cs.CacheComponent != null)
            {
                _componentTypeCacheIds.TryGetValue(cs.CacheComponent, out id);
            }
            else if (
                sel is TrecsAccessorSelection acs
                && !string.IsNullOrEmpty(acs.CacheAccessorName)
            )
            {
                _accessorCacheIds.TryGetValue(acs.CacheAccessorName, out id);
            }
            else if (sel is TrecsSetSelection ss && ss.CacheSet != null)
            {
                _setCacheIds.TryGetValue(ss.CacheSet, out id);
            }
            else if (sel is TrecsTagSelection tgs && tgs.CacheTag != null)
            {
                _tagCacheIds.TryGetValue(tgs.CacheTag, out id);
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
            return id;
        }

        // The "more not shown" placeholder rows aren't keyed (they're not
        // selectable and we never need to find them later), so allocate a
        // fresh unkeyed id each rebuild.
        int AllocateAnonId() => _nextId++;

        // Drop key→id entries whose id no longer appears in _dataById. Called
        // at the end of each structural rebuild. Without it, every entity /
        // template / accessor we've ever seen sticks around in _idByKey for
        // the editor session.
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
                    _idByKey.Remove(k);
                }
            }
        }

        static object BoxedAccessorIdKey(int accessorId) => ("accessor", accessorId);

        static object BoxedPhaseKey(string title) => ("phase", title);

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

        static Texture IconForKind(RowKind kind)
        {
            switch (kind)
            {
                case RowKind.Template:
                case RowKind.AbstractTemplate:
                    return _iconTemplate ??= EditorGUIUtility.IconContent("Prefab Icon").image;
                case RowKind.Entity:
                    return _iconEntity ??= EditorGUIUtility.IconContent("GameObject Icon").image;
                case RowKind.Group:
                case RowKind.AccessorPhase:
                    return _iconFolder ??= EditorGUIUtility.IconContent("Folder Icon").image;
                case RowKind.Accessor:
                    return _iconScript ??= EditorGUIUtility.IconContent("cs Script Icon").image;
                case RowKind.ComponentType:
                    return _iconScriptable ??= EditorGUIUtility
                        .IconContent("ScriptableObject Icon")
                        .image;
                case RowKind.SetItem:
                    return _iconFolder ??= EditorGUIUtility.IconContent("Folder Icon").image;
                case RowKind.TagItem:
                    return EditorGUIUtility.IconContent("FilterByLabel").image;
                default:
                    return null;
            }
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

        // Tooltip for the component-type R/W badge — lists the actual
        // system names so the user can see who's touching the component
        // without having to open the inspector.
        static string BuildAccessTooltip(
            IReadOnlyCollection<string> readers,
            IReadOnlyCollection<string> writers
        )
        {
            var sb = new StringBuilder();
            sb.Append("Read by ");
            sb.Append(
                (readers == null || readers.Count == 0) ? "(none)" : string.Join(", ", readers)
            );
            sb.Append("\nWritten by ");
            sb.Append(
                (writers == null || writers.Count == 0) ? "(none)" : string.Join(", ", writers)
            );
            return sb.ToString();
        }

        static string BuildAccessTooltip(TrecsSchemaAccessInfo access)
        {
            return BuildAccessTooltip(access?.ReadBySystems, access?.WrittenBySystems);
        }

        // ----- Inner types ----------------------------------------------------

        // Scope flags for the search field's prefix syntax. Bit per kind so
        // a row can ask "am I in scope?" with one mask AND. All has every
        // bit set; rows of any kind pass an All-scoped filter, but a
        // scoped filter (e.g. Templates) only lets matching rows through.
        // Partitions has its own bit so partition rows don't slip through
        // a scoped filter via a substring match on their tag-set display
        // string.
        [Flags]
        enum SearchScope
        {
            Templates = 1 << 0,
            Entities = 1 << 1,
            Components = 1 << 2,
            Accessors = 1 << 3,
            Sets = 1 << 4,
            Tags = 1 << 5,
            Partitions = 1 << 6,
            All = Templates | Entities | Components | Accessors | Sets | Tags | Partitions,
        }

        // Parsed form of the search field. Reused across keystrokes (Reset
        // clears the lists) so we don't allocate a new instance per change.
        sealed class ParsedSearch
        {
            public SearchScope ExplicitKind = SearchScope.All;
            public bool HasExplicitKind;

            // Negate is true when the user prefixed the token with '-'.
            // The matcher inverts the per-token result: a negated token
            // passes when the row would NOT have matched it.
            public readonly List<(string Key, string Value, bool Negate)> Predicates = new();
            public readonly List<(string Substring, bool Negate)> BareSubstrings = new();

            public bool IsEmpty =>
                !HasExplicitKind && Predicates.Count == 0 && BareSubstrings.Count == 0;

            public void Reset()
            {
                ExplicitKind = SearchScope.All;
                HasExplicitKind = false;
                Predicates.Clear();
                BareSubstrings.Clear();
            }
        }

        // Per-row data the predicate dispatch reads. Each row-build site
        // stamps the fields its kind cares about and leaves the rest at
        // default. Passed by ref so the struct doesn't get copied per
        // predicate evaluation.
        struct PredicateData
        {
            public static readonly PredicateData Empty = default;

            // Live mode — at most one of these is set, matching the row kind.
            public ResolvedTemplate ResolvedTemplate; // for templates + entity rows
            public Template AbstractTemplate; // for abstract template rows
            public Type ComponentType; // for component rows
            public SetDef Set; // for set rows (Tags carried inside)
            public Tag Tag; // for tag rows
            public string AccessorDebugName; // for accessor rows
            public WorldInfo WorldInfo; // for derived-template lookup

            // Cache mode — same idea, schema entries instead of live refs.
            public TrecsSchemaTemplate CacheTemplate;
            public TrecsSchemaComponentType CacheComponent;
            public TrecsSchemaSet CacheSet;
            public TrecsSchemaTag CacheTag;
            public TrecsSchema CacheSchemaForAccess; // for cache-mode accessor reads/writes
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
            if (ctx.CacheTemplate != null)
            {
                return key switch
                {
                    "tag" => ListContains(ctx.CacheTemplate.AllTagNames, value),
                    "c" or "component" => ListContains(ctx.CacheTemplate.ComponentTypeNames, value)
                        || ListContains(ctx.CacheTemplate.DirectComponentTypeNames, value)
                        || ListContains(ctx.CacheTemplate.InheritedComponentTypeNames, value),
                    "base" => ListContains(ctx.CacheTemplate.BaseTemplateNames, value),
                    "derived" => ListContains(ctx.CacheTemplate.DerivedTemplateNames, value),
                    _ => false,
                };
            }
            var rt = ctx.ResolvedTemplate;
            switch (key)
            {
                case "tag":
                    return rt != null
                        && !rt.AllTags.IsNull
                        && TagSetContainsName(rt.AllTags, value);
                case "c":
                case "component":
                    if (rt == null)
                        return false;
                    foreach (var d in rt.ComponentDeclarations)
                    {
                        if (d.ComponentType == null)
                            continue;
                        var n = ComponentTypeDisplayName(d.ComponentType);
                        if (n != null && n.IndexOf(value, StringComparison.OrdinalIgnoreCase) >= 0)
                        {
                            return true;
                        }
                    }
                    return false;
                case "base":
                    if (rt == null)
                        return false;
                    foreach (var b in rt.AllBaseTemplates)
                    {
                        var n = b.DebugName;
                        if (n != null && n.IndexOf(value, StringComparison.OrdinalIgnoreCase) >= 0)
                        {
                            return true;
                        }
                    }
                    return false;
                case "derived":
                    if (rt == null || ctx.WorldInfo == null)
                        return false;
                    foreach (var other in ctx.WorldInfo.ResolvedTemplates)
                    {
                        if (other.Template == rt.Template)
                            continue;
                        bool hasUs = false;
                        foreach (var b in other.AllBaseTemplates)
                        {
                            if (b == rt.Template)
                            {
                                hasUs = true;
                                break;
                            }
                        }
                        if (!hasUs)
                            continue;
                        var n = other.DebugName;
                        if (n != null && n.IndexOf(value, StringComparison.OrdinalIgnoreCase) >= 0)
                        {
                            return true;
                        }
                    }
                    return false;
                default:
                    return false;
            }
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
                    var rt = ctx.ResolvedTemplate;
                    if (rt == null)
                        return false;
                    var n = rt.DebugName;
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
            string name =
                ctx.CacheComponent != null
                    ? ctx.CacheComponent.DisplayName
                    : (
                        ctx.ComponentType != null
                            ? ComponentTypeDisplayName(ctx.ComponentType)
                            : null
                    );
            return name != null && name.IndexOf(value, StringComparison.OrdinalIgnoreCase) >= 0;
        }

        bool MatchesSetPredicate(string key, string value, in PredicateData ctx)
        {
            if (key != "tag")
                return false;
            if (ctx.CacheSet != null)
            {
                return ListContains(ctx.CacheSet.TagNames, value);
            }
            return !ctx.Set.Tags.IsNull && TagSetContainsName(ctx.Set.Tags, value);
        }

        bool MatchesTagPredicate(string key, string value, in PredicateData ctx)
        {
            // A tag "has" itself — so tag:player matches the tag named
            // player. Other predicates filter out (tags don't have
            // components/templates/etc.).
            if (key != "tag")
                return false;
            string name = ctx.CacheTag != null ? ctx.CacheTag.Name : ctx.Tag.ToString();
            return name != null && name.IndexOf(value, StringComparison.OrdinalIgnoreCase) >= 0;
        }

        bool MatchesAccessorPredicate(string key, string value, in PredicateData ctx)
        {
            if (ctx.CacheSchemaForAccess != null && !string.IsNullOrEmpty(ctx.AccessorDebugName))
            {
                return key switch
                {
                    "reads" => CacheAccessorTouchesComponent(
                        ctx.CacheSchemaForAccess,
                        ctx.AccessorDebugName,
                        value,
                        reads: true
                    ),
                    "writes" => CacheAccessorTouchesComponent(
                        ctx.CacheSchemaForAccess,
                        ctx.AccessorDebugName,
                        value,
                        reads: false
                    ),
                    _ => false,
                };
            }
            if (string.IsNullOrEmpty(ctx.AccessorDebugName) || _selectedWorld == null)
            {
                return false;
            }
            var tracker = TrecsAccessRegistry.GetTracker(_selectedWorld);
            if (tracker == null)
                return false;
            switch (key)
            {
                case "reads":
                    foreach (var id in tracker.GetReadsBy(ctx.AccessorDebugName))
                    {
                        if (ComponentIdMatches(id, value))
                            return true;
                    }
                    return false;
                case "writes":
                    foreach (var id in tracker.GetWritesBy(ctx.AccessorDebugName))
                    {
                        if (ComponentIdMatches(id, value))
                            return true;
                    }
                    return false;
                default:
                    return false;
            }
        }

        static bool CacheAccessorTouchesComponent(
            TrecsSchema schema,
            string accessorName,
            string value,
            bool reads
        )
        {
            if (schema?.Access == null)
                return false;
            foreach (var a in schema.Access)
            {
                var users = reads ? a.ReadBySystems : a.WrittenBySystems;
                if (users == null || !users.Contains(accessorName))
                    continue;
                var n = a.ComponentDisplayName;
                if (n != null && n.IndexOf(value, StringComparison.OrdinalIgnoreCase) >= 0)
                {
                    return true;
                }
            }
            return false;
        }

        static bool ComponentIdMatches(ComponentId id, string value)
        {
            try
            {
                var t = TypeIdProvider.GetTypeFromId(id.Value);
                if (t == null)
                    return false;
                var n = ComponentTypeDisplayName(t);
                return n != null && n.IndexOf(value, StringComparison.OrdinalIgnoreCase) >= 0;
            }
            catch (Exception)
            {
                return false;
            }
        }

        static bool ListContains(List<string> list, string value)
        {
            if (list == null)
                return false;
            for (int i = 0; i < list.Count; i++)
            {
                var v = list[i];
                if (v != null && v.IndexOf(value, StringComparison.OrdinalIgnoreCase) >= 0)
                {
                    return true;
                }
            }
            return false;
        }

        static bool TagSetContainsName(TagSet ts, string value)
        {
            if (ts.IsNull)
                return false;
            foreach (var t in ts.Tags)
            {
                var n = t.ToString();
                if (n != null && n.IndexOf(value, StringComparison.OrdinalIgnoreCase) >= 0)
                {
                    return true;
                }
            }
            return false;
        }

        enum RowKind
        {
            Section,
            Template,
            AbstractTemplate,
            Group,
            Entity,
            MorePlaceholder,
            AccessorPhase,
            Accessor,
            ComponentType,
            SetItem,
            TagItem,
        }

        // Single mutable row payload shared across all kinds. Each TreeView
        // item carries a reference to one of these, so periodic refresh can
        // mutate fields (Count, SystemEnabled) and a follow-up RefreshItems
        // re-binds visible rows without touching tree structure.
        sealed class RowData
        {
            public RowKind Kind;
            public string DisplayName;
            public ResolvedTemplate ResolvedTemplate;
            public Template Template;
            public GroupIndex Group;
            public TagSet PartitionTags;
            public EntityHandle EntityHandle;
            public int AccessorId;
            public int SystemIndex;
            public int? ExecutionPriority;
            public bool SystemEnabled;
            public Type ComponentType;
            public SetDef SetDef;
            public Tag Tag;
            public int Count;
            public bool ShowCount;

            // Live or cache-mode access stats (component-type rows).
            public int ReadCount;
            public int WriteCount;
            public string AccessTooltip;

            // Cache-mode payloads — populated by RebuildTreeFromCache so
            // OnTreeSelectionChanged can route clicks through the proxies'
            // SetCache(...) entry points without re-walking the schema.
            public TrecsSchemaTemplate CacheTemplate;
            public TrecsSchemaComponentType CacheComponent;
            public TrecsSchemaSet CacheSet;
            public TrecsSchemaTag CacheTag;
        }

        struct AccessorRowSpec
        {
            public int AccessorId;
            public string DisplayName;
            public int SystemIndex; // -1 for manual accessors
            public int? ExecutionPriority;
            public bool SystemEnabled;
        }
    }
}
