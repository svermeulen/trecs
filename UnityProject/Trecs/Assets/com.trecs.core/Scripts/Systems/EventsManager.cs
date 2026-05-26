using System;
using System.ComponentModel;
using Trecs.Collections;

namespace Trecs.Internal
{
    [EditorBrowsable(EditorBrowsableState.Never)]
    public sealed class EventsManager : IDisposable
    {
        readonly TrecsLog _log;

        readonly SimpleSubject _deserializeStartedEvent = new();
        readonly SimpleSubject _deserializeCompletedEvent = new();
        readonly SimpleSubject _submissionStartedEvent = new();
        readonly SimpleSubject _submissionCompletedEvent = new();
        readonly SimpleSubject _fixedUpdateStartedEvent = new();
        readonly SimpleSubject _fixedUpdateCompletedEvent = new();
        readonly SimpleSubject _variableUpdateStartedEvent = new();
        readonly SimpleSubject _variableUpdateCompletedEvent = new();
        readonly SimpleSubject _inputsAppliedEvent = new();
        readonly SimpleSubject _shutdownEvent = new();

        readonly IterableDictionary<
            GroupIndex,
            SimpleSubject<EntityRange>
        > _reactiveOnAddedObservers;
        readonly IterableDictionary<
            GroupIndex,
            SimpleSubject<GroupIndex, EntityRange>
        > _reactiveOnMovedObservers;
        readonly IterableDictionary<
            GroupIndex,
            SimpleSubject<EntityRange>
        > _reactiveOnRemovedObservers;

        internal SimpleSubject DeserializeStartedEvent => _deserializeStartedEvent;
        internal SimpleSubject DeserializeCompletedEvent => _deserializeCompletedEvent;
        internal SimpleSubject SubmissionStartedEvent => _submissionStartedEvent;
        internal SimpleSubject SubmissionCompletedEvent => _submissionCompletedEvent;
        internal SimpleSubject FixedUpdateStartedEvent => _fixedUpdateStartedEvent;
        internal SimpleSubject FixedUpdateCompletedEvent => _fixedUpdateCompletedEvent;
        internal SimpleSubject VariableUpdateStartedEvent => _variableUpdateStartedEvent;
        internal SimpleSubject VariableUpdateCompletedEvent => _variableUpdateCompletedEvent;
        internal SimpleSubject InputsAppliedEvent => _inputsAppliedEvent;
        internal SimpleSubject ShutdownEvent => _shutdownEvent;

        internal IterableDictionary<
            GroupIndex,
            SimpleSubject<EntityRange>
        > ReactiveOnAddedObservers => _reactiveOnAddedObservers;

        internal IterableDictionary<
            GroupIndex,
            SimpleSubject<GroupIndex, EntityRange>
        > ReactiveOnMovedObservers => _reactiveOnMovedObservers;

        internal IterableDictionary<
            GroupIndex,
            SimpleSubject<EntityRange>
        > ReactiveOnRemovedObservers => _reactiveOnRemovedObservers;

        public EventsManager(TrecsLog log)
        {
            _log = log;
            _reactiveOnAddedObservers = new();
            _reactiveOnMovedObservers = new();
            _reactiveOnRemovedObservers = new();
        }

        internal IDisposable ObserveEntitiesAddedEvent(
            GroupIndex group,
            EntitiesAddedObserver observer,
            int priority = 0,
            string debugName = null
        )
        {
            if (!_reactiveOnAddedObservers.TryGetValue(group, out var subject))
            {
                subject = new SimpleSubject<EntityRange>();
                _reactiveOnAddedObservers.Add(group, subject);
            }

            return subject.Subscribe(indices => observer(group, indices), priority, debugName);
        }

        internal IDisposable ObserveEntitiesMovedEvent(
            GroupIndex toGroup,
            EntitiesMovedObserver observer,
            int priority = 0,
            string debugName = null
        )
        {
            if (!_reactiveOnMovedObservers.TryGetValue(toGroup, out var subject))
            {
                subject = new SimpleSubject<GroupIndex, EntityRange>();
                _reactiveOnMovedObservers.Add(toGroup, subject);
            }

            return subject.Subscribe(
                (fromGroup, indices) => observer(fromGroup, toGroup, indices),
                priority,
                debugName
            );
        }

        internal IDisposable ObserveEntitiesRemovedEvent(
            GroupIndex group,
            EntitiesRemovedObserver observer,
            int priority = 0,
            string debugName = null
        )
        {
            if (!_reactiveOnRemovedObservers.TryGetValue(group, out var subject))
            {
                subject = new SimpleSubject<EntityRange>();
                _reactiveOnRemovedObservers.Add(group, subject);
            }

            return subject.Subscribe(indices => observer(group, indices), priority, debugName);
        }

