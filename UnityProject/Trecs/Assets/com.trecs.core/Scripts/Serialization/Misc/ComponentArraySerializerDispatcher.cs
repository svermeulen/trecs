using Unity.Collections;

namespace Trecs.Internal
{
    /// <summary>
    /// Type-erased adapter used inside <see cref="Trecs.ComponentArraySerializerRegistry"/>
    /// and <see cref="Trecs.WorldStateSerializer"/> to dispatch to a user-provided
    /// <see cref="Trecs.IComponentArraySerializer{T}"/> without leaking
    /// <see cref="IComponentArray"/> into the public interface.
    /// </summary>
    internal interface IComponentArraySerializerDispatcher
    {
        void Serialize(IComponentArray array, ISerializationWriter writer);
        void Deserialize(IComponentArray array, int requiredCount, ISerializationReader reader);
    }

    internal sealed class ComponentArraySerializerDispatcher<T>
        : IComponentArraySerializerDispatcher
        where T : unmanaged, IEntityComponent
    {
        readonly IComponentArraySerializer<T> _user;

        public ComponentArraySerializerDispatcher(IComponentArraySerializer<T> user)
        {
            _user = user;
        }

        public IComponentArraySerializer<T> UserSerializer => _user;

        public void Serialize(IComponentArray array, ISerializationWriter writer)
        {
            var typed = (ComponentArray<T>)array;
            var list = typed.RawValues;
            var origLength = list.Length;

            // ComponentArray<T> treats _values.Length as capacity and tracks the
            // logical count separately. Collapse Length to the count so the user
            // sees a NativeList<T> whose Length equals the entity count; restore
            // Length afterward so subsequent EnsureCapacity calls still see the
            // original capacity-as-length invariant.
            list.Resize(typed.Count, NativeArrayOptions.UninitializedMemory);
            try
            {
                _user.Serialize(list, writer);
            }
            finally
            {
                list.Resize(origLength, NativeArrayOptions.UninitializedMemory);
            }
        }

        public void Deserialize(
            IComponentArray array,
            int requiredCount,
            ISerializationReader reader
        )
        {
            var typed = (ComponentArray<T>)array;
            var list = typed.RawValues;

            // Collapse Length to the LIVE count so the user can compare it
            // against requiredCount and decide how to reconcile (preserve in
            // place, resize-and-clear, dispose-and-rebuild, etc.).
            list.Resize(typed.Count, NativeArrayOptions.UninitializedMemory);
            _user.Deserialize(list, requiredCount, reader);
            TrecsAssert.That(
                list.Length == requiredCount,
                "IComponentArraySerializer<{0}>.Deserialize left values.Length == {1}, but the group requires {2}. Every component array in a group must share the same length on return.",
                typeof(T).Name,
                list.Length,
                requiredCount
            );
            typed.ForceSetCount(list.Length);
        }
    }
}
