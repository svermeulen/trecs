using System;
using Trecs.Internal;
using Unity.Mathematics;
using UnityEngine;

namespace Trecs.Serialization
{
    public static class TrecsSerialization
    {
        public static SerializationServices Create(
            World world,
            Action<SerializerRegistry> customRegistrations = null
        )
        {
            var registry = new SerializerRegistry();

            RegisterCoreSerializers(registry);
            RegisterTrecsSerializers(registry);

            customRegistrations?.Invoke(registry);

            BookmarkMetadata.RegisterAllRecordingSerializers(registry);

            var ecsStateSerializer = new EcsStateSerializer(world);
            var gameStateSerializer = new SimpleGameStateSerializer(ecsStateSerializer);
            var checksumCalculator = new RecordingChecksumCalculator(gameStateSerializer);
            var blobCache = world.GetBlobCache();
            var bookmarkSerializer = new BookmarkSerializer(
                gameStateSerializer,
                blobCache,
                world,
                registry
            );
            var recordingHandler = new RecordingHandler(
                blobCache,
                checksumCalculator,
                gameStateSerializer,
                registry,
                world
            );
            var playbackHandler = new PlaybackHandler(
                gameStateSerializer,
                checksumCalculator,
                bookmarkSerializer,
                registry,
                world
            );

            return new SerializationServices(
                registry,
                ecsStateSerializer,
                gameStateSerializer,
                checksumCalculator,
                bookmarkSerializer,
                recordingHandler,
                playbackHandler
            );
        }

        /// <summary>
        /// Registers core primitive and math type serializers.
        /// Call this if you need to set up a SerializerRegistry manually
        /// without using <see cref="Create"/>.
        /// </summary>
        public static void RegisterCoreSerializers(SerializerRegistry registry)
        {
            registry.RegisterBlit<ComponentId>();

            // Primitives — blit is preferred over BinaryReader to avoid allocs
            registry.RegisterBlit<float>();
            registry.RegisterBlit<byte>(includeDelta: true);
            registry.RegisterBlit<sbyte>(includeDelta: true);
            registry.RegisterBlit<short>(includeDelta: true);
            registry.RegisterBlit<ushort>(includeDelta: true);
            registry.RegisterBlit<int>(includeDelta: true);
            registry.RegisterBlit<uint>(includeDelta: true);
            registry.RegisterBlit<ulong>(includeDelta: true);
            registry.RegisterBlit<long>(includeDelta: true);
            registry.RegisterBlit<double>(includeDelta: true);
            registry.RegisterBlit<decimal>(includeDelta: true);

            // Math types
            registry.RegisterBlit<float2>(includeDelta: true);
            registry.RegisterBlit<int2>(includeDelta: true);
            registry.RegisterBlit<float3>(includeDelta: true);
            registry.RegisterBlit<quaternion>(includeDelta: true);
            registry.RegisterBlit<Vector3>(includeDelta: true);
            registry.RegisterBlit<Vector4>(includeDelta: true);

            registry.RegisterSerializer<BoolSerializer>();
            registry.RegisterSerializer<TypeSerializer>();
            registry.RegisterSerializer<StringSerializer>();
        }

        /// <summary>
        /// Registers Trecs-specific serializers for ECS state, heaps, and blobs.
        /// Call this if you need to set up a SerializerRegistry manually
        /// without using <see cref="Create"/>.
        /// </summary>
        public static void RegisterTrecsSerializers(SerializerRegistry registry)
        {
            // Heap serializers
            RegisterNativeSharedHeapSerializers(registry);
            RegisterSharedHeapSerializers(registry);
            RegisterBlobManifestSerializers(registry);

            // Entity serializers
            registry.RegisterBlit<EntityHandleMapElement>();
            registry.RegisterBlit<EntityHandle>();
            registry.RegisterBlit<Group>();

            // For EntityInputQueue
            registry.RegisterSerializer<DenseHashSetSerializer<EntityHandle>>();

            registry.RegisterSerializer<RngSerializer>();

            registry.RegisterSerializer<NativeDenseDictionarySerializer<uint, uint>>();
            registry.RegisterSerializer<NativeArraySerializer<uint>>();
        }

        static void RegisterNativeSharedHeapSerializers(SerializerRegistry registry)
        {
            registry.RegisterSerializer<
                DenseDictionarySerializer<BlobId, NativeSharedHeap.BlobInfo>
            >();
            registry.RegisterSerializer<DenseDictionarySerializer<PtrHandle, BlobId>>();
            registry.RegisterBlit<PtrHandle>();
            registry.RegisterBlit<NativeSharedHeap.BlobInfo>();
        }

        static void RegisterSharedHeapSerializers(SerializerRegistry registry)
        {
            registry.RegisterSerializer<DenseDictionarySerializer<BlobId, SharedHeap.BlobInfo>>();
            registry.RegisterBlit<SharedHeap.BlobInfo>();
        }

        static void RegisterBlobManifestSerializers(SerializerRegistry registry)
        {
            registry.RegisterSerializer<ListSerializer<object>>();
            registry.RegisterSerializer<BlobManifestSerializer>();
            registry.RegisterSerializer<DenseDictionarySerializer<BlobId, BlobMetadata>>();
            registry.RegisterSerializer<BlobMetadataSerializer>();
            registry.RegisterBlit<BlobId>();
        }
    }
}