        public EntityEventsBuilder Events(
            WorldInfo worldInfo,
            WorldAccessor world,
            SystemRunner systemRunner
        )
        {
            return new EntityEventsBuilder(this, worldInfo, world, systemRunner);
        }

        internal void NotifyOnSubmissionStarted()
        {
            _submissionStartedEvent.Invoke();
        }

        internal void NotifyOnSubmissionCompleted()
        {
            _submissionCompletedEvent.Invoke();
        }

        public void Dispose()
        {
            if (_log.IsWarningEnabled())
            {
                if (_deserializeStartedEvent.NumObservers > 0)
                    _log.Warning(
                        "DeserializeStarted observers not cleaned up by accessors {0} (count: {1})",
                        _deserializeStartedEvent.GetDebugNamesSummary(),
                        _deserializeStartedEvent.NumObservers
                    );
                if (_deserializeCompletedEvent.NumObservers > 0)
                    _log.Warning(
                        "DeserializeCompleted observers not cleaned up by accessors {0} (count: {1})",
                        _deserializeCompletedEvent.GetDebugNamesSummary(),
                        _deserializeCompletedEvent.NumObservers
                    );
                if (_submissionStartedEvent.NumObservers > 0)
                    _log.Warning(
                        "SubmissionStarted observers not cleaned up by accessors {0} (count: {1})",
                        _submissionStartedEvent.GetDebugNamesSummary(),
                        _submissionStartedEvent.NumObservers
                    );
                if (_submissionCompletedEvent.NumObservers > 0)
                    _log.Warning(
                        "SubmissionCompleted observers not cleaned up by accessors {0} (count: {1})",
                        _submissionCompletedEvent.GetDebugNamesSummary(),
                        _submissionCompletedEvent.NumObservers
                    );
                if (_fixedUpdateStartedEvent.NumObservers > 0)
                    _log.Warning(
                        "FixedUpdateStarted observers not cleaned up by accessors {0} (count: {1})",
                        _fixedUpdateStartedEvent.GetDebugNamesSummary(),
                        _fixedUpdateStartedEvent.NumObservers
                    );
                if (_fixedUpdateCompletedEvent.NumObservers > 0)
                    _log.Warning(
                        "FixedUpdateCompleted observers not cleaned up by accessors {0} (count: {1})",
                        _fixedUpdateCompletedEvent.GetDebugNamesSummary(),
                        _fixedUpdateCompletedEvent.NumObservers
                    );
                if (_variableUpdateStartedEvent.NumObservers > 0)
                    _log.Warning(
                        "VariableUpdateStarted observers not cleaned up by accessors {0} (count: {1})",
                        _variableUpdateStartedEvent.GetDebugNamesSummary(),
                        _variableUpdateStartedEvent.NumObservers
                    );
                if (_variableUpdateCompletedEvent.NumObservers > 0)
                    _log.Warning(
                        "VariableUpdateCompleted observers not cleaned up by accessors {0} (count: {1})",
                        _variableUpdateCompletedEvent.GetDebugNamesSummary(),
                        _variableUpdateCompletedEvent.NumObservers
                    );
                if (_inputsAppliedEvent.NumObservers > 0)
                    _log.Warning(
                        "InputsApplied observers not cleaned up by accessors {0} (count: {1})",
                        _inputsAppliedEvent.GetDebugNamesSummary(),
                        _inputsAppliedEvent.NumObservers
                    );
                if (_shutdownEvent.NumObservers > 0)
                    _log.Warning(
                        "Shutdown observers not cleaned up by accessors {0} (count: {1})",
                        _shutdownEvent.GetDebugNamesSummary(),
                        _shutdownEvent.NumObservers
                    );
                foreach (var (group, subject) in _reactiveOnAddedObservers)
                    if (subject.NumObservers > 0)
                        _log.Warning(
                            "Entity added observers not cleaned up by accessors {0} for group {1} (count: {2})",
                            subject.GetDebugNamesSummary(),
                            group,
                            subject.NumObservers
                        );
                foreach (var (group, subject) in _reactiveOnRemovedObservers)
                    if (subject.NumObservers > 0)
                        _log.Warning(
                            "Entity removed observers not cleaned up by accessors {0} for group {1} (count: {2})",
                            subject.GetDebugNamesSummary(),
                            group,
                            subject.NumObservers
                        );
                foreach (var (group, subject) in _reactiveOnMovedObservers)
                    if (subject.NumObservers > 0)
                        _log.Warning(
                            "Entity moved observers not cleaned up by accessors {0} for group {1} (count: {2})",
                            subject.GetDebugNamesSummary(),
                            group,
                            subject.NumObservers
                        );
            }

            _reactiveOnAddedObservers.Clear();
            _reactiveOnMovedObservers.Clear();
            _reactiveOnRemovedObservers.Clear();
        }
    }
}
