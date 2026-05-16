using System;
using System.ComponentModel;
using System.Runtime.CompilerServices;
using Trecs.Collections;
using Unity.Collections;
using Unity.Collections.LowLevel.Unsafe;

namespace Trecs.Internal
{
    static class ComponentArrayUtilities
    {
        internal static EntityIndexMapper<T> ToEntityIndexMapper<T>(
            this IComponentArray<T> dic,
            GroupIndex groupStructId
        )
            where T : unmanaged, IEntityComponent
        {
            var mapper = new EntityIndexMapper<T>(groupStructId, dic);

            return mapper;
        }
    }

    [EditorBrowsable(EditorBrowsableState.Never)]
    public sealed class ComponentArray<TValue> : IComponentArray<TValue>
        where TValue : unmanaged, IEntityComponent
    {
        NativeList<TValue> _values;
        int _count;

        public ComponentArray(int size)
        {
            _values = new NativeList<TValue>(size, Allocator.Persistent);
            _values.Resize(size, NativeArrayOptions.ClearMemory);
            _count = 0;
        }

        public int Count
        {
            [MethodImpl(MethodImplOptions.AggressiveInlining)]
            get => _count;
        }

        public Type ComponentType => typeof(TValue);

        [MethodImpl(MethodImplOptions.AggressiveInlining)]
        public NativeBuffer<TValue> GetValues(out int count)
        {
            count = _count;
            return new NativeBuffer<TValue>(_values);
        }

        [MethodImpl(MethodImplOptions.AggressiveInlining)]
        public ref TValue GetValueAtIndexByRef(int index)
        {
            unsafe
            {
                return ref UnsafeUtility.ArrayElementAsRef<TValue>(_values.GetUnsafePtr(), index);
            }
        }

        [MethodImpl(MethodImplOptions.AggressiveInlining)]
        public int Add(in TValue entityComponent)
        {
            var index = _count;

            if (_count >= _values.Length)
            {
                _values.Resize((int)((_count + 1) * 1.5f), NativeArrayOptions.UninitializedMemory);
            }

            unsafe
            {
                UnsafeUtility.ArrayElementAsRef<TValue>(_values.GetUnsafePtr(), _count) =
                    entityComponent;
            }
            _count++;

            return index;
        }

        [MethodImpl(MethodImplOptions.AggressiveInlining)]
        public IComponentArray Create()
        {
            return new ComponentArray<TValue>(1);
        }

        [MethodImpl(MethodImplOptions.AggressiveInlining)]
        public void Clear()
        {
            _count = 0;
        }

        internal NativeList<TValue> RawValues => _values;

        public int ElementSize
        {
            [MethodImpl(MethodImplOptions.AggressiveInlining)]
            get => UnsafeUtility.SizeOf<TValue>();
        }

        [MethodImpl(MethodImplOptions.AggressiveInlining)]
        public unsafe void* GetUnsafePtr()
        {
            return _values.GetUnsafePtr();
        }

        [MethodImpl(MethodImplOptions.AggressiveInlining)]
        public void SetCount(int count)
        {
            _count = count;
        }

        internal void ForceSetCount(int count)
        {
            _count = count;
        }

        [MethodImpl(MethodImplOptions.AggressiveInlining)]
        public void ReorderRange(int startIndex, int count, NativeList<int> permutation)
        {
            if (count <= 1)
            {
                return;
            }

            unsafe
            {
                var elementSize = UnsafeUtility.SizeOf<TValue>();
                var ptr = (byte*)_values.GetUnsafePtr();
                var rangePtr = ptr + startIndex * elementSize;

                // Copy the range to a temp buffer
                var temp = new NativeArray<TValue>(
                    count,
                    Allocator.Temp,
                    NativeArrayOptions.UninitializedMemory
                );
                UnsafeUtility.MemCpy(temp.GetUnsafePtr(), rangePtr, count * elementSize);

                // Scatter-write from temp back using permutation
                var tempPtr = (byte*)temp.GetUnsafeReadOnlyPtr();
                for (int i = 0; i < count; i++)
                {
                    UnsafeUtility.MemCpy(
                        rangePtr + i * elementSize,
                        tempPtr + permutation[i] * elementSize,
                        elementSize
                    );
                }

                temp.Dispose();
            }
        }

        [MethodImpl(MethodImplOptions.AggressiveInlining)]
        public void EnsureCapacity(int size)
        {
            if (size > _values.Length)
            {
                _values.Resize(size, NativeArrayOptions.UninitializedMemory);
            }
        }

        public void Dispose()
        {
            _values.Dispose();
            _count = 0;

            GC.SuppressFinalize(this);
        }

        /// *********************************
        /// the following methods are executed during the submission of entities
        /// *********************************
        [MethodImpl(MethodImplOptions.AggressiveInlining)]
        public void AddEntitiesToDictionary(IComponentArray toDictionary, GroupIndex groupId)
        {
            if (_count == 0)
            {
                return;
            }

            var toDic = (ComponentArray<TValue>)toDictionary;
            var newCount = toDic._count + _count;
            toDic.EnsureCapacity(newCount);

            unsafe
            {
                var elementSize = UnsafeUtility.SizeOf<TValue>();
                UnsafeUtility.MemCpy(
                    (byte*)toDic._values.GetUnsafePtr() + toDic._count * elementSize,
                    _values.GetUnsafeReadOnlyPtr(),
                    _count * elementSize
                );
            }

            toDic._count = newCount;
        }

        /// <summary>
        /// Remove entities using swap-back. Indices must be sorted descending so that
        /// each removal's swap-back only affects positions above the next removal index,
        /// eliminating the need for transitive chain resolution.
        /// Removed values are placed at the tail of the array (past the new count)
        /// so that remove callbacks can still access them.
        /// </summary>
        [MethodImpl(MethodImplOptions.AggressiveInlining)]
        public void RemoveEntitiesFromArray(FastList<int> sortedDescendingIndices)
        {
            var iterations = sortedDescendingIndices.Count;

            for (var i = 0; i < iterations; i++)
            {
                var indexToRemove = sortedDescendingIndices[i];

                TrecsAssert.That(
                    indexToRemove < _count,
                    "Removing an entity at an index that is out of range"
                );

                var lastIndex = _count - 1;

                unsafe
                {
                    var ptr = _values.GetUnsafePtr();
                    var removedValue = UnsafeUtility.ArrayElementAsRef<TValue>(ptr, indexToRemove);

                    if (indexToRemove != lastIndex)
                    {
                        // Swap-back: move last element into the removed slot
                        UnsafeUtility.ArrayElementAsRef<TValue>(ptr, indexToRemove) =
                            UnsafeUtility.ArrayElementAsRef<TValue>(ptr, lastIndex);
                    }

                    // Place the removed value at the end so it's accessible for remove callbacks
                    // (they iterate from count to count + numRemoved)
                    UnsafeUtility.ArrayElementAsRef<TValue>(ptr, lastIndex) = removedValue;
                }

                _count--;
            }
        }

        /// <summary>
        /// Move entities from this array to the destination using swap-back.
        /// Each MoveInfo.ResolvedFromIndex must be pre-set by the caller to the entity's
        /// current position after accounting for prior swap-backs.
        /// This eliminates per-component-type chain resolution.
        /// The destination is written via direct memcpy after gathering values,
        /// avoiding per-element Add() bounds checks.
        /// </summary>
        [MethodImpl(MethodImplOptions.AggressiveInlining)]
        public void SwapEntitiesBetweenDictionaries(
            in DenseDictionary<int, MoveInfo> entitiesIDsToSwap,
            GroupIndex fromgroup,
            GroupIndex togroup,
            IComponentArray toComponentsDictionary
        )
        {
            var toDic = (ComponentArray<TValue>)toComponentsDictionary;

            var iterations = entitiesIDsToSwap.Count;
            var entitiesToSwapInfo = entitiesIDsToSwap.UnsafeValues;

            // The caller has already called EnsureCapacity on toDic.
            // Pre-compute destination base index so we can set toIndex without calling Add().
            var destBase = toDic._count;

            unsafe
            {
                var srcPtr = _values.GetUnsafePtr();
                var dstPtr = toDic._values.GetUnsafePtr();
                var elementSize = UnsafeUtility.SizeOf<TValue>();

                for (var i = 0; i < iterations; i++)
                {
                    ref MoveInfo swapInfo = ref entitiesToSwapInfo[i];
                    var indexToRemove = swapInfo.ResolvedFromIndex;

                    TrecsAssert.That(
                        indexToRemove < _count,
                        "Swapping an entity at an index that is out of range"
                    );

                    var lastIndex = _count - 1;

                    // Copy value to destination via direct pointer write
                    UnsafeUtility.MemCpy(
                        (byte*)dstPtr + (destBase + i) * elementSize,
                        (byte*)srcPtr + indexToRemove * elementSize,
                        elementSize
                    );

                    if (indexToRemove != lastIndex)
                    {
                        // Swap-back: move last element into the removed slot
                        UnsafeUtility.MemCpy(
                            (byte*)srcPtr + indexToRemove * elementSize,
                            (byte*)srcPtr + lastIndex * elementSize,
                            elementSize
                        );
                    }

                    _count--;

                    // Set destination index directly (sequential from destBase)
                    swapInfo.ToIndex = destBase + i;
                }
            }

            toDic._count = destBase + iterations;
        }
    }
}
