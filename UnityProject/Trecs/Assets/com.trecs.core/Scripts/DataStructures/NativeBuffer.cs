#if DEBUG
#define ENABLE_DEBUG_CHECKS
#endif

using System;
using System.ComponentModel;
using System.Runtime.CompilerServices;
using Unity.Collections;
using Unity.Collections.LowLevel.Unsafe;

namespace Trecs.Internal
{
    /// <summary>
    /// NativeBuffer is a low-level pointer + length view over an unmanaged array.
    /// It is the unsafe core that the public <c>NativeComponentBufferRead/Write&lt;T&gt;</c> wrapper
    /// types layer their atomic safety handles on top of.
    /// <para>
    /// NativeBuffer holds a raw pointer behind <c>[NativeDisableUnsafePtrRestriction]</c>, so
    /// Unity's job-reflection walker treats it as opaque — it does NOT traverse into any
    /// underlying <c>NativeList&lt;T&gt;</c>'s atomic safety handle. This is what allows two
    /// jobs to touch component buffers for disjoint groups without false-positive conflicts.
    /// </para>
    /// <para>
    /// Lifetime: NativeBuffer is a temporary view. The underlying storage must remain valid
    /// (not resized, not freed) for the duration of the buffer's use. Trecs guarantees this
    /// by only handing out NativeBuffers in contexts (like job execution) where structural
    /// changes are deferred to phase boundaries.
    /// </para>
    /// </summary>
    [EditorBrowsable(EditorBrowsableState.Never)]
    public readonly unsafe struct NativeBuffer<T>
        where T : unmanaged
    {
        /// <summary>
        /// Note: static constructors are NOT compiled by burst as long as there are no static fields in the struct
        /// </summary>
        static NativeBuffer()
        {
#if ENABLE_DEBUG_CHECKS
            if (!TypeMeta<T>.IsUnmanaged)
                throw new TrecsException("NativeBuffer supports only unmanaged types");
#endif
        }

        [NativeDisableUnsafePtrRestriction]
        readonly T* _data;
        readonly int _length;

        public NativeBuffer(NativeList<T> array)
            : this((T*)NativeListUnsafeUtility.GetUnsafePtr(array), array.Length) { }

        public NativeBuffer(T* data, int length)
        {
            _data = data;
            _length = length;
        }

        public bool IsCreated
        {
            [MethodImpl(MethodImplOptions.AggressiveInlining)]
            get => _data != null;
        }

        public void Clear()
        {
            UnsafeUtility.MemClear(_data, (long)_length * UnsafeUtility.SizeOf<T>());
        }

        [MethodImpl(MethodImplOptions.AggressiveInlining)]
        public T* GetRawPointer(out int length)
        {
            length = _length;
            return _data;
        }

        public int Length
        {
            [MethodImpl(MethodImplOptions.AggressiveInlining)]
            get => _length;
        }

        [MethodImpl(MethodImplOptions.AggressiveInlining)]
        public ref readonly T IndexAsReadOnly(int index)
        {
            if (index < 0 || index >= _length)
            {
                throw new IndexOutOfRangeException(
                    $"NativeBuffer - out of bound access: index {index} - length {Length}"
                );
            }

            return ref UnsafeUtility.ArrayElementAsRef<T>(_data, index);
        }

        public ref T this[int index]
        {
            [MethodImpl(MethodImplOptions.AggressiveInlining)]
            get
            {
                if (index < 0 || index >= _length)
                {
                    throw new IndexOutOfRangeException(
                        $"NativeBuffer - out of bound access: index {index} - length {Length}"
                    );
                }

                return ref UnsafeUtility.ArrayElementAsRef<T>(_data, index);
            }
        }
    }
}
