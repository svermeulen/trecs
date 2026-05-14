using System;
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

    // Have this separate from TypeMeta so it can be used in burst code
    public static class TypeHash<T>
    {
        public static readonly int Value = BurstRuntime.GetHashCode32<T>();
    }
}
