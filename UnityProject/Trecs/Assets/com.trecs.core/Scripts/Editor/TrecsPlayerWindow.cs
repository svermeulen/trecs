using System;
using System.Collections.Generic;
using UnityEditor;
using UnityEngine;
using UnityEngine.UIElements;

namespace Trecs.Internal
{
    /// <summary>
    /// Editor UI for <see cref="TrecsRecordingSession"/>. Layout:
    /// <list type="bullet">
    /// <item>World dropdown (one row per registered <see cref="World"/>).</item>
    /// <item>Recording header row — current recording name, state badge
    /// (LIVE/REC/PLAY), Speed dropdown, Actions ▾ menu.</item>
    /// <item>Transport row — Record button, ⏮ ◂ ▶ ▸ ⏭ navigation, Loop
    /// button (enabled in Playback only). Timeline strip with adaptive time
    /// ruler underneath — scrub/hover/marker behavior lives in
    /// <see cref="TrecsTimelineView"/>. Capacity banner when the recorder
    /// is paused against its memory cap.</item>
    /// </list>
    /// Keyboard shortcuts (Space, Home/End, ←/→, Shift+arrows) drive the
    /// same actions when the window has focus.
    /// </summary>
    internal class TrecsPlayerWindow : EditorWindow
    {
        [InitializeOnLoadMethod]
        static void RegisterEditorAccessorName() =>
            TrecsEditorAccessorNames.Register("TrecsPlayerWindow");

        // Speed presets the inline button cycles through. A discrete set
        // matches user expectations (YouTube / VLC / Replay Mod) and is more
        // useful for debug than fine-grained slider control — nobody needs
        // 0.83×.
        static readonly float[] SpeedPresets = { 0.1f, 0.25f, 0.5f, 1f, 2f, 4f, 8f };

        // Shared "this transport control is active" tint — Loop ON, Play
        // when paused. One color so the visual vocabulary stays consistent;
        // darker than Unity's stock green so the white caption / icon pops
        // against it.
        static readonly Color ActiveTransportButtonColor = new Color(0.10f, 0.45f, 0.18f);

        // Label shown for the recording-name slot when an in-memory buffer
        // is being captured but hasn't been saved to disk yet.
        const string UnsavedRecordingDisplayName = "(unsaved)";

        // Label shown when the recorder is stopped (no controller, the
        // Record button hasn't been pressed yet, or just-stopped) — no
        // in-memory buffer exists, so "(unsaved)" would falsely imply
        // there's something to save.
        const string NoRecordingDisplayName = "(no recording)";

        const string DefaultBadgeTooltip =
            "Recorder state.\n"
            + "LIVE — not recording (Record hasn't been pressed, or "
            + "Auto-record on start is off).\n"
            + "REC — recording at the live edge of the buffer.\n"
            + "PLAY — scrubbed back into the buffer or playing through a "
            + "loaded recording. Pauses at the tail by default; press "
            + "Record (R) to fork and go live, or Loop (L) to repeat.\n"
            + "⏸ suffix means paused.";

        // Tooltips for the context-sensitive Record button. UpdateRecordButton
        // swaps between these per tick so the text always describes what
        // clicking will actually do right now.
        const string RecordButtonNoControllerTooltip =
            "Record (disabled): no Trecs world is currently loaded. Enter "
            + "Play mode — the Player window auto-attaches to active worlds.";
        const string RecordButtonIdleTooltip =
            "Record (R) — start capturing snapshots from the current frame.";
        const string RecordButtonRecordingTooltip =
            "Stop recording (R) — drop the buffer and return to live " + "(no capture).";
        const string RecordButtonPlaybackTooltip =
            "Fork at current frame (R) — commit snapshots up to here as the "
            + "new recording and continue capturing live past this point. "
            + "Trailing snapshots are dropped.";
        const string LoopButtonInactiveTooltip =
            "Loop playback (L) — when on, the playhead jumps back to the "
            + "first snapshot on reaching the end instead of pausing.";
        const string LoopButtonActiveTooltip =
            "Loop playback ON (L) — click to disable. Reaching the end "
            + "will rewind to the first snapshot; turn off to pause at "
            + "the end instead.";
        const string LoopButtonDisabledTooltip =
            "Loop playback (L) — only available during Playback (scrub "
            + "back from the live edge or load a saved recording first).";
        const string BookmarkButtonEnabledTooltip =
            "Bookmark this frame (B) — capture a labelled bookmark at the "
            + "current frame. Bookmarks are saved with the recording and "
            + "appear as bright markers on the timeline; click a marker to "
            + "jump back, right-click to remove.";
        const string BookmarkButtonDisabledTooltip =
            "Bookmark (disabled) — start recording first. Bookmarks live "
            + "in the in-memory recording buffer.";

        DropdownField _worldDropdown;
        Label _emptyState;

        // Help opens via the Actions ▾ menu's Help… item — see ShowHelp().
        // Recording start/stop is the transport-row Record button (not a
        // header toggle anymore); the auto-on-play preference lives in
        // Player Settings as "Auto-record on start" backed by
        // TrecsGameStateActivator.AutoRecordEnabled.

        // Recording header row: current-recording name + persistence actions.
        VisualElement _recordingHeaderRow;
        Label _recordingNameLabel;

        // Single "Actions ▾" dropdown that replaces the older
        // New/Save/Save As/Load/Delete button row — those took too much
        // horizontal real estate. The button opens a GenericMenu with the
        // same actions; Save/Delete go disabled when they don't apply.
        Button _recordingActionsButton;
        Label _recordingStatusLabel;
        IVisualElementScheduledItem _recordingStatusClearTask;
        const long RecordingStatusClearDelayMs = 4000;

        // The "current recording name" is owned by the recorder (set on
        // load / save, cleared on reset / fork / fresh-start) so that any
        // surface acting on the same controller — the Saves window's
        // load, the Player's save / save-as, etc. — stays in sync. Read
        // through this helper rather than caching it.
        string CurrentRecordingName => GetController()?.LoadedRecordingName;

        // Controller we're currently subscribed to for LoadedRecordingChanged.
        // Tracked separately from _selectedWorld because the controller for
        // a world can register/unregister independently of selection.
        TrecsRecordingSession _subscribedController;

        // Transport row: Record button, 5 nav buttons, Loop button (always
        // visible, enabled in Playback only). The Record button is context-
        // sensitive: Idle→start, Recording→stop, Playback→fork (the Fork
        // action moved out of the Actions ▾ menu now that there's a
        // transport-row home for it). Loop is a session-local toggle on the
        // recorder; see TrecsRewindBuffer.LoopPlayback. The state badge and
        // Speed dropdown live up in the recording header row; see
        // BuildRecordingHeaderRow.
        VisualElement _transportPanel;
        Label _stateBadge;
        Button _recordButton;
        Button _jumpStartButton;
        Button _stepBackButton;
        Button _playPauseButton;
        Image _playPauseIcon;
        Button _stepForwardButton;
        Button _jumpEndButton;
        Button _loopButton;

        // Snapshot capture: prompts for a label and forwards to
        // TrecsRewindBuffer.CaptureBookmarkAtCurrentFrame. Disabled when
        // not recording (matches the recorder's own no-op-while-stopped
        // contract). Sits at the right of the transport row, after Loop,
        // so the primary playback controls stay grouped to the left.
        Button _bookmarkCaptureButton;

        // Trim lives in the Actions ▾ menu (Playback-only); see
        // OnRecordingActionsClicked.

        // Cached editor icons for the transport buttons. Loaded lazily once
        // per domain reload via EditorGUIUtility.IconContent so we get the
        // correct light/dark variants automatically.
        static Texture2D _iconJumpStart;
        static Texture2D _iconStepBack;
        static Texture2D _iconPlay;
        static Texture2D _iconPause;
        static Texture2D _iconStepForward;
        static Texture2D _iconJumpEnd;
        static Texture2D _iconMenu;
        static Texture2D _iconRecord;

        // Unity has no clean "loop" icon in the public icon set; we fall
        // back to a Unicode "↻" if the lookup fails.
        static Texture2D _iconLoop;

        // Snapshot icon: Unity ships "Favorite" (a yellow star) which reads
        // well as "pin this moment". Falls back to a Unicode "★" when the
        // icon isn't available on a given Unity version.
        static Texture2D _iconBookmark;

        // Timeline strip: slider scrubber with hover indicator + frame
        // readout, adaptive time ruler, keyframe / bookmark marker overlay,
        // and the scrub-drag state machine. See TrecsTimelineView.
        TrecsTimelineView _timeline;

        // Subtle bottom-right stats line: snapshot count, frame span, byte
        // size. Tucked away so it doesn't dominate the UI but available for
        // a glance at the buffer's footprint.
        Label _bufferInfoLabel;
        Label _capacityBanner;

        // Surfaces a desync detected during Playback (simulation re-ran from
        // an earlier snapshot and produced a different state at a frame where
        // we had captured one). Sticks until the buffer "moves" (Reset, Fork,
        // JumpToFrame, load) so the user has time to notice and inspect.
        Label _desyncBanner;
#if DEBUG
        Button _dumpDesyncDiffButton;
#endif

        // Manual per-control tooltips — UIElements' built-in TooltipEvent
        // dispatch is unreliable for buttons in this window; see
        // TrecsManualTooltips for the full rationale.
        TrecsManualTooltips _tooltips;

        // Speed dropdown: a single button labelled with the current
        // multiplier ("1×") that opens a GenericMenu of preset speeds.
        Button _speedButton;

        World _selectedWorld;
        WorldAccessor _selectedAccessor;
        readonly List<World> _dropdownWorlds = new();

        [MenuItem("Window/Trecs/Player")]
        public static void ShowWindow()
        {
            var window = GetWindow<TrecsPlayerWindow>();
            window.titleContent = new GUIContent("Trecs Player");
            window.minSize = new Vector2(320, 260);
        }

