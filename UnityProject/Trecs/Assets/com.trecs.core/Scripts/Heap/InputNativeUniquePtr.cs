using System;
using System.Runtime.CompilerServices;
using System.Runtime.InteropServices;
using Trecs.Internal;
using Unity.Collections.LowLevel.Unsafe;

namespace Trecs
{
    /// <summary>
    /// Static factories for <see cref="InputNativeUniquePtr{T}"/>. Allocation is
    /// gated to input-role accessors; the heap that backs these pointers reclaims
    /// the underlying bytes when the input queue trims the allocating frame.
    /// </summary>
    public static class InputNativeUniquePtr
    {
        public static InputNativeUniquePtr<T> Alloc<T>(WorldAccessor world, in T value)
            where T : unmanaged
        {
            world.AssertCanAddInputsHeap();
            return world.InputNativeUniqueHeap.Alloc<T>(world.FixedFrame, in value);
        }

        public static InputNativeUniquePtr<T> Alloc<T>(WorldAccessor world)
            where T : unmanaged
        {
            world.AssertCanAddInputsHeap();
            return world.InputNativeUniqueHeap.Alloc<T>(world.FixedFrame);
        }
    }

    /// <summary>
    /// Pointer to an unmanaged value allocated through the input pipeline. The
    /// lifetime is owned by the input queue: the value is valid for as long as
    /// the input frame that created it is retained (currently the allocating
    /// frame plus any frames a history locker keeps alive). Burst-compatible.
    ///
    /// <para>Distinct from <see cref="NativeUniquePtr{T}"/>:
    /// the type-level split encodes the lifetime contract — input pointers can
    /// only be allocated from input-role accessors, cannot be manually disposed,
    /// have no Write (input components are read-only in Fixed phase), and live
    /// in a separate backing store from persistent allocations. Source-gen
    /// rejects this type in <c>[Input(MissingInputBehavior.Retain)]</c> fields,
    /// since a retained component value pointing into a freed input allocation
    /// would be a use-after-free.</para>
    ///
    /// <para>The struct stores only an <see cref="InputPtrHandle"/> (4 bytes) —
    /// cheap to copy and store on components. Read access goes through the
    /// owning <see cref="InputNativeUniqueHeap"/> on the main thread or through
    /// an <see cref="InputNativeUniqueResolver"/> from Burst jobs; both return
    /// the underlying <c>ref readonly T</c> directly.</para>
    /// </summary>
    [StructLayout(LayoutKind.Sequential, Pack = 1)]
    public readonly unsafe struct InputNativeUniquePtr<T> : IEquatable<InputNativeUniquePtr<T>>
        where T : unmanaged
    {
        public readonly InputPtrHandle Handle;

        // Internal so external code can't fabricate a handle from arbitrary bits.
        // Allocation goes through InputNativeUniquePtr.Alloc; serialization paths
        // live in InternalsVisibleTo-allowed assemblies.
        internal InputNativeUniquePtr(InputPtrHandle handle)
        {
            Handle = handle;
        }

        public readonly bool IsNull
        {
            [MethodImpl(MethodImplOptions.AggressiveInlining)]
            get { return Handle.IsNull; }
        }

        /// <summary>
        /// Main-thread read view. For Burst jobs use
        /// <see cref="Read(in InputNativeUniqueResolver)"/>.
        /// </summary>
        [MethodImpl(MethodImplOptions.AggressiveInlining)]
        public readonly ref readonly T Read(WorldAccessor world)
        {
            var ptr = world.InputNativeUniqueHeap.ResolveUnsafePtr<T>(Handle);
            return ref UnsafeUtility.AsRef<T>(ptr);
        }

        /// <summary>
        /// Burst-compatible read view. Pass a resolver obtained via
        /// <see cref="InputNativeUniqueHeap.Resolver"/>.
        /// </summary>
        [MethodImpl(MethodImplOptions.AggressiveInlining)]
        public readonly ref readonly T Read(in InputNativeUniqueResolver resolver)
        {
            return ref UnsafeUtility.AsRef<T>(resolver.Resolve<T>(Handle));
        }

        public readonly bool Equals(InputNativeUniquePtr<T> other) => Handle.Equals(other.Handle);

        public override readonly bool Equals(object obj) =>
            obj is InputNativeUniquePtr<T> other && Equals(other);

        public override readonly int GetHashCode() => Handle.GetHashCode();

        public static bool operator ==(InputNativeUniquePtr<T> l, InputNativeUniquePtr<T> r) =>
            l.Equals(r);

        public static bool operator !=(InputNativeUniquePtr<T> l, InputNativeUniquePtr<T> r) =>
            !l.Equals(r);
    }
}
