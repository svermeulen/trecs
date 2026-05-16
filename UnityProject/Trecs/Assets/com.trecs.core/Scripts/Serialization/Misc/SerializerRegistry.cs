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
    /// - Type IDs are computed by TypeIdProvider (from [SerializationId] attribute or stable hash).
    ///
    /// - The default constructor auto-populates the built-in primitive, math,
    ///   ECS, and recording-metadata serializers; register additional
    ///   <see cref="ISerializer{T}"/> implementations via
    ///   <see cref="WorldBuilder.RegisterSerializer(ISerializer)"/> or directly
    ///   on <see cref="World.SerializerRegistry"/>.
    ///
    /// </summary>
    public sealed class SerializerRegistry
    {
        static readonly TrecsLog _log = TrecsLog.Default;

        readonly Dictionary<Type, ISerializer> _objectTypeToSerializer = new();
        readonly Dictionary<Type, ISerializerDelta> _objectTypeToSerializerDelta = new();

        public void RegisterSerializer(ISerializer serializer)
        {
            var objectType = ExtractObjectType(serializer.GetType(), typeof(ISerializer<>));

            TrecsAssert.That(
                !_objectTypeToSerializer.ContainsKey(objectType),
                "Serializer for type {0} is already registered",
                objectType
            );

            _log.Trace("Registering serializer {0} for type {1}", serializer.GetType(), objectType);

            TypeIdProvider.Register(objectType);
            _objectTypeToSerializer.Add(objectType, serializer);
        }

        public void RegisterSerializerDelta(ISerializerDelta serializer)
        {
            var objectType = ExtractObjectType(serializer.GetType(), typeof(ISerializerDelta<>));

            TrecsAssert.That(
                !_objectTypeToSerializerDelta.ContainsKey(objectType),
                "Serializer delta for type {0} is already registered",
                objectType
            );

            _log.Trace(
                "Registering serializer delta {0} for type {1}",
                serializer.GetType(),
                objectType
            );

            TypeIdProvider.Register(objectType);
            _objectTypeToSerializerDelta.Add(objectType, serializer);
        }

        public void RegisterSerializerDelta<TSerializer>()
            where TSerializer : ISerializerDelta, new()
        {
            RegisterSerializerDelta(new TSerializer());
        }

        // finds or creates a serializer for the given type
        public ISerializer<T> TryGetSerializer<T>()
        {
            return (ISerializer<T>)TryGetSerializer(typeof(T));
        }

        public bool HasSerializer(Type type)
        {
            return _objectTypeToSerializer.ContainsKey(type);
        }

        public bool HasSerializer<T>()
        {
            return HasSerializer(typeof(T));
        }

        public ISerializer<T> GetSerializer<T>()
        {
            var serializer = TryGetSerializer<T>();
            TrecsAssert.IsNotNull(serializer, "No serializer found for type '{0}'", typeof(T));
            return serializer;
        }

        public ISerializer GetSerializer(Type objectType)
        {
            var serializer = TryGetSerializer(objectType);
            TrecsAssert.IsNotNull(serializer, "No serializer found for type '{0}'", objectType);
            return serializer;
        }

        public ISerializer TryGetSerializer(Type objectType)
        {
            if (_objectTypeToSerializer.TryGetValue(objectType, out var serializer))
            {
                return serializer;
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
            TrecsAssert.IsNotNull(
                serializer,
                "No serializer delta found for type '{0}'",
                typeof(T)
            );
            return serializer;
        }

        public ISerializerDelta GetSerializerDelta(Type objectType)
        {
            var serializer = TryGetSerializerDelta(objectType);
            TrecsAssert.IsNotNull(serializer, "No serializer found for type '{0}'", objectType);
            return serializer;
        }

        public ISerializerDelta TryGetSerializerDelta(Type objectType)
        {
            if (_objectTypeToSerializerDelta.TryGetValue(objectType, out var serializer))
            {
                return serializer;
            }

            return null;
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
                    TrecsAssert.That(
                        !objectType.IsAbstract || objectType == typeof(Type),
                        "Expected non abstract type but found {0}",
                        objectType
                    );
                    return objectType;
                }
            }

            throw TrecsAssert.CreateException(
                "Serializer {0} does not implement {1}",
                serializerType,
                genericBase
            );
        }
    }
}
