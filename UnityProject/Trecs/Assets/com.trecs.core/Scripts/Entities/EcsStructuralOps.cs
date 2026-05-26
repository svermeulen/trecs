using System.Linq;
using Trecs.Internal;
using Unity.Collections;

namespace Trecs
{
    internal class EcsStructuralOps
    {
        readonly TrecsLog _log;

        readonly EntitySubmitter _entitySubmitter;
        readonly WorldInfo _worldInfo;
        readonly EntityQuerier.TrecsSets _sets;
        readonly SetStore _setStore;

        bool _isDisposed;

        public EcsStructuralOps(
            TrecsLog log,
            EntitySubmitter entitySubmitter,
            WorldInfo worldInfo,
            EntityQuerier.TrecsSets sets,
            SetStore setStore
        )
        {
            _log = log;
            _entitySubmitter = entitySubmitter;
            _worldInfo = worldInfo;
            _sets = sets;
            _setStore = setStore;
        }

        internal void MarkDisposed()
        {
            _isDisposed = true;
        }

        internal void WarmupGroup(
            GroupIndex group,
            int initialCapacity,
            IComponentBuilder[] componentBuilders
        )
        {
            TrecsDebugAssert.That(!_isDisposed);
            _entitySubmitter.Preallocate(group, initialCapacity, componentBuilders);
        }

        internal EntityInitializer AddEntity(
            GroupIndex group,
            string callerFile = "",
            int callerLine = 0
        )
        {
            TrecsDebugAssert.That(!_isDisposed);

            var resolvedTemplate = _worldInfo.GetResolvedTemplateForGroup(group);

            if (_log.IsTraceEnabled())
            {
                _log.Trace(
                    "Constructing entity with components {0}",
                    resolvedTemplate
                        .ComponentDeclarations.Select(c => c.ComponentType.ToString())
                        .Join(", ")
                );
            }

            _log.Trace("Building new entity of type {0}", resolvedTemplate);

            using (TrecsProfiling.Start("EntitySubmitter.AddEntity"))
            {
                return _entitySubmitter.AddEntity(
                    group,
                    resolvedTemplate.ComponentBuilders,
                    resolvedTemplate.DebugName,
                    callerFile,
                    callerLine
                );
            }
        }

        internal void QueueSetTag(int accessorId, EntityIndex from, Tag tag)
        {
            TrecsDebugAssert.That(!_isDisposed);
            _entitySubmitter.QueueManagedSetTag(accessorId, from, tag);
        }

        internal void QueueUnsetTag(int accessorId, EntityIndex from, Tag tag)
        {
            TrecsDebugAssert.That(!_isDisposed);
            _entitySubmitter.QueueManagedUnsetTag(accessorId, from, tag);
        }

        internal bool IsScheduledForRemove(EntityIndex entityIndex)
        {
            TrecsDebugAssert.That(!_isDisposed);
            return _entitySubmitter.IsScheduledForRemove(entityIndex);
        }

        internal bool IsScheduledForMove(EntityIndex entityIndex)
        {
            TrecsDebugAssert.That(!_isDisposed);
            return _entitySubmitter.IsScheduledForMove(entityIndex);
        }

        internal void RemoveEntity(EntityIndex entityIndex)
        {
            TrecsDebugAssert.That(!_isDisposed);
            TrecsDebugAssert.That(!entityIndex.IsNull);

            var template = _worldInfo.GetResolvedTemplateForGroup(entityIndex.GroupIndex);

            _entitySubmitter.CheckRemoveEntityHandle(entityIndex, template.DebugName);
            _entitySubmitter.QueueRemoveEntityOperation(entityIndex, template.ComponentBuilders);
        }

        internal void RemoveAllEntitiesInGroup(GroupIndex group, int entityCount)
        {
            TrecsDebugAssert.That(!_isDisposed);

            _entitySubmitter.QueueRemoveAllInGroup(group, entityCount);
        }

        internal EntitySetStorage GetSet(EntitySet entitySet)
        {
            return GetSet(entitySet.Id);
        }

        internal EntitySetStorage GetSet(SetId setId)
        {
            TrecsDebugAssert.That(!_isDisposed);

            var sets = _sets.EntitySets;

            var found = sets.TryGetValue(setId, out var result);
            TrecsDebugAssert.That(
                found,
                "Set with ID '{0}' not registered. Add it to the WorldBuilder via AddSet<T>().",
                setId
            );

            return result;
        }

        internal NativeSetDeferredQueues GetDeferredQueues(SetId setId)
        {
            return _setStore.GetDeferredQueues(setId);
        }

        internal NativeWorldAccessor GetNativeWorldAccessor(
            int accessorId,
            bool canMutateSimulation,
            float deltaTime,
            float elapsedTime
        )
        {
            TrecsDebugAssert.That(!_isDisposed);
            return _entitySubmitter.ProvideNativeWorldAccessor(
                accessorId,
                canMutateSimulation,
                deltaTime,
                elapsedTime
            );
        }

        internal NativeArray<EntityHandle> BatchClaimIds(int count, Allocator allocator)
        {
            TrecsDebugAssert.That(!_isDisposed);
            return _entitySubmitter.BatchClaimIds(count, allocator);
        }
    }
}
