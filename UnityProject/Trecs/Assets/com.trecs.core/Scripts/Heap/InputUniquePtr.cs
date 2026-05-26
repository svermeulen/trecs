using System;
using System.Runtime.CompilerServices;

namespace Trecs
{
    /// <summary>
    /// Static factories for <see cref="InputUniquePtr{T}"/>. Allocation is
    /// gated to input-role accessors; the input heap despawns the object back
    /// to its pool when the allocating frame is trimmed.
    /// </summary>
    public static class InputUniquePtr
    {
        public static InputUniquePtr<T> Alloc<T>(WorldAccessor world)
            where T : class
        {
            world.AssertCanAddInputsHeap();
            return world.InputUniqueHeap.Alloc<T>(world.FixedFrame);
        }

        public static InputUniquePtr<T> Alloc<T>(WorldAccessor world, T value)
            where T : class
        {
            world.AssertCanAddInputsHeap();
            return world.InputUniqueHeap.Alloc<T>(world.FixedFrame, value);
        }
    }

    /// <summary>
    /// Exclusive-ownership pointer to a managed object, allocated through the
    /// input pipeline. The object is owned by the input heap and despawned back
    /// to the pool when the allocating input frame is trimmed.
    ///
    /// <para>Distinct from <see cref="UniquePtr{T}"/>: the type-level split
    /// encodes the lifetime contract — input pointers can only be allocated
    /// from input-role accessors, cannot be manually disposed, and source-gen
    /// rejects them in <c>[Input(MissingInputBehavior.Retain)]</c> fields.
    /// There is intentionally no <c>Clone</c> — exclusive ownership.</para>
    /// </summary>
    public readonly struct InputUniquePtr<T> : IEquatable<InputUniquePtr<T>>
        where T : class
    {
        public readonly PtrHandle Handle;

        internal InputUniquePtr(PtrHandle handle)
        {
            Handle = handle;
        }

        public readonly bool IsNull
        {
            [MethodImpl(MethodImplOptions.AggressiveInlining)]
            get { return Handle.IsNull; }
        }

        [MethodImpl(MethodImplOptions.AggressiveInlining)]
        public readonly T Get(WorldAccessor world) => world.InputUniqueHeap.ResolveValue<T>(Handle);

        [MethodImpl(MethodImplOptions.AggressiveInlining)]
        public readonly bool TryGet(WorldAccessor world, out T value) =>
            world.InputUniqueHeap.TryResolveValue<T>(Handle, out value);

        public readonly bool CanGet(WorldAccessor world) =>
            world.InputUniqueHeap.ContainsEntry(Handle);

        public readonly bool Equals(InputUniquePtr<T> other) => Handle.Equals(other.Handle);

        public override readonly bool Equals(object obj) =>
            obj is InputUniquePtr<T> other && Equals(other);

        public override readonly int GetHashCode() => Handle.GetHashCode();

        public static bool operator ==(InputUniquePtr<T> l, InputUniquePtr<T> r) => l.Equals(r);

        public static bool operator !=(InputUniquePtr<T> l, InputUniquePtr<T> r) => !l.Equals(r);
    }
}
