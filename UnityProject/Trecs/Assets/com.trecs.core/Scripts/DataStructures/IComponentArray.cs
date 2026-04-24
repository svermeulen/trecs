using System;
using System.ComponentModel;
using Trecs.Collections;
using Unity.Collections;

namespace Trecs.Internal
{
    [EditorBrowsable(EditorBrowsableState.Never)]
    public interface IComponentArray<TValue> : IComponentArray
        where TValue : unmanaged, IEntityComponent
    {
        int Add(in TValue entityComponent);

        NativeBuffer<TValue> GetValues(out int count);
        ref TValue GetValueAtIndexByRef(int index);
    }

    [EditorBrowsable(EditorBrowsableState.Never)]
    public interface IComponentArray : IDisposable
    {
        int Count { get; }

        IComponentArray Create();

        void AddEntitiesToDictionary(IComponentArray toDictionary, GroupIndex groupId);

        /// <summary>
        /// Remove entities at the given indices from the array using swap-back.
        /// Indices must be sorted in descending order so that swap-back never invalidates
        /// a future removal index, eliminating chain resolution.
        /// </summary>
        void RemoveEntitiesFromArray(FastList<int> sortedDescendingIndices);

        /// <summary>
        /// Move entities from this array to the destination array using swap-back.
        /// Each MoveInfo must have resolvedFromIndex pre-set to the entity's current
        /// position after accounting for prior swap-backs (precomputed by the caller).
        /// </summary>
        void SwapEntitiesBetweenDictionaries(
            in DenseDictionary<int, MoveInfo> infosToProcess,
            GroupIndex fromGroup,
            GroupIndex toGroup,
            IComponentArray toComponentsDictionary
        );

        void EnsureCapacity(int size);
        void Clear();

        /// <summary>
        /// Reorder a contiguous range of elements [startIndex, startIndex+count) according
        /// to the given permutation. permutation[i] gives the source index (relative to
        /// startIndex) for destination position startIndex+i.
        /// </summary>
        void ReorderRange(int startIndex, int count, NativeList<int> permutation);

        /// <summary>
        /// Returns the element size in bytes and the unsafe pointer to the underlying
        /// native buffer. Used by Burst jobs to perform type-erased data movement.
        /// </summary>
        int ElementSize { get; }

        /// <summary>
        /// Returns the raw pointer to the underlying NativeList buffer.
        /// Only valid after EnsureCapacity has been called (no reallocation will occur).
        /// </summary>
        unsafe void* GetUnsafePtr();

        /// <summary>
        /// Set the count directly. Used after Burst jobs to update the managed count
        /// to reflect data movement performed by the job.
        /// </summary>
        void SetCount(int count);
    }
}
