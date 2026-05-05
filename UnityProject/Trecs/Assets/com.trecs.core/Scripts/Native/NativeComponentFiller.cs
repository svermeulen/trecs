namespace Trecs.Internal
{
    interface IFiller
    {
        void FillFromByteArray(
            DenseDictionary<ComponentId, IComponentArray> groupDictionary,
            int indexInTransientBuffer,
            NativeBag buffer
        );
    }

    class Filler<T> : IFiller
        where T : unmanaged, IEntityComponent
    {
        static Filler()
        {
            Assert.That(TypeMeta<T>.IsUnmanaged, "invalid type used");
        }

        public void FillFromByteArray(
            DenseDictionary<ComponentId, IComponentArray> groupDictionary,
            int indexInTransientBuffer,
            NativeBag buffer
        )
        {
            var component = buffer.Dequeue<T>();
            var dictionary = (IComponentArray<T>)groupDictionary[ComponentTypeId<T>.Value];
            dictionary.GetValueAtIndexByRef(indexInTransientBuffer) = component;
        }
    }

    static class EntityComponentIdMap
    {
        static readonly DenseDictionary<ComponentId, IFiller> _map;

        static EntityComponentIdMap()
        {
            _map = new();
        }

        internal static void Register<T>(IFiller entityBuilder)
            where T : unmanaged, IEntityComponent
        {
            Assert.That(UnityThreadHelper.IsMainThread);
            ComponentId location = ComponentTypeId<T>.Value;
            _map.Add(location, entityBuilder);
        }

        internal static IFiller GetBuilderFromId(ComponentId typeId)
        {
            Assert.That(UnityThreadHelper.IsMainThread);
            return _map[typeId];
        }
    }
}
