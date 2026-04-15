using Trecs.Internal;
using Unity.Jobs;

namespace Trecs
{
    /// <summary>
    /// Fluent builder returned by <see cref="WorldAccessor.TrackExternalJob"/> for declaring
    /// component dependencies of externally-scheduled jobs.
    /// </summary>
    public readonly struct ExternalJobTracker
    {
        readonly RuntimeJobScheduler _scheduler;
        readonly WorldInfo _worldInfo;
        readonly JobHandle _handle;

        internal ExternalJobTracker(
            RuntimeJobScheduler scheduler,
            WorldInfo worldInfo,
            JobHandle handle
        )
        {
            _scheduler = scheduler;
            _worldInfo = worldInfo;
            _handle = handle;
        }

        /// <summary>
        /// Declare that this external job writes to a component on entities matching the given tags.
        /// </summary>
        public ExternalJobTracker Writes<TComponent>(TagSet tags)
            where TComponent : unmanaged, IEntityComponent
        {
            var resourceId = ResourceId.Component(ComponentTypeId<TComponent>.Value);
            foreach (var group in _worldInfo.GetGroupsWithTags(tags))
            {
                _scheduler.TrackJobWrite(_handle, resourceId, group);
            }
            return this;
        }

        /// <summary>
        /// Declare that this external job writes to a component in a specific group.
        /// </summary>
        public ExternalJobTracker Writes<TComponent>(Group group)
            where TComponent : unmanaged, IEntityComponent
        {
            _scheduler.TrackJobWrite(
                _handle,
                ResourceId.Component(ComponentTypeId<TComponent>.Value),
                group
            );
            return this;
        }

        /// <summary>
        /// Declare that this external job writes to a global component.
        /// </summary>
        public ExternalJobTracker WritesGlobal<TComponent>()
            where TComponent : unmanaged, IEntityComponent
        {
            _scheduler.TrackJobWrite(
                _handle,
                ResourceId.Component(ComponentTypeId<TComponent>.Value),
                _worldInfo.GlobalGroup
            );
            return this;
        }

        /// <summary>
        /// Declare that this external job reads a component on entities matching the given tags.
        /// </summary>
        public ExternalJobTracker Reads<TComponent>(TagSet tags)
            where TComponent : unmanaged, IEntityComponent
        {
            var resourceId = ResourceId.Component(ComponentTypeId<TComponent>.Value);
            foreach (var group in _worldInfo.GetGroupsWithTags(tags))
            {
                _scheduler.TrackJobRead(_handle, resourceId, group);
            }
            return this;
        }

        /// <summary>
        /// Declare that this external job reads a component in a specific group.
        /// </summary>
        public ExternalJobTracker Reads<TComponent>(Group group)
            where TComponent : unmanaged, IEntityComponent
        {
            _scheduler.TrackJobRead(
                _handle,
                ResourceId.Component(ComponentTypeId<TComponent>.Value),
                group
            );
            return this;
        }

        /// <summary>
        /// Declare that this external job reads a global component.
        /// </summary>
        public ExternalJobTracker ReadsGlobal<TComponent>()
            where TComponent : unmanaged, IEntityComponent
        {
            _scheduler.TrackJobRead(
                _handle,
                ResourceId.Component(ComponentTypeId<TComponent>.Value),
                _worldInfo.GlobalGroup
            );
            return this;
        }
    }
}
