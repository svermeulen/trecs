using System;
using System.ComponentModel;
using System.Runtime.CompilerServices;
using Trecs.Collections;

namespace Trecs.Internal
{
    [EditorBrowsable(EditorBrowsableState.Never)]
    public class ComponentStore : IDisposable
    {
        static readonly TrecsLog _log = new(nameof(ComponentStore));

        //one datastructure rule them all:
        //split by group
        //split by type per group. It's possible to get all the entities of a give type T per group thanks
        //to the DenseDictionary capabilities OR it's possible to get a specific entityComponent indexed by
        //ID. This ID doesn't need to be the EntityIndex, it can be just the entityHandle
        //for each group id, save a dictionary indexed by entity type of entities indexed by id
        //                                        group                  EntityComponentType     entityHandle, EntityComponent
        readonly DenseDictionary<
            Group,
            DenseDictionary<ComponentId, IComponentArray>
        > _groupEntityComponentsDB;

        //for each aspect type, return the groups (dictionary of entities indexed by entity id) where they are
        //found indexed by group id. ComponentArray are never created, they instead point to the ones hold
        //by _groupEntityComponentsDB
        //                        <EntityComponentType                            <groupId  <entityHandle, EntityComponent>>>
        readonly DenseDictionary<
            ComponentId,
            DenseDictionary<Group, IComponentArray>
        > _groupsPerEntity;

        bool _configurationFrozen;

        public ComponentStore()
        {
            _groupEntityComponentsDB =
                new DenseDictionary<Group, DenseDictionary<ComponentId, IComponentArray>>();
            _groupsPerEntity =
                new DenseDictionary<ComponentId, DenseDictionary<Group, IComponentArray>>();
        }

        public DenseDictionary<
            Group,
            DenseDictionary<ComponentId, IComponentArray>
        > GroupEntityComponentsDB => _groupEntityComponentsDB;

        public DenseDictionary<
            ComponentId,
            DenseDictionary<Group, IComponentArray>
        > GroupsPerComponent => _groupsPerEntity;

        public bool ConfigurationFrozen => _configurationFrozen;

        public void FreezeConfiguration()
        {
            _configurationFrozen = true;
        }

        [MethodImpl(MethodImplOptions.AggressiveInlining)]
        public DenseDictionary<ComponentId, IComponentArray> GetDBGroup(Group fromIdGroupId)
        {
            if (
                !_groupEntityComponentsDB.TryGetValue(
                    fromIdGroupId,
                    out DenseDictionary<ComponentId, IComponentArray> fromGroup
                )
            )
            {
                throw new TrecsException($"Group doesn't exist: {fromIdGroupId}");
            }

            return fromGroup;
        }

        [MethodImpl(MethodImplOptions.AggressiveInlining)]
        public DenseDictionary<ComponentId, IComponentArray> GetOrAddDBGroup(Group toGroupId)
        {
            if (!_groupEntityComponentsDB.TryGetValue(toGroupId, out var fromGroup))
            {
                if (_configurationFrozen)
                {
                    throw new TrecsException(
                        $"Attempted to add group {toGroupId} after group set has been frozen"
                    );
                }

                fromGroup = new DenseDictionary<ComponentId, IComponentArray>();
                _groupEntityComponentsDB.Add(toGroupId, fromGroup);
            }

            return fromGroup;
        }

        [MethodImpl(MethodImplOptions.AggressiveInlining)]
        public IComponentArray GetOrAddTypeSafeDictionary(
            Group groupId,
            DenseDictionary<ComponentId, IComponentArray> groupPerComponentType,
            ComponentId typeId,
            IComponentArray fromDictionary
        )
        {
            //be sure that the ComponentArray for the entity Type exists
            if (
                !groupPerComponentType.TryGetValue(typeId, out IComponentArray toEntitiesDictionary)
            )
            {
                Assert.That(
                    !_configurationFrozen,
                    "Attempted to add a new component dictionary {} after the configuration has been frozen",
                    typeId
                );

                toEntitiesDictionary = fromDictionary.Create();
                groupPerComponentType.Add(typeId, toEntitiesDictionary);
            }

            {
                //update GroupsPerEntity
                if (!_groupsPerEntity.TryGetValue(typeId, out var groupedGroup))
                {
                    Assert.That(
                        !_configurationFrozen,
                        "Attempted to add a new component dictionary {} after the configuration has been frozen",
                        typeId
                    );

                    groupedGroup = _groupsPerEntity[typeId] =
                        new DenseDictionary<Group, IComponentArray>();
                }

                groupedGroup[groupId] = toEntitiesDictionary;
            }

            return toEntitiesDictionary;
        }

        [MethodImpl(MethodImplOptions.AggressiveInlining)]
        public static IComponentArray GetTypeSafeDictionary(
            Group groupId,
            DenseDictionary<ComponentId, IComponentArray> @group,
            ComponentId refWrapper
        )
        {
            if (!@group.TryGetValue(refWrapper, out IComponentArray fromTypeSafeDictionary))
            {
                throw new TrecsException($"no group found: {groupId}");
            }

            return fromTypeSafeDictionary;
        }

        /// <summary>
        /// Preallocate the DB-side storage for a group with a given set of component builders.
        /// </summary>
        public void PreallocateDBGroup(
            Group groupId,
            int size,
            IComponentBuilder[] entityComponentsToBuild
        )
        {
            Assert.That(!_configurationFrozen);

            var numberOfEntityComponents = entityComponentsToBuild.Length;
            DenseDictionary<ComponentId, IComponentArray> group = GetOrAddDBGroup(groupId);
            group.EnsureCapacity((uint)numberOfEntityComponents);

            for (var index = 0; index < numberOfEntityComponents; index++)
            {
                var entityComponentBuilder = entityComponentsToBuild[index];
                var entityComponentType = entityComponentBuilder.ComponentId;

                var components = group.GetOrAdd(
                    entityComponentType,
                    () => entityComponentBuilder.CreateDictionary(size)
                );

                entityComponentBuilder.Preallocate(components, size);

                if (!_groupsPerEntity.TryGetValue(entityComponentType, out var groupedGroup))
                {
                    groupedGroup = _groupsPerEntity[entityComponentType] =
                        new DenseDictionary<Group, IComponentArray>();
                }

                if (groupedGroup.TryGetValue(groupId, out var existingComponents))
                {
                    Assert.That(existingComponents == components);
                }
                else
                {
                    groupedGroup.Add(groupId, components);
                }
            }

            _log.Trace(
                "Initialized group {} with {} component arrays",
                groupId,
                numberOfEntityComponents
            );
        }

        public void Dispose()
        {
            foreach (var groups in _groupEntityComponentsDB)
            {
                foreach (var entityList in groups.Value)
                {
                    entityList.Value.Dispose();
                }
            }

            _groupEntityComponentsDB.Clear();
            _groupsPerEntity.Clear();
        }
    }
}
