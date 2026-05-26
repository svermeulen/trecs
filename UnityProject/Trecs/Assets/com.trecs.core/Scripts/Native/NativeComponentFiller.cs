using Trecs.Collections;

namespace Trecs.Internal
{
    interface IFiller
    {
        void FillFromByteArray(
            IterableDictionary<TypeId, IComponentArray> groupDictionary,
            int indexInTransientBuffer,
            NativeBag buffer
        );
    }

    class Filler<T> : IFiller
        where T : unmanaged, IEntityComponent
    {
        static Filler()
        {
            TrecsDebugAssert.That(TypeMeta<T>.IsUnmanaged, "invalid type used");
        }

        public void FillFromByteArray(
            IterableDictionary<TypeId, IComponentArray> groupDictionary,
            int indexInTransientBuffer,
            NativeBag buffer
        )
        {
            var component = buffer.Dequeue<T>();
            var dictionary = (IComponentArray<T>)groupDictionary[TypeId<T>.Value];
            dictionary.GetValueAtIndexByRef(indexInTransientBuffer) = component;
        }
    }

    static class EntityComponentIdMap
    {
        static readonly IterableDictionary<TypeId, IFiller> _map;

        static EntityComponentIdMap()
        {
            _map = new();
        }

        internal static void Register<T>(IFiller entityBuilder)
            where T : unmanaged, IEntityComponent
        {
            TrecsDebugAssert.That(UnityThreadHelper.IsMainThread);
            TypeId location = TypeId<T>.Value;
            _map.Add(location, entityBuilder);
        }

        internal static IFiller GetBuilderFromId(TypeId typeId)
        {
            TrecsDebugAssert.That(UnityThreadHelper.IsMainThread);
            return _map[typeId];
        }
    }
}
