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

        /// <summary>
        /// A <c>ref readonly</c> into the shared blob. Callers get a true reference into the
        /// underlying memory, with a compile-time guarantee they cannot use it to mutate it.
        /// </summary>
        /// <remarks>
        /// <para>
        /// <b>Preferred read pattern.</b> For most call sites — reading one or more getters on
        /// the value, passing the value (or one of its fields) to a method that takes the
        /// value or a <c>ref readonly</c> — bind a <c>ref readonly</c> local and use it
        /// directly:
        /// <code>
        /// ref readonly var x = ref ptr.Read(ecs).Value;
        /// // ... use x.SomeProperty, x.SomeMethod(), pass x to helpers ...
        /// </code>
        /// Or, for one-shot reads, fold inline:
        /// <code>
        /// DoSomething(ptr.Read(ecs).Value.SomeProperty);
        /// </code>
        /// </para>
        /// <para>
        /// <b>Anti-pattern.</b> Do <i>not</i> launder the <c>ref readonly</c> through
        /// <c>Unsafe.AsRef</c> + <c>UnsafeUtility.AddressOf</c> to get a raw
        /// <c>T*</c> just to satisfy CS8329 ("cannot pass <c>ref readonly</c> as <c>ref</c>"):
        /// <code>
        /// // BAD — only valid when you genuinely need a T*.
        /// T* p = (T*)UnsafeUtility.AddressOf(ref Unsafe.AsRef(in ptr.Read(ecs).Value));
        /// </code>
        /// The pointer form is only correct when the result is <i>stored</i> as a pointer
        /// field, <i>passed</i> across a Burst-job boundary in a struct field, or fed into
        /// an existing pointer-typed API. For local getter reads or value passing, the
        /// <c>ref readonly</c> local is enough.
        /// </para>
        /// <para>
        /// <c>ref readonly T</c> is safe here because TRECS124
        /// (<c>NativeSharedPtrImmutabilityAnalyzer</c>) requires <c>T</c> to be a
        /// <c>readonly struct</c> (or a primitive / enum). On a <c>readonly struct</c>,
        /// every instance method is implicitly <c>readonly</c>, so the C# compiler never
        /// has to spill the receiver to a stack local to defend against a mutating call —
        /// the defensive-copy footgun that would otherwise motivate a pointer-laundering
        /// rewrite cannot arise.
        /// </para>
        /// </remarks>
        public ref readonly T Value
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
