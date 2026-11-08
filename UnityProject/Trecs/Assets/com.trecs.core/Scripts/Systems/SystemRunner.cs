using System.Collections.Generic;
using System.ComponentModel;
using System.Diagnostics;
using System.Runtime.CompilerServices;
using Unity.Jobs;
using UnityEngine;

namespace Trecs.Internal
{
    [EditorBrowsable(EditorBrowsableState.Never)]
    public sealed partial class SystemRunner
    {
        readonly TrecsLog _log;

        readonly InterpolatedPreviousSaverManager _interpolatedPreviousSaverManager;
        readonly Stopwatch _fixedUpdateStopwatch = new();
        readonly WorldSettings _settings;
        readonly float? _maxSecondsPerFixedUpdate;

        readonly SimpleSubject<float> _variableDeltaTimeChangeEvent = new();
        readonly SimpleSubject<int> _fixedCurrentFrameChangeEvent = new();
        readonly SimpleSubject<float> _elapsedVariableTimeChangeEvent = new();
        readonly SimpleSubject<int> _variableFrameCountChangeEvent = new();
        readonly SimpleSubject<float> _fixedElapsedTimeChangeEvent = new();
        readonly SimpleSubject<bool> _fixedIsPausedChangedEvent = new();
        readonly RuntimeJobScheduler _jobScheduler;
        readonly WorldSafetyManager _safetyManager = new();
        readonly EntityInputQueue _entityInputQueue;
        readonly EntitySubmitter _entitySubmitter;

        SimpleSubject _fixedUpdateCompleted;
        SimpleSubject _fixedUpdateStarted;
        SimpleSubject _variableUpdateStarted;
        SimpleSubject _variableUpdateCompleted;

        SystemEnableState _enableState;
        List<SystemEntry> _systems;
        List<SystemRuntimeInfo> _systemRuntimeInfos;
        List<int> _sortedInputSystems;
        List<int> _sortedFixedSystems;
        List<int> _sortedEarlyPresentationSystems;
        List<int> _sortedPresentationSystems;
        List<int> _sortedLatePresentationSystems;
        int[] _systemSortIndex;
        int _lastTickFrame;

        internal int _currentFixedFrameCount;
        internal float _elapsedFixedTime;
        float _elapsedVariableTime;

        // InterpolatedFixedElapsedTime minus FixedElapsedTime. In
        // [-FixedTimeStep, 0) after a catch-up; 0 shows the newest fixed
        // state. Presentation state: not serialized.
        float _interpolationOffset;
        float? _variableDeltaTime;
        int _variableFrameCount;
        bool _isExecutingSystems;

        // Tracks the WorldAccessor.Id of the Fixed-phase system currently inside
        // its Execute method, or 0 when no Fixed system is executing. Used by
        // WorldAccessor.AssertIsCurrentlyExecutingAccessor to enforce the
        // "Fixed execute uses only the executing system's accessor" rule —
        // service-class accessors and other-system accessors are rejected so
        // they can't smuggle non-deterministic state into the simulation
        // (or scramble debug-attribution by recording access under the wrong
        // accessor name). Only set during Fixed; left at 0 for variable
        // phases since the rule doesn't apply there.
        int _currentlyExecutingAccessorId;
        bool _isPaused = false;
        bool _fixedIsPaused = false;
        bool _stepFixedFrame = false;
        bool _desiredFixedIsPaused = false;
        int? _lastSpiralOfDeathWarningVariableFrame;

        // External fast-forward request: set by callers via the public
        // FastForwardTargetFrame property, consumed at the start of the next
        // variable tick when the catch-up loop kicks off (see Tick()).
        int? _pendingFastForwardRequest;

        // Frame a fast-forward in progress runs to, past MaxSecondsPerFixedUpdate.
        // Stops exactly there: FixedFrameChangeEvent listeners (debug recorder)
        // would read one frame more as user-driven progress.
        int? _fastForwardCatchupTargetFrame;
        float _timeScale = 1f;

