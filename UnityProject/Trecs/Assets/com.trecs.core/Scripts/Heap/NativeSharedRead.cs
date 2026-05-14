using System.ComponentModel;
using System.Runtime.CompilerServices;
using Unity.Burst;
using Unity.Collections;
using Unity.Collections.LowLevel.Unsafe;

namespace Trecs
{
    /// <summary>
    /// Read-only safety-checked view over a <see cref="NativeSharedPtr{T}"/> allocation.
    /// Obtain via <see cref="NativeSharedPtr{T}.Read(HeapAccessor)"/> on the main thread,
    /// or <see cref="NativeSharedPtr{T}.Read(in NativeSharedPtrResolver)"/> in Burst jobs. Shared native data is
    /// immutable by design — there is no <c>Write</c> counterpart; multiple jobs may concurrently
    /// hold readers over the same blob without conflict, since the per-blob
    /// <c>AtomicSafetyHandle</c> is marked read-only.
    /// </summary>
    [NativeContainer]
    [NativeContainerIsReadOnly]
    public readonly unsafe struct NativeSharedRead<T>
        where T : unmanaged
    {
        [NativeDisableUnsafePtrRestriction]
        readonly void* _ptr;

#if ENABLE_UNITY_COLLECTIONS_CHECKS
        readonly AtomicSafetyHandle m_Safety;

        static readonly SharedStatic<int> s_staticSafetyId = SharedStatic<int>.GetOrCreate<
            NativeSharedRead<T>
        >();
#endif

#if ENABLE_UNITY_COLLECTIONS_CHECKS
        [EditorBrowsable(EditorBrowsableState.Never)]
        public NativeSharedRead(void* ptr, AtomicSafetyHandle safety)
        {
            _ptr = ptr;
            m_Safety = safety;
            CollectionHelper.SetStaticSafetyId<NativeSharedRead<T>>(
                ref m_Safety,
                ref s_staticSafetyId.Data
            );
        }
#else
        [EditorBrowsable(EditorBrowsableState.Never)]
        public NativeSharedRead(void* ptr)
        {
            _ptr = ptr;
        }
#endif

        // DO NOT change to `ref readonly T`.
        //
        // Calling any non-`readonly` instance method through a readonly-rooted reference
        // (`ref readonly`, `in`, a `readonly` field, etc.) forces the C# compiler to spill
        // the receiver to a fresh stack local first and dispatch the call against the
        // local — because the method could otherwise mutate state the caller promised
        // wouldn't change. So a `ref readonly` return here would silently hand callers a
        // stack copy the moment they call any non-readonly method on the value, with no
        // compile error, no exception, no Burst warning — the code typechecks and runs,
        // just against the wrong bytes.
        //
        // The wrapper is [NativeContainerIsReadOnly] at the Unity safety-system layer,
        // so concurrent readers are fine and writes-while-readers conflicts are caught
        // at Schedule time. "Don't mutate shared data" stays a convention rather than a
        // C# language-level guarantee, intentionally.
        public ref T Value
        {
            [MethodImpl(MethodImplOptions.AggressiveInlining)]
            get
            {
#if ENABLE_UNITY_COLLECTIONS_CHECKS
                AtomicSafetyHandle.CheckReadAndThrow(m_Safety);
#endif
                return ref UnsafeUtility.AsRef<T>(_ptr);
            }
        }
    }
}
