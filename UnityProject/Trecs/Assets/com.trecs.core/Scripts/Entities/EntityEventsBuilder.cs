using System;
using System.Collections.Generic;
using System.ComponentModel;
using System.Diagnostics;
using Trecs.Collections;
using Trecs.Internal;

namespace Trecs.Internal
{
    [EditorBrowsable(EditorBrowsableState.Never)]
    public delegate void EntitiesAddedObserver(GroupIndex group, EntityRange indices);

    [EditorBrowsable(EditorBrowsableState.Never)]
    public delegate void EntitiesRemovedObserver(GroupIndex group, EntityRange indices);

    [EditorBrowsable(EditorBrowsableState.Never)]
    public delegate void EntitiesMovedObserver(
        GroupIndex fromGroup,
        GroupIndex toGroup,
        EntityRange indices
    );
}

namespace Trecs
{
    /// <summary>
    /// Fluent builder for subscribing to entity lifecycle and world events. Accessed via
    /// <see cref="WorldAccessor.Events"/>. First select target entities (e.g.
    /// <see cref="EntitiesWithTags{T1}()"/>, <see cref="AllEntities()"/>), then chain
    /// <see cref="EntityEventsSubscription.OnAdded"/>, <see cref="EntityEventsSubscription.OnRemoved"/>,
    /// or <see cref="EntityEventsSubscription.OnMoved"/> on the returned subscription.
    /// Dispose the subscription to unsubscribe.
    /// </summary>
    public readonly ref struct EntityEventsBuilder
    {
        readonly EventsManager _eventsManager;
        readonly WorldInfo _worldInfo;
        readonly WorldAccessor _world;
        readonly SystemRunner _systemRunner;

        public EntityEventsBuilder(
            EventsManager eventsManager,
            WorldInfo worldInfo,
            WorldAccessor world,
            SystemRunner systemRunner
        )
        {
            _eventsManager = eventsManager;
            _worldInfo = worldInfo;
            _world = world;
            _systemRunner = systemRunner;
        }

        public EntityEventsSubscription EntitiesInGroups(ReadOnlyList<GroupIndex> groups)
        {
            return new EntityEventsSubscription(_eventsManager, _world, groups);
        }

        public EntityEventsSubscription EntitiesInGroup(GroupIndex group)
        {
            return EntitiesInGroups(new List<GroupIndex> { group });
        }

        public EntityEventsSubscription EntitiesWithTags(TagSet tagSet)
        {
            return EntitiesInGroups(_worldInfo.GetGroupsWithTags(tagSet));
        }

        public EntityEventsSubscription EntitiesWithTags<T1>()
            where T1 : struct, ITag => EntitiesWithTags(TagSet<T1>.Value);

        public EntityEventsSubscription EntitiesWithTags<T1, T2>()
            where T1 : struct, ITag
            where T2 : struct, ITag => EntitiesWithTags(TagSet<T1, T2>.Value);

        public EntityEventsSubscription EntitiesWithTags<T1, T2, T3>()
            where T1 : struct, ITag
            where T2 : struct, ITag
            where T3 : struct, ITag => EntitiesWithTags(TagSet<T1, T2, T3>.Value);

        public EntityEventsSubscription EntitiesWithTags<T1, T2, T3, T4>()
            where T1 : struct, ITag
            where T2 : struct, ITag
            where T3 : struct, ITag
            where T4 : struct, ITag => EntitiesWithTags(TagSet<T1, T2, T3, T4>.Value);

        public EntityEventsSubscription EntitiesWithComponents<T>()
            where T : unmanaged, IEntityComponent
        {
            return EntitiesInGroups(_worldInfo.GetGroupsWithComponents<T>());
        }

        public EntityEventsSubscription EntitiesWithComponents<T1, T2>()
            where T1 : unmanaged, IEntityComponent
            where T2 : unmanaged, IEntityComponent
        {
            return EntitiesInGroups(_worldInfo.GetGroupsWithComponents<T1, T2>());
        }

        public EntityEventsSubscription EntitiesWithComponents<T1, T2, T3>()
            where T1 : unmanaged, IEntityComponent
            where T2 : unmanaged, IEntityComponent
            where T3 : unmanaged, IEntityComponent
        {
            return EntitiesInGroups(_worldInfo.GetGroupsWithComponents<T1, T2, T3>());
        }

        public EntityEventsSubscription EntitiesWithComponents<T1, T2, T3, T4>()
            where T1 : unmanaged, IEntityComponent
            where T2 : unmanaged, IEntityComponent
            where T3 : unmanaged, IEntityComponent
            where T4 : unmanaged, IEntityComponent
        {
            return EntitiesInGroups(_worldInfo.GetGroupsWithComponents<T1, T2, T3, T4>());
        }

        public EntityEventsSubscription EntitiesWithTagsAndComponents<T>(TagSet tagSet)
            where T : unmanaged, IEntityComponent
        {
            return EntitiesInGroups(_worldInfo.GetGroupsWithTagsAndComponents<T>(tagSet));
        }

        public EntityEventsSubscription EntitiesWithTagsAndComponents<T1, T2>(TagSet tagSet)
            where T1 : unmanaged, IEntityComponent
            where T2 : unmanaged, IEntityComponent
        {
            return EntitiesInGroups(_worldInfo.GetGroupsWithTagsAndComponents<T1, T2>(tagSet));
        }

        public EntityEventsSubscription EntitiesWithTagsAndComponents<T1, T2, T3>(TagSet tagSet)
            where T1 : unmanaged, IEntityComponent
            where T2 : unmanaged, IEntityComponent
            where T3 : unmanaged, IEntityComponent
        {
            return EntitiesInGroups(_worldInfo.GetGroupsWithTagsAndComponents<T1, T2, T3>(tagSet));
        }

        public EntityEventsSubscription EntitiesWithTagsAndComponents<T1, T2, T3, T4>(TagSet tagSet)
            where T1 : unmanaged, IEntityComponent
            where T2 : unmanaged, IEntityComponent
            where T3 : unmanaged, IEntityComponent
            where T4 : unmanaged, IEntityComponent
        {
            return EntitiesInGroups(
                _worldInfo.GetGroupsWithTagsAndComponents<T1, T2, T3, T4>(tagSet)
            );
        }

        public EntityEventsSubscription AllEntities()
        {
            return EntitiesInGroups(_worldInfo.AllGroups);
        }

        public IDisposable OnDeserializeStarted(Action cb) =>
            _eventsManager.DeserializeStartedEvent.Subscribe(cb, 0, _world.DebugName);

        public IDisposable OnDeserializeStarted(Action cb, int priority) =>
            _eventsManager.DeserializeStartedEvent.Subscribe(cb, priority, _world.DebugName);

        public IDisposable OnDeserializeCompleted(Action cb) =>
            _eventsManager.DeserializeCompletedEvent.Subscribe(cb, 0, _world.DebugName);

        public IDisposable OnDeserializeCompleted(Action cb, int priority) =>
            _eventsManager.DeserializeCompletedEvent.Subscribe(cb, priority, _world.DebugName);

        public IDisposable OnSubmissionStarted(Action cb) =>
            _eventsManager.SubmissionStartedEvent.Subscribe(cb, 0, _world.DebugName);

        public IDisposable OnSubmissionStarted(Action cb, int priority) =>
            _eventsManager.SubmissionStartedEvent.Subscribe(cb, priority, _world.DebugName);

        public IDisposable OnSubmissionCompleted(Action cb) =>
            _eventsManager.SubmissionCompletedEvent.Subscribe(cb, 0, _world.DebugName);

        public IDisposable OnSubmissionCompleted(Action cb, int priority) =>
            _eventsManager.SubmissionCompletedEvent.Subscribe(cb, priority, _world.DebugName);

        public IDisposable OnFixedUpdateStarted(Action cb) =>
            _eventsManager.FixedUpdateStartedEvent.Subscribe(cb, 0, _world.DebugName);

        public IDisposable OnFixedUpdateStarted(Action cb, int priority) =>
            _eventsManager.FixedUpdateStartedEvent.Subscribe(cb, priority, _world.DebugName);

        public IDisposable OnFixedUpdateCompleted(Action cb) =>
            _eventsManager.FixedUpdateCompletedEvent.Subscribe(cb, 0, _world.DebugName);

        public IDisposable OnFixedUpdateCompleted(Action cb, int priority) =>
            _eventsManager.FixedUpdateCompletedEvent.Subscribe(cb, priority, _world.DebugName);

        public IDisposable OnVariableUpdateStarted(Action cb) =>
            _eventsManager.VariableUpdateStartedEvent.Subscribe(cb, 0, _world.DebugName);

        public IDisposable OnVariableUpdateStarted(Action cb, int priority) =>
            _eventsManager.VariableUpdateStartedEvent.Subscribe(cb, priority, _world.DebugName);

        public IDisposable OnVariableUpdateCompleted(Action cb) =>
            _eventsManager.VariableUpdateCompletedEvent.Subscribe(cb, 0, _world.DebugName);

        public IDisposable OnVariableUpdateCompleted(Action cb, int priority) =>
            _eventsManager.VariableUpdateCompletedEvent.Subscribe(cb, priority, _world.DebugName);

        public IDisposable OnInputsApplied(Action cb) =>
            _eventsManager.InputsAppliedEvent.Subscribe(cb, 0, _world.DebugName);

        public IDisposable OnInputsApplied(Action cb, int priority) =>
            _eventsManager.InputsAppliedEvent.Subscribe(cb, priority, _world.DebugName);

        /// <summary>
        /// Fires when <see cref="WorldAccessor.FixedIsPaused"/> changes. The callback
        /// receives the new value (true when fixed-update has just been paused, false
        /// when it has just been unpaused).
        /// </summary>
        public IDisposable OnFixedPauseChanged(Action<bool> cb) =>
            _systemRunner.FixedIsPausedChangedEvent.Subscribe(cb);

        public IDisposable OnFixedPauseChanged(Action<bool> cb, int priority) =>
            _systemRunner.FixedIsPausedChangedEvent.Subscribe(cb, priority);

        /// <summary>
        /// Fires during <see cref="World.Dispose"/>, after <see cref="World.RemoveAllEntities"/>
        /// and system <c>OnShutdown</c> hooks have run but before infrastructure teardown.
        /// Use this to dispose event subscriptions from non-system code at the right point in
        /// the shutdown sequence.
        /// </summary>
        public IDisposable OnShutdown(Action cb) =>
            _eventsManager.ShutdownEvent.Subscribe(cb, 0, _world.DebugName);

        public IDisposable OnShutdown(Action cb, int priority) =>
            _eventsManager.ShutdownEvent.Subscribe(cb, priority, _world.DebugName);
    }

