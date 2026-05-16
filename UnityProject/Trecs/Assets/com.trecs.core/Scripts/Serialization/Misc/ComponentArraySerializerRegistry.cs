using System;
using System.Collections.Generic;
using Trecs.Internal;

namespace Trecs
{
    /// <summary>
    /// Registry of <see cref="IComponentArraySerializer{T}"/> implementations.
    /// One entry per component value type; the registered serializer overrides
    /// the default blit-based serialization used during world snapshots,
    /// recordings, and checksums.
    ///
    /// Owned by <see cref="World"/>; access via <see cref="World.ComponentArraySerializerRegistry"/>.
    /// </summary>
    public sealed class ComponentArraySerializerRegistry
    {
        readonly Dictionary<Type, IComponentArraySerializerDispatcher> _byComponentType = new();

        /// <summary>
        /// Register a serializer for component type <typeparamref name="T"/>.
        /// Asserts if a serializer is already registered for the type.
        /// </summary>
        public void Register<T>(IComponentArraySerializer<T> serializer)
            where T : unmanaged, IEntityComponent
        {
            TrecsAssert.IsNotNull(serializer);
            TrecsAssert.That(
                !_byComponentType.ContainsKey(typeof(T)),
                "Component array serializer for type {0} is already registered",
                typeof(T)
            );

            // Dictionary.Add (not the indexer) so duplicate registration also
            // throws in release builds, where the TrecsAssert above is a no-op.
            _byComponentType.Add(typeof(T), new ComponentArraySerializerDispatcher<T>(serializer));
        }

        /// <summary>
        /// Remove the serializer registered for component type
        /// <typeparamref name="T"/>. Returns <c>true</c> if a serializer was
        /// removed, <c>false</c> if none was registered.
        /// </summary>
        public bool Unregister<T>()
            where T : unmanaged, IEntityComponent
        {
            return _byComponentType.Remove(typeof(T));
        }

        /// <summary>
        /// Retrieve the serializer registered for component type
        /// <typeparamref name="T"/>, if any.
        /// </summary>
        public bool TryGet<T>(out IComponentArraySerializer<T> serializer)
            where T : unmanaged, IEntityComponent
        {
            if (_byComponentType.TryGetValue(typeof(T), out var dispatcher))
            {
                serializer = ((ComponentArraySerializerDispatcher<T>)dispatcher).UserSerializer;
                return true;
            }

            serializer = null;
            return false;
        }

        /// <summary>
        /// Enumerate the component types that currently have a serializer
        /// registered.
        /// </summary>
        public IEnumerable<Type> GetRegisteredComponentTypes()
        {
            return _byComponentType.Keys;
        }

        internal bool TryGetDispatcher(
            Type componentType,
            out IComponentArraySerializerDispatcher dispatcher
        )
        {
            return _byComponentType.TryGetValue(componentType, out dispatcher);
        }
    }
}
