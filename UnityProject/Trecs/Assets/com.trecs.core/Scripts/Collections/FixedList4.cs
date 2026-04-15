using System;
using System.Runtime.CompilerServices;
using Trecs.Internal;

namespace Trecs.Collections
{
    /// <summary>
    /// This just wraps FixedArray4, adds a count, and does bounds checking
    /// </summary>
    public struct FixedList4<T>
        where T : unmanaged
    {
        public FixedArray4<T> Buffer; // Don't access directly

        int _count;

        public readonly int Capacity
        {
            [MethodImpl(MethodImplOptions.AggressiveInlining)]
            get => 4;
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
                Require.That(value >= 0 && value <= 4);
                _count = value;
            }
        }

        /// Do not use this if T is very large since it copies
        public T this[int index]
        {
            readonly get
            {
                Require.That(index >= 0 && index < _count, "out of bound index");
                return Buffer[index];
            }
            [MethodImpl(MethodImplOptions.AggressiveInlining)]
            set
            {
                Require.That(index >= 0 && index < _count, "out of bound index");
                Buffer[index] = value;
            }
        }

        public override int GetHashCode()
        {
            throw new NotImplementedException();
        }

        public override readonly bool Equals(object obj)
        {
            FixedTypeCommon.Log.Warning("Used object Equals on FixedList4, causing boxing");
            return obj is FixedList4<T> other && this == other;
        }

        public static bool operator ==(in FixedList4<T> lhs, in FixedList4<T> rhs)
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
                ref T lhsRef = ref Unsafe.As<FixedArray4<T>, T>(ref Unsafe.AsRef(in lhs.Buffer));
                ref T rhsRef = ref Unsafe.As<FixedArray4<T>, T>(ref Unsafe.AsRef(in rhs.Buffer));

                void* lhsPtr = Unsafe.AsPointer(ref lhsRef);
                void* rhsPtr = Unsafe.AsPointer(ref rhsRef);

                return Unity.Collections.LowLevel.Unsafe.UnsafeUtility.MemCmp(
                        lhsPtr,
                        rhsPtr,
                        lhs.Count * sizeof(T)
                    ) == 0;
            }
        }

        public static bool operator !=(in FixedList4<T> lhs, in FixedList4<T> rhs)
        {
            return !(lhs == rhs);
        }
    }

    public static class FixedList4Extensions
    {
        [MethodImpl(MethodImplOptions.AggressiveInlining)]
        public static ref T GetRef<T>(this ref FixedList4<T> pose, int index)
            where T : unmanaged
        {
            Require.That((int)index >= 0 && (int)index < pose.Count);
            return ref pose.Buffer.GetRef(index);
        }

        [MethodImpl(MethodImplOptions.AggressiveInlining)]
        public static ref readonly T Get<T>(this in FixedList4<T> pose, int index)
            where T : unmanaged
        {
            Require.That((int)index >= 0 && (int)index < pose.Count);
            return ref pose.Buffer.Get(index);
        }

        [MethodImpl(MethodImplOptions.AggressiveInlining)]
        public static void Add<T>(this ref FixedList4<T> list, T item)
            where T : unmanaged
        {
            Require.That(list.Count < 4, "FixedList4 is full");
            list.Buffer[list.Count] = item;
            list.Count++;
        }

        [MethodImpl(MethodImplOptions.AggressiveInlining)]
        public static void Clear<T>(this ref FixedList4<T> list)
            where T : unmanaged
        {
            list.Count = 0;
        }

        [MethodImpl(MethodImplOptions.AggressiveInlining)]
        public static void RemoveAtSwapBack<T>(this ref FixedList4<T> list, int index)
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
