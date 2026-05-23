using System.Runtime.CompilerServices;
using System.Runtime.InteropServices;
using Trecs.Internal;

namespace Trecs
{
    /// <summary>
    /// An immutable fixed-size array of unmanaged values whose storage lives in
    /// the same allocation as the <see cref="BlobArray{T}"/> field itself,
    /// reached via an offset relative to the field's own address.
    ///
    /// <para>Designed to live <i>inside</i> a blob root type that is itself
    /// stored behind a <see cref="NativeSharedPtr{T}"/>. The relative-offset
    /// design makes the whole allocation relocatable: <c>memcpy</c> the entire
    /// blob to a new address and every internal <c>BlobArray</c> still resolves
    /// correctly, because each one's offset is a delta from its own location,
    /// not from the root or from any absolute pointer.</para>
    ///
    /// <para>Constructed via <see cref="BlobBuilder.Allocate{T}(ref BlobArray{T}, int)"/>.
    /// Storing this struct directly on an ECS component is meaningless — the
    /// offset would point at whatever bytes happened to follow the component
    /// in chunk memory. Always store the enclosing blob root behind a
    /// <see cref="NativeSharedPtr{T}"/> (or its managed sibling) instead.</para>
    ///
    /// <para>Marked <see cref="NonCopyableAttribute"/> because the relative-
    /// offset trick depends on the struct living at its original address inside
    /// the blob allocation: a by-value copy onto the stack would keep the
    /// original <c>m_OffsetPtr</c> but compute the target from the stack
    /// address, producing garbage reads. Use <c>ref readonly var</c>,
    /// <c>in BlobArray&lt;T&gt;</c> parameters, or repeated field access
    /// (<c>blob.Heights[i]</c>) to keep accesses going through the original
    /// field's address. The <c>NonCopyableAnalyzer</c> (TRECS118 / TRECS119)
    /// catches by-value local copies and non-<c>in</c> parameter passes at
    /// compile time; the rule propagates transitively, so blob roots
    /// containing a <see cref="BlobArray{T}"/> field must also be marked
    /// <see cref="NonCopyableAttribute"/>.</para>
    /// </summary>
    [NonCopyable]
    [StructLayout(LayoutKind.Sequential)]
    public readonly unsafe struct BlobArray<T>
        where T : unmanaged
    {
        // Layout matches DOTS BlobArray<T>: int offset + int length, 8 bytes.
        // m_OffsetPtr is bytes from &m_OffsetPtr to the first element, NOT
        // from the blob root. Self-relative offsets keep the blob relocatable.
        // Sequential layout is asserted because the patching algorithm in
        // BlobBuilder relies on m_OffsetPtr being at offset 0 of the struct
        // and m_Length immediately following at offset 4 — anyone adding a
        // field here would silently break Build's two-int patch writes.
        internal readonly int m_OffsetPtr;
        internal readonly int m_Length;

        public int Length => m_Length;

        public ref readonly T this[int index]
        {
            [MethodImpl(MethodImplOptions.AggressiveInlining)]
            get
            {
                TrecsAssert.That(
                    (uint)index < (uint)m_Length,
                    "BlobArray index {0} out of range [0, {1})",
                    index,
                    m_Length
                );
                fixed (int* thisPtr = &m_OffsetPtr)
                {
                    return ref *((T*)((byte*)thisPtr + m_OffsetPtr) + index);
                }
            }
        }

        /// <summary>
        /// Raw pointer to the first element. Valid only while the enclosing
        /// blob allocation is alive. Returns an invalid pointer when
        /// <see cref="Length"/> is zero — guard with the length check before
        /// dereferencing.
        /// </summary>
        [MethodImpl(MethodImplOptions.AggressiveInlining)]
        public T* GetUnsafePtr()
        {
            fixed (int* thisPtr = &m_OffsetPtr)
            {
                return (T*)((byte*)thisPtr + m_OffsetPtr);
            }
        }

        /// <summary>
        /// Enables <c>foreach</c> over a <see cref="BlobArray{T}"/>. The C#
        /// compiler resolves <c>foreach</c> via this method by name — no
        /// <c>IEnumerable&lt;T&gt;</c> implementation is needed, which keeps
        /// the struct <c>readonly</c> and the enumerator allocation-free.
        ///
        /// <para>The returned <see cref="Enumerator"/> is a <c>ref struct</c>
        /// — same constraint as <see cref="BlobBuilderArray{T}"/> — which
        /// prevents accidental capture into a heap allocation or async state
        /// machine and keeps the cached data pointer from escaping the
        /// enclosing blob's lifetime.</para>
        ///
        /// <para>The data pointer is resolved once here against the field's
        /// current address; the enumerator then walks it by index. The
        /// pointer stays valid as long as the enclosing blob isn't moved
        /// or freed — same constraint indexer-based iteration already has.
        /// No modification-during-iteration check is performed: the blob is
        /// immutable by construction (built once via <see cref="BlobBuilder"/>,
        /// then never mutated), so the only way iteration can break is
        /// freeing the enclosing blob mid-loop, which is a lifetime bug the
        /// indexer would mis-resolve identically.</para>
        /// </summary>
        [MethodImpl(MethodImplOptions.AggressiveInlining)]
        public Enumerator GetEnumerator()
        {
            fixed (int* thisPtr = &m_OffsetPtr)
            {
                return new Enumerator((T*)((byte*)thisPtr + m_OffsetPtr), m_Length);
            }
        }

        public ref struct Enumerator
        {
            readonly T* _data;
            readonly int _length;
            int _index;

            [MethodImpl(MethodImplOptions.AggressiveInlining)]
            internal Enumerator(T* data, int length)
            {
                _data = data;
                _length = length;
                _index = -1;
            }

            public ref readonly T Current
            {
                [MethodImpl(MethodImplOptions.AggressiveInlining)]
                get => ref _data[_index];
            }

            [MethodImpl(MethodImplOptions.AggressiveInlining)]
            public bool MoveNext() => ++_index < _length;
        }
    }
}
