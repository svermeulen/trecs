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

        static void RegisterTrecsSerializers(SerializerRegistry registry)
        {
            registry.RegisterBlit<ComponentId>();

            // Heap serializers
            registry.RegisterSerializer<
                DenseDictionarySerializer<BlobId, NativeSharedHeap.BlobInfo>
            >();
            registry.RegisterSerializer<DenseDictionarySerializer<PtrHandle, BlobId>>();
            registry.RegisterBlit<PtrHandle>();
            registry.RegisterBlit<NativeSharedHeap.BlobInfo>();

            registry.RegisterSerializer<DenseDictionarySerializer<BlobId, SharedHeap.BlobInfo>>();
            registry.RegisterBlit<SharedHeap.BlobInfo>();

            registry.RegisterSerializer<ListSerializer<object>>();
            registry.RegisterSerializer<BlobManifest.Serializer>();
            registry.RegisterSerializer<DenseDictionarySerializer<BlobId, BlobMetadata>>();
            registry.RegisterSerializer<BlobMetadata.Serializer>();
            registry.RegisterBlit<BlobId>();

            // Entity serializers
            registry.RegisterBlit<EntityHandleMapElement>();
            registry.RegisterBlit<EntityHandle>();
            registry.RegisterBlit<TagSet>();
            // SetId is written by WorldStateSerializer.WriteSets /
            // WriteSetRoutingIndex unconditionally — register it here so
            // any world serialization works without callers having to
            // remember to register it themselves. Same for the per-set
            // entity-id-to-dense-index dictionary, written for each
            // group's entries.
            registry.RegisterBlit<SetId>();
            registry.RegisterSerializer<NativeDenseDictionarySerializer<int, int>>();

            // For EntityInputQueue
            registry.RegisterSerializer<DenseHashSetSerializer<EntityHandle>>();

            registry.RegisterSerializer<RngSerializer>();

            registry.RegisterSerializer<NativeDenseDictionarySerializer<uint, uint>>();
            registry.RegisterSerializer<NativeArraySerializer<uint>>();

            SnapshotMetadata.RegisterSerializers(registry);
            BundleHeader.RegisterSerializers(registry);
        }
    }
}
