using System.Linq;
using Trecs.Internal;
using Unity.Collections;

namespace Trecs
{
    internal class EcsStructuralOps
    {
        static readonly TrecsLog _log = new(nameof(EcsStructuralOps));

        readonly EntitySubmitter _entitySubmitter;
        readonly WorldInfo _worldInfo;
        readonly EntityQuerier.TrecsSets _sets;
        readonly SetStore _setStore;

        bool _isDisposed;

        public EcsStructuralOps(
            EntitySubmitter entitySubmitter,
            WorldInfo worldInfo,
            EntityQuerier.TrecsSets sets,
            SetStore setStore
        )
        {
            _entitySubmitter = entitySubmitter;
            _worldInfo = worldInfo;
            _sets = sets;
            _setStore = setStore;
        }

        internal void MarkDisposed()
        {
            _isDisposed = true;
        }

        internal EntityInitializer AddEntity(
            Group group,
            string callerFile = "",
            int callerLine = 0
        )
        {
            Assert.That(!_isDisposed);

            var resolvedTemplate = _worldInfo.GetResolvedTemplateForGroup(group);

            if (_log.IsTraceEnabled())
            {
                _log.Trace(
                    "Constructing entity with components {}",
                    resolvedTemplate
                        .ComponentDeclarations.Select(c => c.ComponentType.ToString())
                        .Join(", ")
                );
            }

            _log.Trace("Building new entity of type {}", resolvedTemplate);

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

        internal void MoveTo(EntityIndex fromEntityIndex, Group toGroupId)
        {
            Assert.That(!_isDisposed);

            // Remove supersedes swap - skip if entity is already scheduled for removal
            if (_entitySubmitter.IsScheduledForRemove(fromEntityIndex))
                return;

            var fromTemplate = _worldInfo.GetResolvedTemplateForGroup(fromEntityIndex.Group);
            var toTemplate = _worldInfo.GetResolvedTemplateForGroup(toGroupId);
            Assert.That(fromTemplate == toTemplate);

            _log.Trace(
                "Moving entity {} (group {}) to group {}",
                fromEntityIndex.Index,
                fromEntityIndex.Group,
                toGroupId
            );

            Assert.That(!fromEntityIndex.Group.IsNull);
            Assert.That(!toGroupId.IsNull);

            Assert.That(fromEntityIndex != EntityIndex.Null);

            _entitySubmitter.CheckMoveEntityHandle(
                fromEntityIndex,
                toGroupId,
                toTemplate.DebugName
            );
            _entitySubmitter.QueueMoveEntityOperation(
                fromEntityIndex,
                toGroupId,
                toTemplate.ComponentBuilders
            );
        }

        internal bool IsScheduledForRemove(EntityIndex entityIndex)
        {
            Assert.That(!_isDisposed);
            return _entitySubmitter.IsScheduledForRemove(entityIndex);
        }

        internal bool IsScheduledForMove(EntityIndex entityIndex)
        {
            Assert.That(!_isDisposed);
            return _entitySubmitter.IsScheduledForMove(entityIndex);
        }

        internal void RemoveEntity(EntityIndex entityIndex)
        {
            Assert.That(!_isDisposed);
            Assert.That(!entityIndex.IsNull);
            Assert.That(!entityIndex.Group.IsNull);

            var template = _worldInfo.GetResolvedTemplateForGroup(entityIndex.Group);

            _entitySubmitter.CheckRemoveEntityHandle(entityIndex, template.DebugName);
            _entitySubmitter.QueueRemoveEntityOperation(entityIndex, template.ComponentBuilders);
        }

        internal void RemoveAllEntitiesInGroup(Group group, int entityCount)
        {
            Assert.That(!_isDisposed);
            Assert.That(!group.IsNull);

            _entitySubmitter.QueueRemoveAllInGroup(group, entityCount);
        }

        internal ref EntitySet GetSet(SetDef setDef)
        {
            return ref GetSet(setDef.Id);
        }

        internal ref EntitySet GetSet(SetId setId)
        {
            Assert.That(!_isDisposed);

            var sets = _sets.EntitySets;

            Assert.That(
                sets.TryGetIndex(setId, out var index),
                "Set with ID '{}' not registered. Add it to the WorldBuilder via AddSet<T>().",
                setId
            );

            return ref sets.GetValueAtIndexByRef(index);
        }

        internal ref NativeSetDeferredQueues GetDeferredQueues(SetId setId)
        {
            return ref _setStore.GetDeferredQueues(setId);
        }

        internal NativeWorldAccessor GetNativeWorldAccessor(
            int accessorId,
            bool canMakeStructuralChanges,
            float deltaTime,
            float elapsedTime
        )
        {
            Assert.That(!_isDisposed);
            return _entitySubmitter.ProvideNativeWorldAccessor(
                accessorId,
                canMakeStructuralChanges,
                deltaTime,
                elapsedTime
            );
        }

        internal NativeArray<EntityHandle> BatchClaimIds(int count, Allocator allocator)
        {
            Assert.That(!_isDisposed);
            return _entitySubmitter.BatchClaimIds(count, allocator);
        }
    }
}
