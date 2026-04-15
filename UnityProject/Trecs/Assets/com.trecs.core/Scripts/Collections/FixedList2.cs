using System;
using System.Runtime.CompilerServices;
using Trecs.Internal;

namespace Trecs.Collections
{
    /// <summary>
    /// This just wraps FixedArray2, adds a count, and does bounds checking
    /// </summary>
    public struct FixedList2<T>
        where T : unmanaged
    {
        public FixedArray2<T> Buffer; // Don't access directly

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

        /// Do not use this if T is very large since it copies
        public T this[int index]
        {
            readonly get
            {
                Require.That(
                    index >= 0 && index < _count,
                    "Out of bound index (index: {}, count: {})",
                    index,
                    _count
                );
                return Buffer[index];
            }
            [MethodImpl(MethodImplOptions.AggressiveInlining)]
            set
            {
                Require.That(
                    index >= 0 && index < _count,
                    "Out of bound index (index: {}, count: {})",
                    index,
                    _count
                );
                Buffer[index] = value;
            }
        }

        public override int GetHashCode()
        {
            throw new NotImplementedException();
        }

        public override readonly bool Equals(object obj)
        {
            FixedTypeCommon.Log.Warning("Used object Equals on FixedList2, causing boxing");
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
        [MethodImpl(MethodImplOptions.AggressiveInlining)]
        public static ref T GetRef<T>(this ref FixedList2<T> pose, int index)
            where T : unmanaged
        {
            Require.That((int)index >= 0 && (int)index < pose.Count);
            return ref pose.Buffer.GetRef(index);
        }

        [MethodImpl(MethodImplOptions.AggressiveInlining)]
        public static ref readonly T Get<T>(this in FixedList2<T> pose, int index)
            where T : unmanaged
        {
            Require.That(
                (int)index >= 0 && (int)index < pose.Count,
                "Index {} out of bounds for FixedList2 of count {}",
                index,
                pose.Count
            );
            return ref pose.Buffer.Get(index);
        }

        [MethodImpl(MethodImplOptions.AggressiveInlining)]
        public static void Add<T>(this ref FixedList2<T> list, T item)
            where T : unmanaged
        {
            Require.That(list.Count < 2, "FixedList2 is full");
            list.Buffer[list.Count] = item;
            list.Count++;
        }

        [MethodImpl(MethodImplOptions.AggressiveInlining)]
        public static void Clear<T>(this ref FixedList2<T> list)
            where T : unmanaged
        {
            list.Count = 0;
        }

        [MethodImpl(MethodImplOptions.AggressiveInlining)]
        public static void RemoveAtSwapBack<T>(this ref FixedList2<T> list, int index)
            where T : unmanaged
        {
            Require.That(index >= 0 && index < list.Count, "index out of bounds");
            int lastIndex = list.Count - 1;
            if (index != lastIndex)
            {
                list.Buffer[index] = list.Buffer[lastIndex];
            }
            list.Count--;
        }
    }
}
