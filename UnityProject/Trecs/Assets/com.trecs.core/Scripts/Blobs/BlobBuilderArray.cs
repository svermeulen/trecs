using System.Runtime.CompilerServices;
using Trecs.Internal;

namespace Trecs
{
    /// <summary>
    /// Write-side view over a <see cref="BlobArray{T}"/> slot that
    /// <see cref="BlobBuilder.Allocate{T}(ref BlobArray{T}, int)"/> just
    /// reserved. Indexer returns by writable <c>ref</c>; valid only while the
    /// owning <see cref="BlobBuilder"/> hasn't been disposed and no subsequent
    /// allocation has invalidated the underlying chunk (it can't — chunks
    /// have stable addresses for their lifetime, but the rule keeps the
    /// invariant explicit).
    /// </summary>
    public unsafe ref struct BlobBuilderArray<T>
        where T : unmanaged
    {
        readonly T* m_Data;
        readonly int m_Length;

        internal BlobBuilderArray(void* data, int length)
        {
            m_Data = (T*)data;
            m_Length = length;
        }

        public int Length => m_Length;

        public ref T this[int index]
        {
            [MethodImpl(MethodImplOptions.AggressiveInlining)]
            get
            {
                TrecsAssert.That(
                    (uint)index < (uint)m_Length,
                    "BlobBuilderArray index {0} out of range [0, {1})",
                    index,
                    m_Length
                );
                return ref m_Data[index];
            }
        }

        /// <summary>
        /// Raw pointer to the reserved write region. Use for bulk fills
        /// (<c>UnsafeUtility.MemCpy</c>, a fill loop, etc.) that would
        /// otherwise pay per-element bounds checks through the indexer.
        /// </summary>
        [MethodImpl(MethodImplOptions.AggressiveInlining)]
        public T* GetUnsafePtr() => m_Data;
    }
}
