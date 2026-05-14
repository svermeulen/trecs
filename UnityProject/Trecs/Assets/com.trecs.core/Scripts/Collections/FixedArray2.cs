using System.Runtime.CompilerServices;
using Trecs.Internal;

namespace Trecs.Collections
{
    // Fixed size collection
    // Useful for ECS systems where we want everything inside a struct
    public struct FixedArray2<T>
        where T : unmanaged
    {
        const int _length = 2;

#pragma warning disable CS0169
        T field0;
        T field1;
#pragma warning restore CS0169

        public readonly int Length => _length;

        // Readonly indexer returning `ref readonly T`. This shape is deliberate:
        // - On mutable instances, reads via `arr[i]` are ergonomic.
        // - On `in` parameters, the readonly modifier skips the defensive copy,
        //   and the `ref readonly T` return prevents silent mutation.
        // Writes go through the `Mut` extension method, which requires `ref` to
        //   the array and therefore cannot be called on `in` parameters.
        public readonly ref readonly T this[int index]
        {
            [MethodImpl(MethodImplOptions.AggressiveInlining)]
            get
            {
                TrecsRequire.That(index >= 0 && index < _length, "out of bound index");
                unsafe
                {
                    return ref *((T*)Unsafe.AsPointer(ref Unsafe.AsRef(in this)) + index);
                }
            }
        }

        public override readonly bool Equals(object obj)
        {
            return obj is FixedArray2<T> other && this == other;
        }

        public override readonly int GetHashCode()
        {
            return UnmanagedUtil.BlittableHashCode(this);
        }

        public static bool operator ==(in FixedArray2<T> left, in FixedArray2<T> right)
        {
            return UnmanagedUtil.BlittableEquals(left, right);
        }

        public static bool operator !=(in FixedArray2<T> left, in FixedArray2<T> right)
        {
            return !UnmanagedUtil.BlittableEquals(left, right);
        }
    }

    public static class FixedArray2Extensions
    {
        /// <summary>
        /// Returns a mutable ref to element <paramref name="index"/>. Requires a
        /// mutable reference to the array, so cannot be called on `in` parameters.
        /// </summary>
        [MethodImpl(MethodImplOptions.AggressiveInlining)]
        public static ref T Mut<T>(this ref FixedArray2<T> arr, int index)
            where T : unmanaged
        {
            TrecsRequire.That(index >= 0 && index < 2, "out of bound index");
            unsafe
            {
                return ref *((T*)Unsafe.AsPointer(ref arr) + index);
            }
        }
    }
}
