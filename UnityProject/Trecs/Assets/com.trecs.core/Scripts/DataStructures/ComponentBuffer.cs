using System;
using System.ComponentModel;
using System.Runtime.CompilerServices;
using Trecs.Internal;
using Unity.Burst;
using Unity.Collections;
using Unity.Collections.LowLevel.Unsafe;

namespace Trecs
{
    /// <summary>
    /// Read-only safety-checked view over a single group's component buffer.
    /// Constructed via <see cref="WorldAccessor.GetBufferRead{T}"/> on the main thread or by
    /// source-generated job code at schedule time. Both paths fetch the underlying
    /// <c>AtomicSafetyHandle</c> from the world's <c>WorldSafetyManager</c> pool keyed by
    /// <c>(component, group)</c>, so cross-job conflicts are detected at the handle level
    /// without the walker needing to traverse to any underlying <c>NativeList&lt;T&gt;</c>.
    /// </summary>
    [NativeContainer]
    [NativeContainerIsReadOnly]
    public readonly unsafe struct NativeComponentBufferRead<T>
        where T : unmanaged
    {
        readonly NativeBuffer<T> _nb;

#if ENABLE_UNITY_COLLECTIONS_CHECKS
        readonly AtomicSafetyHandle m_Safety;

        static readonly SharedStatic<int> s_staticSafetyId = SharedStatic<int>.GetOrCreate<
            NativeComponentBufferRead<T>
        >();
#endif

        // Public for cross-assembly source-gen access. Hidden from IntelliSense via
        // [EditorBrowsable(Never)].
#if ENABLE_UNITY_COLLECTIONS_CHECKS
        [EditorBrowsable(EditorBrowsableState.Never)]
        public NativeComponentBufferRead(NativeBuffer<T> nb, AtomicSafetyHandle safety)
        {
            _nb = nb;
            m_Safety = safety;
            CollectionHelper.SetStaticSafetyId<NativeComponentBufferRead<T>>(
                ref m_Safety,
                ref s_staticSafetyId.Data
            );
        }
#else
        [EditorBrowsable(EditorBrowsableState.Never)]
        public NativeComponentBufferRead(NativeBuffer<T> nb)
        {
            _nb = nb;
        }
#endif

        public int Length
        {
            [MethodImpl(MethodImplOptions.AggressiveInlining)]
            get
            {
#if ENABLE_UNITY_COLLECTIONS_CHECKS
                AtomicSafetyHandle.CheckReadAndThrow(m_Safety);
#endif
                return _nb.Length;
            }
        }

        public ref readonly T this[int index]
        {
            [MethodImpl(MethodImplOptions.AggressiveInlining)]
            get
            {
#if ENABLE_UNITY_COLLECTIONS_CHECKS
                AtomicSafetyHandle.CheckReadAndThrow(m_Safety);
#endif
                return ref _nb.IndexAsReadOnly(index);
            }
        }

        [MethodImpl(MethodImplOptions.AggressiveInlining)]
        internal IntPtr GetRawPointer(out int length)
        {
#if ENABLE_UNITY_COLLECTIONS_CHECKS
            AtomicSafetyHandle.CheckReadAndThrow(m_Safety);
#endif
            return new IntPtr(_nb.GetRawPointer(out length));
        }
    }

    /// <summary>
    /// Writable safety-checked view over a single group's component buffer.
    /// See <see cref="NativeComponentBufferRead{T}"/> for safety details.
    /// </summary>
    [NativeContainer]
    public readonly unsafe struct NativeComponentBufferWrite<T>
        where T : unmanaged
    {
        readonly NativeBuffer<T> _nb;

#if ENABLE_UNITY_COLLECTIONS_CHECKS
        readonly AtomicSafetyHandle m_Safety;

        static readonly SharedStatic<int> s_staticSafetyId = SharedStatic<int>.GetOrCreate<
            NativeComponentBufferWrite<T>
        >();
#endif

#if ENABLE_UNITY_COLLECTIONS_CHECKS
        [EditorBrowsable(EditorBrowsableState.Never)]
        public NativeComponentBufferWrite(NativeBuffer<T> nb, AtomicSafetyHandle safety)
        {
            _nb = nb;
            m_Safety = safety;
            CollectionHelper.SetStaticSafetyId<NativeComponentBufferWrite<T>>(
                ref m_Safety,
                ref s_staticSafetyId.Data
            );
        }
#else
        [EditorBrowsable(EditorBrowsableState.Never)]
        public NativeComponentBufferWrite(NativeBuffer<T> nb)
        {
            _nb = nb;
        }
#endif

        public int Length
        {
            [MethodImpl(MethodImplOptions.AggressiveInlining)]
            get
            {
#if ENABLE_UNITY_COLLECTIONS_CHECKS
                AtomicSafetyHandle.CheckWriteAndThrow(m_Safety);
#endif
                return _nb.Length;
            }
        }

        public ref T this[int index]
        {
            [MethodImpl(MethodImplOptions.AggressiveInlining)]
            get
            {
#if ENABLE_UNITY_COLLECTIONS_CHECKS
                AtomicSafetyHandle.CheckWriteAndThrow(m_Safety);
#endif
                return ref _nb[index];
            }
        }

        [MethodImpl(MethodImplOptions.AggressiveInlining)]
        internal IntPtr GetRawPointer(out int length)
        {
#if ENABLE_UNITY_COLLECTIONS_CHECKS
            AtomicSafetyHandle.CheckWriteAndThrow(m_Safety);
#endif
            return new IntPtr(_nb.GetRawPointer(out length));
        }
    }
}