        void OnEnable()
        {
            // Auto-attach must come first so by the time we subscribe to
            // ControllerRegistered below, any already-active worlds have had
            // their controllers constructed and registered.
            TrecsEditorRecordingAutoAttach.Activate();

            WorldRegistry.WorldRegistered += OnWorldRegistered;
            WorldRegistry.WorldUnregistered += OnWorldUnregistered;
            TrecsRecordingSessionRegistry.ControllerRegistered +=
                OnControllerRegisteredOrUnregistered;
            TrecsRecordingSessionRegistry.ControllerUnregistered +=
                OnControllerRegisteredOrUnregistered;
            TrecsEditorSelection.ActiveWorldChanged += OnSharedActiveWorldChanged;

            // Auto-start any controllers that registered before this window
            // opened — so the user can press Play first, then open the window
            // without losing access to the buffer (and without missing frames
            // beyond the cap, since recording auto-pauses on capacity).
            TrecsGameStateActivator.StartAllIdleControllers();
        }

        void OnDisable()
        {
            WorldRegistry.WorldRegistered -= OnWorldRegistered;
            WorldRegistry.WorldUnregistered -= OnWorldUnregistered;
            TrecsRecordingSessionRegistry.ControllerRegistered -=
                OnControllerRegisteredOrUnregistered;
            TrecsRecordingSessionRegistry.ControllerUnregistered -=
                OnControllerRegisteredOrUnregistered;
            TrecsEditorSelection.ActiveWorldChanged -= OnSharedActiveWorldChanged;
            UnsubscribeFromController();
            TrecsReplayHelpPopup.CloseIfOpen();
            ClearAccessor();

            // Deactivate last so the controllers are still alive while we
            // unhook from registries above. The refcount holds them alive
            // when another Player window is also open.
            TrecsEditorRecordingAutoAttach.Deactivate();
        }

        // Keep _subscribedController in sync with the controller for the
        // currently-selected world. Called whenever world selection or
        // controller registration changes.
        void RefreshControllerSubscription()
        {
            var controller = GetController();
            if (_subscribedController == controller)
                return;
            UnsubscribeFromController();
            _subscribedController = controller;
            if (_subscribedController != null)
            {
                _subscribedController.LoadedRecordingChanged += UpdateRecordingHeader;
            }
            UpdateRecordingHeader();
        }

