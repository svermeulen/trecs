using System.Runtime.CompilerServices;
using Trecs.Internal;
using Unity.Collections.LowLevel.Unsafe;

namespace Trecs.Collections
{
    /// <summary>
    /// A bounded list with compile-time capacity of 256 elements, stored inline.
    /// </summary>
    public struct FixedList256<T>
        where T : unmanaged
    {
        public FixedArray256<T> Buffer; // Raw storage — prefer the indexer / Mut() for bounds-checked access.

        int _count;

        public readonly int Capacity
        {
            [MethodImpl(MethodImplOptions.AggressiveInlining)]
            get => 256;
        }

        public readonly bool IsEmpty
        {
            [MethodImpl(MethodImplOptions.AggressiveInlining)]
            get => _count == 0;
        }

        public int Count
        {
            [MethodImpl(MethodImplOptions.AggressiveInlining)]
            readonly get => _count;
            [MethodImpl(MethodImplOptions.AggressiveInlining)]
            set
            {
                TrecsRequire.That(value >= 0 && value <= 256);
                _count = value;
            }
        }

        public readonly ref readonly T this[int index]
        {
            [MethodImpl(MethodImplOptions.AggressiveInlining)]
            get
            {
                TrecsRequire.That(
                    index >= 0 && index < _count,
                    "Out of bound index (index: {0}, count: {1})",
                    index,
                    _count
                );
                unsafe
                {
                    return ref *((T*)Unsafe.AsPointer(ref Unsafe.AsRef(in Buffer)) + index);
                }
            }
        }

        public override readonly int GetHashCode()
        {
            unsafe
            {
                fixed (FixedArray256<T>* bufPtr = &Buffer)
                {
                    return UnmanagedUtil.BlittableHashCode(bufPtr, _count * sizeof(T));
                }
            }
        }

        public override readonly bool Equals(object obj)
        {
            return obj is FixedList256<T> other && this == other;
        }

        public static bool operator ==(in FixedList256<T> lhs, in FixedList256<T> rhs)
        {
            if (lhs.Count != rhs.Count)
            {
                return false;
            }

            if (lhs.Count == 0)
            {
                return true;
            }

            unsafe
            {
                ref T lhsRef = ref Unsafe.As<FixedArray256<T>, T>(ref Unsafe.AsRef(in lhs.Buffer));
                ref T rhsRef = ref Unsafe.As<FixedArray256<T>, T>(ref Unsafe.AsRef(in rhs.Buffer));

                void* lhsPtr = Unsafe.AsPointer(ref lhsRef);
                void* rhsPtr = Unsafe.AsPointer(ref rhsRef);

                return UnsafeUtility.MemCmp(lhsPtr, rhsPtr, lhs.Count * sizeof(T)) == 0;
            }
        }

        public static bool operator !=(in FixedList256<T> lhs, in FixedList256<T> rhs)
        {
            return !(lhs == rhs);
        }
    }

    public static class FixedList256Extensions
    {
        [MethodImpl(MethodImplOptions.AggressiveInlining)]
        public static ref T Mut<T>(this ref FixedList256<T> list, int index)
            where T : unmanaged
        {
            TrecsRequire.That(
                index >= 0 && index < list.Count,
                "Out of bound index (index: {0}, count: {1})",
                index,
                list.Count
            );
            unsafe
            {
                return ref *((T*)Unsafe.AsPointer(ref list.Buffer) + index);
            }
        }

        [MethodImpl(MethodImplOptions.AggressiveInlining)]
        public static void Add<T>(this ref FixedList256<T> list, T item)
            where T : unmanaged
        {
            TrecsRequire.That(list.Count < 256, "FixedList256 is full");
            list.Buffer.Mut(list.Count) = item;
            list.Count++;
        }

        [MethodImpl(MethodImplOptions.AggressiveInlining)]
        public static void Clear<T>(this ref FixedList256<T> list)
            where T : unmanaged
        {
            list.Count = 0;
        }

        [MethodImpl(MethodImplOptions.AggressiveInlining)]
        public static void RemoveAt<T>(this ref FixedList256<T> list, int index)
            where T : unmanaged
        {
            TrecsRequire.That(index >= 0 && index < list.Count, "index out of bounds");
            int lastIndex = list.Count - 1;
            for (int i = index; i < lastIndex; i++)
            {
                list.Buffer.Mut(i) = list.Buffer[i + 1];
            }
            list.Count--;
        }

        [MethodImpl(MethodImplOptions.AggressiveInlining)]
        public static void RemoveAtSwapBack<T>(this ref FixedList256<T> list, int index)
            where T : unmanaged
        {
            TrecsRequire.That(index >= 0 && index < list.Count, "index out of bounds");
            int lastIndex = list.Count - 1;
            if (index != lastIndex)
            {
                list.Buffer.Mut(index) = list.Buffer[lastIndex];
            }
            list.Count--;
        }
    }
}
