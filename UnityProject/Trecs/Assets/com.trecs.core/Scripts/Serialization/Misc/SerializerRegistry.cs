using System;
using System.Collections.Generic;
using Trecs.Internal;

namespace Trecs
{
    /// <summary>
    /// Registry of serializers and their associated type IDs.
    ///
    /// - Each serializer declares the type it handles via ISerializer&lt;T&gt;.
    ///   During serialization, the type ID is written first, then the serializer is called.
    ///
    /// - Type IDs are computed by <see cref="TypeId.FromType"/> (from [TypeId] attribute or stable hash).
    ///
    /// - The default constructor auto-populates the built-in primitive, math,
    ///   ECS, and recording-metadata serializers; register additional
    ///   <see cref="ISerializer{T}"/> implementations via
    ///   <see cref="WorldBuilder.RegisterSerializer(ISerializer)"/> or directly
    ///   on <see cref="World.SerializerRegistry"/>.
    ///
    /// - Two registration shapes are supported:
    ///   <list type="bullet">
    ///   <item><description><b>Instance</b> (<see cref="RegisterSerializer(ISerializer)"/>):
    ///   exclusive — throws if another serializer (instance or Type-based) already
    ///   handles the same target object type. Use for serializers that need non-default
    ///   construction (constructor args, DI-resolved dependencies).</description></item>
    ///   <item><description><b>Type</b> (<see cref="RegisterSerializer{TSerializer}"/> /
    ///   <see cref="RegisterSerializer(Type)"/>): the target object type is read via
    ///   reflection at registration time (so type-IDs and conflicts are detected up
    ///   front), but the serializer itself is lazily constructed via
    ///   <c>Activator.CreateInstance</c> on first lookup and cached. The same
    ///   serializer Type may be registered from multiple call sites; extra registrations
    ///   are silently ignored. Different serializer Types targeting the same object
    ///   type still throw. Use for common reusable serializers (e.g.
    ///   <c>ListSerializer&lt;int&gt;</c>) so unrelated systems can register what they
    ///   need without coordinating.</description></item>
    ///   </list>
    ///
    /// </summary>
    public sealed class SerializerRegistry
    {
        static readonly TrecsLog _log = TrecsLog.Default;

        readonly Dictionary<Type, ISerializer> _objectTypeToSerializer = new();
        readonly Dictionary<Type, ISerializerDelta> _objectTypeToSerializerDelta = new();
        readonly Dictionary<Type, Type> _pendingLazySerializers = new();
        readonly Dictionary<Type, Type> _pendingLazySerializerDeltas = new();
        readonly HashSet<Type> _registeredSerializerTypes = new();
        readonly HashSet<Type> _registeredSerializerDeltaTypes = new();

        public void RegisterSerializer(ISerializer serializer)
        {
            var objectType = ExtractObjectType(serializer.GetType(), typeof(ISerializer<>));

            AssertSerializerSlotEmpty(objectType);

            _log.Trace("Registering serializer {0} for type {1}", serializer.GetType(), objectType);

            TypeId.Register(objectType);
            _objectTypeToSerializer.Add(objectType, serializer);
        }

        public void RegisterSerializer<TSerializer>()
            where TSerializer : ISerializer, new()
        {
            RegisterSerializer(typeof(TSerializer));
        }

        public void RegisterSerializer(Type serializerType)
        {
            if (!_registeredSerializerTypes.Add(serializerType))
            {
                return;
            }

            var objectType = ExtractObjectType(serializerType, typeof(ISerializer<>));

            AssertSerializerSlotEmpty(objectType);

            _log.Trace("Registering lazy serializer {0} for type {1}", serializerType, objectType);

            TypeId.Register(objectType);
            _pendingLazySerializers.Add(objectType, serializerType);
        }

        public void RegisterSerializerDelta(ISerializerDelta serializer)
        {
            var objectType = ExtractObjectType(serializer.GetType(), typeof(ISerializerDelta<>));

            AssertSerializerDeltaSlotEmpty(objectType);

            _log.Trace(
                "Registering serializer delta {0} for type {1}",
                serializer.GetType(),
                objectType
            );

            TypeId.Register(objectType);
            _objectTypeToSerializerDelta.Add(objectType, serializer);
        }

        public void RegisterSerializerDelta<TSerializer>()
            where TSerializer : ISerializerDelta, new()
        {
            RegisterSerializerDelta(typeof(TSerializer));
        }

