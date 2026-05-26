using Trecs.Collections;
using Unity.Collections;

namespace Trecs.Internal
{
    static class EntityFactory
    {
        public static (
            IterableDictionary<TypeId, IComponentArray> group,
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
            IterableDictionary<TypeId, IComponentArray> @group,
            IComponentBuilder[] componentBuilders
#if DEBUG
            ,
            string descriptorName
#endif
        )
        {
#if DEBUG
            TrecsDebugAssert.That(
                componentBuilders != null,
                "Invalid Entity Descriptor {0}",
                descriptorName
            );
#endif
            var numberOfComponents = componentBuilders.Length;

#if DEBUG
            var types = new NativeHashSet<TypeId>(numberOfComponents, Allocator.Temp);

            for (var index = 0; index < numberOfComponents; ++index)
            {
                var entityComponentType = TypeId.FromType(componentBuilders[index].ComponentType);

                TrecsDebugAssert.That(
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
            IterableDictionary<TypeId, IComponentArray> group,
            IComponentBuilder componentBuilder
        )
        {
            IComponentArray safeDictionary = group.GetOrAdd(
                componentBuilder.TypeId,
                (ref IComponentBuilder cb) => cb.CreateDictionary(1),
                ref componentBuilder
            );

            componentBuilder.BuildEntityAndAddToList(safeDictionary);
        }
    }
}
