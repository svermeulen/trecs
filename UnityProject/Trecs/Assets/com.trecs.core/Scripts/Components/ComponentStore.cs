using System;
using System.ComponentModel;
using System.Runtime.CompilerServices;
using Trecs.Collections;

namespace Trecs.Internal
{
    [EditorBrowsable(EditorBrowsableState.Never)]
    public sealed class ComponentStore : IDisposable
    {
        readonly TrecsLog _log;

        // Per-group component arrays, indexed directly by GroupIndex.Index.
        // The outer array is sized at world build (one IterableDictionary per
        // group); the inner per-component IComponentArray slots are populated
        // lazily on first touch — usually the group's first AddEntity, or the
        // serializer's eager pre-materialize for snapshot/recording reads. A
        // direct array index replaces the old Dict<GroupIndex, ...> hash
        // lookup on the hot path.
        readonly IterableDictionary<TypeId, IComponentArray>[] _groupEntityComponentsDB;

        bool _configurationFrozen;

        public ComponentStore(TrecsLog log, int groupCount)
        {
            _log = log;
            _groupEntityComponentsDB = new IterableDictionary<TypeId, IComponentArray>[groupCount];
            for (int i = 0; i < groupCount; i++)
            {
                _groupEntityComponentsDB[i] = new IterableDictionary<TypeId, IComponentArray>();
            }
        }

        public IterableDictionary<TypeId, IComponentArray>[] GroupEntityComponentsDB =>
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
        public IterableDictionary<TypeId, IComponentArray> GetDBGroup(GroupIndex groupId)
        {
            return _groupEntityComponentsDB[groupId.Index];
        }

        [MethodImpl(MethodImplOptions.AggressiveInlining)]
        public IComponentArray GetOrAddTypeSafeDictionary(
            GroupIndex groupId,
            IterableDictionary<TypeId, IComponentArray> groupPerComponentType,
            TypeId typeId,
            IComponentArray fromDictionary
        )
        {
            // Per-group component-array slots are materialized lazily on first
            // touch — both during world build (no eager warm-up) and during
            // entity submission (a fresh group's slots show up when entities
            // first land in it). The freeze flag is intentionally not checked
            // here; it only signals "no more groups" upstream, not "no more
            // slots on existing groups."
            if (
                !groupPerComponentType.TryGetValue(typeId, out IComponentArray toEntitiesDictionary)
            )
            {
                toEntitiesDictionary = fromDictionary.Create();
                groupPerComponentType.Add(typeId, toEntitiesDictionary);
            }

            return toEntitiesDictionary;
        }

        /// <summary>
        /// Eagerly materialize the per-group component-array slots for a given
        /// template's builders. Component slots are normally lazy (created on
        /// first entity), but serialization read paths need them populated up
        /// front so the in-memory layout matches the on-wire shape.
        /// </summary>
        public void PreallocateDBGroup(
            GroupIndex groupId,
            int size,
            IComponentBuilder[] entityComponentsToBuild
        )
        {
            var numberOfEntityComponents = entityComponentsToBuild.Length;
            IterableDictionary<TypeId, IComponentArray> group = GetDBGroup(groupId);
            group.EnsureCapacity(numberOfEntityComponents);

            for (var index = 0; index < numberOfEntityComponents; index++)
            {
                var entityComponentBuilder = entityComponentsToBuild[index];
                var entityComponentType = entityComponentBuilder.TypeId;

                var components = group.GetOrAdd(
                    entityComponentType,
                    () => entityComponentBuilder.CreateDictionary(size)
                );

                entityComponentBuilder.Preallocate(components, size);
            }

            _log.Trace(
                "Initialized group {0} with {1} component arrays",
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
