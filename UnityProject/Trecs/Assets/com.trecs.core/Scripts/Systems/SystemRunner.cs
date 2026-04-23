using System.Collections.Generic;
using System.ComponentModel;
using System.Diagnostics;
using UnityEngine;

namespace Trecs.Internal
{
    [EditorBrowsable(EditorBrowsableState.Never)]
    public partial class SystemRunner
    {
        static readonly TrecsLog _log = new(nameof(SystemRunner));

        readonly SimpleSubject _readyToApplyInputs = new();

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

        readonly EntitySubmitter _entitySubmitter;

        SimpleSubject _fixedUpdateCompleted;
        SimpleSubject _fixedUpdateStarted;
        SimpleSubject _variableUpdateStarted;

        List<ExecutableSystemInfo> _systems;
        List<SystemRuntimeInfo> _systemRuntimeInfos;
        List<int> _sortedInputSystems;
        List<int> _sortedVariableSystems;
        List<int> _sortedLateVariableSystems;
        List<int> _sortedFixedSystems;
        int[] _systemSortIndex;
        int? _fixedUpdateGlobalIndex;
        int _lastTickFrame;

        internal int _currentFixedFrameCount;
        internal float _elapsedFixedTime;
        float _elapsedVariableTime;
        float? _variableDeltaTime;
        int _variableFrameCount;
        bool _isExecutingSystems;
        bool _isPaused = false;
        bool _fixedIsPaused = false;
        bool _stepFixedFrame = false;
        bool _desiredFixedIsPaused = false;
        int? _lastSpiralOfDeathWarningVariableFrame;
        float? _fastForwardTime;
        bool _postFastForwardSkipFrame;
        float _timeScale = 1f;

        bool _hasDisposed;
        bool _hasRunFirstTick;

        public SystemRunner(
            EntitySubmitter entitySubmitter,
            WorldSettings settings,
            InterpolatedPreviousSaverManager interpolatedPreviousSaverManager,
            RuntimeJobScheduler jobScheduler
        )
        {
            settings ??= new WorldSettings();

            _maxSecondsPerFixedUpdate = settings.MaxSecondsForFixedUpdatePerFrame;
            _interpolatedPreviousSaverManager = interpolatedPreviousSaverManager;

            _isPaused = settings.StartPaused;
            _settings = settings;
            _jobScheduler = jobScheduler;

            _entitySubmitter = entitySubmitter;

            _log.Trace("Using Trecs Settings: {@}", settings);
        }

        /// <summary>
        /// Note that this is different from unity's Time.timeScale
        /// Changing either one will affect the variable delta time
        /// </summary>
        public float TimeScale
        {
            get
            {
                Assert.That(!_hasDisposed);
                return _timeScale;
            }
            set
            {
                Assert.That(!_hasDisposed);
                _timeScale = value;
            }
        }

        public float? FastForwardTime
        {
            get
            {
                Assert.That(!_hasDisposed);
                return _fastForwardTime;
            }
            set
            {
                Assert.That(!_hasDisposed);
                _fastForwardTime = value;
            }
        }

        internal ISimpleObservable ReadyToApplyInputs
        {
            get
            {
                Assert.That(!_hasDisposed);
                return _readyToApplyInputs;
            }
        }

        public int FixedUpdateIndex
        {
            get
            {
                Assert.That(!_hasDisposed);
                return _fixedUpdateGlobalIndex.Value;
            }
        }

        public RuntimeJobScheduler JobScheduler => _jobScheduler;
        public WorldSafetyManager SafetyManager => _safetyManager;
        internal bool WarnOnJobSyncPoints => _settings.WarnOnJobSyncPoints;
        internal bool RequireDeterministicSubmission => _settings.RequireDeterministicSubmission;
        internal bool AssertNoTimeInFixedPhase => _settings.AssertNoTimeInFixedPhase;

        public bool IsExecutingSystems
        {
            get
            {
                Assert.That(!_hasDisposed);
                return _isExecutingSystems;
            }
        }

