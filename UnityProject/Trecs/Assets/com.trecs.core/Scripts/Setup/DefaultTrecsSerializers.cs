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

            registry.RegisterSerializer<BoolSerializer>();
            registry.RegisterSerializer<TypeSerializer>();
            registry.RegisterSerializer<StringSerializer>();
        }

        static void RegisterTrecsSerializers(SerializerRegistry registry)
        {
            RegisterBlit<ComponentId>(registry);

            // Heap serializers
            registry.RegisterSerializer<
                DenseDictionarySerializer<BlobId, NativeSharedHeap.BlobInfo>
            >();
            registry.RegisterSerializer<DenseDictionarySerializer<PtrHandle, BlobId>>();
            RegisterBlit<PtrHandle>(registry);
            RegisterBlit<NativeSharedHeap.BlobInfo>(registry);

            registry.RegisterSerializer<DenseDictionarySerializer<BlobId, SharedHeap.BlobInfo>>();
            RegisterBlit<SharedHeap.BlobInfo>(registry);

            registry.RegisterSerializer<ListSerializer<object>>();
            registry.RegisterSerializer<BlobManifest.Serializer>();
            registry.RegisterSerializer<DenseDictionarySerializer<BlobId, BlobMetadata>>();
            registry.RegisterSerializer<BlobMetadata.Serializer>();
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
            registry.RegisterSerializer<NativeDenseDictionarySerializer<int, int>>();

            // For EntityInputQueue
            registry.RegisterSerializer<DenseHashSetSerializer<EntityHandle>>();

            registry.RegisterSerializer<RngSerializer>();

            registry.RegisterSerializer<NativeDenseDictionarySerializer<uint, uint>>();
            registry.RegisterSerializer<NativeArraySerializer<uint>>();

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