        bool _hasDisposed;
        bool _hasRunFirstTick;

        internal SystemRunner(
            TrecsLog log,
            EntitySubmitter entitySubmitter,
            WorldSettings settings,
            InterpolatedPreviousSaverManager interpolatedPreviousSaverManager,
            RuntimeJobScheduler jobScheduler,
            EntityInputQueue entityInputQueue
        )
        {
            settings ??= new WorldSettings();

            _log = log;
            _maxSecondsPerFixedUpdate = settings.MaxSecondsForFixedUpdatePerFrame;
            _interpolatedPreviousSaverManager = interpolatedPreviousSaverManager;

            _isPaused = settings.StartPaused;
            _settings = settings;
            _jobScheduler = jobScheduler;
            _entityInputQueue = entityInputQueue;
            _entitySubmitter = entitySubmitter;

            _log.Trace("Using Trecs Settings: {0}", settings);
        }

        public bool IsPaused
        {
            get { return _isPaused; }
            set { _isPaused = value; }
        }

        public bool FixedIsPaused
        {
            get { return _desiredFixedIsPaused; }
            set
            {
                if (_desiredFixedIsPaused != value)
                {
                    _desiredFixedIsPaused = value;
                    if (!value)
                    {
                        // Drop any pending step request when leaving the paused
                        // state — otherwise the flag would survive into a normal
                        // unpaused tick and narrow its end-of-frame bound to a
                        // single fixed step.
                        _stepFixedFrame = false;
                    }
                    _fixedIsPausedChangedEvent.Invoke(_desiredFixedIsPaused);
                }
            }
        }

        public SimpleSubject<bool> FixedIsPausedChangedEvent => _fixedIsPausedChangedEvent;

        /// <summary>
        /// Note that this is different from unity's Time.timeScale
        /// Changing either one will affect the variable delta time, and thus
        /// how fast VariableElapsedTime and InterpolatedFixedElapsedTime advance.
        /// A negative scale runs VariableElapsedTime backwards; fixed update
        /// never runs backwards, it holds at the previous fixed state.
        /// </summary>
        public float TimeScale
        {
            get
            {
                TrecsDebugAssert.That(!_hasDisposed);
                return _timeScale;
            }
            set
            {
                TrecsDebugAssert.That(!_hasDisposed);
                _timeScale = value;
            }
        }

        /// <summary>
        /// The next variable tick will fast-forward fixed updates until
        /// <see cref="FixedFrame"/> reaches this value, then auto-pause.
        /// Requires <see cref="FixedIsPaused"/> to be false at the start of
        /// that tick. No-op if the target is at or below the current frame.
        /// </summary>
        public int? FastForwardTargetFrame
        {
            get
            {
                TrecsDebugAssert.That(!_hasDisposed);
                return _pendingFastForwardRequest;
            }
            set
            {
                TrecsDebugAssert.That(!_hasDisposed);
                _pendingFastForwardRequest = value;
            }
        }

        public RuntimeJobScheduler JobScheduler => _jobScheduler;
        public WorldSafetyManager SafetyManager => _safetyManager;
        internal int CurrentlyExecutingAccessorId => _currentlyExecutingAccessorId;
        internal bool WarnOnJobSyncPoints => _settings.WarnOnJobSyncPoints;
        internal bool AssertNoTimeInFixedPhase => _settings.AssertNoTimeInFixedPhase;

        public bool IsExecutingSystems
        {
            get
            {
                TrecsDebugAssert.That(!_hasDisposed);
                return _isExecutingSystems;
            }
        }

        public float VariableDeltaTime
        {
            get
            {
                TrecsDebugAssert.That(!_hasDisposed);
                return _variableDeltaTime.Value;
            }
            set
            {
                TrecsDebugAssert.That(!_hasDisposed);
                _variableDeltaTime = value;
                _variableDeltaTimeChangeEvent.Invoke(value);
            }
        }

