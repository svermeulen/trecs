using Trecs.Internal;
using Unity.Collections;

namespace Trecs.Serialization
{
    /// <summary>
    /// Adapts an <see cref="ISerializer{T}"/> (per-entity) to the
    /// <see cref="IComponentArraySerializer{T}"/> (per-array) contract by
    /// iterating the array and dispatching each entity's value through the
    /// inner serializer. Use this when the per-entity format you want is
    /// identical to what an existing <c>ISerializer&lt;T&gt;</c> already
    /// produces — e.g. you have a versioned <c>ISerializer&lt;CMyComponent&gt;</c>
    /// and want the same logic to apply to component arrays:
    /// <code>
    /// world.ComponentArraySerializerRegistry.Register(
    ///     new PerEntityComponentArraySerializer&lt;CMyComponent&gt;(new MyComponentSerializer()));
    /// </code>
    ///
    /// Trade-off: one virtual call per entity instead of one per array. For
    /// most use cases (snapshots, recordings) the difference is negligible,
    /// but for hot rollback paths over large groups prefer writing a
    /// dedicated <see cref="IComponentArraySerializer{T}"/> that inlines the
    /// per-entity work into one loop.
    ///
    /// On <c>Deserialize</c>, the array is resized to <c>requiredCount</c>
    /// with <b>uninitialized</b> memory before iteration. The inner
    /// <see cref="ISerializer{T}"/> contract is to fully (re)write every
    /// field of the element it owns, so a zero-fill would just be wasted
    /// work; the adapter trusts the serializer to do that. Existing values
    /// are discarded; this adapter is <b>not</b> safe for components
    /// holding native allocations (<c>UnsafeHashMap</c>, <c>UnsafeList</c>,
    /// etc.) because those allocations would be overwritten without being
    /// disposed. Write a dedicated <see cref="IComponentArraySerializer{T}"/>
    /// for those — see the docs example for the pattern.
    /// </summary>
    public sealed class PerEntityComponentArraySerializer<T> : IComponentArraySerializer<T>
        where T : unmanaged, IEntityComponent
    {
        readonly ISerializer<T> _elementSerializer;

        public PerEntityComponentArraySerializer(ISerializer<T> elementSerializer)
        {
            TrecsDebugAssert.IsNotNull(elementSerializer);
            _elementSerializer = elementSerializer;
        }

        public void Serialize(NativeList<T> values, ISerializationWriter writer)
        {
            for (int i = 0; i < values.Length; i++)
            {
                ref readonly var v = ref values.ElementAt(i);
                _elementSerializer.Serialize(in v, writer);
            }
        }

        public void Deserialize(
            NativeList<T> values,
            int requiredCount,
            ISerializationReader reader
        )
        {
            // UninitializedMemory is safe here: the inner ISerializer<T>
            // contract is to fully (re)write every field of the element it
            // owns. Zero-filling the buffer first would just be wasted work
            // — every byte gets overwritten by the per-element Deserialize
            // call below. Saves a memset over potentially-many-MB arrays on
            // large groups.
            values.Clear();
            values.Resize(requiredCount, NativeArrayOptions.UninitializedMemory);
            for (int i = 0; i < requiredCount; i++)
            {
                ref var v = ref values.ElementAt(i);
                _elementSerializer.Deserialize(ref v, reader);
            }
        }
    }
}
