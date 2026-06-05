using Trecs.Serialization;
using Unity.Mathematics;
using UnityEngine;

namespace Trecs.Internal
{
    /// <summary>
    /// Registers the serializers for the common trecs core types (ids, math types, collections, …)
    /// into a <see cref="SerializerRegistry"/>. Public so external tooling — e.g. an editor bake
    /// pipeline — can stand up a registry able to (de)serialize core types without constructing a
    /// full world.
    /// </summary>
    public static class DefaultTrecsSerializers
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
            RegisterBlit<TypeId>(registry);

            // Stamped into snapshot metadata + bundle headers; validated on load.
            RegisterBlit<WorldSchemaFingerprint>(registry);

            // Heap serializers
            registry.RegisterSerializer<IterableDictionarySerializerUnmanaged<PtrHandle, BlobId>>();
            RegisterBlit<PtrHandle>(registry);

            registry.RegisterSerializer<
                IterableDictionarySerializerUnmanaged<BlobId, SharedHeap.BlobInfo>
            >();
            RegisterBlit<SharedHeap.BlobInfo>(registry);

            registry.RegisterSerializer<ListSerializerManaged<object>>();
            RegisterBlit<BlobId>(registry);

            // Entity serializers
            RegisterBlit<EntityHandleMapElement>(registry);
            RegisterBlit<EntityHandle>(registry);
            RegisterBlit<TagSet>(registry);
            // SetId and the per-set entity-id-to-dense-index dictionary
            // are written by SetStore.Serialize unconditionally.
            RegisterBlit<SetId>(registry);
            registry.RegisterSerializer<NativeIterableDictionarySerializer<int, int>>();

            // For EntityInputQueue
            registry.RegisterSerializer<IterableHashSetSerializer<EntityHandle>>();

            registry.RegisterSerializer<RngSerializer>();

            registry.RegisterSerializer<NativeIterableDictionarySerializer<uint, uint>>();
            registry.RegisterSerializer<NativeArraySerializer<uint>>();

            // Per-group Refs list in WorldStateSerializer's
            // EntityIndexToReferenceMap.
            registry.RegisterSerializer<UnsafeListSerializer<int>>();
            // Per-group SetIds list in WorldStateSerializer's set routing
            // index.
            registry.RegisterSerializer<UnsafeListSerializer<SetId>>();
            // Entity-handle map backing buffer in WorldStateSerializer.
            registry.RegisterSerializer<NativeListSerializer<EntityHandleMapElement>>();

            SnapshotMetadata.RegisterSerializers(registry);
            BundleHeader.RegisterSerializers(registry);
        }

        static void RegisterBlit<T>(SerializerRegistry registry, bool includeDelta = false)
            where T : unmanaged
        {
            registry.RegisterSerializer<BlitSerializer<T>>();
            if (includeDelta)
            {
                registry.RegisterSerializerDelta<BlitSerializer<T>>();
            }
        }
    }
}
