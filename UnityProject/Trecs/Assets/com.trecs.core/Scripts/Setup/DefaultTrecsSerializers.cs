using Trecs.Serialization;
using Unity.Mathematics;
using UnityEngine;

namespace Trecs.Internal
{
    internal static class DefaultTrecsSerializers
    {
        public static void RegisterCommonTrecsSerializers(SerializerRegistry registry)
        {
            RegisterCoreSerializers(registry);
            RegisterTrecsSerializers(registry);
        }

        static void RegisterCoreSerializers(SerializerRegistry registry)
        {
            RegisterBlit<float>(registry);
            RegisterBlit<byte>(registry, includeDelta: true);
            RegisterBlit<sbyte>(registry, includeDelta: true);
            RegisterBlit<short>(registry, includeDelta: true);
            RegisterBlit<ushort>(registry, includeDelta: true);
            RegisterBlit<int>(registry, includeDelta: true);
            RegisterBlit<uint>(registry, includeDelta: true);
            RegisterBlit<ulong>(registry, includeDelta: true);
            RegisterBlit<long>(registry, includeDelta: true);
            RegisterBlit<double>(registry, includeDelta: true);
            RegisterBlit<decimal>(registry, includeDelta: true);
            RegisterBlit<float2>(registry, includeDelta: true);
            RegisterBlit<int2>(registry, includeDelta: true);
            RegisterBlit<float3>(registry, includeDelta: true);
            RegisterBlit<quaternion>(registry, includeDelta: true);
            RegisterBlit<Vector3>(registry, includeDelta: true);
            RegisterBlit<Vector4>(registry, includeDelta: true);

            registry.RegisterSerializer(new BoolSerializer());
            registry.RegisterSerializer(new TypeSerializer());
            registry.RegisterSerializer(new StringSerializer());
        }

        static void RegisterTrecsSerializers(SerializerRegistry registry)
        {
            RegisterBlit<ComponentId>(registry);

            // Heap serializers
            registry.RegisterSerializer(
                new DenseDictionarySerializer<BlobId, NativeSharedHeap.BlobInfo>()
            );
            registry.RegisterSerializer(new DenseDictionarySerializer<PtrHandle, BlobId>());
            RegisterBlit<PtrHandle>(registry);
            RegisterBlit<NativeSharedHeap.BlobInfo>(registry);

            registry.RegisterSerializer(
                new DenseDictionarySerializer<BlobId, SharedHeap.BlobInfo>()
            );
            RegisterBlit<SharedHeap.BlobInfo>(registry);

            registry.RegisterSerializer(new ListSerializer<object>());
            registry.RegisterSerializer(new BlobManifest.Serializer());
            registry.RegisterSerializer(new DenseDictionarySerializer<BlobId, BlobMetadata>());
            registry.RegisterSerializer(new BlobMetadata.Serializer());
            RegisterBlit<BlobId>(registry);

            // Entity serializers
            RegisterBlit<EntityHandleMapElement>(registry);
            RegisterBlit<EntityHandle>(registry);
            RegisterBlit<TagSet>(registry);
            // SetId is written by WorldStateSerializer.WriteSets /
            // WriteSetRoutingIndex unconditionally — register it here so
            // any world serialization works without callers having to
            // remember to register it themselves. Same for the per-set
            // entity-id-to-dense-index dictionary, written for each
            // group's entries.
            RegisterBlit<SetId>(registry);
            registry.RegisterSerializer(new NativeDenseDictionarySerializer<int, int>());

            // For EntityInputQueue
            registry.RegisterSerializer(new DenseHashSetSerializer<EntityHandle>());

            registry.RegisterSerializer(new RngSerializer());

            registry.RegisterSerializer(new NativeDenseDictionarySerializer<uint, uint>());
            registry.RegisterSerializer(new NativeArraySerializer<uint>());

            SnapshotMetadata.RegisterSerializers(registry);
            BundleHeader.RegisterSerializers(registry);
        }

        static void RegisterBlit<T>(SerializerRegistry registry, bool includeDelta = false)
            where T : unmanaged
        {
            var serializer = new BlitSerializer<T>();
            registry.RegisterSerializer(serializer);
            if (includeDelta)
            {
                registry.RegisterSerializerDelta(serializer);
            }
        }
    }
}