        public ISimpleObservable<float> VariableDeltaTimeChangeEvent
        {
            get
            {
                TrecsDebugAssert.That(!_hasDisposed);
                return _variableDeltaTimeChangeEvent;
            }
        }

        public float VariableElapsedTime
        {
            get
            {
                TrecsDebugAssert.That(!_hasDisposed);
                return _elapsedVariableTime;
            }
            set
            {
                TrecsDebugAssert.That(!_hasDisposed);

                if (_elapsedVariableTime != value)
                {
                    _elapsedVariableTime = value;
                    _elapsedVariableTimeChangeEvent.Invoke(value);
                }
            }
        }

        public ISimpleObservable<float> VariableElapsedTimeChangeEvent
        {
            get
            {
                TrecsDebugAssert.That(!_hasDisposed);
                return _elapsedVariableTimeChangeEvent;
            }
        }

        public int VariableFrame
        {
            get
            {
                TrecsDebugAssert.That(!_hasDisposed);
                return _variableFrameCount;
            }
            set
            {
                TrecsDebugAssert.That(!_hasDisposed);

                if (_variableFrameCount != value)
                {
                    _variableFrameCount = value;
                    _variableFrameCountChangeEvent.Invoke(value);
                }
            }
        }

        public ISimpleObservable<int> VariableFrameChangeEvent
        {
            get
            {
                TrecsDebugAssert.That(!_hasDisposed);
                return _variableFrameCountChangeEvent;
            }
        }

        public float FixedDeltaTime
        {
            get
            {
                TrecsDebugAssert.That(!_hasDisposed);
                return _settings.FixedTimeStep;
            }
        }

        public float FixedElapsedTime
        {
            get
            {
                TrecsDebugAssert.That(!_hasDisposed);
                return _elapsedFixedTime;
            }
            set
            {
                TrecsDebugAssert.That(!_hasDisposed);
                if (_elapsedFixedTime != value)
                {
                    _elapsedFixedTime = value;
                    _fixedElapsedTimeChangeEvent.Invoke(value);
                }
            }
        }

        public ISimpleObservable<float> FixedElapsedTimeChangeEvent
        {
            get
            {
                TrecsDebugAssert.That(!_hasDisposed);
                return _fixedElapsedTimeChangeEvent;
            }
        }

        public float InterpolatedFixedElapsedTime
        {
            get
            {
                TrecsDebugAssert.That(!_hasDisposed);
                return _elapsedFixedTime + _interpolationOffset;
            }
        }

        internal float InterpolationOffset
        {
            get
            {
                TrecsDebugAssert.That(!_hasDisposed);
                return _interpolationOffset;
            }
            set
            {
                TrecsDebugAssert.That(!_hasDisposed);
                _interpolationOffset = value;
            }
        }

        public int FixedFrame
        {
            get
            {
                TrecsDebugAssert.That(!_hasDisposed);
                return _currentFixedFrameCount;
            }
            set
            {
                TrecsDebugAssert.That(!_hasDisposed);
                if (_currentFixedFrameCount != value)
                {
                    _currentFixedFrameCount = value;
                    _fixedCurrentFrameChangeEvent.Invoke(value);
                }
            }
        }

        public ISimpleObservable<int> FixedFrameChangeEvent
        {
            get
            {
                TrecsDebugAssert.That(!_hasDisposed);
                return _fixedCurrentFrameChangeEvent;
            }
        }

        public IReadOnlyList<SystemEntry> Systems
        {
            get
            {
                TrecsDebugAssert.That(!_hasDisposed);
                return _systems;
            }
        }

        public IReadOnlyList<int> SortedFixedSystems
        {
            get
            {
                TrecsDebugAssert.That(!_hasDisposed);
                return _sortedFixedSystems;
            }
        }

        public IReadOnlyList<int> SortedInputSystems
        {
            get
            {
                TrecsDebugAssert.That(!_hasDisposed);
                return _sortedInputSystems;
            }
        }

