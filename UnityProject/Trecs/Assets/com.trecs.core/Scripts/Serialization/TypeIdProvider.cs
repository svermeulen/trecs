using System;
using System.Collections.Generic;
using System.ComponentModel;
using System.Reflection;
using Trecs.Collections;
using Unity.Collections;
using Unity.Mathematics;
using UnityEngine;

namespace Trecs.Internal
{
    [EditorBrowsable(EditorBrowsableState.Never)]
    public static class TypeIdProvider
    {
        static readonly DenseDictionary<Type, int> _cache = new();
        static readonly DenseDictionary<int, Type> _reverseCache = new();

        static TypeIdProvider()
        {
            // Pre-register explicit IDs for common types so that projects using
            // TRECS_REQUIRE_EXPLICIT_TYPE_IDS don't need to register these manually,
            // and so that IDs are consistent regardless of registration order.

            // Primitives
            Register<float>(432190420);
            Register<byte>(381917204);
            Register<sbyte>(539120847);
            Register<short>(629841073);
            Register<ushort>(517293846);
            Register<int>(294710538);
            Register<uint>(851637492);
            Register<ulong>(763284105);
            Register<long>(408526931);
            Register<double>(195847362);
            Register<decimal>(674031285);
            Register<bool>(182867271);
            Register<Type>(172575652);
            Register<string>(941382057);
            Register<object>(196483075);

            // Unity types
            Register<Vector3>(148593027);
            Register<Vector4>(687320941);
            Register<float2>(831746209);
            Register<int2>(726150483);
            Register<float3>(259483710);
            Register<quaternion>(903172654);

            // Trecs.Internal types
            Register<Rng>(392558428);

            // Nested types that can't have [TypeId] attributes
            Register<NativeSharedHeap.BlobInfo>(815203647);
            Register<SharedHeap.BlobInfo>(493760182);

            // Generic type definitions
            Register(typeof(List<>), 374829150);
            Register(typeof(Dictionary<,>), 819463257);
            Register(typeof(DenseDictionary<,>), 518072634);
            Register(typeof(NativeDenseDictionary<,>), 940316758);
            Register(typeof(DenseHashSet<>), 682415093);
            Register(typeof(NativeArray<>), 357928041);
        }

        public static int GetTypeId(Type type)
        {
            Assert.That(UnityThreadHelper.IsMainThread);
            if (_cache.TryGetValue(type, out var cachedId))
            {
                return cachedId;
            }

            // For constructed generic types, compose the definition's ID with each type argument's ID
            if (type.IsGenericType && !type.IsGenericTypeDefinition)
            {
                int compositeId = GetTypeId(type.GetGenericTypeDefinition());
                foreach (var arg in type.GetGenericArguments())
                {
                    compositeId = DenseHashUtil.CombineHashes(compositeId, GetTypeId(arg));
                }
                Register(type, compositeId);
                return compositeId;
            }

            var attr = type.GetCustomAttribute<TypeIdAttribute>();

            if (attr != null)
            {
                Register(type, attr.Id);
                return attr.Id;
            }

            // For projects that really want to ensure that serialized data is backwards compatible, they
            // can set TRECS_REQUIRE_EXPLICIT_TYPE_IDS, then any refactors involving type names or namespaces
            // will never corrupt save files
            // And any data changes can have custom logic in custom serializers to do the upgrade during load
#if TRECS_REQUIRE_EXPLICIT_TYPE_IDS
            // Throw instead of warning to avoid corrupting save files
            throw Assert.CreateException(
                "Type {} does not have an explicit ID, but TRECS_REQUIRE_EXPLICIT_TYPE_IDS is defined. Either add a [SerializationId] attribute to the type, or remove the TRECS_REQUIRE_EXPLICIT_TYPE_IDS define.",
                type
            );
#else
            var id = DenseHashUtil.StableStringHash(type.FullName);

            if (id == 0)
            {
                // reserve 0 to mean uninitialized
                id = 1;
            }

            Register(type, id);
            return id;
#endif
        }

        public static void Register(Type type)
        {
            var _ = GetTypeId(type);
        }

        public static bool IsRegistered(Type type)
        {
            return _cache.ContainsKey(type);
        }

        public static void Register(Type type, int id)
        {
            Assert.That(UnityThreadHelper.IsMainThread);
            if (_cache.TryGetValue(type, out var existingId))
            {
                Assert.That(
                    existingId == id,
                    "Attempted to register type {} with ID {}, but it is already registered with ID {}.",
                    type,
                    id,
                    existingId
                );

                Assert.That(_reverseCache[existingId] == type);
                return;
            }

            if (_reverseCache.TryGetValue(id, out var existingType))
            {
                Assert.That(
                    existingType == type,
                    "TypeId collision: {} and {} both resolve to ID {}. Use [TypeId] to assign explicit IDs.",
                    type.FullName,
                    existingType.FullName,
                    id
                );
            }

            _cache.Add(type, id);
            _reverseCache.Add(id, type);
        }

        public static void Register<T>(int id)
        {
            Register(typeof(T), id);
        }

        public static void Register<T>()
        {
            Register(typeof(T));
        }

        public static int GetTypeId<T>()
        {
            return GetTypeId(typeof(T));
        }

        public static Type GetTypeFromId(int id)
        {
            Assert.That(UnityThreadHelper.IsMainThread);
            if (_reverseCache.TryGetValue(id, out var type))
            {
                return type;
            }

            throw Assert.CreateException("Unrecognized type ID {}", id);
        }
    }
}