        public void RegisterSerializerDelta(Type serializerType)
        {
            if (!_registeredSerializerDeltaTypes.Add(serializerType))
            {
                return;
            }

            var objectType = ExtractObjectType(serializerType, typeof(ISerializerDelta<>));

            AssertSerializerDeltaSlotEmpty(objectType);

            _log.Trace(
                "Registering lazy serializer delta {0} for type {1}",
                serializerType,
                objectType
            );

            TypeId.Register(objectType);
            _pendingLazySerializerDeltas.Add(objectType, serializerType);
        }

        // finds or creates a serializer for the given type
        public ISerializer<T> TryGetSerializer<T>()
        {
            return (ISerializer<T>)TryGetSerializer(typeof(T));
        }

        public bool HasSerializer(Type type)
        {
            return _objectTypeToSerializer.ContainsKey(type)
                || _pendingLazySerializers.ContainsKey(type);
        }

        public bool HasSerializer<T>()
        {
            return HasSerializer(typeof(T));
        }

        public ISerializer<T> GetSerializer<T>()
        {
            var serializer = TryGetSerializer<T>();
            TrecsDebugAssert.IsNotNull(serializer, "No serializer found for type '{0}'", typeof(T));
            return serializer;
        }

        public ISerializer GetSerializer(Type objectType)
        {
            var serializer = TryGetSerializer(objectType);
            TrecsDebugAssert.IsNotNull(
                serializer,
                "No serializer found for type '{0}'",
                objectType
            );
            return serializer;
        }

        public ISerializer TryGetSerializer(Type objectType)
        {
            if (_objectTypeToSerializer.TryGetValue(objectType, out var serializer))
            {
                return serializer;
            }

            if (_pendingLazySerializers.TryGetValue(objectType, out var serializerType))
            {
                var instance = (ISerializer)Activator.CreateInstance(serializerType);
                _objectTypeToSerializer.Add(objectType, instance);
                _pendingLazySerializers.Remove(objectType);
                return instance;
            }

            return null;
        }

        // finds or creates a serializer for the given type
        public ISerializerDelta<T> TryGetSerializerDelta<T>()
        {
            return (ISerializerDelta<T>)TryGetSerializerDelta(typeof(T));
        }

        public ISerializerDelta<T> GetSerializerDelta<T>()
        {
            var serializer = TryGetSerializerDelta<T>();
            TrecsDebugAssert.IsNotNull(
                serializer,
                "No serializer delta found for type '{0}'",
                typeof(T)
            );
            return serializer;
        }

        public ISerializerDelta GetSerializerDelta(Type objectType)
        {
            var serializer = TryGetSerializerDelta(objectType);
            TrecsDebugAssert.IsNotNull(
                serializer,
                "No serializer found for type '{0}'",
                objectType
            );
            return serializer;
        }

        public ISerializerDelta TryGetSerializerDelta(Type objectType)
        {
            if (_objectTypeToSerializerDelta.TryGetValue(objectType, out var serializer))
            {
                return serializer;
            }

            if (_pendingLazySerializerDeltas.TryGetValue(objectType, out var serializerType))
            {
                var instance = (ISerializerDelta)Activator.CreateInstance(serializerType);
                _objectTypeToSerializerDelta.Add(objectType, instance);
                _pendingLazySerializerDeltas.Remove(objectType);
                return instance;
            }

            return null;
        }

        void AssertSerializerSlotEmpty(Type objectType)
        {
            TrecsDebugAssert.That(
                !_objectTypeToSerializer.ContainsKey(objectType)
                    && !_pendingLazySerializers.ContainsKey(objectType),
                "Serializer for type {0} is already registered",
                objectType
            );
        }

        void AssertSerializerDeltaSlotEmpty(Type objectType)
        {
            TrecsDebugAssert.That(
                !_objectTypeToSerializerDelta.ContainsKey(objectType)
                    && !_pendingLazySerializerDeltas.ContainsKey(objectType),
                "Serializer delta for type {0} is already registered",
                objectType
            );
        }

        static Type ExtractObjectType(Type serializerType, Type genericBase)
        {
            foreach (var interfaceCandidate in serializerType.GetInterfaces())
            {
                if (
                    interfaceCandidate.IsGenericType
                    && interfaceCandidate.GetGenericTypeDefinition() == genericBase
                )
                {
                    var objectType = interfaceCandidate.GetGenericArguments()[0];
                    TrecsDebugAssert.That(
                        !objectType.IsAbstract || objectType == typeof(Type),
                        "Expected non abstract type but found {0}",
                        objectType
                    );
                    return objectType;
                }
            }

            throw TrecsDebugAssert.CreateException(
                "Serializer {0} does not implement {1}",
                serializerType,
                genericBase
            );
        }
    }
}