        public IReadOnlyList<int> SortedEarlyPresentationSystems
        {
            get
            {
                TrecsDebugAssert.That(!_hasDisposed);
                return _sortedEarlyPresentationSystems;
            }
        }

        public IReadOnlyList<int> SortedPresentationSystems
        {
            get
            {
                TrecsDebugAssert.That(!_hasDisposed);
                return _sortedPresentationSystems;
            }
        }

        public IReadOnlyList<int> SortedLatePresentationSystems
        {
            get
            {
                TrecsDebugAssert.That(!_hasDisposed);
                return _sortedLatePresentationSystems;
            }
        }

        internal void SetEventSubjects(EventsManager eventsManager)
        {
            TrecsDebugAssert.IsNull(_fixedUpdateStarted);
            TrecsDebugAssert.IsNull(_fixedUpdateCompleted);
            TrecsDebugAssert.IsNull(_variableUpdateStarted);
            TrecsDebugAssert.IsNull(_variableUpdateCompleted);

            _fixedUpdateStarted = eventsManager.FixedUpdateStartedEvent;
            _fixedUpdateCompleted = eventsManager.FixedUpdateCompletedEvent;
            _variableUpdateStarted = eventsManager.VariableUpdateStartedEvent;
            _variableUpdateCompleted = eventsManager.VariableUpdateCompletedEvent;
        }

        public void Dispose()
        {
            TrecsDebugAssert.That(!_hasDisposed);
            _hasDisposed = true;

            // Drain any remaining tracked jobs before releasing the safety pool.
            _jobScheduler.CompleteAllOutstanding();
#if TRECS_IS_PROFILING
            _jobScheduler.DisposeTimingBuffers();
#endif
            _safetyManager.Dispose();

            TrecsDebugAssert.That(_variableDeltaTimeChangeEvent.NumObservers == 0);
            TrecsDebugAssert.That(_fixedCurrentFrameChangeEvent.NumObservers == 0);
            TrecsDebugAssert.That(_elapsedVariableTimeChangeEvent.NumObservers == 0);
            TrecsDebugAssert.That(_variableFrameCountChangeEvent.NumObservers == 0);
            TrecsDebugAssert.That(_fixedElapsedTimeChangeEvent.NumObservers == 0);
            TrecsDebugAssert.That(_fixedIsPausedChangedEvent.NumObservers == 0);
        }

        // Note that there are two categories of systems - fixed and variable
        // And they each have their own separate sort indices
        // So it only makes sense to compare sort indices within each group
        public int GetSystemSortIndex(int i)
        {
            TrecsDebugAssert.That(!_hasDisposed);
            TrecsDebugAssert.That(i >= 0 && i < _systemSortIndex.Length);
            return _systemSortIndex[i];
        }

