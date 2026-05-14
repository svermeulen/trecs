using System;
using System.Collections.Generic;
using System.ComponentModel;
using Trecs.Collections;

namespace Trecs.Internal
{
    internal readonly struct PrioritizedObserver<T>
    {
        public readonly int Priority;
        public readonly T Observer;
        public readonly string DebugName;

        public PrioritizedObserver(int priority, T observer, string debugName)
        {
            Priority = priority;
            Observer = observer;
            DebugName = debugName;
        }
    }

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

        readonly DenseDictionary<
            GroupIndex,
            FastList<PrioritizedObserver<EntitiesAddedObserver>>
        > _reactiveOnAddedObservers;

        readonly DenseDictionary<
            GroupIndex,
            FastList<PrioritizedObserver<EntitiesMovedObserver>>
        > _reactiveOnMovedObservers;

        readonly DenseDictionary<
            GroupIndex,
            FastList<PrioritizedObserver<EntitiesRemovedObserver>>
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

        internal DenseDictionary<
            GroupIndex,
            FastList<PrioritizedObserver<EntitiesAddedObserver>>
        > ReactiveOnAddedObservers => _reactiveOnAddedObservers;

        internal DenseDictionary<
            GroupIndex,
            FastList<PrioritizedObserver<EntitiesMovedObserver>>
        > ReactiveOnMovedObservers => _reactiveOnMovedObservers;

        internal DenseDictionary<
            GroupIndex,
            FastList<PrioritizedObserver<EntitiesRemovedObserver>>
        > ReactiveOnRemovedObservers => _reactiveOnRemovedObservers;

        public EventsManager(TrecsLog log)
        {
            _log = log;
            _reactiveOnAddedObservers = new();
            _reactiveOnMovedObservers = new();
            _reactiveOnRemovedObservers = new();
        }

        internal void ObserveEntitiesAddedEvent(
            GroupIndex group,
            EntitiesAddedObserver observer,
            int priority = 0,
            string debugName = null
        )
        {
            if (!_reactiveOnAddedObservers.TryGetValue(group, out var list))
            {
                list = new FastList<PrioritizedObserver<EntitiesAddedObserver>>();
                _reactiveOnAddedObservers.Add(group, list);
            }

            InsertSorted(
                list,
                new PrioritizedObserver<EntitiesAddedObserver>(priority, observer, debugName)
            );
        }

        internal void UnobserveEntitiesAddedEvent(GroupIndex group, EntitiesAddedObserver observer)
        {
            if (_reactiveOnAddedObservers.TryGetValue(group, out var list))
            {
                bool wasRemoved = RemoveByObserver(list, observer);
                TrecsAssert.That(wasRemoved);
            }
        }

        internal void ObserveEntitiesMovedEvent(
            GroupIndex group,
            EntitiesMovedObserver observer,
            int priority = 0,
            string debugName = null
        )
        {
            if (!_reactiveOnMovedObservers.TryGetValue(group, out var list))
            {
                list = new FastList<PrioritizedObserver<EntitiesMovedObserver>>();
                _reactiveOnMovedObservers.Add(group, list);
            }

            InsertSorted(
                list,
                new PrioritizedObserver<EntitiesMovedObserver>(priority, observer, debugName)
            );
        }

        internal void UnobserveEntitiesMovedEvent(GroupIndex group, EntitiesMovedObserver observer)
        {
            if (_reactiveOnMovedObservers.TryGetValue(group, out var list))
            {
                bool wasRemoved = RemoveByObserver(list, observer);
                TrecsAssert.That(wasRemoved);
            }
        }

        internal void ObserveEntitiesRemovedEvent(
            GroupIndex group,
            EntitiesRemovedObserver observer,
            int priority = 0,
            string debugName = null
        )
        {
            if (!_reactiveOnRemovedObservers.TryGetValue(group, out var list))
            {
                list = new FastList<PrioritizedObserver<EntitiesRemovedObserver>>();
                _reactiveOnRemovedObservers.Add(group, list);

                _log.Trace("Added to observe removes for group {0}", group);
            }

            InsertSorted(
                list,
                new PrioritizedObserver<EntitiesRemovedObserver>(priority, observer, debugName)
            );
        }

