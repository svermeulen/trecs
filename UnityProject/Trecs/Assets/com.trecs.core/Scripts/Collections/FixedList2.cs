using System.Runtime.CompilerServices;
using Trecs.Internal;

namespace Trecs.Collections
{
    /// <summary>
    /// A bounded list with compile-time capacity of 2 elements, stored inline.
    /// </summary>
    public struct FixedList2<T>
        where T : unmanaged
    {
        public FixedArray2<T> Buffer; // Raw storage — prefer the indexer / Mut() for bounds-checked access.

        int _count;

        public readonly int Capacity
        {
            [MethodImpl(MethodImplOptions.AggressiveInlining)]
            get => 2;
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
                Require.That(value >= 0 && value <= 2);
                _count = value;
            }
        }

        public readonly ref readonly T this[int index]
        {
            [MethodImpl(MethodImplOptions.AggressiveInlining)]
            get
            {
                Require.That(
                    index >= 0 && index < _count,
                    "Out of bound index (index: {}, count: {})",
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
                fixed (FixedArray2<T>* bufPtr = &Buffer)
                {
                    return UnmanagedUtil.BlittableHashCode(bufPtr, _count * sizeof(T));
                }
            }
        }

        public override readonly bool Equals(object obj)
        {
            return obj is FixedList2<T> other && this == other;
        }

        public static bool operator ==(in FixedList2<T> lhs, in FixedList2<T> rhs)
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
                ref T lhsRef = ref Unsafe.As<FixedArray2<T>, T>(ref Unsafe.AsRef(in lhs.Buffer));
                ref T rhsRef = ref Unsafe.As<FixedArray2<T>, T>(ref Unsafe.AsRef(in rhs.Buffer));

                void* lhsPtr = Unsafe.AsPointer(ref lhsRef);
                void* rhsPtr = Unsafe.AsPointer(ref rhsRef);

                return Unity.Collections.LowLevel.Unsafe.UnsafeUtility.MemCmp(
                        lhsPtr,
                        rhsPtr,
                        lhs.Count * sizeof(T)
                    ) == 0;
            }
        }

        public static bool operator !=(in FixedList2<T> lhs, in FixedList2<T> rhs)
        {
            return !(lhs == rhs);
        }
    }

    public static class FixedList2Extensions
    {
        /// <summary>
        /// Returns a mutable ref to element <paramref name="index"/>. Requires a
        /// mutable reference to the list, so cannot be called on `in` parameters.
        /// </summary>
        [MethodImpl(MethodImplOptions.AggressiveInlining)]
        public static ref T Mut<T>(this ref FixedList2<T> list, int index)
            where T : unmanaged
        {
            Require.That(
                index >= 0 && index < list.Count,
                "Out of bound index (index: {}, count: {})",
                index,
                list.Count
            );
            unsafe
            {
                return ref *((T*)Unsafe.AsPointer(ref list.Buffer) + index);
            }
        }

        [MethodImpl(MethodImplOptions.AggressiveInlining)]
        public static void Add<T>(this ref FixedList2<T> list, T item)
            where T : unmanaged
        {
            Require.That(list.Count < 2, "FixedList2 is full");
            list.Buffer.Mut(list.Count) = item;
            list.Count++;
        }

        [MethodImpl(MethodImplOptions.AggressiveInlining)]
        public static void Clear<T>(this ref FixedList2<T> list)
            where T : unmanaged
        {
            list.Count = 0;
        }

        [MethodImpl(MethodImplOptions.AggressiveInlining)]
        public static void RemoveAt<T>(this ref FixedList2<T> list, int index)
            where T : unmanaged
        {
            Require.That(index >= 0 && index < list.Count, "index out of bounds");
            int lastIndex = list.Count - 1;
            for (int i = index; i < lastIndex; i++)
            {
                list.Buffer.Mut(i) = list.Buffer[i + 1];
            }
            list.Count--;
        }

        [MethodImpl(MethodImplOptions.AggressiveInlining)]
        public static void RemoveAtSwapBack<T>(this ref FixedList2<T> list, int index)
            where T : unmanaged
        {
            Require.That(index >= 0 && index < list.Count, "index out of bounds");
            int lastIndex = list.Count - 1;
            if (index != lastIndex)
            {
                list.Buffer.Mut(index) = list.Buffer[lastIndex];
            }
            list.Count--;
        }
    }
}