        public void Initialize(
            World world,
            SystemLoader.LoadInfo loadInfo,
            SystemEnableState enableState
        )
        {
            TrecsDebugAssert.That(!_hasDisposed);
            TrecsDebugAssert.IsNotNull(
                _fixedUpdateStarted,
                "SetEventSubjects must be called before Initialize"
            );
            TrecsDebugAssert.IsNotNull(enableState);

            _enableState = enableState;
            TrecsDebugAssert.IsNull(_systems);
            _systems = new List<SystemEntry>(loadInfo.Systems.Count);
            TrecsDebugAssert.IsNull(_systemRuntimeInfos);
            _systemRuntimeInfos = new List<SystemRuntimeInfo>(loadInfo.Systems.Count);
            TrecsDebugAssert.IsNull(_sortedInputSystems);
            _sortedInputSystems = loadInfo.SortedInputSystems;
            TrecsDebugAssert.IsNull(_sortedFixedSystems);
            _sortedFixedSystems = loadInfo.SortedFixedSystems;
            TrecsDebugAssert.IsNull(_sortedEarlyPresentationSystems);
            _sortedEarlyPresentationSystems = loadInfo.SortedEarlyPresentationSystems;
            TrecsDebugAssert.IsNull(_sortedPresentationSystems);
            _sortedPresentationSystems = loadInfo.SortedPresentationSystems;
            TrecsDebugAssert.IsNull(_sortedLatePresentationSystems);
            _sortedLatePresentationSystems = loadInfo.SortedLatePresentationSystems;
            _systemSortIndex = new int[loadInfo.Systems.Count];

            void IndexPhase(List<int> sorted)
            {
                for (int i = 0; i < sorted.Count; i++)
                {
                    var globalIndex = sorted[i];
                    TrecsDebugAssert.That(_systemSortIndex[globalIndex] == 0);
                    _systemSortIndex[globalIndex] = i;
                }
            }

            IndexPhase(_sortedInputSystems);
            IndexPhase(_sortedFixedSystems);
            IndexPhase(_sortedEarlyPresentationSystems);
            IndexPhase(_sortedPresentationSystems);
            IndexPhase(_sortedLatePresentationSystems);

            for (int i = 0; i < loadInfo.Systems.Count; i++)
            {
                _systemRuntimeInfos.Add(new SystemRuntimeInfo());
                _systems.Add(loadInfo.Systems[i]);
            }

            _log.Debug(
                "SystemRunner initialization complete. Loaded {0} trecs systems in total",
                _systems.Count
            );
        }

        public void Submit()
        {
            TrecsDebugAssert.That(!_hasDisposed);
            TrecsDebugAssert.That(!_isExecutingSystems);

            using (TrecsProfiling.Start("Submit"))
            {
                _entitySubmitter.Submit();
            }
        }

