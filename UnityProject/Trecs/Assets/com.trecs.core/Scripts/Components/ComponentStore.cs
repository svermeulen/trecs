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

        // Per-group component arrays, indexed directly by GroupIndex.Index.
        // Pre-allocated at construction — every slot holds a (possibly empty)
        // DenseDictionary<ComponentId, IComponentArray>. A direct array index
        // replaces the old Dict<GroupIndex, ...> hash lookup on the hot path.
        readonly DenseDictionary<ComponentId, IComponentArray>[] _groupEntityComponentsDB;

        bool _configurationFrozen;

        public ComponentStore(int groupCount)
        {
            _groupEntityComponentsDB = new DenseDictionary<ComponentId, IComponentArray>[
                groupCount
            ];
            for (int i = 0; i < groupCount; i++)
            {
                _groupEntityComponentsDB[i] = new DenseDictionary<ComponentId, IComponentArray>();
            }
        }

        public DenseDictionary<ComponentId, IComponentArray>[] GroupEntityComponentsDB =>
            _groupEntityComponentsDB;

        public bool ConfigurationFrozen => _configurationFrozen;

        public void FreezeConfiguration()
        {
            _configurationFrozen = true;
        }

        /// <summary>
        /// Returns the per-component inner dictionary for a group. Every valid
        /// GroupIndex has a pre-allocated (initially empty) inner dictionary
        /// populated by <see cref="PreallocateDBGroup"/>.
        /// </summary>
        [MethodImpl(MethodImplOptions.AggressiveInlining)]
        public DenseDictionary<ComponentId, IComponentArray> GetDBGroup(GroupIndex groupId)
        {
            return _groupEntityComponentsDB[groupId.Index];
        }

        [MethodImpl(MethodImplOptions.AggressiveInlining)]
        public IComponentArray GetOrAddTypeSafeDictionary(
            GroupIndex groupId,
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

            return toEntitiesDictionary;
        }

        [MethodImpl(MethodImplOptions.AggressiveInlining)]
        public static IComponentArray GetTypeSafeDictionary(
            GroupIndex groupId,
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
            GroupIndex groupId,
            int size,
            IComponentBuilder[] entityComponentsToBuild
        )
        {
            Assert.That(!_configurationFrozen);

            var numberOfEntityComponents = entityComponentsToBuild.Length;
            DenseDictionary<ComponentId, IComponentArray> group = GetDBGroup(groupId);
            group.EnsureCapacity(numberOfEntityComponents);

            for (var index = 0; index < numberOfEntityComponents; index++)
            {
                var entityComponentBuilder = entityComponentsToBuild[index];
                var entityComponentType = entityComponentBuilder.ComponentId;

                var components = group.GetOrAdd(
                    entityComponentType,
                    () => entityComponentBuilder.CreateDictionary(size)
                );

                entityComponentBuilder.Preallocate(components, size);
            }

            _log.Trace(
                "Initialized group {} with {} component arrays",
                groupId,
                numberOfEntityComponents
            );
        }

        public void Dispose()
        {
            for (int i = 0; i < _groupEntityComponentsDB.Length; i++)
            {
                var group = _groupEntityComponentsDB[i];
                foreach (var entityList in group)
                {
                    entityList.Value.Dispose();
                }
                group.Clear();
            }
        }
    }
}