        void UnsubscribeFromController()
        {
            if (_subscribedController != null)
            {
                _subscribedController.LoadedRecordingChanged -= UpdateRecordingHeader;
                _subscribedController = null;
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

        void OnControllerRegisteredOrUnregistered(TrecsRecordingSession _)
        {
            // Header label may need to flip between "(unsaved)" and any
            // backing-file name once a controller appears or disappears.
            // Re-subscribing covers both transitions and refreshes the label.
            RefreshControllerSubscription();
        }

        void CreateGUI()
        {
            // UI collaborators are rebuilt with the rest of the visual tree
            // on every CreateGUI (window open, domain reload).
            _tooltips = new TrecsManualTooltips(rootVisualElement);
            _timeline = new TrecsTimelineView(
                rootVisualElement,
                jumpToFrame: frame => GetController()?.JumpToFrame(frame),
                removeBookmark: (frame, label) =>
                    RemoveBookmarkAndRefresh(GetController()?.AutoRecorder, frame, label),
                currentFrame: () => _selectedAccessor?.FixedFrame,
                fixedDeltaTime: () => TryGetFixedDeltaTime(out var dt) ? dt : 0f
            );

            var scroll = new ScrollView(ScrollViewMode.Vertical);
            scroll.style.flexGrow = 1;
            // Header/transport rows can grow wider than the window when it's
            // dragged narrow (each button is fixed-width), which would otherwise
            // surface a horizontal scrollbar. Buttons clipping off-screen is
            // preferable to a scrollbar — the user can just widen the window.
            scroll.horizontalScrollerVisibility = ScrollerVisibility.Hidden;
            rootVisualElement.Add(scroll);

            var root = scroll.contentContainer;
            root.style.paddingLeft = 8;
            root.style.paddingRight = 8;
            root.style.paddingTop = 8;
            root.style.paddingBottom = 8;

            _worldDropdown = new DropdownField("World", new List<string>(), 0);
            _worldDropdown.tooltip =
                "Trecs World whose recorder this window drives. One row per "
                + "registered world (typically host + client).";
            _worldDropdown.RegisterValueChangedCallback(OnWorldDropdownChanged);
            root.Add(_worldDropdown);

            _emptyState = new Label("No active worlds (enter Play mode)");
            _emptyState.style.marginTop = 8;
            _emptyState.style.opacity = 0.7f;
            root.Add(_emptyState);

            _recordingHeaderRow = BuildRecordingHeaderRow();
            root.Add(_recordingHeaderRow);

            _transportPanel = BuildTransportPanel();
            root.Add(_transportPanel);

            // Bottom-right stats overlay. Sits on rootVisualElement (above
            // the scroll content) so it stays fixed in place as the scroll grows
            // and doesn't fight transport-panel layout. Picking is off so
            // it never intercepts clicks on whatever's underneath.
            _bufferInfoLabel = new Label();
            _bufferInfoLabel.style.position = Position.Absolute;
            _bufferInfoLabel.style.bottom = 4;
            _bufferInfoLabel.style.right = 8;
            _bufferInfoLabel.style.fontSize = 10;
            _bufferInfoLabel.style.opacity = 0.45f;
            _bufferInfoLabel.style.color = Color.white;
            _bufferInfoLabel.pickingMode = PickingMode.Ignore;
            _bufferInfoLabel.tooltip =
                "In-memory buffer summary: source mode · snapshot count · "
                + "frame span (and elapsed seconds) · total bytes.";
            rootVisualElement.Add(_bufferInfoLabel);

            // Bottom-left transient status overlay (mirrors _bufferInfoLabel
            // on the opposite corner). Used for action feedback like
            // "Saved 'foo'" or "Recording disabled" — text auto-clears after
            // RecordingStatusClearDelayMs. Sits on rootVisualElement so the
            // header row no longer reserves vertical space for an
            // empty-most-of-the-time status line.
            _recordingStatusLabel = new Label();
            _recordingStatusLabel.style.position = Position.Absolute;
            _recordingStatusLabel.style.bottom = 4;
            _recordingStatusLabel.style.left = 8;
            _recordingStatusLabel.style.fontSize = 10;
            // Background + padding keep the text readable when it grows
            // long enough to overlap the bottom-right buffer-info overlay.
            // Use explicit alpha on text/background instead of style.opacity
            // so the dimming doesn't fade the background too.
            _recordingStatusLabel.style.color = new Color(1f, 1f, 1f, 0.85f);
            _recordingStatusLabel.style.backgroundColor = new Color(0f, 0f, 0f, 0.65f);
            _recordingStatusLabel.style.paddingLeft = 4;
            _recordingStatusLabel.style.paddingRight = 4;
            _recordingStatusLabel.style.paddingTop = 1;
            _recordingStatusLabel.style.paddingBottom = 1;
            _recordingStatusLabel.style.borderTopLeftRadius = 2;
            _recordingStatusLabel.style.borderTopRightRadius = 2;
            _recordingStatusLabel.style.borderBottomLeftRadius = 2;
            _recordingStatusLabel.style.borderBottomRightRadius = 2;
            _recordingStatusLabel.style.display = DisplayStyle.None;
            _recordingStatusLabel.pickingMode = PickingMode.Ignore;
            rootVisualElement.Add(_recordingStatusLabel);

            RebuildDropdown();
            UpdateRecordingHeader();
            root.schedule.Execute(RefreshTick)
                .Every(TrecsDebugWindowSettings.Get().RefreshIntervalMs);

            rootVisualElement.focusable = true;
            rootVisualElement.RegisterCallback<KeyDownEvent>(OnKeyDown);
        }

        void OnKeyDown(KeyDownEvent evt)
        {
            // Don't steal keys when a TextField has focus (snapshot/recording
            // name typing). The event target is usually the TextField's
            // internal TextElement so walk up for the ancestor check.
            if (evt.target is VisualElement target && IsInsideTextField(target))
            {
                return;
            }
            switch (evt.keyCode)
            {
                case KeyCode.Space:
                    OnPlayPauseClicked();
                    evt.StopPropagation();
                    break;
                case KeyCode.Home:
                    OnJumpStartClicked();
                    evt.StopPropagation();
                    break;
                case KeyCode.End:
                    OnJumpEndClicked();
                    evt.StopPropagation();
                    break;
                case KeyCode.LeftArrow:
                    if (evt.shiftKey)
                    {
                        GetController()?.JumpToPreviousKeyframe();
                    }
                    else
                    {
                        OnStepBackClicked();
                    }
                    evt.StopPropagation();
                    break;
                case KeyCode.RightArrow:
                    if (evt.shiftKey)
                    {
                        GetController()?.JumpToNextKeyframe();
                    }
                    else
                    {
                        OnStepForwardClicked();
                    }
                    evt.StopPropagation();
                    break;
                case KeyCode.R:
                    // R routes to the same context-sensitive handler as the
                    // Record button: Idle→start, Recording→stop, Playback→
                    // fork. Same enable/refuse semantics so the keyboard
                    // and the click do exactly the same thing.
                    OnRecordButtonClicked();
                    evt.StopPropagation();
                    break;
                case KeyCode.L:
                    // L mirrors the Loop button — only act when the button
                    // would be enabled (Playback). Setting LoopPlayback
                    // outside Playback would be silently honoured by the
                    // recorder but the visible button would stay greyed,
                    // which would be a confusing state mismatch.
                    if (GetController()?.CurrentMode == GameStateMode.Playback)
                    {
                        OnLoopButtonClicked();
                    }
                    evt.StopPropagation();
                    break;
                case KeyCode.B:
                    // B mirrors the Snapshot capture button. Only act when
                    // recording — outside that state the button is disabled
                    // and the recorder would no-op anyway, so we'd surface
                    // a misleading "Snapshot unavailable" status if we ran
                    // unconditionally.
                    if (GetController()?.AutoRecorder?.IsRecording == true)
                    {
                        OnBookmarkCaptureClicked();
                    }
                    evt.StopPropagation();
                    break;
            }
        }

        VisualElement BuildTransportPanel()
        {
            var panel = new VisualElement();
            panel.style.marginTop = 12;

            var row = new VisualElement();
            row.style.flexDirection = FlexDirection.Row;
            row.style.alignItems = Align.Center;

            EnsureTransportIcons();

            // Record button sits left of the navigation buttons because it's
            // the primary state-changing action — start, stop, or fork
            // depending on current mode. Tooltip is updated per-tick from
            // UpdateRecordButton so it always describes the action that will
            // happen if you click *now*.
            _recordButton = MakeIconTransportButton(_iconRecord, "●", OnRecordButtonClicked);
            _tooltips.Apply(_recordButton, RecordButtonIdleTooltip);
            row.Add(_recordButton);

            _jumpStartButton = MakeIconTransportButton(_iconJumpStart, "⏮", OnJumpStartClicked);
            _tooltips.Apply(_jumpStartButton, "Jump to start of buffer (Home)");
            _stepBackButton = MakeIconTransportButton(_iconStepBack, "◂", OnStepBackClicked);
            _tooltips.Apply(
                _stepBackButton,
                "Step back one frame (←)\nShift+← jumps to previous snapshot"
            );
            _playPauseButton = MakeIconTransportButton(_iconPlay, "▶", OnPlayPauseClicked);
            _playPauseIcon = _playPauseButton.Q<Image>();
            _tooltips.Apply(_playPauseButton, "Play / Pause (Space)");
            _stepForwardButton = MakeIconTransportButton(
                _iconStepForward,
                "▸",
                OnStepForwardClicked
            );
            _tooltips.Apply(
                _stepForwardButton,
                "Step forward one frame (→)\nShift+→ jumps to next snapshot"
            );
            _jumpEndButton = MakeIconTransportButton(_iconJumpEnd, "⏭", OnJumpEndClicked);
            _tooltips.Apply(_jumpEndButton, "Go to live edge (End)");
            row.Add(_jumpStartButton);
            row.Add(_stepBackButton);
            row.Add(_playPauseButton);
            row.Add(_stepForwardButton);
            row.Add(_jumpEndButton);

            // Loop controls whether the playhead jumps back to the start
            // when it reaches the end of the loaded recording, instead of
            // pausing. Always visible so the layout is stable; disabled
            // outside Playback (where it has no effect since the recorder
            // only consults LoopPlayback at the loaded recording's tail).
            _loopButton = MakeIconTransportButton(_iconLoop, "↻", OnLoopButtonClicked);
            _tooltips.Apply(_loopButton, LoopButtonDisabledTooltip);
            _loopButton.SetEnabled(false);
            row.Add(_loopButton);

            // Snapshot capture: prompts for a label and writes a labelled
            // snapshot at the current fixed frame. Disabled outside the
            // Recording mode — CaptureSnapshotAtCurrentFrame is a no-op
            // there anyway, so the button reflects what'll actually
            // happen on click.
            _bookmarkCaptureButton = MakeIconTransportButton(
                _iconBookmark,
                "★",
                OnBookmarkCaptureClicked
            );
            _tooltips.Apply(_bookmarkCaptureButton, BookmarkButtonDisabledTooltip);
            _bookmarkCaptureButton.SetEnabled(false);
            // A small left margin separates the snapshot from the loop
            // button visually — these aren't both transport-direction
            // controls, so a thin gap reads as "different concern".
            _bookmarkCaptureButton.style.marginLeft = 6;
            row.Add(_bookmarkCaptureButton);

            // State badge and Speed dropdown live in the recording header
            // row, not here — see BuildRecordingHeaderRow. That keeps this
            // row tight to its purpose (transport-only, directly above the
            // slider).

            panel.Add(row);

            // Slider scrubber + hover readout + adaptive ruler + keyframe /
            // bookmark markers — see TrecsTimelineView.
            _timeline.BuildInto(panel);

            _capacityBanner = new Label();
            _capacityBanner.tooltip =
                "The recorder hit its configured cap and paused fixed phase to "
                + "prevent runaway memory. Save / Fork / Reset to relieve.";
            _capacityBanner.style.marginTop = 4;
            _capacityBanner.style.paddingLeft = 6;
            _capacityBanner.style.paddingRight = 6;
            _capacityBanner.style.paddingTop = 2;
            _capacityBanner.style.paddingBottom = 2;
            _capacityBanner.style.color = Color.white;
            _capacityBanner.style.backgroundColor = new Color(0.6f, 0.25f, 0.05f);
            _capacityBanner.style.unityFontStyleAndWeight = FontStyle.Bold;
            _capacityBanner.style.whiteSpace = WhiteSpace.Normal;
            _capacityBanner.style.display = DisplayStyle.None;
            panel.Add(_capacityBanner);

            _desyncBanner = new Label();
            _desyncBanner.tooltip =
                "The simulation produced a different world state than originally "
                + "captured at this frame. Indicates non-determinism in your code or "
                + "data — common causes: reading Time.time, using Random outside "
                + "the deterministic RNG, mutating shared static state, or ordering "
                + "issues in component access.";
            _desyncBanner.style.marginTop = 4;
            _desyncBanner.style.paddingLeft = 6;
            _desyncBanner.style.paddingRight = 6;
            _desyncBanner.style.paddingTop = 2;
            _desyncBanner.style.paddingBottom = 2;
            _desyncBanner.style.color = Color.white;
            _desyncBanner.style.backgroundColor = new Color(0.7f, 0.15f, 0.15f);
            _desyncBanner.style.unityFontStyleAndWeight = FontStyle.Bold;
            _desyncBanner.style.whiteSpace = WhiteSpace.Normal;
            _desyncBanner.style.display = DisplayStyle.None;
            panel.Add(_desyncBanner);

#if DEBUG
            _dumpDesyncDiffButton = new Button(() =>
            {
                var recorder = GetController()?.AutoRecorder;
                if (recorder == null || !recorder.HasDesynced)
                    return;
                var result = recorder.DumpDesyncDiff();
                if (result.HasValue)
                {
                    EditorUtility.RevealInFinder(result.Value.recordedPath);
                }
            })
            {
                text = "Dump Desync Diff",
                tooltip =
                    "Write both snapshots as flat-path text files and reveal in Finder. Diff the two files to see which fields diverged.",
            };
            _dumpDesyncDiffButton.style.marginTop = 2;
            _dumpDesyncDiffButton.style.display = DisplayStyle.None;
            panel.Add(_dumpDesyncDiffButton);
#endif

            return panel;
        }

        // Builds an icon-styled transport button. Uses Unity's built-in
        // editor icons so we get crisp, monochrome glyphs that match the
        // surrounding editor chrome — much cleaner than Unicode characters
        // (⏮ ◀ ▶ ⏭) which macOS renders as colored emoji-style boxes.
        // Falls back to plain text if the icon couldn't be loaded. Caller
        // registers the tooltip via _tooltips.Apply after construction.
        static Button MakeIconTransportButton(Texture2D icon, string fallbackText, Action onClick)
        {
            var b = new Button(onClick);
            b.style.width = 30;
            b.style.height = 22;
            b.style.marginLeft = 1;
            b.style.marginRight = 1;
            b.style.paddingLeft = 0;
            b.style.paddingRight = 0;
            b.style.paddingTop = 0;
            b.style.paddingBottom = 0;
            b.style.alignItems = Align.Center;
            b.style.justifyContent = Justify.Center;
            if (icon != null)
            {
                var img = new Image
                {
                    image = icon,
                    scaleMode = ScaleMode.ScaleToFit,
                    pickingMode = PickingMode.Ignore,
                };
                img.style.width = 14;
                img.style.height = 14;
                b.Add(img);
            }
            else
            {
                b.text = fallbackText;
            }
            return b;
        }

        static void EnsureTransportIcons()
        {
            // EditorGUIUtility.IconContent returns a GUIContent whose .image
            // is the appropriate light/dark variant for the active skin. The
            // icons are managed by Unity, so we just cache the texture
            // reference; no manual disposal needed.
            _iconJumpStart ??= LoadEditorIcon("Animation.FirstKey");
            _iconStepBack ??= LoadEditorIcon("Animation.PrevKey");
            _iconPlay ??= LoadEditorIcon("PlayButton");
            _iconPause ??= LoadEditorIcon("PauseButton");
            _iconStepForward ??= LoadEditorIcon("Animation.NextKey");
            _iconJumpEnd ??= LoadEditorIcon("Animation.LastKey");
            _iconMenu ??= LoadEditorIcon("_Menu");
            _iconRecord ??= LoadEditorIcon("Animation.Record");
            // No reliable cross-version Unity loop icon; "↻" text fallback
            // is fine. preAudioLoopOff/On are internal and not always present.
            _iconLoop ??= LoadEditorIcon("preAudioLoopOff");
            // "Favorite" is Unity's standard snapshot/star icon — used in
            // the Project window, etc. "★" is the text fallback when the
            // editor icon set doesn't expose it.
            _iconBookmark ??= LoadEditorIcon("Favorite");
        }

        static Texture2D LoadEditorIcon(string name)
        {
            try
            {
                var content = EditorGUIUtility.IconContent(name);
                return content?.image as Texture2D;
            }
            catch (Exception)
            {
                return null;
            }
        }

        // ---- Help popup ----
        //
        // The body lives in its own EditorWindow (TrecsReplayHelpPopup)
        // because the manual-tooltip plumbing is bounded by the editor
        // window's rectangle, and 70 lines of help just don't fit. Opens
        // just below the Actions ▾ button.

        void ShowHelp()
        {
            // ShowAtMouse takes a screen-space origin; aim it just under the
            // Actions button so the popup appears in a predictable place
            // (close to the menu the user just opened) instead of wherever
            // the cursor happened to be.
            var origin =
                _recordingActionsButton != null
                    ? _recordingActionsButton.worldBound
                    : rootVisualElement.worldBound;
            var screenPos = new Vector2(position.x + origin.x, position.y + origin.yMax + 4f);
            TrecsReplayHelpPopup.ShowAtMouse(screenPos, HelpBodyText);
        }

        const string HelpBodyText =
            "Record your simulation, scrub back to any earlier moment, and "
            + "replay forward — useful for diagnosing transient bugs.\n\n"
            + "<b>Transport row</b>\n"
            + "  ●  Record    Context-sensitive (R):\n"
            + "                LIVE → start capturing,\n"
            + "                REC  → stop capturing,\n"
            + "                PLAY → fork at the scrub frame (commit "
            + "the captured frames up to here as the new live edge; the "
            + "trailing buffer is dropped).\n"
            + "  ⏮ ◂ ▶ ▸ ⏭   Standard transport.\n"
            + "  ↻  Loop (L)  Enabled in PLAY only. When on, reaching "
            + "the end of the recording rewinds to the buffer start "
            + "instead of pausing. Resets each playback session.\n"
            + "  ★  Bookmark (B)  Enabled while recording. Prompts for a "
            + "label and pins a labelled bookmark at the current frame; "
            + "appears as a yellow marker on the timeline. Click a "
            + "marker to jump there, right-click to remove. Bookmarks "
            + "are saved with the recording.\n\n"
            + "<b>Keyboard shortcuts</b>\n"
            + "  Space          Play / Pause\n"
            + "  Home / End     Jump to buffer start / live edge\n"
            + "  ← / →          Step back / forward one frame\n"
            + "  Shift+← / →    Jump to previous / next keyframe\n"
            + "  R              Record (start / stop / fork — see above)\n"
            + "  L              Loop (PLAY only)\n"
            + "  B              Bookmark frame (recording only)\n\n"
            + "<b>State badge</b>\n"
            + "  LIVE  Not recording (Record hasn't been pressed, or "
            + "Auto-record on start is off)\n"
            + "  REC   Recording at the live edge\n"
            + "  PLAY  Scrubbed back, or playing a loaded recording\n"
            + "(The play/pause button turns green when paused.)\n\n"
            + "<b>Recording vs Playback</b>\n"
            + "You're in Playback whenever you've scrubbed back from the "
            + "live edge or loaded a recording from disk. Live input "
            + "systems are silenced; the inputs captured during recording "
            + "drive the world instead, so pressing Play replays the "
            + "session verbatim. At the recording's tail the playhead "
            + "always pauses (unless Loop is on) — to continue past it "
            + "into live capture, press Record (Fork).\n\n"
            + "<b>Auto-record on start</b>\n"
            + "Settings → 'Auto-record on start' controls whether the "
            + "recorder begins capturing the moment a Trecs world appears "
            + "in play mode (window must be open). Off → press Record to "
            + "begin capturing on demand. State persists across editor "
            + "sessions.\n\n"
            + "<b>Trim (Playback only)</b>\n"
            + "Trim drops captured frames before or after the current frame "
            + "to shrink the in-memory buffer (saved files on disk are "
            + "untouched). Lives in the ▾ menu.\n\n"
            + "<b>Live vs loaded recording</b>\n"
            + "  Live: in-memory buffer; header shows '(unsaved)' until "
            + "you Save Recording.\n"
            + "  Loaded: a saved recording from disk; the trailing buffer "
            + "is preserved during scrub so you can replay the same "
            + "moment repeatedly.\n\n"
            + "<b>Recordings vs snapshots</b>\n"
            + "  Recording: a time-range capture you can scrub through.\n"
            + "  Snapshot: a single-frame world state, useful as a QA "
            + "repro fixture or a 'revert here later' pin. Loading a "
            + "snapshot stops the current recording and (if Auto-record on "
            + "start is on) starts a fresh one from the snapshot's frame.\n\n"
            + "<b>▾ menu</b>\n"
            + "Recording actions (New / Save / Load / Delete), Snapshot "
            + "actions (Save / Load), Trim, Settings, and this Help.\n\n"
            + "<b>Trecs Saves window</b>\n"
            + "Library view of all on-disk saves (recordings + snapshots) "
            + "with search, rename, and delete. Open from "
            + "Window ▸ Trecs ▸ Saves.";

        // Top-level header row showing the in-memory recording's name (or
        // "(unsaved)") with persistence actions. Replaces the older Saved
        // Recordings + Named Snapshots foldouts; standalone snapshots are
        // intentionally dropped — the recording is the single unit users
        // think about.
        VisualElement BuildRecordingHeaderRow()
        {
            var panel = new VisualElement();
            panel.style.marginTop = 10;

            var row = new VisualElement();
            row.style.flexDirection = FlexDirection.Row;
            row.style.alignItems = Align.Center;

            _recordingNameLabel = new Label(UnsavedRecordingDisplayName);
            _recordingNameLabel.style.flexGrow = 1;
            // Long names previously pushed the action buttons off-screen and
            // triggered a horizontal scroll bar. Clip with ellipsis instead;
            // the full name is available via the manual tooltip on hover.
            _recordingNameLabel.style.flexShrink = 1;
            _recordingNameLabel.style.minWidth = 0;
            _recordingNameLabel.style.overflow = Overflow.Hidden;
            _recordingNameLabel.style.textOverflow = TextOverflow.Ellipsis;
            _recordingNameLabel.style.whiteSpace = WhiteSpace.NoWrap;
            _recordingNameLabel.style.unityFontStyleAndWeight = FontStyle.Italic;
            _recordingNameLabel.style.opacity = 0.7f;
            _recordingNameLabel.style.marginRight = 4;
            _tooltips.Apply(
                _recordingNameLabel,
                "Name of the on-disk file backing the in-memory recording. "
                    + "'(unsaved)' until you Save."
            );
            row.Add(_recordingNameLabel);

            // Recording start/stop now lives in the transport row's Record
            // button (context-sensitive: starts in Idle, stops in Recording,
            // forks in Playback). The "auto-record on start" preference
            // moved into Player Settings — TrecsGameStateActivator.
            // AutoRecordEnabled is still the underlying EditorPref.

            // State badge sits on the right of the header rather than
            // inline with the transport row — keeps the play buttons
            // tight against the slider, and the LIVE/REC/PLAY status
            // ends up in a more glanceable corner-of-window position.
            _stateBadge = new Label("LIVE");
            _stateBadge.tooltip = DefaultBadgeTooltip;
            _stateBadge.style.unityFontStyleAndWeight = FontStyle.Bold;
            _stateBadge.style.fontSize = 10;
            _stateBadge.style.minWidth = 56;
            _stateBadge.style.unityTextAlign = TextAnchor.MiddleCenter;
            _stateBadge.style.paddingLeft = 6;
            _stateBadge.style.paddingRight = 6;
            _stateBadge.style.paddingTop = 2;
            _stateBadge.style.paddingBottom = 2;
            _stateBadge.style.marginRight = 4;
            _stateBadge.style.borderTopLeftRadius = 3;
            _stateBadge.style.borderTopRightRadius = 3;
            _stateBadge.style.borderBottomLeftRadius = 3;
            _stateBadge.style.borderBottomRightRadius = 3;
            row.Add(_stateBadge);

            // Speed dropdown also moves up here — it's a session-rate
            // setting, not a per-frame control, so grouping it with the
            // other "header-level" affordances reads better than burying
            // it beside the play buttons.
            EnsureTransportIcons();
            _speedButton = new Button(OnSpeedButtonClicked) { text = FormatSpeedLabel(1f) };
            _speedButton.style.minWidth = 44;
            _speedButton.style.marginRight = 4;
            _tooltips.Apply(
                _speedButton,
                "Simulation speed. Click for presets (0.1×, 0.25×, 0.5×, 1×, 2×). "
                    + "Shown amber when not at real-time."
            );
            row.Add(_speedButton);

            // Unity's built-in _Menu icon (the kebab/three-dot glyph used
            // throughout the editor for "more options" menus). Users seek
            // this menu for the less-frequent file operations and Trim;
            // the primary recording controls live in the transport row.
            // Falls back to a text "▾" if the icon couldn't load.
            _recordingActionsButton = new Button(OnRecordingActionsClicked);
            _recordingActionsButton.style.marginLeft = 6;
            _recordingActionsButton.style.minWidth = 22;
            _recordingActionsButton.style.height = 22;
            _recordingActionsButton.style.paddingLeft = 0;
            _recordingActionsButton.style.paddingRight = 0;
            _recordingActionsButton.style.paddingTop = 0;
            _recordingActionsButton.style.paddingBottom = 0;
            _recordingActionsButton.style.alignItems = Align.Center;
            _recordingActionsButton.style.justifyContent = Justify.Center;
            if (_iconMenu != null)
            {
                var img = new Image
                {
                    image = _iconMenu,
                    scaleMode = ScaleMode.ScaleToFit,
                    pickingMode = PickingMode.Ignore,
                };
                img.style.width = 14;
                img.style.height = 14;
                _recordingActionsButton.Add(img);
            }
            else
            {
                _recordingActionsButton.text = "▾";
            }
            _tooltips.Apply(_recordingActionsButton, "More actions");
            row.Add(_recordingActionsButton);

            // Settings, Help, and (in Playback) Trim live in the Actions ▾
            // menu — see OnRecordingActionsClicked. Header row layout:
            // recording name (left, flex) | state badge | speed | Actions ▾.
            // Recording start/stop and Loop are transport-row controls.

            panel.Add(row);

            return panel;
        }

        void OnWorldRegistered(World world) => RebuildDropdown();

        void OnWorldUnregistered(World world) => RebuildDropdown();

        void RebuildDropdown()
        {
            if (_worldDropdown == null)
            {
                return;
            }

            _dropdownWorlds.Clear();
            var labels = new List<string>();
            var active = WorldRegistry.ActiveWorlds;
            for (int i = 0; i < active.Count; i++)
            {
                _dropdownWorlds.Add(active[i]);
                labels.Add(active[i].DebugName ?? $"World #{i}");
            }

            _worldDropdown.choices = labels;

            if (_dropdownWorlds.Count == 0)
            {
                _worldDropdown.style.display = DisplayStyle.None;
                _emptyState.style.display = DisplayStyle.Flex;
                SetMainPanelsEnabled(false);
                SelectWorld(null);
                return;
            }

            // Hide the dropdown row entirely in the single-world case; it's
            // wasted vertical space when there's nothing to choose between.
            _worldDropdown.style.display =
                _dropdownWorlds.Count > 1 ? DisplayStyle.Flex : DisplayStyle.None;
            _emptyState.style.display = DisplayStyle.None;
            SetMainPanelsEnabled(true);

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

        void OnWorldDropdownChanged(ChangeEvent<string> evt)
        {
            var index = _worldDropdown.index;
            if (index >= 0 && index < _dropdownWorlds.Count)
            {
                SelectWorld(_dropdownWorlds[index]);
            }
        }

        void SelectWorld(World world)
        {
            if (_selectedWorld == world)
            {
                return;
            }
            ClearAccessor();
            _selectedWorld = world;
            // Re-bind LoadedRecordingChanged to the new world's controller
            // (and refresh the header through it).
            RefreshControllerSubscription();
            if (world != null)
            {
                TrecsEditorSelection.ActiveWorld = world;
            }
            RefreshTick();
        }

        void ClearAccessor()
        {
            _selectedAccessor = null;
        }

        bool TryGetRunner(out SystemRunner runner)
        {
            runner = null;
            if (_selectedWorld == null || _selectedWorld.IsDisposed)
            {
                return false;
            }
            try
            {
                _selectedAccessor ??= _selectedWorld.CreateAccessor(
                    AccessorRole.Unrestricted,
                    "TrecsPlayerWindow"
                );
                runner = _selectedAccessor.GetSystemRunner();
                return runner != null;
            }
            catch (Exception)
            {
                return false;
            }
        }

        TrecsRecordingSession GetController()
        {
            return _selectedWorld == null
                ? null
                : TrecsRecordingSessionRegistry.GetForWorld(_selectedWorld);
        }

        void RefreshTick()
        {
            if (_selectedWorld == null || _transportPanel == null)
            {
                return;
            }

            if (_selectedWorld.IsDisposed)
            {
                ResetTransportUI();
                UpdateBadge(
                    GameStateMode.Idle,
                    paused: false,
                    byCapacity: false,
                    recorder: null,
                    controllerInstalled: true
                );
                UpdateRecordButton(controller: null, GameStateMode.Idle);
                UpdateLoopButton(GameStateMode.Idle, recorder: null);
                return;
            }

            _selectedAccessor ??= _selectedWorld.CreateAccessor(
                AccessorRole.Unrestricted,
                "TrecsPlayerWindow"
            );

            var controller = GetController();
            if (controller == null)
            {
                // World is alive but its composition root never wired up a
                // TrecsRecordingSession. Surface that loudly via the badge
                // instead of falling through to the LIVE/Idle branch — the two
                // cases are indistinguishable from the user's POV otherwise.
                ResetTransportUI();
                UpdateBadge(
                    GameStateMode.Idle,
                    paused: false,
                    byCapacity: false,
                    recorder: null,
                    controllerInstalled: false
                );
                UpdateRecordButton(controller: null, GameStateMode.Idle);
                UpdateLoopButton(GameStateMode.Idle, recorder: null);
                return;
            }
            controller.PollModeChanged();

            var mode = controller.CurrentMode;
            var paused = controller.IsPaused;
            var recorder = controller.AutoRecorder;
            var byCapacity = recorder?.IsPausedByCapacity ?? false;

            UpdateBadge(mode, paused, byCapacity, recorder, controllerInstalled: true);
            UpdateRecordButton(controller, mode);
            UpdateLoopButton(mode, recorder);
            SyncTimeScaleFromRunner();
            RefreshScrubber(recorder);
            RefreshBufferInfo(recorder);
            RefreshCapacityBanner(byCapacity, recorder);
            RefreshDesyncBanner(recorder);
            // Set play/pause tooltip + forward-only enable state in one place
            // so order doesn't matter.
            UpdateForwardControlsForCapacity(byCapacity, paused);
        }

        void UpdateForwardControlsForCapacity(bool byCapacity, bool paused)
        {
            // When paused-by-capacity, forward-only controls are disabled so
            // the user can't immediately re-trigger the pause by pressing Play.
            // Backward scrub, jump-to-start and Fork still work so they can
            // inspect the existing buffer and decide how to relieve pressure.
            // Set both states explicitly so we don't rely on call-order with
            // SetTransportEnabled / UpdatePlayPauseButton to recover.
            _playPauseButton.SetEnabled(!byCapacity);
            _stepForwardButton.SetEnabled(!byCapacity);
            var playPauseTooltip = byCapacity
                ? "Recording paused — cap reached. Save / Fork / Reset / raise the cap "
                    + "before resuming."
                : (paused ? "Play (Space)" : "Pause (Space)");
            _tooltips.Apply(_playPauseButton, playPauseTooltip);
            // Swap the glyph so the button reflects the action it'd take if
            // pressed: a play arrow when currently paused, two bars when
            // currently running. Falls back gracefully if icons couldn't load.
            if (_playPauseIcon != null)
            {
                var nextIcon = paused ? _iconPlay : _iconPause;
                if (nextIcon != null)
                {
                    _playPauseIcon.image = nextIcon;
                }
            }
            // Loud paused indicator: tint the play/pause button background
            // a vivid green ("press to play") when the simulation is paused.
            // White icon stays default, which contrasts well against the
            // saturated green. (An earlier attempt to tint the icon itself
            // via style.unityBackgroundImageTintColor was a no-op — the
            // Image element renders via its own .image property, not via
            // style.backgroundImage, so that tint channel doesn't apply.)
            // Skip for the capacity-pause state — it already has its own
            // banner + disabled-control treatment.
            _playPauseButton.style.backgroundColor =
                paused && !byCapacity ? ActiveTransportButtonColor : StyleKeyword.Null;
        }

        void ResetTransportUI()
        {
            _bufferInfoLabel.text = string.Empty;
            _capacityBanner.style.display = DisplayStyle.None;
            _desyncBanner.style.display = DisplayStyle.None;
#if DEBUG
            _dumpDesyncDiffButton.style.display = DisplayStyle.None;
#endif
            _timeline.Reset();
            SetTransportEnabled(false);
            UpdateBookmarkButton(recordingActive: false);
            SetTimeScaleEnabled(false);
        }

        void UpdateBadge(
            GameStateMode mode,
            bool paused,
            bool byCapacity,
            TrecsRewindBuffer recorder,
            bool controllerInstalled
        )
        {
            if (!controllerInstalled)
            {
                _stateBadge.text = "NOT INSTALLED";
                _stateBadge.style.backgroundColor = new Color(0.5f, 0.35f, 0.05f);
                _stateBadge.tooltip =
                    "Trecs Player failed to auto-attach to this World — "
                    + "recording/scrubbing is unavailable. Check the Console "
                    + "for errors from TrecsEditorRecordingAutoAttach.";
                return;
            }
            // Restore the default tooltip on every controller-installed call so
            // a NOT INSTALLED → installed transition (e.g. auto-attach failed
            // initially but later succeeded) doesn't leave the diagnostic
            // tooltip stuck.
            _stateBadge.tooltip = DefaultBadgeTooltip;
            // Desync overrides the mode badge: the user really wants to see
            // this loud and immediate, not buried in a side banner. Mode is
            // implicit from the visible transport controls anyway.
            if (recorder != null && recorder.HasDesynced)
            {
                _stateBadge.text = "✕ DESYNC";
                _stateBadge.style.backgroundColor = new Color(0.7f, 0.15f, 0.15f);
                return;
            }
            // Mode badge stays constant per mode — paused state is signalled
            // separately via the play/pause button colour (see
            // UpdateForwardControlsForCapacity), so the user has a single
            // loud cue rather than a subtle text mutation here.
            string text;
            Color bg;
            switch (mode)
            {
                case GameStateMode.Recording:
                    text = "● REC";
                    bg = byCapacity ? new Color(0.6f, 0.25f, 0.05f) : new Color(0.55f, 0.1f, 0.1f);
                    break;
                case GameStateMode.Playback:
                    text = "▶ PLAY";
                    bg = new Color(0.15f, 0.35f, 0.6f);
                    break;
                default:
                    text = "LIVE";
                    bg = new Color(0.3f, 0.3f, 0.3f);
                    break;
            }
            _stateBadge.text = text;
            _stateBadge.style.backgroundColor = bg;
        }

        // Mode-driven Record-button refresher. Called every poll tick so the
        // tooltip always describes the action that will fire on the *next*
        // click — no stale text after a Recording → Playback transition the
        // user didn't trigger from the button itself.
        void UpdateRecordButton(TrecsRecordingSession controller, GameStateMode mode)
        {
            if (_recordButton == null)
            {
                return;
            }
            if (controller == null)
            {
                _recordButton.SetEnabled(false);
                _tooltips.Apply(_recordButton, RecordButtonNoControllerTooltip);
                UpdateRecordButtonActive(false);
                return;
            }
            _recordButton.SetEnabled(true);
            switch (mode)
            {
                case GameStateMode.Recording:
                    _tooltips.Apply(_recordButton, RecordButtonRecordingTooltip);
                    UpdateRecordButtonActive(true);
                    break;
                case GameStateMode.Playback:
                    _tooltips.Apply(_recordButton, RecordButtonPlaybackTooltip);
                    UpdateRecordButtonActive(false);
                    break;
                default:
                    _tooltips.Apply(_recordButton, RecordButtonIdleTooltip);
                    UpdateRecordButtonActive(false);
                    break;
            }
        }

        // Active state == "currently capturing snapshots". Saturated red
        // background mirrors the REC badge so the two cues reinforce each
        // other; cleared via StyleKeyword.Null so the editor's default
        // button background returns when the recorder stops.
        void UpdateRecordButtonActive(bool active)
        {
            _recordButton.style.backgroundColor = active
                ? new Color(0.55f, 0.1f, 0.1f)
                : StyleKeyword.Null;
        }

        // Loop stays visible at all times (stable layout) but is enabled
        // only in Playback mode, where the recorder actually consults
        // LoopPlayback at the loaded recording's tail. Outside Playback
        // we clear the active highlight and swap to a disabled-state
        // tooltip explaining when the button becomes usable.
        void UpdateLoopButton(GameStateMode mode, TrecsRewindBuffer recorder)
        {
            if (_loopButton == null)
            {
                return;
            }
            var enabled = mode == GameStateMode.Playback;
            _loopButton.SetEnabled(enabled);
            if (!enabled)
            {
                _loopButton.style.backgroundColor = StyleKeyword.Null;
                _tooltips.Apply(_loopButton, LoopButtonDisabledTooltip);
                return;
            }
            UpdateLoopButtonActive(recorder?.LoopPlayback ?? false);
        }

        void UpdateLoopButtonActive(bool active)
        {
            _loopButton.style.backgroundColor = active
                ? ActiveTransportButtonColor
                : StyleKeyword.Null;
            _tooltips.Apply(
                _loopButton,
                active ? LoopButtonActiveTooltip : LoopButtonInactiveTooltip
            );
        }

        void RefreshScrubber(TrecsRewindBuffer recorder)
        {
            var recordingActive = recorder != null && recorder.IsRecording;
            SetTransportEnabled(recordingActive);
            UpdateBookmarkButton(recordingActive);

            if (!recordingActive || recorder.Keyframes.Count == 0)
            {
                _timeline.Reset();
                return;
            }

            var startFrame = recorder.StartFrame;
            var currentFrame = _selectedAccessor.FixedFrame;
            var maxFrame = Math.Max(currentFrame, recorder.LatestCapturedFrame);
            _timeline.Refresh(
                startFrame,
                maxFrame,
                currentFrame,
                recorder.Keyframes,
                recorder.Bookmarks
            );
        }

        void UpdateBookmarkButton(bool recordingActive)
        {
            if (_bookmarkCaptureButton == null)
            {
                return;
            }
            _bookmarkCaptureButton.SetEnabled(recordingActive);
            _tooltips.Apply(
                _bookmarkCaptureButton,
                recordingActive ? BookmarkButtonEnabledTooltip : BookmarkButtonDisabledTooltip
            );
        }

        void RemoveBookmarkAndRefresh(TrecsRewindBuffer recorder, int frame, string label)
        {
            if (recorder == null)
            {
                return;
            }
            if (recorder.RemoveBookmarkAtFrame(frame))
            {
                SetRecordingStatus($"Removed bookmark '{label}' @ frame {frame}.");
                RefreshTick();
            }
            else
            {
                SetRecordingStatus($"No bookmark at frame {frame} to remove.");
            }
        }

        bool TryGetFixedDeltaTime(out float dt)
        {
            if (TryGetRunner(out var runner))
            {
                dt = runner.FixedDeltaTime;
                return dt > 0f;
            }
            dt = 0f;
            return false;
        }

        void RefreshBufferInfo(TrecsRewindBuffer recorder)
        {
            if (recorder == null)
            {
                _bufferInfoLabel.text = string.Empty;
                return;
            }
            if (!recorder.IsRecording)
            {
                _bufferInfoLabel.text = "idle";
                return;
            }
            if (recorder.Keyframes.Count == 0)
            {
                _bufferInfoLabel.text = "awaiting first keyframe";
                return;
            }
            var span = recorder.LatestCapturedFrame - recorder.StartFrame;
            var seconds = TryGetFixedDeltaTime(out var dt) ? span * dt : 0f;
            _bufferInfoLabel.text =
                $"{recorder.Keyframes.Count} keyframes · "
                + $"{span} frames ({FormatDuration(seconds)}) · "
                + $"{FormatBytes(recorder.TotalBytes)}";
        }

        static string FormatDuration(float seconds)
        {
            if (seconds <= 0f)
            {
                return "0s";
            }
            if (seconds < 60f)
            {
                return $"{seconds:F1}s";
            }
            var m = (int)(seconds / 60);
            var s = seconds - m * 60;
            return $"{m}m{s:F0}s";
        }

        void RefreshCapacityBanner(bool byCapacity, TrecsRewindBuffer recorder)
        {
            if (byCapacity && recorder != null)
            {
                _capacityBanner.text =
                    "Recording paused — capacity reached "
                    + $"({recorder.Keyframes.Count} keyframes, {FormatBytes(recorder.TotalBytes)}). "
                    + "Save, fork, reset, or raise the cap before resuming.";
                _capacityBanner.style.display = DisplayStyle.Flex;
            }
            else
            {
                _capacityBanner.style.display = DisplayStyle.None;
            }
        }

        void RefreshDesyncBanner(TrecsRewindBuffer recorder)
        {
            var show = recorder != null && recorder.HasDesynced;
            if (show)
            {
                _desyncBanner.text =
                    $"Desync at frame {recorder.DesyncedFrame.Value} — re-running the simulation "
                    + "from an earlier snapshot produced different state than originally "
                    + "captured. Check the editor log for the checksum mismatch.";
            }
            _desyncBanner.style.display = show ? DisplayStyle.Flex : DisplayStyle.None;
#if DEBUG
            _dumpDesyncDiffButton.style.display = show ? DisplayStyle.Flex : DisplayStyle.None;
#endif
        }

        void SetTransportEnabled(bool enabled)
        {
            _jumpStartButton.SetEnabled(enabled);
            _stepBackButton.SetEnabled(enabled);
            _playPauseButton.SetEnabled(enabled);
            _stepForwardButton.SetEnabled(enabled);
            _jumpEndButton.SetEnabled(enabled);
        }

        // When no world is active (typically: not in play mode), keep the panels
        // visible but grayed out so the window's affordances are still
        // discoverable instead of replaced by a single empty-state label.
        void SetMainPanelsEnabled(bool enabled)
        {
            // Leave the recording header row alone (just the recording
            // name + Actions ▾ button) — the Actions menu hosts Help /
            // Settings / pre-load actions that should remain reachable
            // even without a world. Each menu item gates itself on the
            // current recorder/controller state. Only the transport panel
            // (frame controls + Record + Loop buttons) is gated here,
            // since none of its operations make sense without a world.
            _transportPanel.SetEnabled(enabled);
            _transportPanel.style.opacity = enabled ? 1f : 0.4f;
            // Buffer-info / status sit on rootVisualElement (above the
            // scroll) and only make sense with a world — hide them entirely
            // rather than gray them.
            _bufferInfoLabel.style.display = enabled ? DisplayStyle.Flex : DisplayStyle.None;
            _recordingStatusLabel.style.display = enabled ? DisplayStyle.Flex : DisplayStyle.None;
        }

        void SetTimeScaleEnabled(bool enabled)
        {
            _speedButton.SetEnabled(enabled);
        }

        void SyncTimeScaleFromRunner()
        {
            if (!TryGetRunner(out var runner))
            {
                SetTimeScaleEnabled(false);
                return;
            }
            SetTimeScaleEnabled(true);

            _speedButton.text = FormatSpeedLabel(runner.TimeScale);
            // Tint the button amber whenever the runner isn't at real-time —
            // easy to forget you've set 0.1× or 2× and end up confused about
            // why the world feels slow/fast.
            HighlightSpeedButton(!Mathf.Approximately(runner.TimeScale, 1f));
        }

        void HighlightSpeedButton(bool nonDefault)
        {
            var amber = new Color(0.95f, 0.7f, 0.1f);
            _speedButton.style.color = nonDefault ? amber : (StyleColor)StyleKeyword.Null;
            _speedButton.style.unityFontStyleAndWeight = nonDefault
                ? FontStyle.Bold
                : (StyleEnum<FontStyle>)StyleKeyword.Null;
        }

        // Render a speed multiplier with a tidy "×" suffix. Whole numbers
        // (1, 2) get no decimal; fractional values keep the leading zero
        // dropped so "0.5" reads as "0.5×" not ".5×".
        static string FormatSpeedLabel(float speed)
        {
            if (Mathf.Approximately(speed, Mathf.Round(speed)))
            {
                return $"{Mathf.RoundToInt(speed)}×";
            }
            return $"{speed:0.##}×";
        }

        // ---- Transport callbacks ----

        void OnPlayPauseClicked()
        {
            var controller = GetController();
            if (controller == null)
            {
                return;
            }
            // Refuse to un-pause at the tail of a Playback recording —
            // un-pausing would let the runner take one more fixed tick
            // before the at-tail logic re-pauses, advancing past the end.
            // Mirrors OnStepForwardClicked's guard. Pause→play only;
            // play→pause always works.
            var recorder = controller.AutoRecorder;
            if (
                controller.IsPaused
                && recorder != null
                && controller.CurrentMode == GameStateMode.Playback
                && _selectedAccessor != null
                && _selectedAccessor.FixedFrame >= recorder.LatestCapturedFrame
            )
            {
                SetRecordingStatus(
                    "At the end of the recording — press Record to fork and continue live."
                );
                return;
            }
            controller.SetPaused(!controller.IsPaused);
        }

        void OnStepForwardClicked()
        {
            var controller = GetController();
            if (controller == null)
            {
                return;
            }
            // Refuse to step past the recording's tail in Playback.
            // runner.StepFixedFrame() bypasses the pause-at-tail logic in
            // OnFixedFrameChange, so without this gate the next handler
            // tick lands in the "frame > _lastKeyframeFrame" branch — the
            // loaded-recording side silently flips to live capture, and
            // the scrubbed-back side advances into territory the buffer
            // doesn't cover. Force Record/Fork as the only path out.
            var recorder = controller.AutoRecorder;
            if (
                recorder != null
                && controller.CurrentMode == GameStateMode.Playback
                && _selectedAccessor != null
                && _selectedAccessor.FixedFrame >= recorder.LatestCapturedFrame
            )
            {
                SetRecordingStatus(
                    "At the end of the recording — press Record to fork and continue live."
                );
                return;
            }
            controller.StepFixedFrame();
        }

        void OnStepBackClicked()
        {
            var controller = GetController();
            if (controller == null || !TryGetRunner(out var runner))
            {
                return;
            }
            runner.FixedIsPaused = true;
            controller.JumpToFrame(_selectedAccessor.FixedFrame - 1);
        }

        void OnJumpStartClicked()
        {
            var controller = GetController();
            var recorder = controller?.AutoRecorder;
            if (controller == null || recorder == null || recorder.Keyframes.Count == 0)
            {
                return;
            }
            controller.JumpToFrame(recorder.StartFrame);
        }

        void OnJumpEndClicked()
        {
            var controller = GetController();
            var recorder = controller?.AutoRecorder;
            if (controller == null || recorder == null || recorder.Keyframes.Count == 0)
            {
                return;
            }
            controller.JumpToFrame(recorder.LatestCapturedFrame);
        }

        // Context-sensitive Record button. Behaves differently depending on
        // CurrentMode — see UpdateRecordButton for the matching tooltip
        // text. Idle starts capture, Recording stops it, Playback forks at
        // the current scrub frame. Status text confirms the action took
        // effect (especially Fork, which is destructive but otherwise
        // invisible — the badge change alone could be missed).
        void OnRecordButtonClicked()
        {
            var controller = GetController();
            if (controller == null)
            {
                return;
            }
            switch (controller.CurrentMode)
            {
                case GameStateMode.Idle:
                    controller.StartAutoRecording();
                    SetRecordingStatus("Recording started.");
                    break;
                case GameStateMode.Recording:
                    controller.StopAutoRecording();
                    SetRecordingStatus("Recording stopped.");
                    break;
                case GameStateMode.Playback:
                    var forkFrame = _selectedAccessor?.FixedFrame ?? 0;
                    if (controller.ForkAtCurrentFrame())
                    {
                        SetRecordingStatus($"Forked at frame {forkFrame} — continuing live.");
                    }
                    else
                    {
                        SetRecordingStatus(
                            "Fork failed: no snapshot at or before the current frame."
                        );
                    }
                    break;
            }
            // Tick once so the button's tooltip + active-state visuals
            // refresh immediately rather than waiting for the next poll.
            RefreshTick();
        }

        // Bookmark capture: prompt for a label, then write a labelled
        // bookmark at the current frame via the recorder. CaptureBookmark-
        // AtCurrentFrame is a no-op when not recording — the button is
        // disabled in that state, but we double-check the mode here so
        // the keyboard shortcut path doesn't surface a misleading status
        // message either. Edit-after-capture is intentionally not offered;
        // the workflow is "delete + recreate" via right-click on a marker.
        void OnBookmarkCaptureClicked()
        {
            var controller = GetController();
            var recorder = controller?.AutoRecorder;
            if (recorder == null || !recorder.IsRecording)
            {
                SetRecordingStatus("Bookmark unavailable — not recording.");
                return;
            }
            var frame = _selectedAccessor?.FixedFrame ?? recorder.LatestCapturedFrame;
            // Default label encodes the frame so a quick capture without
            // typing still lands in the timeline with something readable
            // ("frame 1234"); the user can re-prompt later by right-click-
            // delete + recapture if they want to refine the name.
            var label = TrecsTextPromptWindow.Prompt(
                "Bookmark frame",
                $"Label for bookmark at frame {frame}:",
                $"frame {frame}",
                anchor: this
            );
            if (label == null)
            {
                // User cancelled — silent return matches the Save flow.
                return;
            }
            label = label.Trim();
            if (recorder.CaptureBookmarkAtCurrentFrame(label))
            {
                var displayLabel = string.IsNullOrEmpty(label) ? "(unlabelled)" : $"'{label}'";
                SetRecordingStatus($"Bookmarked frame {frame} {displayLabel}.");
                // Re-render markers immediately so the new bookmark shows
                // without waiting for the next poll tick.
                RefreshTick();
            }
            else
            {
                SetRecordingStatus("Bookmark failed.");
            }
        }

        // Loop is purely session-local — the recorder owns the flag and
        // resets it on Start / LoadRecording / Fork / Reset, so the user
        // re-opts in for each playback session. We don't persist it via
        // EditorPrefs.
        void OnLoopButtonClicked()
        {
            var recorder = GetController()?.AutoRecorder;
            if (recorder == null)
            {
                return;
            }
            recorder.LoopPlayback = !recorder.LoopPlayback;
            UpdateLoopButtonActive(recorder.LoopPlayback);
        }

        void OnSettingsClicked()
        {
            // Settings are EditorPrefs-backed (see TrecsPlayerSettingsStore),
            // so the window is reachable any time — no live recorder
            // required. Save propagates onto any live recorders too.
            TrecsPlayerSettingsWindow.Show(this);
        }

        // Adds the Trim Before / Trim After items into a parent menu under the
        // "Trim" submenu path. Counts the keyframes that would drop so the
        // labels reflect what each action will actually do; disables an
        // option when there's nothing on that side to drop.
        void AddTrimMenuItems(GenericMenu menu, TrecsRewindBuffer recorder)
        {
            if (_selectedAccessor == null || recorder == null || recorder.Keyframes.Count == 0)
            {
                menu.AddDisabledItem(new GUIContent("Trim/Before current frame"));
                menu.AddDisabledItem(new GUIContent("Trim/After current frame"));
                return;
            }
            var current = _selectedAccessor.FixedFrame;
            var strictlyBefore = 0;
            var hasExactKeyframe = false;
            var after = 0;
            for (var i = 0; i < recorder.Keyframes.Count; i++)
            {
                var f = recorder.Keyframes[i].FixedFrame;
                if (f < current)
                    strictlyBefore++;
                else if (f > current)
                    after++;
                else
                    hasExactKeyframe = true;
            }
            // TrimRecordingBefore preserves the closest keyframe at-or-before
            // `current` so JumpToFrame(current) still resolves. If `current` is
            // itself a keyframe frame, that's the keyframe and all strictly-prior
            // keyframes drop. Otherwise the keyframe is the last strictly-prior
            // keyframe, so one fewer drops.
            var before = hasExactKeyframe ? strictlyBefore : Math.Max(0, strictlyBefore - 1);

            var beforeLabel =
                $"Trim/Before frame {current} (drops {before} keyframe{(before == 1 ? "" : "s")})";
            var afterLabel =
                $"Trim/After frame {current} (drops {after} keyframe{(after == 1 ? "" : "s")})";
            if (before > 0)
            {
                menu.AddItem(
                    new GUIContent(beforeLabel),
                    false,
                    () => DoTrim(trimBefore: true, current)
                );
            }
            else
            {
                menu.AddDisabledItem(new GUIContent(beforeLabel));
            }
            if (after > 0)
            {
                menu.AddItem(
                    new GUIContent(afterLabel),
                    false,
                    () => DoTrim(trimBefore: false, current)
                );
            }
            else
            {
                menu.AddDisabledItem(new GUIContent(afterLabel));
            }
        }

        void DoTrim(bool trimBefore, int frame)
        {
            var controller = GetController();
            var recorder = controller?.AutoRecorder;
            if (recorder == null)
                return;
            var direction = trimBefore ? "before" : "after";
            if (
                !EditorUtility.DisplayDialog(
                    "Trim recording?",
                    $"Drop snapshots {direction} frame {frame} from the in-memory buffer? "
                        + "Saved files on disk are not affected.",
                    "Trim",
                    "Cancel"
                )
            )
            {
                return;
            }
            var dropped = trimBefore
                ? recorder.TrimRecordingBefore(frame)
                : recorder.TrimRecordingAfter(frame);
            if (dropped == 0)
            {
                SetRecordingStatus($"Nothing to trim {direction} frame {frame}.");
                return;
            }
            SetRecordingStatus(
                $"Trimmed {dropped} snapshot{(dropped == 1 ? "" : "s")} {direction} frame {frame}."
            );
            RefreshTick();
        }

        // ---- Recording header callbacks ----

        // Status messages are transient feedback for the last save/load/delete/new
        // action. The recording header itself shows persistent state (current
        // filename + dirty marker), so the status line only needs to flash briefly.
        void SetRecordingStatus(string text)
        {
            _recordingStatusClearTask?.Pause();
            _recordingStatusLabel.text = text;
            // Hide the label entirely when empty so the background pill
            // doesn't render as a thin sliver in the corner.
            _recordingStatusLabel.style.display = string.IsNullOrEmpty(text)
                ? DisplayStyle.None
                : DisplayStyle.Flex;
            if (string.IsNullOrEmpty(text))
            {
                _recordingStatusClearTask = null;
                return;
            }
            _recordingStatusClearTask = _recordingStatusLabel
                .schedule.Execute(() =>
                {
                    _recordingStatusLabel.text = "";
                    _recordingStatusLabel.style.display = DisplayStyle.None;
                })
                .StartingIn(RecordingStatusClearDelayMs);
        }

        void OnNewClicked()
        {
            var controller = GetController();
            if (controller == null)
            {
                SetRecordingStatus("No active world.");
                return;
            }
            var recorder = controller.AutoRecorder;
            // Skip the confirm if the buffer is trivially small (just hit
            // Play, no data to lose) — same threshold used by the old
            // "Reset auto-recording" advanced button.
            var hasRealData = recorder != null && recorder.Keyframes.Count > 4;
            if (hasRealData)
            {
                var savedName = CurrentRecordingName;
                var diskNote = string.IsNullOrEmpty(savedName)
                    ? " The unsaved buffer will be lost."
                    : $" Saved file '{savedName}' on disk is not affected.";
                if (
                    !EditorUtility.DisplayDialog(
                        "Start a new recording?",
                        $"Discard {recorder.Keyframes.Count} in-memory keyframes "
                            + $"({FormatBytes(recorder.TotalBytes)})?"
                            + diskNote,
                        "Discard & start fresh",
                        "Cancel"
                    )
                )
                {
                    return;
                }
            }
            controller.ResetAutoRecording();
            // Reset clears the recorder's backing path; LoadedRecordingChanged
            // fires from the controller's poll so the header updates itself.
            SetRecordingStatus("Started a new recording.");
            RefreshTick();
        }

        // Routes the result of a shared TrecsRecordingActions flow to this
        // window's surfaces: transient status text, plus an immediate tick
        // so the header / transport reflect the new state without waiting
        // for the next scheduled refresh.
        void ApplyActionResult(in TrecsRecordingActions.Result result)
        {
            if (!result.Acted)
            {
                return;
            }
            SetRecordingStatus(result.Status);
            if (result.Succeeded)
            {
                RefreshTick();
            }
        }

        void OnSaveClicked()
        {
            var controller = GetController();
            if (controller == null)
            {
                SetRecordingStatus("No active world.");
                return;
            }
            var name = CurrentRecordingName;
            // Already saved → write straight back to the same file. Unsaved
            // → same prompt + overwrite-confirm flow as Save As.
            ApplyActionResult(
                string.IsNullOrEmpty(name)
                    ? TrecsRecordingActions.SaveRecordingPrompted(
                        controller,
                        this,
                        TrecsRecordingActions.SuggestRecordingName()
                    )
                    : TrecsRecordingActions.SaveRecording(controller, name)
            );
        }

        void OnSaveAsClicked()
        {
            var controller = GetController();
            if (controller == null)
            {
                SetRecordingStatus("No active world.");
                return;
            }
            var existing = CurrentRecordingName;
            var suggested = string.IsNullOrEmpty(existing)
                ? TrecsRecordingActions.SuggestRecordingName()
                : existing;
            ApplyActionResult(
                TrecsRecordingActions.SaveRecordingPrompted(controller, this, suggested)
            );
        }

        void OnRecordingActionsClicked()
        {
            var controller = GetController();
            var recorder = controller?.AutoRecorder;
            var isRecording = recorder?.IsRecording ?? false;
            var hasBuffer = isRecording && recorder.Keyframes.Count > 0;
            var savedName = CurrentRecordingName;
            var hasSavedName = !string.IsNullOrEmpty(savedName);
            var inPlayback = controller != null && controller.CurrentMode == GameStateMode.Playback;

            var menu = new GenericMenu();

            // ── Recording group ──
            // New resets the in-memory buffer — only meaningful while the
            // recorder is actually running. With the recorder stopped
            // (no capture has been started, or it was stopped via the
            // Record button), New is disabled until capture resumes.
            if (isRecording)
            {
                menu.AddItem(new GUIContent("New Recording"), false, OnNewClicked);
            }
            else
            {
                menu.AddDisabledItem(new GUIContent("New Recording"));
            }

            // Save / Save As require something in the buffer to write.
            if (hasBuffer)
            {
                menu.AddItem(new GUIContent("Save Recording"), false, OnSaveClicked);
                menu.AddItem(new GUIContent("Save Recording As…"), false, OnSaveAsClicked);
            }
            else
            {
                menu.AddDisabledItem(new GUIContent("Save Recording"));
                menu.AddDisabledItem(new GUIContent("Save Recording As…"));
            }

            // Load Recording cascade — needs a controller to load into,
            // but does NOT require the recorder to already be running.
            if (controller == null)
            {
                menu.AddDisabledItem(new GUIContent("Load Recording/(no active world)"));
            }
            else
            {
                var names = TrecsRecordingSession.GetSavedRecordingNames();
                if (names.Count == 0)
                {
                    menu.AddDisabledItem(new GUIContent("Load Recording/(none saved)"));
                }
                else
                {
                    foreach (var n in names)
                    {
                        var captured = n;
                        menu.AddItem(
                            new GUIContent($"Load Recording/{captured}"),
                            captured == savedName,
                            () => DoLoadRecording(captured)
                        );
                    }
                }
            }

            // Delete operates on the on-disk recording file, independent
            // of whether the recorder is running.
            if (hasSavedName)
            {
                menu.AddItem(
                    new GUIContent($"Delete Recording '{savedName}'"),
                    false,
                    OnDeleteClicked
                );
            }
            else
            {
                menu.AddDisabledItem(new GUIContent("Delete Recording"));
            }

            menu.AddSeparator(string.Empty);

            // ── Snapshot group ──
            // Snapshots are single-frame world states distinct from
            // recordings (which are time-ranges). SaveSnapshot doesn't
            // require an active recorder; LoadSnapshot stops the
            // recorder, restores world state to the snapshot frame, and
            // (if recording was active) restarts a fresh buffer there.
            if (controller != null)
            {
                menu.AddItem(new GUIContent("Save Snapshot…"), false, OnSaveSnapshotClicked);
            }
            else
            {
                menu.AddDisabledItem(new GUIContent("Save Snapshot…"));
            }

            if (controller == null)
            {
                menu.AddDisabledItem(new GUIContent("Load Snapshot/(no active world)"));
            }
            else
            {
                var snapNames = TrecsSnapshotLibrary.GetSavedSnapshotNames();
                if (snapNames.Count == 0)
                {
                    menu.AddDisabledItem(new GUIContent("Load Snapshot/(none saved)"));
                }
                else
                {
                    foreach (var n in snapNames)
                    {
                        var captured = n;
                        menu.AddItem(
                            new GUIContent($"Load Snapshot/{captured}"),
                            false,
                            () => DoLoadSnapshot(captured)
                        );
                    }
                }
            }

            menu.AddSeparator(string.Empty);

            // ── Playback-only group ──
            // Trim operates on the current scrub frame, which only makes
            // sense once the user has rewound. Disabled (rather than
            // hidden) so the affordance stays discoverable. Fork moved to
            // the transport-row Record button — pressing Record while in
            // Playback forks at the current frame.
            if (inPlayback)
            {
                AddTrimMenuItems(menu, recorder);
            }
            else
            {
                menu.AddDisabledItem(new GUIContent("Trim/Before current frame"));
                menu.AddDisabledItem(new GUIContent("Trim/After current frame"));
            }

            menu.AddSeparator(string.Empty);

            // Settings is EditorPrefs-backed and always reachable — even
            // outside play mode — so users can pre-tune defaults before
            // starting a session.
            menu.AddItem(new GUIContent("Settings…"), false, OnSettingsClicked);
            menu.AddItem(new GUIContent("Help…"), false, ShowHelp);

            menu.DropDown(_recordingActionsButton.worldBound);
        }

        void DoLoadRecording(string name)
        {
            ApplyActionResult(TrecsRecordingActions.LoadRecording(GetController(), name));
        }

        void OnSaveSnapshotClicked()
        {
            ApplyActionResult(
                TrecsRecordingActions.SaveSnapshotPrompted(
                    TrecsEditorRecordingAutoAttach.GetSnapshotLibrary(_selectedWorld),
                    this
                )
            );
        }

        void DoLoadSnapshot(string name)
        {
            ApplyActionResult(
                TrecsRecordingActions.LoadSnapshot(
                    GetController(),
                    TrecsEditorRecordingAutoAttach.GetSnapshotLibrary(_selectedWorld),
                    name
                )
            );
        }

        void OnDeleteClicked()
        {
            var name = CurrentRecordingName;
            if (string.IsNullOrEmpty(name))
            {
                SetRecordingStatus("No saved file to delete (current recording is unsaved).");
                return;
            }
            ApplyActionResult(TrecsRecordingActions.DeleteRecording(GetController(), name));
        }

        void UpdateRecordingHeader()
        {
            if (_recordingNameLabel == null)
            {
                return;
            }
            var name = CurrentRecordingName;
            var saved = !string.IsNullOrEmpty(name);
            var recorder = GetController()?.AutoRecorder;
            var isRecording = recorder?.IsRecording ?? false;

            // Three states: Saved (named on disk) → bold blue file name.
            // Recording with no save → dimmed italic "(unsaved)".
            // Stopped recorder → dimmed italic "(no recording)" — avoids
            // implying there's an unsaved buffer when in fact nothing is
            // being captured.
            string displayName;
            string tooltip;
            if (saved)
            {
                displayName = name;
                tooltip = $"Recording: {displayName}\n(file backing the in-memory recording)";
            }
            else if (isRecording)
            {
                displayName = UnsavedRecordingDisplayName;
                tooltip = "Recording is unsaved — Save to persist to disk.";
            }
            else
            {
                displayName = NoRecordingDisplayName;
                tooltip =
                    "Recorder is stopped — press the Record button to start "
                    + "capturing or load a saved recording from the Actions menu.";
            }

            _recordingNameLabel.text = displayName;
            _recordingNameLabel.style.unityFontStyleAndWeight = saved
                ? FontStyle.Bold
                : FontStyle.Italic;
            _recordingNameLabel.style.opacity = saved ? 1f : 0.7f;
            // Highlight the saved name so it reads as the header's identity
            // without needing a "Recording:" prefix label. Unsaved /
            // no-recording fall back to the default text color (dimmed via
            // opacity above).
            _recordingNameLabel.style.color = saved
                ? new StyleColor(new Color(0.55f, 0.85f, 1f))
                : new StyleColor(StyleKeyword.Null);
            // Surface the full name via the hover tooltip so the user can
            // still read it when the label gets ellipsised by the row.
            _tooltips.Apply(_recordingNameLabel, tooltip);
        }

        // ---- Speed dropdown ----

        void OnSpeedButtonClicked()
        {
            var runner = TryGetRunner(out var r) ? r : null;
            var current = runner?.TimeScale ?? 1f;
            var menu = new GenericMenu();
            foreach (var preset in SpeedPresets)
            {
                var captured = preset;
                var isCurrent = Mathf.Approximately(current, captured);
                menu.AddItem(
                    new GUIContent(FormatSpeedLabel(captured)),
                    isCurrent,
                    () => SetTimeScale(captured)
                );
            }
            menu.DropDown(_speedButton.worldBound);
        }

        void SetTimeScale(float speed)
        {
            if (TryGetRunner(out var runner))
            {
                runner.TimeScale = speed;
                _speedButton.text = FormatSpeedLabel(speed);
                HighlightSpeedButton(!Mathf.Approximately(speed, 1f));
            }
        }

        // ---- Misc helpers ----

        static bool IsInsideTextField(VisualElement element)
        {
            for (var e = element; e != null; e = e.parent)
            {
                if (e is TextField)
                {
                    return true;
                }
            }
            return false;
        }

        static string FormatBytes(long bytes)
        {
            if (bytes < 1024)
            {
                return $"{bytes} B";
            }
            if (bytes < 1024 * 1024)
            {
                return $"{bytes / 1024.0:F1} KB";
            }
            return $"{bytes / (1024.0 * 1024.0):F2} MB";
        }
    }
}
