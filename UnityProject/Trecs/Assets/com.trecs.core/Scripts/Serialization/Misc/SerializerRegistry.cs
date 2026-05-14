using System;
using System.Collections.Generic;
using System.IO;
using Unity.Mathematics;
using UnityEngine;

namespace Trecs.Internal
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
    ///   <see cref="WorldBuilder.RegisterSerializer{TSerializer}"/> or directly
    ///   on <see cref="World.SerializerRegistry"/>.
    ///
    /// Implementation notes:
    ///
    /// - Note that there is a reason we don't construct generic serializer types via
    ///   reflection, since this causes problems with IL2CPP (requires that we manually
    ///   specify all these generic instantiations of the serializers which is annoying).
    ///
    /// </summary>
    public sealed class SerializerRegistry
    {
        static readonly TrecsLog _log = TrecsLog.Default;

        readonly Dictionary<Type, ISerializer> _objectTypeToSerializer = new();
        readonly Dictionary<Type, ISerializerDelta> _objectTypeToSerializerDelta = new();

        public SerializerRegistry()
        {
            RegisterCoreSerializers();
            RegisterTrecsSerializers();
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

        public void RegisterBlit<T>(bool includeDelta = false)
            where T : unmanaged
        {
            var serializer = new BlitSerializer<T>();
            RegisterSerializer(serializer);

            if (includeDelta)
            {
                RegisterSerializerDelta(serializer);
            }
        }

        public void RegisterEnum<T>(bool includeDelta = false)
            where T : unmanaged
        {
            TrecsAssert.That(typeof(T).IsEnum);
            var serializer = new EnumSerializer<T>();
            RegisterSerializer(serializer);

            if (includeDelta)
            {
                RegisterSerializerDelta(serializer);
            }
        }

        public void RegisterSkip<T>()
        {
            RegisterSerializer(new SkipSerializer<T>());
        }

        public void RegisterSerializer<TSerializer>()
            where TSerializer : ISerializer, new()
        {
            RegisterSerializer(new TSerializer());
        }

        public void RegisterSerializer(Type serializerType)
        {
            TrecsRequire.That(serializerType != null, "serializerType must not be null");
            TrecsRequire.That(
                typeof(ISerializer).IsAssignableFrom(serializerType),
                "Type '{0}' does not implement ISerializer",
                serializerType
            );
            RegisterSerializer((ISerializer)Activator.CreateInstance(serializerType));
        }

        public void RegisterSerializerDelta<TSerializer>()
            where TSerializer : ISerializerDelta, new()
        {
            RegisterSerializerDelta(new TSerializer());
        }

        void RegisterCoreSerializers()
        {
            RegisterBlit<ComponentId>();

            // Primitives — blit is preferred over BinaryReader to avoid allocs
            RegisterBlit<float>();
            RegisterBlit<byte>(includeDelta: true);
            RegisterBlit<sbyte>(includeDelta: true);
            RegisterBlit<short>(includeDelta: true);
            RegisterBlit<ushort>(includeDelta: true);
            RegisterBlit<int>(includeDelta: true);
            RegisterBlit<uint>(includeDelta: true);
            RegisterBlit<ulong>(includeDelta: true);
            RegisterBlit<long>(includeDelta: true);
            RegisterBlit<double>(includeDelta: true);
            RegisterBlit<decimal>(includeDelta: true);

            // Math types
            RegisterBlit<float2>(includeDelta: true);
            RegisterBlit<int2>(includeDelta: true);
            RegisterBlit<float3>(includeDelta: true);
            RegisterBlit<quaternion>(includeDelta: true);
            RegisterBlit<Vector3>(includeDelta: true);
            RegisterBlit<Vector4>(includeDelta: true);

            RegisterSerializer<BoolSerializer>();
            RegisterSerializer<TypeSerializer>();
            RegisterSerializer<StringSerializer>();
        }

        void RegisterTrecsSerializers()
        {
            // Heap serializers
            RegisterSerializer<DenseDictionarySerializer<BlobId, NativeSharedHeap.BlobInfo>>();
            RegisterSerializer<DenseDictionarySerializer<PtrHandle, BlobId>>();
            RegisterBlit<PtrHandle>();
            RegisterBlit<NativeSharedHeap.BlobInfo>();

            RegisterSerializer<DenseDictionarySerializer<BlobId, SharedHeap.BlobInfo>>();
            RegisterBlit<SharedHeap.BlobInfo>();

            RegisterSerializer<ListSerializer<object>>();
            RegisterSerializer<BlobManifestSerializer>();
            RegisterSerializer<DenseDictionarySerializer<BlobId, BlobMetadata>>();
            RegisterSerializer<BlobMetadataSerializer>();
            RegisterBlit<BlobId>();

            // Entity serializers
            RegisterBlit<EntityHandleMapElement>();
            RegisterBlit<EntityHandle>();
            RegisterBlit<TagSet>();
            // SetId is written by WorldStateSerializer.WriteSets /
            // WriteSetRoutingIndex unconditionally — register it here so
            // any world serialization works without callers having to
            // remember to register it themselves. Same for the per-set
            // entity-id-to-dense-index dictionary, written for each
            // group's entries.
            RegisterBlit<SetId>();
            RegisterSerializer<NativeDenseDictionarySerializer<int, int>>();

            // For EntityInputQueue
            RegisterSerializer<DenseHashSetSerializer<EntityHandle>>();

            RegisterSerializer<RngSerializer>();

            RegisterSerializer<NativeDenseDictionarySerializer<uint, uint>>();
            RegisterSerializer<NativeArraySerializer<uint>>();

            SnapshotMetadata.RegisterSerializers(this);
            BundleHeader.RegisterSerializers(this);
        }

        public void WriteTypeId(Type objectType, BinaryWriter writer)
        {
            using var _ = TrecsProfiling.Start("WriteTypeId");

            if (objectType.DerivesFrom<Type>())
            {
                objectType = typeof(Type);
            }

            TrecsAssert.That(!objectType.IsGenericTypeDefinition);
            writer.Write(TypeIdProvider.GetTypeId(objectType));
        }

        public Type ReadTypeId(BinaryReader reader)
        {
            int id = reader.ReadInt32();
            return TypeIdProvider.GetTypeFromId(id);
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
    }
}