        void FixedUpdateSystemExecute()
        {
            TrecsDebugAssert.That(!_hasDisposed);

            if (_fixedIsPaused && !_stepFixedFrame)
            {
                return;
            }

            // Single step and fast-forward run to a frame. Otherwise the loop
            // runs until the newest fixed state is past the frame's
            // InterpolatedFixedElapsedTime, so interpolation has both ends.
            int? targetFrame = _fastForwardCatchupTargetFrame;

            if (_stepFixedFrame)
            {
                _stepFixedFrame = false;
                targetFrame = _currentFixedFrameCount + 1;
            }

            var numUpdates = 0;

            _fixedUpdateStopwatch.Restart();

            var skipForward = false;

            var fixedTimeStep = _settings.FixedTimeStep;
            TrecsDebugAssert.That(fixedTimeStep > 0);

            // Fast-forward and single-step run with the pause already
            // requested; only a request made by a step of this loop stops it.
            var wasPauseRequested = _desiredFixedIsPaused;

            while (
                targetFrame.HasValue
                    ? _currentFixedFrameCount < targetFrame.Value
                    : _interpolationOffset >= 0
            )
            {
                // A pause requested mid-loop (a recording's tail, a desync)
                // stops at the step that asked, not at the end of the frame.
                if (!wasPauseRequested && _desiredFixedIsPaused)
                {
                    break;
                }
                if (
                    !targetFrame.HasValue
                    && _maxSecondsPerFixedUpdate.HasValue
                    && _fixedUpdateStopwatch.Elapsed.TotalSeconds >= _maxSecondsPerFixedUpdate
                )
                {
                    skipForward = true;
                    break;
                }

                SyncJobsAndFlushWrites();

                using (TrecsProfiling.Start("Input Tick"))
                {
                    ExecuteSystemGroup(_sortedInputSystems);

                    SyncJobsAndFlushWrites();
                }

                TrecsDebugAssert.That(_isExecutingSystems);
                _isExecutingSystems = false;

                // Save interpolated values asap so that we interpolate changes
                // made in inputs or the fixedUpdateStarted event, since inputs
                // can be interpolated also
                using (TrecsProfiling.Start("InterpolatePreviousValues"))
                {
                    _interpolatedPreviousSaverManager?.SaveAll();
                }

                using (TrecsProfiling.Start("FixedUpdateStartedEvent"))
                {
                    _fixedUpdateStarted.Invoke();
                }

                using (TrecsProfiling.Start("ApplyInputs"))
                {
                    _entityInputQueue.ApplyInputs(_currentFixedFrameCount);
                }

                // We can't add "TrecsDebugAssert.That(allFixedJobs.IsCompleted)" because it seems that
                // IsCompleted doesn't return the most up-to-date information

                using (TrecsProfiling.Start("FixedTick"))
                {
                    TrecsDebugAssert.That(!_isExecutingSystems);
                    _isExecutingSystems = true;

                    ExecuteSystemGroup(_sortedFixedSystems);

                    _currentFixedFrameCount += 1;
                    _fixedCurrentFrameChangeEvent.Invoke(_currentFixedFrameCount);

                    // It's tempting to let jobs keep running here, and then
                    // rely on the jobs dependencies to ensure that required
                    // state is completed before each subsequence system,
                    // however, it's better to do it every
                    // time, since some code will assume that the adds/removes/moves
                    // are completed in subsequent fixed updates (when executing
                    // multiple fixed updates per frame) and also in variable update

                    // For example, we might have a group that processes all Foos and then moves
                    // them into another group
                    // Without this Submit call, if we run fixed update again, they would
                    // be processed multiple times resulting in bugs

                    // Another example is a case where we remove an entity then immediately
                    // substitute it for an identical entity at the same position/rotation
                    // If we didn't submit entities here, then these removes wouldn't be
                    // processed until the end of the frame, resulting in the game objects
                    // being removed, however the new game object would not be rendered,
                    // resulting in one frame where the object appears to have disappeared
                    // This is because it's often required that variable update runs in
                    // order to actually enable the linked game objects
                    SyncJobsAndFlushWrites();

                    TrecsDebugAssert.That(_isExecutingSystems);
                    _isExecutingSystems = false;
                    Submit();
                }

                _elapsedFixedTime += fixedTimeStep;
                _fixedElapsedTimeChangeEvent.Invoke(_elapsedFixedTime);
                if (!targetFrame.HasValue)
                {
                    _interpolationOffset -= fixedTimeStep;
                }
                numUpdates += 1;

                // This event is used to serialize/deserialize entire state
                // and also do things like generate checksums to detect desyncs
                if (_fixedUpdateCompleted.NumObservers > 0)
                {
                    using (TrecsProfiling.Start("FixedUpdateCompleted Handlers"))
                    {
                        _fixedUpdateCompleted.Invoke();
                    }
                }

                TrecsDebugAssert.That(!_isExecutingSystems);
                _isExecutingSystems = true;
            }

            if (skipForward && _settings.WarnOnFixedUpdateFallingBehind)
            {
                _log.Warning(
                    "Fixed update is falling behind variable update!  Dropping {0} seconds to catch up",
                    _interpolationOffset
                );
            }

            // Time the loop did not run is dropped, never added to fixed time:
            // fixed time is sim state. The frame shows the newest fixed state.
            if (targetFrame.HasValue || _interpolationOffset > 0)
            {
                _interpolationOffset = 0;
            }

            // The target covers exactly one catch-up: clear whether the loop
            // ran to completion or hit some other early-exit path.
            _fastForwardCatchupTargetFrame = null;

            if (
                !_maxSecondsPerFixedUpdate.HasValue
                && numUpdates > 5
                && (
                    !_lastSpiralOfDeathWarningVariableFrame.HasValue
                    || _variableFrameCount - _lastSpiralOfDeathWarningVariableFrame.Value
                        > MinVariableFramesBetweenSpiralOfDeathWarnings
                )
            )
            {
                _lastSpiralOfDeathWarningVariableFrame = _variableFrameCount;

                if (_settings.WarnOnFixedUpdateFallingBehind)
                {
                    _log.Warning(
                        "Fixed update has started falling behind render update! ({0} frames behind) This could enter the dreaded physics timestep spiral of death.  To fix this - provide a value for MaxSecondsPerFixedUpdate in trecs setting.  Note that the simulation then runs slower than real time while it falls behind",
                        numUpdates
                    );
                }
            }
        }

