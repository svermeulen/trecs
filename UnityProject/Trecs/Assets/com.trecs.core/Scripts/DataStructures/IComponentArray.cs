using System;
using System.ComponentModel;

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

        /// <summary>
        /// The component value type stored in this array (i.e. the <c>T</c> in
        /// <c>ComponentArray&lt;T&gt;</c>).
        /// </summary>
        Type ComponentType { get; }

        IComponentArray Create();

        void AddEntitiesToDictionary(IComponentArray toDictionary, GroupIndex groupId);

        void EnsureCapacity(int size);
        void Clear();

        /// <summary>
        /// The element size in bytes of the stored component type. Combined with
        /// <see cref="GetUnsafePtr"/> for type-erased data movement (Burst jobs,
        /// raw serialization blits).
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

        void ResetToDefaultValuesWithCount(int count);
    }
}
