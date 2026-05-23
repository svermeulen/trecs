using System;
using Unity.Collections.LowLevel.Unsafe;

namespace Trecs.Internal
{
    public static class TypeMeta<T>
    {
        public static readonly Type Type = typeof(T);
        public static readonly string Name = Type.Name;
        public static readonly bool IsUnmanaged = UnsafeUtility.IsUnmanaged<T>();
    }
}