    /// <summary>
    /// An entity event subscription that can observe entity add, remove, and move events.
    /// Chain <see cref="OnAdded"/>, <see cref="OnRemoved"/>, and <see cref="OnMoved"/> to register
    /// callbacks. Dispose to unsubscribe all registered observers. Optionally call
    /// <see cref="WithPriority"/> before registering observers to control callback ordering.
    /// </summary>
    public sealed class EntityEventsSubscription : IDisposable
    {
        readonly EventsManager _eventsManager;
        readonly WorldAccessor _world;
        readonly ReadOnlyList<GroupIndex> _groups;
        readonly string _debugName;

        IDisposable[] _addedSubscriptions;
        IDisposable[] _removedSubscriptions;
        IDisposable[] _movedSubscriptions;

        int _priority;
        bool _isDisposed;

        internal EntityEventsSubscription(
            EventsManager eventsManager,
            WorldAccessor world,
            ReadOnlyList<GroupIndex> groups
        )
        {
            _eventsManager = eventsManager;
            _world = world;
            _groups = groups;
            _debugName = world?.DebugName;

            AssertNoVariableUpdateOnlyGroupsForFixedRole();
        }

        // A Fixed-role accessor that subscribes to entity lifecycle events
        // on a [VariableUpdateOnly] template's group is registering for
        // structural-change callbacks that, by the VUO rules, can only be
        // *driven* by Variable-role / input-system / Unrestricted-role accessors.
        // The callback would either never fire (defeating the subscription)
        // or fire with the Fixed accessor mid-flight on render-cadence state
        // (leaking non-determinism into the simulation). Reject at
        // registration rather than at first callback fire — same shape as
        // QueryBuilder.AssertNoVariableUpdateOnlyGroupsForFixedRole.
        [Conditional("DEBUG")]
        void AssertNoVariableUpdateOnlyGroupsForFixedRole()
        {
            if (_world == null || _world.Role != AccessorRole.Fixed)
            {
                return;
            }

            var worldInfo = _world.WorldInfo;

            for (int i = 0; i < _groups.Count; i++)
            {
                var group = _groups[i];
                var template = worldInfo.GetResolvedTemplateForGroup(group);
                TrecsDebugAssert.That(
                    !template.VariableUpdateOnly,
                    "Entity-event subscription from Fixed-role accessor {0} resolved to [VariableUpdateOnly] template {1} (group {2}). VUO templates are render-cadence — only Variable-role / input-system / Unrestricted-role accessors drive structural changes there, so a Fixed-role observer would either never fire or leak render-rate state into the simulation. Narrow the predicate (e.g. add a WithoutTags constraint) or move the subscription to a Variable-role or input-system service.",
                    _debugName,
                    template.DebugName,
                    group
                );
            }
        }

