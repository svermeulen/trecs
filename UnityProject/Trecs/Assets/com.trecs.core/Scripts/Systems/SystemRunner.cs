using System.Collections.Generic;
using System.ComponentModel;
using System.Diagnostics;
using System.Runtime.CompilerServices;
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
        // Frame-typed (not time-typed) because _elapsedFixedTime can drift
        // away from _currentFixedFrameCount * FixedTimeStep when
        // MaxSecondsPerFixedUpdate forces a skip-forward; a time-typed target
        // then maps to the wrong frame.
        int? _pendingFastForwardRequest;

        // While a fast-forward request is being serviced the catch-up loop
        // needs two adjustments: (1) bypass MaxSecondsPerFixedUpdate so we
        // actually run all the fixed frames the caller asked for, and (2)
        // stop at exactly the target frame instead of overshooting by ≥1.
        // The variable-time-based loop bound otherwise overshoots because
        // endOfFrameTime is _elapsedVariableTime + a delta, so we'd run one
        // extra frame past target — which then fires FixedFrameChangeEvent
        // for a frame > target, causing downstream listeners (debug
        // recorder) to mistake it for user-driven progress. Mirroring the
        // request frame here lets us cleanly bound the loop.
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

        public ISimpleObservable<bool> FixedIsPausedChangedEvent => _fixedIsPausedChangedEvent;

        /// <summary>
        /// Note that this is different from unity's Time.timeScale
        /// Changing either one will affect the variable delta time
        /// </summary>
        public float TimeScale
        {
            get
            {
                TrecsAssert.That(!_hasDisposed);
                return _timeScale;
            }
            set
            {
                TrecsAssert.That(!_hasDisposed);
                _timeScale = value;
            }
        }

        /// <summary>
        /// The next variable tick will fast-forward fixed updates until
        /// <see cref="FixedFrame"/> reaches this value, then auto-pause.
        /// Requires <see cref="FixedIsPaused"/> to be false at the start of
        /// that tick. No-op if the target is at or below the current frame.
        ///
        /// Frame-typed (not time-typed): when MaxSecondsPerFixedUpdate forces
        /// a skip-forward, _elapsedFixedTime can drift away from
        /// FixedFrame * FixedTimeStep. A time-based target then computes the
        /// wrong number of frames to advance and lands a few frames short of
        /// the user's intent.
        /// </summary>
        public int? FastForwardTargetFrame
        {
            get
            {
                TrecsAssert.That(!_hasDisposed);
                return _pendingFastForwardRequest;
            }
            set
            {
                TrecsAssert.That(!_hasDisposed);
                _pendingFastForwardRequest = value;
            }
        }

        public RuntimeJobScheduler JobScheduler => _jobScheduler;
        public WorldSafetyManager SafetyManager => _safetyManager;
        internal int CurrentlyExecutingAccessorId => _currentlyExecutingAccessorId;
        internal bool WarnOnJobSyncPoints => _settings.WarnOnJobSyncPoints;
        internal bool RequireDeterministicSubmission => _settings.RequireDeterministicSubmission;
        internal bool AssertNoTimeInFixedPhase => _settings.AssertNoTimeInFixedPhase;

        public bool IsExecutingSystems
        {
            get
            {
                TrecsAssert.That(!_hasDisposed);
                return _isExecutingSystems;
            }
        }

        public float VariableDeltaTime
        {
            get
            {
                TrecsAssert.That(!_hasDisposed);
                return _variableDeltaTime.Value;
            }
            set
            {
                TrecsAssert.That(!_hasDisposed);
                _variableDeltaTime = value;
                _variableDeltaTimeChangeEvent.Invoke(value);
            }
        }

        public ISimpleObservable<float> VariableDeltaTimeChangeEvent
        {
            get
            {
                TrecsAssert.That(!_hasDisposed);
                return _variableDeltaTimeChangeEvent;
            }
        }

        public float VariableElapsedTime
        {
            get
            {
                TrecsAssert.That(!_hasDisposed);
                return _elapsedVariableTime;
            }
            set
            {
                TrecsAssert.That(!_hasDisposed);

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
                TrecsAssert.That(!_hasDisposed);
                return _elapsedVariableTimeChangeEvent;
            }
        }

        public int VariableFrame
        {
            get
            {
                TrecsAssert.That(!_hasDisposed);
                return _variableFrameCount;
            }
            set
            {
                TrecsAssert.That(!_hasDisposed);

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
                TrecsAssert.That(!_hasDisposed);
                return _variableFrameCountChangeEvent;
            }
        }

        public float FixedDeltaTime
        {
            get
            {
                TrecsAssert.That(!_hasDisposed);
                return _settings.FixedTimeStep;
            }
        }

        public float FixedElapsedTime
        {
            get
            {
                TrecsAssert.That(!_hasDisposed);
                return _elapsedFixedTime;
            }
            set
            {
                TrecsAssert.That(!_hasDisposed);
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
                TrecsAssert.That(!_hasDisposed);
                return _fixedElapsedTimeChangeEvent;
            }
        }

        public int FixedFrame
        {
            get
            {
                TrecsAssert.That(!_hasDisposed);
                return _currentFixedFrameCount;
            }
            set
            {
                TrecsAssert.That(!_hasDisposed);
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
                TrecsAssert.That(!_hasDisposed);
                return _fixedCurrentFrameChangeEvent;
            }
        }

        public IReadOnlyList<SystemEntry> Systems
        {
            get
            {
                TrecsAssert.That(!_hasDisposed);
                return _systems;
            }
        }

        public IReadOnlyList<int> SortedFixedSystems
        {
            get
            {
                TrecsAssert.That(!_hasDisposed);
                return _sortedFixedSystems;
            }
        }

        public IReadOnlyList<int> SortedInputSystems
        {
            get
            {
                TrecsAssert.That(!_hasDisposed);
                return _sortedInputSystems;
            }
        }

        public IReadOnlyList<int> SortedEarlyPresentationSystems
        {
            get
            {
                TrecsAssert.That(!_hasDisposed);
                return _sortedEarlyPresentationSystems;
            }
        }

        public IReadOnlyList<int> SortedPresentationSystems
        {
            get
            {
                TrecsAssert.That(!_hasDisposed);
                return _sortedPresentationSystems;
            }
        }

        public IReadOnlyList<int> SortedLatePresentationSystems
        {
            get
            {
                TrecsAssert.That(!_hasDisposed);
                return _sortedLatePresentationSystems;
            }
        }

        internal void SetEventSubjects(EventsManager eventsManager)
        {
            TrecsAssert.IsNull(_fixedUpdateStarted);
            TrecsAssert.IsNull(_fixedUpdateCompleted);
            TrecsAssert.IsNull(_variableUpdateStarted);
            TrecsAssert.IsNull(_variableUpdateCompleted);

            _fixedUpdateStarted = eventsManager.FixedUpdateStartedEvent;
            _fixedUpdateCompleted = eventsManager.FixedUpdateCompletedEvent;
            _variableUpdateStarted = eventsManager.VariableUpdateStartedEvent;
            _variableUpdateCompleted = eventsManager.VariableUpdateCompletedEvent;
        }

        public void Dispose()
        {
            TrecsAssert.That(!_hasDisposed);
            _hasDisposed = true;

            // Drain any remaining tracked jobs before releasing the safety pool, otherwise
            // EnforceAllBufferJobsHaveCompletedAndRelease would block on Burst code that
            // still references handles we're about to release.
            _jobScheduler.CompleteAllOutstanding();
            _safetyManager.Dispose();

            TrecsAssert.That(_variableDeltaTimeChangeEvent.NumObservers == 0);
            TrecsAssert.That(_fixedCurrentFrameChangeEvent.NumObservers == 0);
            TrecsAssert.That(_elapsedVariableTimeChangeEvent.NumObservers == 0);
            TrecsAssert.That(_variableFrameCountChangeEvent.NumObservers == 0);
            TrecsAssert.That(_fixedElapsedTimeChangeEvent.NumObservers == 0);
            TrecsAssert.That(_fixedIsPausedChangedEvent.NumObservers == 0);
        }

        // Note that there are two categories of systems - fixed and variable
        // And they each have their own separate sort indices
        // So it only makes sense to compare sort indices within each group
        public int GetSystemSortIndex(int i)
        {
            TrecsAssert.That(!_hasDisposed);
            TrecsAssert.That(i >= 0 && i < _systemSortIndex.Length);
            return _systemSortIndex[i];
        }

        public void Initialize(
            World world,
            SystemLoader.LoadInfo loadInfo,
            SystemEnableState enableState
        )
        {
            TrecsAssert.That(!_hasDisposed);
            TrecsAssert.IsNotNull(
                _fixedUpdateStarted,
                "SetEventSubjects must be called before Initialize"
            );
            TrecsAssert.IsNotNull(enableState);

            _enableState = enableState;
            TrecsAssert.IsNull(_systems);
            _systems = new List<SystemEntry>(loadInfo.Systems.Count);
            TrecsAssert.IsNull(_systemRuntimeInfos);
            _systemRuntimeInfos = new List<SystemRuntimeInfo>(loadInfo.Systems.Count);
            TrecsAssert.IsNull(_sortedInputSystems);
            _sortedInputSystems = loadInfo.SortedInputSystems;
            TrecsAssert.IsNull(_sortedFixedSystems);
            _sortedFixedSystems = loadInfo.SortedFixedSystems;
            TrecsAssert.IsNull(_sortedEarlyPresentationSystems);
            _sortedEarlyPresentationSystems = loadInfo.SortedEarlyPresentationSystems;
            TrecsAssert.IsNull(_sortedPresentationSystems);
            _sortedPresentationSystems = loadInfo.SortedPresentationSystems;
            TrecsAssert.IsNull(_sortedLatePresentationSystems);
            _sortedLatePresentationSystems = loadInfo.SortedLatePresentationSystems;
            _systemSortIndex = new int[loadInfo.Systems.Count];

            void IndexPhase(List<int> sorted)
            {
                for (int i = 0; i < sorted.Count; i++)
                {
                    var globalIndex = sorted[i];
                    TrecsAssert.That(_systemSortIndex[globalIndex] == 0);
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

        public void SubmitEntities()
        {
            TrecsAssert.That(!_hasDisposed);
            TrecsAssert.That(!_isExecutingSystems);

            using (TrecsProfiling.Start("SubmitEntities"))
            {
                _entitySubmitter.SubmitEntities();
            }
        }

        void FixedUpdateSystemExecute()
        {
            TrecsAssert.That(!_hasDisposed);

            if (_fixedIsPaused && !_stepFixedFrame)
            {
                return;
            }

            float endOfFrameTime;

            if (_stepFixedFrame)
            {
                _stepFixedFrame = false;
                endOfFrameTime = _elapsedFixedTime + _settings.FixedTimeStep - 0.0001f;
            }
            else
            {
                endOfFrameTime = _elapsedVariableTime + _variableDeltaTime.Value;
            }

            var numUpdates = 0;

            _fixedUpdateStopwatch.Restart();

            var skipForward = false;

            var fixedTimeStep = _settings.FixedTimeStep;
            TrecsAssert.That(fixedTimeStep > 0);

            // Keep running until the physics time exceeds the current time
            // so we can interpolate to get the current time
            while (_elapsedFixedTime <= endOfFrameTime)
            {
                // Stop FF catch-up exactly at target — the variable-time
                // bound above would otherwise let one extra iteration
                // through, firing FixedFrameChangeEvent for a frame past
                // the target frame.
                if (
                    _fastForwardCatchupTargetFrame.HasValue
                    && _currentFixedFrameCount >= _fastForwardCatchupTargetFrame.Value
                )
                {
                    break;
                }
                if (
                    !_fastForwardCatchupTargetFrame.HasValue
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

                TrecsAssert.That(_isExecutingSystems);
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

                // We can't add "TrecsAssert.That(allFixedJobs.IsCompleted)" because it seems that
                // IsCompleted doesn't return the most up-to-date information

                using (TrecsProfiling.Start("FixedTick"))
                {
                    TrecsAssert.That(!_isExecutingSystems);
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
                    // Without this SubmitEntities call, if we run fixed update again, they would
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

                    TrecsAssert.That(_isExecutingSystems);
                    _isExecutingSystems = false;
                    SubmitEntities();
                }

                _elapsedFixedTime += fixedTimeStep;
                _fixedElapsedTimeChangeEvent.Invoke(_elapsedFixedTime);
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

                TrecsAssert.That(!_isExecutingSystems);
                _isExecutingSystems = true;
            }

            if (skipForward)
            {
                var fallBehindTime = endOfFrameTime - _elapsedFixedTime;
                _elapsedFixedTime += fallBehindTime + 0.01f;
                _fixedElapsedTimeChangeEvent.Invoke(_elapsedFixedTime);

                if (_settings.WarnOnFixedUpdateFallingBehind)
                {
                    _log.Warning(
                        "Fixed update is falling behind variable update!  Skipping forward by {0} seconds to catch up",
                        fallBehindTime + 0.01f
                    );
                }
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
                        "Fixed update has started falling behind render update! ({0} frames behind) This could enter the dreaded physics timestep spiral of death.  To fix this - provide a value for MaxSecondsPerFixedUpdate in trecs setting.  Note that if you do this that it may get out of sync with recordings or online environments",
                        numUpdates
                    );
                }
            }

            // _dbg.Text("Fixed Update Ticks", numUpdates);
        }

        void ExecuteSystem(int globalIndex)
        {
            TrecsAssert.That(!_hasDisposed);

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
            TrecsAssert.That(
                _desiredFixedIsPaused,
                "StepFixedFrame requires FixedIsPaused to be true"
            );

            _stepFixedFrame = true;
        }

        public void Tick()
        {
            TrecsAssert.That(!_hasDisposed);
            // _dbg.Text("IsPaused: {}", _isPaused);
            // _dbg.Text("FixedIsPaused: {}", _fixedIsPaused);

            if (_isPaused)
            {
                return;
            }

            _lastTickFrame = Time.frameCount;
            _variableFrameCount += 1;
            _variableDeltaTime = Time.deltaTime * _timeScale;
            _variableFrameCountChangeEvent.Invoke(_variableFrameCount);
            _variableDeltaTimeChangeEvent.Invoke(_variableDeltaTime.Value);

            if (_fixedIsPaused != _desiredFixedIsPaused)
            {
                _fixedIsPaused = _desiredFixedIsPaused;

                if (!_fixedIsPaused)
                {
                    // When unpausing fixed update, reset variable time back
                    // Could also have fixed time catch up, but this seems
                    // less safe
                    _elapsedVariableTime = _elapsedFixedTime;
                }
            }

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
                    // Bump variable time so the catch-up loop's
                    // `_elapsedFixedTime <= endOfFrameTime` bound permits all
                    // (targetFrame - currentFrame) iterations. Subtract one
                    // Time.deltaTime so endOfFrameTime (= _elapsedVariableTime
                    // + _variableDeltaTime) lands at the desired final
                    // elapsed-fixed-time without overshooting.
                    var framesToAdvance = targetFrame - _currentFixedFrameCount;
                    _elapsedVariableTime =
                        _elapsedFixedTime
                        + framesToAdvance * _settings.FixedTimeStep
                        - Time.deltaTime;
                    _elapsedVariableTimeChangeEvent.Invoke(_elapsedVariableTime);
                }
            }

            // _dbg.Text("CurrentFixedFrameCount: {}", _currentFixedFrameCount);
            // _dbg.Text("ElapsedFixedTime: {}", _elapsedFixedTime);

            if (!_hasRunFirstTick)
            {
                // Always run a submit just before first frame
                // This way, initialization code can schedule add long lived entities
                // and thereafter assume they exist in every execute
                _hasRunFirstTick = true;
                SubmitEntities();
            }

            TrecsAssert.That(!_isExecutingSystems);
            _isExecutingSystems = true;

            try
            {
                ExecuteSystemGroup(_sortedEarlyPresentationSystems);

                // Drives the Input + Fixed loop, running 0..N times based on
                // accumulated variable time and the fixed time step. Manages
                // _isExecutingSystems internally around its inner phases and
                // restores it to true on exit.
                FixedUpdateSystemExecute();

                ExecuteSystemGroup(_sortedPresentationSystems);
            }
            finally
            {
                SyncJobsAndFlushWrites();

                // Don't shadow other exceptions here
                // TrecsAssert.That(_isExecutingSystems);

                _isExecutingSystems = false;
            }

            // Any reason to do a submit here or is one in late tick sufficient?/
        }

        public void LateTick()
        {
            TrecsAssert.That(!_hasDisposed);

            if (_lastTickFrame != Time.frameCount)
            {
                // only do late tick if we already did tick
                return;
            }

            TrecsAssert.That(!_isExecutingSystems);
            _isExecutingSystems = true;

            try
            {
                ExecuteSystemGroup(_sortedLatePresentationSystems);
            }
            finally
            {
                SyncJobsAndFlushWrites();

                // Don't shadow other exceptions here
                // TrecsAssert.That(_isExecutingSystems);

                _isExecutingSystems = false;
            }

            SubmitEntities();

            _elapsedVariableTime += _variableDeltaTime.Value;
            _elapsedVariableTimeChangeEvent.Invoke(_elapsedVariableTime);

            using (TrecsProfiling.Start("VariableUpdateCompleted Handlers"))
            {
                _variableUpdateCompleted.Invoke();
            }
        }

        internal void OnEcsDeserializeCompleted()
        {
            TrecsAssert.That(!_hasDisposed);

            _variableFrameCount = 0;
            // This is necessary because fixed update is always catching up to whatever this is
            _elapsedVariableTime = _elapsedFixedTime;

            // Note that deserialization updates the State so need to trigger those callbacks too
            _fixedCurrentFrameChangeEvent.Invoke(_currentFixedFrameCount);
            _fixedElapsedTimeChangeEvent.Invoke(_elapsedFixedTime);
            _variableFrameCountChangeEvent.Invoke(_variableFrameCount);
            _elapsedVariableTimeChangeEvent.Invoke(_elapsedVariableTime);
        }

        class SystemRuntimeInfo
        {
            public int LastUpdateFrame;
        }

        const int MinVariableFramesBetweenSpiralOfDeathWarnings = 300;
    }
}
