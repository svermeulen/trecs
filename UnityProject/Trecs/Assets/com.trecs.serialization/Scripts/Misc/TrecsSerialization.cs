using Trecs.Internal;
using Unity.Mathematics;
using UnityEngine;

namespace Trecs.Serialization
{
    /// <summary>
    /// Trecs-specific registration presets for <see cref="SerializerRegistry"/>.
    ///
    /// Most callers want <see cref="CreateSerializerRegistry"/>, which returns a
    /// registry pre-populated with everything Trecs needs (core primitives + ECS
    /// internals + recording metadata).
    ///
    /// The granular building blocks (<see cref="RegisterCoreSerializers"/>,
    /// <see cref="RegisterTrecsSerializers"/>, <see cref="RegisterRecordingSerializers"/>)
    /// are exposed for callers who want partial setup — for example, a save-game-only
    /// project that does not need the recording-metadata serializers.
    /// </summary>
    public static class TrecsSerialization
    {
        /// <summary>
        /// Convenience for the common case — returns a <see cref="SerializerRegistry"/>
        /// pre-populated with core primitive, math, ECS, and recording-metadata
        /// serializers. Equivalent to calling all three Register*Serializers helpers.
        /// </summary>
        public static SerializerRegistry CreateSerializerRegistry()
        {
            var registry = new SerializerRegistry();
            RegisterCoreSerializers(registry);
            RegisterTrecsSerializers(registry);
            RegisterRecordingSerializers(registry);
            return registry;
        }

        /// <summary>
        /// Registers core primitive and math type serializers. Always safe to call.
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
        /// Registers serializers for Trecs ECS internals — entity handles, blob
        /// references, heap manifests, and the entity input queue's underlying types.
        /// Required by <see cref="WorldStateSerializer"/>, <see cref="BookmarkSerializer"/>,
        /// <see cref="RecordingHandler"/>, and <see cref="PlaybackHandler"/>.
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

        /// <summary>
        /// Registers serializers for the recording/playback subsystem — bookmark
        /// metadata and recording metadata. Only needed if you intend to use
        /// <see cref="BookmarkSerializer"/>, <see cref="RecordingHandler"/>, or
        /// <see cref="PlaybackHandler"/>.
        /// </summary>
        public static void RegisterRecordingSerializers(SerializerRegistry registry)
        {
            BookmarkMetadata.RegisterSerializers(registry);
            RecordingMetadata.RegisterSerializers(registry);
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