        public float VariableDeltaTime
        {
            get
            {
                Assert.That(!_hasDisposed);
                return _variableDeltaTime.Value;
            }
            set
            {
                Assert.That(!_hasDisposed);
                _variableDeltaTime = value;
                _variableDeltaTimeChangeEvent.Invoke(value);
            }
        }

        public ISimpleObservable<float> VariableDeltaTimeChangeEvent
        {
            get
            {
                Assert.That(!_hasDisposed);
                return _variableDeltaTimeChangeEvent;
            }
        }

        public float VariableElapsedTime
        {
            get
            {
                Assert.That(!_hasDisposed);
                return _elapsedVariableTime;
            }
            set
            {
                Assert.That(!_hasDisposed);

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
                Assert.That(!_hasDisposed);
                return _elapsedVariableTimeChangeEvent;
            }
        }

        public int VariableFrame
        {
            get
            {
                Assert.That(!_hasDisposed);
                return _variableFrameCount;
            }
            set
            {
                Assert.That(!_hasDisposed);

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
                Assert.That(!_hasDisposed);
                return _variableFrameCountChangeEvent;
            }
        }

        public float FixedDeltaTime
        {
            get
            {
                Assert.That(!_hasDisposed);
                return _settings.FixedTimeStep;
            }
        }

        public float FixedElapsedTime
        {
            get
            {
                Assert.That(!_hasDisposed);
                return _elapsedFixedTime;
            }
            set
            {
                Assert.That(!_hasDisposed);
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
                Assert.That(!_hasDisposed);
                return _fixedElapsedTimeChangeEvent;
            }
        }

        public int FixedFrame
        {
            get
            {
                Assert.That(!_hasDisposed);
                return _currentFixedFrameCount;
            }
            set
            {
                Assert.That(!_hasDisposed);
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
                Assert.That(!_hasDisposed);
                return _fixedCurrentFrameChangeEvent;
            }
        }

        public IReadOnlyList<ExecutableSystemInfo> Systems
        {
            get
            {
                Assert.That(!_hasDisposed);
                return _systems;
            }
        }

        public IReadOnlyList<int> SortedFixedSystems
        {
            get
            {
                Assert.That(!_hasDisposed);
                return _sortedFixedSystems;
            }
        }

        public IReadOnlyList<int> SortedVariableSystems
        {
            get
            {
                Assert.That(!_hasDisposed);
                return _sortedVariableSystems;
            }
        }

        public IReadOnlyList<int> SortedInputSystems
        {
            get
            {
                Assert.That(!_hasDisposed);
                return _sortedInputSystems;
            }
        }

        public IReadOnlyList<int> SortedLateVariableSystems
        {
            get
            {
                Assert.That(!_hasDisposed);
                return _sortedLateVariableSystems;
            }
        }

        internal void SetEventSubjects(EventsManager eventsManager)
        {
            Assert.IsNull(_fixedUpdateStarted);
            Assert.IsNull(_fixedUpdateCompleted);
            Assert.IsNull(_variableUpdateStarted);

            _fixedUpdateStarted = eventsManager.FixedUpdateStartedEvent;
            _fixedUpdateCompleted = eventsManager.FixedUpdateCompletedEvent;
            _variableUpdateStarted = eventsManager.VariableUpdateStartedEvent;
        }

        public void Dispose()
        {
            Assert.That(!_hasDisposed);
            _hasDisposed = true;

            // Drain any remaining tracked jobs before releasing the safety pool, otherwise
            // EnforceAllBufferJobsHaveCompletedAndRelease would block on Burst code that
            // still references handles we're about to release.
            _jobScheduler.CompleteAllOutstanding();
            _safetyManager.Dispose();

            Assert.That(_readyToApplyInputs.NumObservers == 0);
            Assert.That(_variableDeltaTimeChangeEvent.NumObservers == 0);
            Assert.That(_fixedCurrentFrameChangeEvent.NumObservers == 0);
            Assert.That(_elapsedVariableTimeChangeEvent.NumObservers == 0);
            Assert.That(_variableFrameCountChangeEvent.NumObservers == 0);
            Assert.That(_fixedElapsedTimeChangeEvent.NumObservers == 0);
            Assert.That(_fixedIsPausedChangedEvent.NumObservers == 0);
        }