        void ExecuteSystem(int globalIndex)
        {
            TrecsDebugAssert.That(!_hasDisposed);

            var systemInfo = _systems[globalIndex];
            var systemRuntimeInfo = _systemRuntimeInfos[globalIndex];

            systemRuntimeInfo.LastUpdateFrame = _variableFrameCount;

            if (_enableState.ShouldSkipSystem(globalIndex))
            {
                return;
            }

            // Track the executing system's accessor for Fixed phase only — see
            // _currentlyExecutingAccessorId field comment. Variable-cadence
            // phases don't have determinism guarantees that service-class
            // accessors could break, so we leave the tracker at 0 for them.
            bool trackAccessor = systemInfo.Phase == SystemPhase.Fixed;
            if (trackAccessor)
            {
                _currentlyExecutingAccessorId = ((ISystemInternal)systemInfo.System).World.Id;
            }

            try
            {
                using (TrecsProfiling.Start("{0}.Execute", systemInfo.DebugName))
                {
                    ((ISystem)systemInfo.System).Execute();
                }

                // Batched jobs start only on a kick or a Complete. Costs ~0.1 us when
                // the system scheduled nothing.
                JobHandle.ScheduleBatchedJobs();
            }
            finally
            {
                if (trackAccessor)
                {
                    _currentlyExecutingAccessorId = 0;
                }
            }
        }

        [MethodImpl(MethodImplOptions.AggressiveInlining)]
        void ExecuteSystemGroup(List<int> sortedSystems)
        {
            for (int localIndex = 0; localIndex < sortedSystems.Count; localIndex++)
            {
                ExecuteSystem(sortedSystems[localIndex]);
            }
        }

        // Phase boundary: drain all in-flight jobs and flush any pending writes
        // they made into set membership. Both calls together form the canonical
        // "I am about to make structural changes" sync point. Always paired.
        [MethodImpl(MethodImplOptions.AggressiveInlining)]
        void SyncJobsAndFlushWrites()
        {
            using (TrecsProfiling.Start("CompleteAllJobs"))
            {
                _jobScheduler.CompleteAllOutstanding();
            }

            using (TrecsProfiling.Start("FlushSetJobWrites"))
            {
                _entitySubmitter.FlushAllSetJobWrites();
            }
        }

        /// <summary>
        /// Schedule one fixed-update frame to run on the next <see cref="Tick"/>,
        /// then resume the paused state. Requires <see cref="FixedIsPaused"/> to
        /// be true. Only fixed frames are steppable — variable frames are driven
        /// by the Unity update loop.
        /// </summary>
        public void StepFixedFrame()
        {
            TrecsDebugAssert.That(
                _desiredFixedIsPaused,
                "StepFixedFrame requires FixedIsPaused to be true"
            );

            _stepFixedFrame = true;
        }

