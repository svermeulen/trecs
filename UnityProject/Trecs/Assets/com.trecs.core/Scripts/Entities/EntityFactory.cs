using Trecs.Collections;
using Unity.Collections;

namespace Trecs.Internal
{
    static class EntityFactory
    {
        public static (
            DenseDictionary<ComponentId, IComponentArray> group,
            int insertionIndex
        ) BuildGroupedEntities(
            GroupIndex groupId,
            DoubleBufferedEntitiesToAdd groupEntitiesToAdd,
            IComponentBuilder[] componentsToBuild
#if DEBUG
            ,
            string descriptorName
#endif
        )
        {
            var group = groupEntitiesToAdd.GetOrCreateCurrentComponentsForGroup(groupId);

            //track the number of entities created so far in the group.
            groupEntitiesToAdd.IncrementEntityCount(groupId);

            BuildEntitiesAndAddToGroup(group, componentsToBuild
#if DEBUG
                , descriptorName
#endif
            );

            // The entity's insertion index is the last position in any component array.
            // All component arrays for the same group have the same count.
            int insertionIndex = 0;
            foreach (var (_, componentArray) in group)
            {
                insertionIndex = componentArray.Count - 1;
                break;
            }

            return (group, insertionIndex);
        }

        static void BuildEntitiesAndAddToGroup(
            DenseDictionary<ComponentId, IComponentArray> @group,
            IComponentBuilder[] componentBuilders
#if DEBUG
            ,
            string descriptorName
#endif
        )
        {
#if DEBUG
            TrecsAssert.That(
                componentBuilders != null,
                "Invalid Entity Descriptor {0}",
                descriptorName
            );
#endif
            var numberOfComponents = componentBuilders.Length;

#if DEBUG
            var types = new NativeHashSet<int>(numberOfComponents, Allocator.Temp);

            for (var index = 0; index < numberOfComponents; ++index)
            {
                var entityComponentType = TypeIdProvider.GetTypeId(
                    componentBuilders[index].ComponentType
                );

                TrecsAssert.That(
                    !types.Contains(entityComponentType),
                    "EntityBuilders must be unique inside a Template. Descriptor Type {0} Component Type: {1}",
                    descriptorName,
                    entityComponentType
                );

                types.Add(entityComponentType);
            }
            types.Dispose();
#endif
            for (var index = 0; index < numberOfComponents; ++index)
            {
                var entityComponentBuilder = componentBuilders[index];

                AddEntity(@group, entityComponentBuilder);
            }
        }

        static void AddEntity(
            DenseDictionary<ComponentId, IComponentArray> group,
            IComponentBuilder componentBuilder
        )
        {
            IComponentArray safeDictionary = group.GetOrAdd(
                componentBuilder.ComponentId,
                (ref IComponentBuilder cb) => cb.CreateDictionary(1),
                ref componentBuilder
            );

            //   if the safeDictionary hasn't been created yet, it will be created inside this method.
            componentBuilder.BuildEntityAndAddToList(safeDictionary);
        }
    }
}