        internal void UnobserveEntitiesRemovedEvent(
            GroupIndex group,
            EntitiesRemovedObserver observer
        )
        {
            if (_reactiveOnRemovedObservers.TryGetValue(group, out var list))
            {
                bool wasRemoved = RemoveByObserver(list, observer);
                TrecsAssert.That(wasRemoved);
            }
        }

        static void InsertSorted<T>(
            FastList<PrioritizedObserver<T>> list,
            PrioritizedObserver<T> item
        )
        {
            for (int i = 0; i < list.Count; i++)
            {
                if (list[i].Priority > item.Priority)
                {
                    list.InsertAt(i, item);
                    return;
                }
            }
            list.Add(item);
        }

        static bool RemoveByObserver<T>(FastList<PrioritizedObserver<T>> list, T observer)
            where T : Delegate
        {
            for (int i = 0; i < list.Count; i++)
            {
                if (list[i].Observer == observer)
                {
                    list.RemoveAt(i);
                    return true;
                }
            }
            return false;
        }

        public EntityEventsBuilder Events(WorldInfo worldInfo, WorldAccessor world)
        {
            return new EntityEventsBuilder(this, worldInfo, world);
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
                        "DeserializeStarted observers not cleaned up (count: {0})",
                        _deserializeStartedEvent.NumObservers
                    );
                if (_deserializeCompletedEvent.NumObservers > 0)
                    _log.Warning(
                        "DeserializeCompleted observers not cleaned up (count: {0})",
                        _deserializeCompletedEvent.NumObservers
                    );
                if (_submissionStartedEvent.NumObservers > 0)
                    _log.Warning(
                        "SubmissionStarted observers not cleaned up (count: {0})",
                        _submissionStartedEvent.NumObservers
                    );
                if (_submissionCompletedEvent.NumObservers > 0)
                    _log.Warning(
                        "SubmissionCompleted observers not cleaned up (count: {0})",
                        _submissionCompletedEvent.NumObservers
                    );
                if (_fixedUpdateStartedEvent.NumObservers > 0)
                    _log.Warning(
                        "FixedUpdateStarted observers not cleaned up (count: {0})",
                        _fixedUpdateStartedEvent.NumObservers
                    );
                if (_fixedUpdateCompletedEvent.NumObservers > 0)
                    _log.Warning(
                        "FixedUpdateCompleted observers not cleaned up (count: {0})",
                        _fixedUpdateCompletedEvent.NumObservers
                    );
                if (_variableUpdateStartedEvent.NumObservers > 0)
                    _log.Warning(
                        "VariableUpdateStarted observers not cleaned up (count: {0})",
                        _variableUpdateStartedEvent.NumObservers
                    );
                if (_variableUpdateCompletedEvent.NumObservers > 0)
                    _log.Warning(
                        "VariableUpdateCompleted observers not cleaned up (count: {0})",
                        _variableUpdateCompletedEvent.NumObservers
                    );
                if (_inputsAppliedEvent.NumObservers > 0)
                    _log.Warning(
                        "InputsApplied observers not cleaned up (count: {0})",
                        _inputsAppliedEvent.NumObservers
                    );
                foreach (var (group, observers) in _reactiveOnAddedObservers)
                    if (observers.Count > 0)
                        _log.Warning(
                            "Entity added observers not cleaned up by accessors {0} for group {1} (count: {2})",
                            GetDebugNames(observers),
                            group,
                            observers.Count
                        );
                foreach (var (group, observers) in _reactiveOnRemovedObservers)
                    if (observers.Count > 0)
                        _log.Warning(
                            "Entity removed observers not cleaned up by accessors {0} for group {1} (count: {2})",
                            GetDebugNames(observers),
                            group,
                            observers.Count
                        );
                foreach (var (group, observers) in _reactiveOnMovedObservers)
                    if (observers.Count > 0)
                        _log.Warning(
                            "Entity moved observers not cleaned up by accessors {0} for group {1} (count: {2})",
                            GetDebugNames(observers),
                            group,
                            observers.Count
                        );
            }

            _reactiveOnAddedObservers.Clear();
            _reactiveOnMovedObservers.Clear();
            _reactiveOnRemovedObservers.Clear();
        }

        static string GetDebugNames<T>(FastList<PrioritizedObserver<T>> observers)
        {
            var names = new HashSet<string>();
            for (int i = 0; i < observers.Count; i++)
            {
                names.Add(observers[i].DebugName ?? "<unknown>");
            }
            return string.Join(", ", names);
        }
    }
}