        public EntityEventsSubscription WithPriority(int priority)
        {
            TrecsDebugAssert.That(
                _addedSubscriptions == null
                    && _removedSubscriptions == null
                    && _movedSubscriptions == null,
                "WithPriority must be called before OnAdded/OnRemoved/OnMoved"
            );
            _priority = priority;
            return this;
        }

        public EntityEventsSubscription OnAdded(EntitiesAddedObserver observer)
        {
            TrecsDebugAssert.That(_addedSubscriptions == null, "OnAdded already subscribed");
            _addedSubscriptions = new IDisposable[_groups.Count];

            for (int i = 0; i < _groups.Count; i++)
            {
                _addedSubscriptions[i] = _eventsManager.ObserveEntitiesAddedEvent(
                    _groups[i],
                    observer,
                    _priority,
                    _debugName
                );
            }

            return this;
        }

        public EntityEventsSubscription OnRemoved(EntitiesRemovedObserver observer)
        {
            TrecsDebugAssert.That(_removedSubscriptions == null, "OnRemoved already subscribed");
            _removedSubscriptions = new IDisposable[_groups.Count];

            for (int i = 0; i < _groups.Count; i++)
            {
                _removedSubscriptions[i] = _eventsManager.ObserveEntitiesRemovedEvent(
                    _groups[i],
                    observer,
                    _priority,
                    _debugName
                );
            }

            return this;
        }

