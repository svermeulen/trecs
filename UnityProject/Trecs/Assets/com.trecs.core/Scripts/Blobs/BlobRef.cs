using System.Runtime.CompilerServices;
using System.Runtime.InteropServices;
using Trecs.Internal;

namespace Trecs
{
    /// <summary>
    /// A relative pointer to a single unmanaged <typeparamref name="T"/>
    /// whose storage lives in the same allocation as the
    /// <see cref="BlobRef{T}"/> field itself, reached via an offset relative
    /// to the field's own address.
    ///
    /// <para>The single-element counterpart to <see cref="BlobArray{T}"/>.
    /// Same relocatable-by-construction property: <c>memcpy</c> the entire
    /// enclosing blob and the offset still resolves, because it's a delta
    /// from this field's own location rather than from any absolute pointer
    /// or root-relative offset.</para>
    ///
    /// <para>The DOTS equivalent is <c>BlobPtr&lt;T&gt;</c>, but that name is
    /// already taken in Trecs by the heap-pin type in
    /// <see cref="BlobPtr{T}"/>, so we ship this as <c>Trecs.BlobRef&lt;T&gt;</c>.</para>
    ///
    /// <para>Constructed via <see cref="BlobBuilder.Allocate{T}(in BlobRef{T})"/>.
    /// Same storage-on-component caveat as <see cref="BlobArray{T}"/>: storing
    /// a <see cref="BlobRef{T}"/> directly on an ECS component is meaningless
    /// because the offset would resolve against unrelated chunk bytes. Always
    /// store the enclosing blob root behind a <see cref="NativeSharedPtr{T}"/>
    /// (or its managed sibling) instead.</para>
    ///
    /// <para>Marked <see cref="NonCopyableAttribute"/> for the same reason
    /// as <see cref="BlobArray{T}"/>: a by-value copy onto the stack would
    /// keep the original <c>m_OffsetPtr</c> but compute the target from the
    /// stack address, producing garbage reads. The
    /// <c>NonCopyableAnalyzer</c> (TRECS118 / TRECS119) catches by-value
    /// local copies and non-<c>in</c> parameter passes at compile time.</para>
    /// </summary>
    [NonCopyable]
    [StructLayout(LayoutKind.Sequential)]
    public readonly unsafe struct BlobRef<T>
        where T : unmanaged
    {
        // Same layout idiom as BlobArray<T>'s m_OffsetPtr field, minus the
        // length. 4 bytes total. m_OffsetPtr is the byte delta from
        // &m_OffsetPtr to the referent, NOT from the blob root. Self-relative
        // offsets keep the blob relocatable.
        //
        // A zero offset is the default-initialized state and means "not
        // allocated yet" — see IsValid. A patched BlobRef<T> never produces a
        // zero offset because Build writes (targetInBuffer - offsetPtrInBuffer)
        // where the two addresses are always in distinct allocations.
        internal readonly int m_OffsetPtr;

        /// <summary>
        /// <c>true</c> if the builder has allocated a referent for this
        /// field; <c>false</c> for a default-initialized <see cref="BlobRef{T}"/>.
        /// Use to model "optional sub-structure" semantics: skip
        /// <see cref="Value"/> when not valid rather than dereferencing into
        /// garbage.
        /// </summary>
        public bool IsValid => m_OffsetPtr != 0;

        /// <summary>
        /// Reads the referent. Asserts <see cref="IsValid"/> first — a
        /// default-initialized <see cref="BlobRef{T}"/> has no referent and
        /// dereferencing it would resolve to <c>&amp;m_OffsetPtr</c> itself
        /// (offset 0 from its own address), producing garbage reads.
        /// </summary>
        public ref readonly T Value
        {
            [MethodImpl(MethodImplOptions.AggressiveInlining)]
            get
            {
                TrecsAssert.That(
                    IsValid,
                    "BlobRef<T> dereferenced before BlobBuilder.Allocate populated it"
                );
                fixed (int* thisPtr = &m_OffsetPtr)
                {
                    return ref *(T*)((byte*)thisPtr + m_OffsetPtr);
                }
            }
        }

        /// <summary>
        /// Raw pointer to the referent. Valid only when <see cref="IsValid"/>
        /// is <c>true</c> and the enclosing blob allocation is alive. Returns
        /// the address of <c>&amp;m_OffsetPtr</c> itself (a garbage referent)
        /// when not valid — guard with <see cref="IsValid"/> first.
        /// </summary>
        [MethodImpl(MethodImplOptions.AggressiveInlining)]
        public T* GetUnsafePtr()
        {
            fixed (int* thisPtr = &m_OffsetPtr)
            {
                return (T*)((byte*)thisPtr + m_OffsetPtr);
            }
        }
    }
}
