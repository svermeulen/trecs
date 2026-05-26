using System;
using System.Runtime.CompilerServices;
using Trecs.Internal;

namespace Trecs
{
    /// <summary>
    /// Static factories for <see cref="UniquePtr{T}"/>. Per-instance operations
    /// (<c>Get</c>, <c>TryGet</c>, <c>CanGet</c>, <c>Set</c>, <c>Dispose</c>) live on
    /// the struct itself.
    /// </summary>
    public static class UniquePtr
    {
        public static UniquePtr<T> Alloc<T>(WorldAccessor world)
            where T : class
        {
            world.AssertCanMutateHeap();
            return world.UniqueHeap.AllocUnique<T>();
        }

        public static UniquePtr<T> Alloc<T>(WorldAccessor world, T value)
            where T : class
        {
            world.AssertCanMutateHeap();
            return world.UniqueHeap.AllocUnique<T>(value);
        }
    }

    /// <summary>
    /// Exclusive-ownership pointer to a managed (class) heap allocation. Each entity
    /// that holds a <see cref="UniquePtr{T}"/> owns its own independent copy. Allocate via
    /// <see cref="UniquePtr.Alloc{T}(WorldAccessor)"/> or the frame-scoped variant.
    /// <para>
    /// Resolve the value with <see cref="Get(WorldAccessor)"/>.
    /// Frame-scoped pointers are automatically cleaned up; persistent pointers must be
    /// disposed explicitly via <see cref="Dispose(WorldAccessor)"/>.
    /// </para>
    /// <para>
    /// Public verb set: <c>Get</c>, <c>TryGet</c>, <c>CanGet</c>, <c>Set</c>, <c>Dispose</c>,
    /// <c>IsNull</c>. There is intentionally no <c>Clone</c> — exclusive ownership means
    /// duplicating a <see cref="UniquePtr{T}"/> would create two owners of the same heap
    /// entry, which would corrupt the lifetime model. To copy the underlying value, allocate
    /// a new <see cref="UniquePtr{T}"/> and <c>Set</c> it.
    /// </para>
    /// </summary>
    public readonly struct UniquePtr<T> : IEquatable<UniquePtr<T>>
        where T : class
    {
        public readonly PtrHandle Handle;

        // Internal so external code can't fabricate a handle from an arbitrary uint.
        // Allocation goes through UniquePtr.Alloc; deserialization paths live in
        // InternalsVisibleTo-allowed assemblies.
        internal UniquePtr(PtrHandle handle)
        {
            Handle = handle;
        }

        public readonly bool IsNull
        {
            get { return Handle.IsNull; }
        }

        [MethodImpl(MethodImplOptions.AggressiveInlining)]
        internal readonly T Get(UniqueHeap heap)
        {
            return heap.GetEntry<T>(Handle.Value);
        }

        [MethodImpl(MethodImplOptions.AggressiveInlining)]
        internal readonly T Get(World world)
        {
            return Get(world.UniqueHeap);
        }

        [MethodImpl(MethodImplOptions.AggressiveInlining)]
        public readonly T Get(WorldAccessor world)
        {
            return Get(world.UniqueHeap);
        }

        [MethodImpl(MethodImplOptions.AggressiveInlining)]
        internal readonly bool TryGet(UniqueHeap heap, out T value)
        {
            if (IsNull)
            {
                value = null;
                return false;
            }

            value = heap.GetEntry<T>(Handle.Value);
            return true;
        }

        [MethodImpl(MethodImplOptions.AggressiveInlining)]
        internal readonly bool TryGet(World world, out T value) =>
            TryGet(world.UniqueHeap, out value);

        [MethodImpl(MethodImplOptions.AggressiveInlining)]
        public readonly bool TryGet(WorldAccessor world, out T value)
        {
            if (IsNull)
            {
                value = null;
                return false;
            }

            if (world.UniqueHeap.TryGetEntry(Handle.Value, out var entry))
            {
                TrecsDebugAssert.That(
                    entry.Type == typeof(T),
                    "Expected heap memory address ({0}) to be of type {1}, but found type {2}",
                    Handle.Value,
                    typeof(T),
                    entry.Type
                );
                value = (T)entry.Value;
                return true;
            }

            value = null;
            return false;
        }

        [MethodImpl(MethodImplOptions.AggressiveInlining)]
        internal readonly bool CanGet(World world)
        {
            if (IsNull)
            {
                return false;
            }
            return world.UniqueHeap.TryGetEntry(Handle.Value, out _);
        }

        [MethodImpl(MethodImplOptions.AggressiveInlining)]
        public readonly bool CanGet(WorldAccessor world)
        {
            if (IsNull)
            {
                return false;
            }
            return world.UniqueHeap.TryGetEntry(Handle.Value, out _);
        }

        // Public Set overloads live on UniquePtrExtensions as
        // `this ref UniquePtr<T>` extension methods (bottom of file). The
        // implementations here are `internal readonly` — extensions delegate to them.
        // Same pattern as TrecsListExtensions / NativeUniquePtrExtensions.
        [MethodImpl(MethodImplOptions.AggressiveInlining)]
        internal readonly void Set(UniqueHeap heap, T value)
        {
            heap.SetEntry(Handle.Value, value);
        }

        [MethodImpl(MethodImplOptions.AggressiveInlining)]
        internal readonly void Set(WorldAccessor world, T value)
        {
            world.AssertCanMutateHeap();
            Set(world.UniqueHeap, value);
        }

        [MethodImpl(MethodImplOptions.AggressiveInlining)]
        internal readonly void Set(World world, T value)
        {
            Set(world.UniqueHeap, value);
        }

        internal readonly void Dispose(UniqueHeap heap)
        {
            TrecsDebugAssert.That(!IsNull);
            heap.DisposeEntry<T>(Handle.Value);
        }

        internal readonly void Dispose(World world)
        {
            Dispose(world.UniqueHeap);
        }

        public readonly void Dispose(WorldAccessor world)
        {
            world.AssertCanMutateHeap();
            Dispose(world.UniqueHeap);
        }

        /// <remarks>
        /// Equality compares only <see cref="Handle"/>. <see cref="UniquePtr{T}"/> has no
        /// separate blob ID — the handle <i>is</i> the identity (each handle uniquely owns
        /// one heap entry). The shared variants (<see cref="SharedPtr{T}"/> /
        /// <see cref="NativeSharedPtr{T}"/>) additionally compare a <see cref="BlobId"/>
        /// because multiple handles can reference the same underlying blob; that doesn't
        /// apply here.
        /// </remarks>
        public readonly bool Equals(UniquePtr<T> other)
        {
            return Handle.Equals(other.Handle);
        }

        public override readonly bool Equals(object obj)
        {
            return obj is UniquePtr<T> other && Equals(other);
        }

        public override readonly int GetHashCode()
        {
            return Handle.GetHashCode();
        }

        public static bool operator ==(UniquePtr<T> left, UniquePtr<T> right)
        {
            return left.Equals(right);
        }

        public static bool operator !=(UniquePtr<T> left, UniquePtr<T> right)
        {
            return !left.Equals(right);
        }
    }

    /// <summary>
    /// Mutating operations on <see cref="UniquePtr{T}"/>. Each <c>Set</c> takes
    /// <c>this ref UniquePtr&lt;T&gt;</c>, so the caller must hold writable access
    /// to the handle struct — calling <c>Set</c> through an <c>in</c> parameter, a
    /// <c>readonly</c> field, or an <c>IRead&lt;...&gt;</c> aspect field is a
    /// compile error. Same pattern as <see cref="TrecsListExtensions"/> and
    /// <see cref="NativeUniquePtrExtensions"/>.
    /// </summary>
    public static class UniquePtrExtensions
    {
        [MethodImpl(MethodImplOptions.AggressiveInlining)]
        public static void Set<T>(this ref UniquePtr<T> ptr, WorldAccessor world, T value)
            where T : class => ptr.Set(world, value);
    }
}