        public void Tick()
        {
            TrecsDebugAssert.That(!_hasDisposed);

            if (_isPaused)
            {
                return;
            }

            _lastTickFrame = Time.frameCount;
            _variableFrameCount += 1;
            _variableDeltaTime = Time.deltaTime * _timeScale;
            using (TrecsProfiling.Start("VariableFrameCountChange Handlers"))
            {
                _variableFrameCountChangeEvent.Invoke(_variableFrameCount);
            }
            using (TrecsProfiling.Start("VariableDeltaTimeChange Handlers"))
            {
                _variableDeltaTimeChangeEvent.Invoke(_variableDeltaTime.Value);
            }

            _fixedIsPaused = _desiredFixedIsPaused;

            // Paused, the frame shows the newest fixed state. A negative time
            // scale holds at the previous state: nothing runs backwards.
            _interpolationOffset = _fixedIsPaused
                ? 0
                : Mathf.Max(
                    _interpolationOffset + _variableDeltaTime.Value,
                    -_settings.FixedTimeStep
                );

            // Do this after updating _variableFrameCount and _variableDeltaTime
            // since they are accessed sometimes in these handlers
            using (TrecsProfiling.Start("VariableUpdateStarted Handlers"))
            {
                _variableUpdateStarted.Invoke();
            }

            if (_pendingFastForwardRequest.HasValue && !_fixedIsPaused)
            {
                var targetFrame = _pendingFastForwardRequest.Value;
                _pendingFastForwardRequest = null;
                _desiredFixedIsPaused = true;

                if (targetFrame <= _currentFixedFrameCount)
                {
                    _log.Trace(
                        "Ignored fast forward to frame {0} since current frame is already {1}",
                        targetFrame,
                        _currentFixedFrameCount
                    );
                }
                else
                {
                    _fastForwardCatchupTargetFrame = targetFrame;
                }
            }

            if (!_hasRunFirstTick)
            {
                // Always run a submit just before first frame
                // This way, initialization code can schedule add long lived entities
                // and thereafter assume they exist in every execute
                _hasRunFirstTick = true;
                Submit();
            }

            TrecsDebugAssert.That(!_isExecutingSystems);
            _isExecutingSystems = true;

            try
            {
                using (TrecsProfiling.Start("Early Presentation"))
                {
                    ExecuteSystemGroup(_sortedEarlyPresentationSystems);
                }

                // Drives the Input + Fixed loop, running 0..N times based on
                // accumulated variable time and the fixed time step. Manages
                // _isExecutingSystems internally around its inner phases and
                // restores it to true on exit.
                using (TrecsProfiling.Start("Fixed Update"))
                {
                    FixedUpdateSystemExecute();
                }

                using (TrecsProfiling.Start("Presentation"))
                {
                    ExecuteSystemGroup(_sortedPresentationSystems);
                }
            }
            finally
            {
                SyncJobsAndFlushWrites();

                // Don't shadow other exceptions here
                // TrecsDebugAssert.That(_isExecutingSystems);

                _isExecutingSystems = false;
            }
        }

        public void LateTick()
        {
            TrecsDebugAssert.That(!_hasDisposed);

            if (_lastTickFrame != Time.frameCount)
            {
                // only do late tick if we already did tick
                return;
            }

            TrecsDebugAssert.That(!_isExecutingSystems);
            _isExecutingSystems = true;

            try
            {
                ExecuteSystemGroup(_sortedLatePresentationSystems);
            }
            finally
            {
                SyncJobsAndFlushWrites();

                // Don't shadow other exceptions here
                // TrecsDebugAssert.That(_isExecutingSystems);

                _isExecutingSystems = false;
            }

            Submit();

            _elapsedVariableTime += _variableDeltaTime.Value;
            _elapsedVariableTimeChangeEvent.Invoke(_elapsedVariableTime);

            using (TrecsProfiling.Start("VariableUpdateCompleted Handlers"))
            {
                _variableUpdateCompleted.Invoke();
            }
        }

        internal void OnEcsDeserializeCompleted()
        {
            TrecsDebugAssert.That(!_hasDisposed);

            // The frame shows the restored state. Variable time and frame run on.
            _interpolationOffset = 0;

            // Note that deserialization updates the State so need to trigger those callbacks too
            _fixedCurrentFrameChangeEvent.Invoke(_currentFixedFrameCount);
            _fixedElapsedTimeChangeEvent.Invoke(_elapsedFixedTime);
        }

        class SystemRuntimeInfo
        {
            public int LastUpdateFrame;
        }

        const int MinVariableFramesBetweenSpiralOfDeathWarnings = 300;
    }
}
