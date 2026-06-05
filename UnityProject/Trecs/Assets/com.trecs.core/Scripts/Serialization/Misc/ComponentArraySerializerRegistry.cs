using System;
using Trecs.Collections;
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
    /// Registrations are part of the world schema (they change the snapshot
    /// wire format and the <see cref="WorldSchemaFingerprint"/>), so the
    /// registry seals at <see cref="World.Initialize"/> — register via
    /// <c>WorldBuilder.RegisterComponentArraySerializer</c> or between
    /// <c>Build()</c> and <c>Initialize()</c>.
    /// </summary>
    public sealed class ComponentArraySerializerRegistry
    {
        readonly IterableDictionary<
            RefKey<Type>,
            IComponentArraySerializerDispatcher
        > _byComponentType = new();

        bool _isSealed;

        /// <summary>
        /// Called by <c>World.Initialize</c>: registrations define the
        /// snapshot wire format, and the world's schema fingerprint is
        /// computed once at that point, so later mutation would change the
        /// wire format mid-session — making snapshots taken earlier in the
        /// same session (e.g. rewind keyframes) unloadable.
        /// </summary>
        internal void Seal()
        {
            _isSealed = true;
        }

        /// <summary>
        /// Register a serializer for component type <typeparamref name="T"/>.
        /// Must be called before <c>World.Initialize</c>; asserts if a
        /// serializer is already registered for the type.
        /// </summary>
        public void Register<T>(IComponentArraySerializer<T> serializer)
            where T : unmanaged, IEntityComponent
        {
            ThrowIfSealed();
            TrecsDebugAssert.IsNotNull(serializer);
            TrecsDebugAssert.That(
                !_byComponentType.ContainsKey(typeof(T)),
                "Component array serializer for type {0} is already registered",
                typeof(T)
            );

            // Dictionary.Add (not the indexer) so duplicate registration also
            // throws in release builds, where the TrecsDebugAssert above is a no-op.
            _byComponentType.Add(typeof(T), new ComponentArraySerializerDispatcher<T>(serializer));
        }

        /// <summary>
        /// Remove the serializer registered for component type
        /// <typeparamref name="T"/>. Must be called before
        /// <c>World.Initialize</c>. Returns <c>true</c> if a serializer was
        /// removed, <c>false</c> if none was registered.
        /// </summary>
        public bool Unregister<T>()
            where T : unmanaged, IEntityComponent
        {
            ThrowIfSealed();
            return _byComponentType.TryRemove(typeof(T));
        }

        // Release-safe (TrecsAssert, not the stripped debug variant): this is
        // a setup-time call with zero hot-path cost, and a mid-session wire
        // format change is exactly the kind of corruption the schema
        // fingerprint exists to prevent.
        void ThrowIfSealed()
        {
            TrecsAssert.That(
                !_isSealed,
                "ComponentArraySerializerRegistry is sealed — registrations are part of the "
                    + "world schema and must complete before World.Initialize(). Use "
                    + "WorldBuilder.RegisterComponentArraySerializer, or mutate the registry "
                    + "between Build() and Initialize()."
            );
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
        public IterableDictionaryKeyEnumerable<RefKey<Type>> GetRegisteredComponentTypes()
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