        // Note that there are two categories of systems - fixed and variable
        // And they each have their own separate sort indices
        // So it only makes sense to compare sort indices within each group
        public int GetSystemSortIndex(int i)
        {
            Assert.That(!_hasDisposed);
            Assert.That(i >= 0 && i < _systemSortIndex.Length);
            return _systemSortIndex[i];
        }

        public void Initialize(World world, SystemLoader.LoadInfo loadInfo)
        {
            Assert.That(!_hasDisposed);
            Assert.IsNotNull(
                _fixedUpdateStarted,
                "SetEventSubjects must be called before Initialize"
            );

            Assert.IsNull(_systems);
            _systems = new List<ExecutableSystemInfo>(loadInfo.Systems.Count);
            Assert.IsNull(_systemRuntimeInfos);
            _systemRuntimeInfos = new List<SystemRuntimeInfo>(loadInfo.Systems.Count);
            Assert.IsNull(_sortedVariableSystems);
            _sortedVariableSystems = loadInfo.SortedVariableSystems;
            Assert.IsNull(_sortedInputSystems);
            _sortedInputSystems = loadInfo.SortedInputSystems;
            Assert.IsNull(_sortedLateVariableSystems);
            _sortedLateVariableSystems = loadInfo.SortedLateVariableSystems;
            Assert.IsNull(_sortedFixedSystems);
            _sortedFixedSystems = loadInfo.SortedFixedSystems;
            _systemSortIndex = new int[loadInfo.Systems.Count];

            for (int i = 0; i < _sortedVariableSystems.Count; i++)
            {
                var globalIndex = _sortedVariableSystems[i];
                Assert.That(_systemSortIndex[globalIndex] == 0);
                _systemSortIndex[globalIndex] = i;
            }

            for (int i = 0; i < _sortedInputSystems.Count; i++)
            {
                var globalIndex = _sortedInputSystems[i];
                Assert.That(_systemSortIndex[globalIndex] == 0);
                _systemSortIndex[globalIndex] = i;
            }

            for (int i = 0; i < _sortedLateVariableSystems.Count; i++)
            {
                var globalIndex = _sortedLateVariableSystems[i];
                Assert.That(_systemSortIndex[globalIndex] == 0);
                _systemSortIndex[globalIndex] = i;
            }

            for (int i = 0; i < _sortedFixedSystems.Count; i++)
            {
                var globalIndex = _sortedFixedSystems[i];
                Assert.That(_systemSortIndex[globalIndex] == 0);
                _systemSortIndex[globalIndex] = i;
            }

            Assert.That(!_fixedUpdateGlobalIndex.HasValue);

            for (int i = 0; i < loadInfo.Systems.Count; i++)
            {
                var info = loadInfo.Systems[i];

                if (info.System is FixedUpdateSystem fixedUpdateSystem)
                {
                    Assert.That(!_fixedUpdateGlobalIndex.HasValue);
                    _fixedUpdateGlobalIndex = i;
                    fixedUpdateSystem.FixedTickHandler = FixedUpdateSystemExecute;
                }

                if (
                    _log.IsTraceEnabled()
                    && info.Querier == null
                    && info.System is not FixedUpdateSystem
                )
                {
                    // This is fairly common actually, in cases where we add to input queue, or just create/remove entities, etc.
                    _log.Trace(
                        "System {} (type {}) does not have any queriers",
                        info.Metadata.DebugName,
                        info.System.GetType()
                    );
                }

                _systemRuntimeInfos.Add(new SystemRuntimeInfo() { IsEnabled = true });
                _systems.Add(
                    new ExecutableSystemInfo(info.System, info.Metadata, info.DeclarationIndex)
                );
            }

            Assert.That(_fixedUpdateGlobalIndex.HasValue);

            _log.Debug(
                "SystemRunner initialization complete. Loaded {} trecs systems in total",
                _systems.Count
            );
        }

