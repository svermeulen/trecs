using System;
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

        public EntityEventsBuilder(
            EventsManager eventsManager,
            WorldInfo worldInfo,
            WorldAccessor world
        )
        {
            _eventsManager = eventsManager;
            _worldInfo = worldInfo;
            _world = world;
        }

        public EntityEventsSubscription InGroups(ReadOnlyFastList<GroupIndex> groups)
        {
            return new EntityEventsSubscription(_eventsManager, _world, groups);
        }

        public EntityEventsSubscription InGroup(GroupIndex group)
        {
            return InGroups(new FastList<GroupIndex>(group));
        }

        public EntityEventsSubscription EntitiesWithTags(TagSet tagSet)
        {
            return InGroups(_worldInfo.GetGroupsWithTags(tagSet));
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
            return InGroups(_worldInfo.GetGroupsWithComponents<T>());
        }

        public EntityEventsSubscription EntitiesWithComponents<T1, T2>()
            where T1 : unmanaged, IEntityComponent
            where T2 : unmanaged, IEntityComponent
        {
            return InGroups(_worldInfo.GetGroupsWithComponents<T1, T2>());
        }

        public EntityEventsSubscription EntitiesWithComponents<T1, T2, T3>()
            where T1 : unmanaged, IEntityComponent
            where T2 : unmanaged, IEntityComponent
            where T3 : unmanaged, IEntityComponent
        {
            return InGroups(_worldInfo.GetGroupsWithComponents<T1, T2, T3>());
        }

        public EntityEventsSubscription EntitiesWithComponents<T1, T2, T3, T4>()
            where T1 : unmanaged, IEntityComponent
            where T2 : unmanaged, IEntityComponent
            where T3 : unmanaged, IEntityComponent
            where T4 : unmanaged, IEntityComponent
        {
            return InGroups(_worldInfo.GetGroupsWithComponents<T1, T2, T3, T4>());
        }

        public EntityEventsSubscription EntitiesWithTagsAndComponents<T>(TagSet tagSet)
            where T : unmanaged, IEntityComponent
        {
            return InGroups(_worldInfo.GetGroupsWithTagsAndComponents<T>(tagSet));
        }

        public EntityEventsSubscription EntitiesWithTagsAndComponents<T1, T2>(TagSet tagSet)
            where T1 : unmanaged, IEntityComponent
            where T2 : unmanaged, IEntityComponent
        {
            return InGroups(_worldInfo.GetGroupsWithTagsAndComponents<T1, T2>(tagSet));
        }

        public EntityEventsSubscription EntitiesWithTagsAndComponents<T1, T2, T3>(TagSet tagSet)
            where T1 : unmanaged, IEntityComponent
            where T2 : unmanaged, IEntityComponent
            where T3 : unmanaged, IEntityComponent
        {
            return InGroups(_worldInfo.GetGroupsWithTagsAndComponents<T1, T2, T3>(tagSet));
        }

        public EntityEventsSubscription EntitiesWithTagsAndComponents<T1, T2, T3, T4>(TagSet tagSet)
            where T1 : unmanaged, IEntityComponent
            where T2 : unmanaged, IEntityComponent
            where T3 : unmanaged, IEntityComponent
            where T4 : unmanaged, IEntityComponent
        {
            return InGroups(_worldInfo.GetGroupsWithTagsAndComponents<T1, T2, T3, T4>(tagSet));
        }

        public EntityEventsSubscription AllEntities()
        {
            return InGroups(_worldInfo.AllGroups);
        }

        public IDisposable OnDeserializeStarted(Action cb) =>
            _eventsManager.DeserializeStartedEvent.Subscribe(cb);

        public IDisposable OnDeserializeStarted(Action cb, int priority) =>
            _eventsManager.DeserializeStartedEvent.Subscribe(cb, priority);

        public IDisposable OnDeserializeCompleted(Action cb) =>
            _eventsManager.DeserializeCompletedEvent.Subscribe(cb);

        public IDisposable OnDeserializeCompleted(Action cb, int priority) =>
            _eventsManager.DeserializeCompletedEvent.Subscribe(cb, priority);

        public IDisposable OnSubmissionStarted(Action cb) =>
            _eventsManager.SubmissionStartedEvent.Subscribe(cb);

        public IDisposable OnSubmissionStarted(Action cb, int priority) =>
            _eventsManager.SubmissionStartedEvent.Subscribe(cb, priority);

        public IDisposable OnSubmissionCompleted(Action cb) =>
            _eventsManager.SubmissionCompletedEvent.Subscribe(cb);

        public IDisposable OnSubmissionCompleted(Action cb, int priority) =>
            _eventsManager.SubmissionCompletedEvent.Subscribe(cb, priority);

        public IDisposable OnFixedUpdateStarted(Action cb) =>
            _eventsManager.FixedUpdateStartedEvent.Subscribe(cb);

        public IDisposable OnFixedUpdateStarted(Action cb, int priority) =>
            _eventsManager.FixedUpdateStartedEvent.Subscribe(cb, priority);

        public IDisposable OnFixedUpdateCompleted(Action cb) =>
            _eventsManager.FixedUpdateCompletedEvent.Subscribe(cb);

        public IDisposable OnFixedUpdateCompleted(Action cb, int priority) =>
            _eventsManager.FixedUpdateCompletedEvent.Subscribe(cb, priority);

        public IDisposable OnVariableUpdateStarted(Action cb) =>
            _eventsManager.VariableUpdateStartedEvent.Subscribe(cb);

        public IDisposable OnVariableUpdateStarted(Action cb, int priority) =>
            _eventsManager.VariableUpdateStartedEvent.Subscribe(cb, priority);

        public IDisposable OnVariableUpdateCompleted(Action cb) =>
            _eventsManager.VariableUpdateCompletedEvent.Subscribe(cb);

        public IDisposable OnVariableUpdateCompleted(Action cb, int priority) =>
            _eventsManager.VariableUpdateCompletedEvent.Subscribe(cb, priority);

        public IDisposable OnInputsApplied(Action cb) =>
            _eventsManager.InputsAppliedEvent.Subscribe(cb);

        public IDisposable OnInputsApplied(Action cb, int priority) =>
            _eventsManager.InputsAppliedEvent.Subscribe(cb, priority);
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
        readonly ReadOnlyFastList<GroupIndex> _groups;
        readonly string _debugName;

        EntitiesAddedObserver _addedObserver;
        EntitiesRemovedObserver _removedObserver;
        EntitiesMovedObserver _movedObserver;

        int _priority;
        bool _isDisposed;

        internal EntityEventsSubscription(
            EventsManager eventsManager,
            WorldAccessor world,
            ReadOnlyFastList<GroupIndex> groups
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
                Assert.That(
                    !template.VariableUpdateOnly,
                    "Entity-event subscription from Fixed-role accessor {} resolved to [VariableUpdateOnly] template {} (group {}). VUO templates are render-cadence — only Variable-role / input-system / Unrestricted-role accessors drive structural changes there, so a Fixed-role observer would either never fire or leak render-rate state into the simulation. Narrow the predicate (e.g. add a WithoutTags constraint) or move the subscription to a Variable-role or input-system service.",
                    _debugName,
                    template.DebugName,
                    group
                );
            }
        }

        public EntityEventsSubscription WithPriority(int priority)
        {
            Assert.That(
                _addedObserver == null && _removedObserver == null && _movedObserver == null,
                "WithPriority must be called before OnAdded/OnRemoved/OnMoved"
            );
            _priority = priority;
            return this;
        }

        public EntityEventsSubscription OnAdded(EntitiesAddedObserver observer)
        {
            Assert.That(_addedObserver == null, "OnAdded already subscribed");
            _addedObserver = observer;

            foreach (var group in _groups)
            {
                _eventsManager.ObserveEntitiesAddedEvent(group, observer, _priority, _debugName);
            }

            return this;
        }

        public EntityEventsSubscription OnRemoved(EntitiesRemovedObserver observer)
        {
            Assert.That(_removedObserver == null, "OnRemoved already subscribed");
            _removedObserver = observer;

            foreach (var group in _groups)
            {
                _eventsManager.ObserveEntitiesRemovedEvent(group, observer, _priority, _debugName);
            }

            return this;
        }

        public EntityEventsSubscription OnMoved(EntitiesMovedObserver observer)
        {
            Assert.That(_movedObserver == null, "OnMoved already subscribed");
            _movedObserver = observer;

            foreach (var group in _groups)
            {
                _eventsManager.ObserveEntitiesMovedEvent(group, observer, _priority, _debugName);
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

            if (_addedObserver != null)
            {
                foreach (var group in _groups)
                {
                    _eventsManager.UnobserveEntitiesAddedEvent(group, _addedObserver);
                }
                _addedObserver = null;
            }

            if (_removedObserver != null)
            {
                foreach (var group in _groups)
                {
                    _eventsManager.UnobserveEntitiesRemovedEvent(group, _removedObserver);
                }
                _removedObserver = null;
            }

            if (_movedObserver != null)
            {
                foreach (var group in _groups)
                {
                    _eventsManager.UnobserveEntitiesMovedEvent(group, _movedObserver);
                }
                _movedObserver = null;
            }
        }
    }
}