        public EntityEventsSubscription OnMoved(EntitiesMovedObserver observer)
        {
            TrecsDebugAssert.That(_movedSubscriptions == null, "OnMoved already subscribed");
            _movedSubscriptions = new IDisposable[_groups.Count];

            for (int i = 0; i < _groups.Count; i++)
            {
                _movedSubscriptions[i] = _eventsManager.ObserveEntitiesMovedEvent(
                    _groups[i],
                    observer,
                    _priority,
                    _debugName
                );
            }

            return this;
        }

        public EntityEventsSubscription OnAdded(
            Action<GroupIndex, EntityRange, WorldAccessor> observer
        )
        {
            return OnAdded((group, indices) => observer(group, indices, _world));
        }

        public EntityEventsSubscription OnRemoved(
            Action<GroupIndex, EntityRange, WorldAccessor> observer
        )
        {
            return OnRemoved((group, indices) => observer(group, indices, _world));
        }

        public EntityEventsSubscription OnMoved(
            Action<GroupIndex, EntityRange, WorldAccessor> observer
        )
        {
            return OnMoved((from, to, indices) => observer(to, indices, _world));
        }

        public void Dispose()
        {
            if (_isDisposed)
                return;

            _isDisposed = true;

            if (_addedSubscriptions != null)
            {
                for (int i = 0; i < _addedSubscriptions.Length; i++)
                    _addedSubscriptions[i].Dispose();
                _addedSubscriptions = null;
            }

            if (_removedSubscriptions != null)
            {
                for (int i = 0; i < _removedSubscriptions.Length; i++)
                    _removedSubscriptions[i].Dispose();
                _removedSubscriptions = null;
            }

            if (_movedSubscriptions != null)
            {
                for (int i = 0; i < _movedSubscriptions.Length; i++)
                    _movedSubscriptions[i].Dispose();
                _movedSubscriptions = null;
            }
        }
    }
}
