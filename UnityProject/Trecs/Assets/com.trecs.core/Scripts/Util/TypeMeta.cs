using System;
using System.Collections.Generic;
using Unity.Burst;
using Unity.Collections.LowLevel.Unsafe;

namespace Trecs.Internal
{
    public static class TypeMeta<T>
    {
        public static readonly Type Type = typeof(T);
        public static readonly string Name = Type.Name;
        public static readonly bool IsUnmanaged = UnsafeUtility.IsUnmanaged<T>();
        public static readonly int Hash = TypeHash<T>.Value;
    }

    // Non generic version
    // prefer the generic version when possible
    public static class TypeMeta
    {
        class CachedTypeInfo
        {
            public string Name;
            public int Hash;
        }

        static readonly Dictionary<Type, CachedTypeInfo> _typeInfos = new();

        static readonly Dictionary<int, Type> _hashToTypeMap = new();

        public static string GetName(Type type)
        {
            return GetInfo(type).Name;
        }

        public static int GetHash(Type type)
        {
            return GetInfo(type).Hash;
        }

        public static void Warmup(Type type)
        {
            GetInfo(type);
        }

        // NOTE! This only works if GetInfo is called first
        public static Type GetTypeFromHash(int hash)
        {
            if (_hashToTypeMap.TryGetValue(hash, out var type))
            {
                return type;
            }

            throw TrecsAssert.CreateException("Unable to find type for given hash");
        }

        static CachedTypeInfo GetInfo(Type type)
        {
            if (!_typeInfos.TryGetValue(type, out var info))
            {
                info = new CachedTypeInfo
                {
                    Name = type.Name,
                    Hash = BurstRuntime.GetHashCode32(type),
                };

                _typeInfos.Add(type, info);
                _hashToTypeMap.Add(info.Hash, type);
            }

            return info;
        }
    }

    // Have this separate from TypeMeta so it can be used in burst code
    public static class TypeHash<T>
    {
        public static readonly int Value = BurstRuntime.GetHashCode32<T>();
    }
}