        public void SubmitEntities()
        {
            Assert.That(!_hasDisposed);
            Assert.That(!_isExecutingSystems);

            using (TrecsProfiling.Start("SubmitEntities"))
            {
                _entitySubmitter.SubmitEntities();
            }
        }

        void FixedUpdateSystemExecute()
        {
            Assert.That(!_hasDisposed);

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
            Assert.That(fixedTimeStep > 0);

            // Keep running until the physics time exceeds the current time
            // so we can interpolate to get the current time
            while (_elapsedFixedTime <= endOfFrameTime)
            {
                if (
                    _maxSecondsPerFixedUpdate.HasValue
                    && _fixedUpdateStopwatch.Elapsed.TotalSeconds >= _maxSecondsPerFixedUpdate
                )
                {
                    skipForward = true;
                    break;
                }

                _jobScheduler.CompleteAllOutstanding();
                _entitySubmitter._setStore.FlushAllSetJobWrites();

                using (TrecsProfiling.Start("Input Tick"))
                {
                    for (int localIndex = 0; localIndex < _sortedInputSystems.Count; localIndex++)
                    {
                        var globalIndex = _sortedInputSystems[localIndex];
                        ExecuteSystem(globalIndex);
                    }

                    _jobScheduler.CompleteAllOutstanding();
                    _entitySubmitter._setStore.FlushAllSetJobWrites();
                }

                Assert.That(_isExecutingSystems);
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
                    _readyToApplyInputs.Invoke();
                }

                // We can't add "Assert.That(allFixedJobs.IsCompleted)" because it seems that
                // IsCompleted doesn't return the most up-to-date information

                using (TrecsProfiling.Start("FixedTick"))
                {
                    Assert.That(!_isExecutingSystems);
                    _isExecutingSystems = true;

                    for (int localIndex = 0; localIndex < _sortedFixedSystems.Count; localIndex++)
                    {
                        var globalIndex = _sortedFixedSystems[localIndex];
                        ExecuteSystem(globalIndex);
                    }

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
                    using (TrecsProfiling.Start("CompleteAllJobs"))
                    {
                        _jobScheduler.CompleteAllOutstanding();
                    }

                    using (TrecsProfiling.Start("FlushSetJobWrites"))
                    {
                        _entitySubmitter._setStore.FlushAllSetJobWrites();
                    }

                    Assert.That(_isExecutingSystems);
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

                Assert.That(!_isExecutingSystems);
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
                        "Fixed update is falling behind variable update!  Skipping forward by {} seconds to catch up",
                        fallBehindTime + 0.01f
                    );
                }
            }

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
                        "Fixed update has started falling behind render update! ({} frames behind) This could enter the dreaded physics timestep spiral of death.  To fix this - provide a value for MaxSecondsPerFixedUpdate in trecs setting.  Note that if you do this that it may get out of sync with recordings or online environments",
                        numUpdates
                    );
                }
            }

            // _dbg.Text("Fixed Update Ticks", numUpdates);
        }

        public bool IsSystemEnabled(int i)
        {
            Assert.That(!_hasDisposed);
            return _systemRuntimeInfos[i].IsEnabled;
        }

        public void SetSystemEnabled(int index, bool enabled)
        {
            Assert.That(!_hasDisposed);
            Assert.That(index >= 0 && index < _systemRuntimeInfos.Count);
            _systemRuntimeInfos[index].IsEnabled = enabled;
        }

        public bool IsPaused
        {
            get { return _isPaused; }
            set { _isPaused = value; }
        }

        public ISimpleObservable<bool> FixedIsPausedChangedEvent => _fixedIsPausedChangedEvent;

        public bool FixedIsPaused
        {
            get { return _desiredFixedIsPaused; }
            set
            {
                if (_desiredFixedIsPaused != value)
                {
                    _desiredFixedIsPaused = value;
                    _fixedIsPausedChangedEvent.Invoke(_desiredFixedIsPaused);
                }
            }
        }

        void ExecuteSystem(int globalIndex)
        {
            Assert.That(!_hasDisposed);

            var systemInfo = _systems[globalIndex];
            var systemRuntimeInfo = _systemRuntimeInfos[globalIndex];

            systemRuntimeInfo.LastUpdateFrame = _variableFrameCount;

            if (!systemRuntimeInfo.IsEnabled)
            {
                return;
            }

            using (TrecsProfiling.Start("{l}.Execute", systemInfo.Metadata.DebugName))
            {
                ((ISystem)systemInfo.System).Execute();
            }
        }

        public void StepFrame()
        {
            if (!_fixedIsPaused)
            {
                _log.Warning("Attempted to step frame but not paused");
                return;
            }

            _stepFixedFrame = true;
        }

        public void Tick()
        {
            Assert.That(!_hasDisposed);
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

            if (_postFastForwardSkipFrame)
            {
                _postFastForwardSkipFrame = false;
                _log.Debug("Skipping post fast forward frame with delta time {}", Time.deltaTime);
            }

            if (_fastForwardTime.HasValue && !_fixedIsPaused)
            {
                var newVariableTime = Mathf.Max(0, _fastForwardTime.Value - Time.deltaTime);
                _fastForwardTime = null;
                _desiredFixedIsPaused = true;

                if (newVariableTime < _elapsedVariableTime)
                {
                    _log.Warning("Ignored fast forward request since given time is in the past");
                }
                else
                {
                    _postFastForwardSkipFrame = true;
                    _elapsedVariableTime = newVariableTime;
                    _elapsedVariableTimeChangeEvent.Invoke(_elapsedVariableTime);

                    if (_maxSecondsPerFixedUpdate.HasValue)
                    {
                        _log.Warning(
                            "Fast forwarding while also using MaxSecondsPerFixedUpdate - this can create desyncs"
                        );
                    }
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

            Assert.That(!_isExecutingSystems);
            _isExecutingSystems = true;

            try
            {
                for (int localIndex = 0; localIndex < _sortedVariableSystems.Count; localIndex++)
                {
                    var globalIndex = _sortedVariableSystems[localIndex];
                    ExecuteSystem(globalIndex);
                }
            }
            finally
            {
                _jobScheduler.CompleteAllOutstanding();
                _entitySubmitter._setStore.FlushAllSetJobWrites();

                // Don't shadow other exceptions here
                // Assert.That(_isExecutingSystems);

                _isExecutingSystems = false;
            }

            // Any reason to do a submit here or is one in late tick sufficient?/
        }

        public void LateTick()
        {
            Assert.That(!_hasDisposed);

            if (_lastTickFrame != Time.frameCount)
            {
                // only do late tick if we already did tick
                return;
            }

            Assert.That(!_isExecutingSystems);
            _isExecutingSystems = true;

            try
            {
                for (
                    int localIndex = 0;
                    localIndex < _sortedLateVariableSystems.Count;
                    localIndex++
                )
                {
                    var globalIndex = _sortedLateVariableSystems[localIndex];
                    ExecuteSystem(globalIndex);
                }
            }
            finally
            {
                _jobScheduler.CompleteAllOutstanding();
                _entitySubmitter._setStore.FlushAllSetJobWrites();

                // Don't shadow other exceptions here
                // Assert.That(_isExecutingSystems);

                _isExecutingSystems = false;
            }

            SubmitEntities();

            _elapsedVariableTime += _variableDeltaTime.Value;
            _elapsedVariableTimeChangeEvent.Invoke(_elapsedVariableTime);
        }

        internal void OnEcsDeserializeCompleted()
        {
            Assert.That(!_hasDisposed);

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
            public bool IsEnabled;
        }

        const int MinVariableFramesBetweenSpiralOfDeathWarnings = 300;
    }
}
