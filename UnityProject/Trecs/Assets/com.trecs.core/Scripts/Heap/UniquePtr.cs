using System;
using System.Runtime.CompilerServices;
using Trecs.Internal;

namespace Trecs
{
    /// <summary>
    /// Exclusive-ownership pointer to a managed (class) heap allocation. Each entity
    /// that holds a <see cref="UniquePtr{T}"/> owns its own independent copy. Allocate via
    /// <see cref="HeapAccessor.AllocUnique{T}"/> or the frame-scoped variant.
    /// <para>
    /// Resolve the value with <see cref="Get(HeapAccessor)"/> or <see cref="Get(WorldAccessor)"/>.
    /// Frame-scoped pointers are automatically cleaned up; persistent pointers must be
    /// disposed explicitly via <see cref="Dispose(HeapAccessor)"/>.
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

        public UniquePtr(PtrHandle handle)
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
            if (world.UniqueHeap.TryGetEntry(Handle.Value, out var entry))
            {
                Assert.That(
                    entry.Type == typeof(T),
                    "Expected heap memory address ({}) to be of type {}, but found type {}",
                    Handle.Value,
                    typeof(T),
                    entry.Type
                );
                return (T)entry.Value;
            }

            return world.FrameScopedUniqueHeap.ResolveValue<T>(world.FixedFrame, Handle.Value);
        }

        [MethodImpl(MethodImplOptions.AggressiveInlining)]
        public readonly T Get(HeapAccessor heap)
        {
            if (heap.UniqueHeap.TryGetEntry(Handle.Value, out var entry))
            {
                Assert.That(
                    entry.Type == typeof(T),
                    "Expected heap memory address ({}) to be of type {}, but found type {}",
                    Handle.Value,
                    typeof(T),
                    entry.Type
                );
                return (T)entry.Value;
            }

            return heap.FrameScopedUniqueHeap.ResolveValue<T>(heap.FixedFrame, Handle.Value);
        }

        [MethodImpl(MethodImplOptions.AggressiveInlining)]
        public readonly T Get(WorldAccessor accessor) => Get(accessor.Heap);

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
        internal readonly bool TryGet(World world, out T value)
        {
            if (IsNull)
            {
                value = null;
                return false;
            }

            if (world.UniqueHeap.TryGetEntry(Handle.Value, out var entry))
            {
                Assert.That(
                    entry.Type == typeof(T),
                    "Expected heap memory address ({}) to be of type {}, but found type {}",
                    Handle.Value,
                    typeof(T),
                    entry.Type
                );
                value = (T)entry.Value;
                return true;
            }

            if (
                world.FrameScopedUniqueHeap.TryResolveValue<T>(
                    Handle.Value,
                    world.FixedFrame,
                    out value
                )
            )
            {
                return true;
            }

            value = null;
            return false;
        }

        [MethodImpl(MethodImplOptions.AggressiveInlining)]
        public readonly bool TryGet(HeapAccessor heap, out T value)
        {
            if (IsNull)
            {
                value = null;
                return false;
            }

            if (heap.UniqueHeap.TryGetEntry(Handle.Value, out var entry))
            {
                Assert.That(
                    entry.Type == typeof(T),
                    "Expected heap memory address ({}) to be of type {}, but found type {}",
                    Handle.Value,
                    typeof(T),
                    entry.Type
                );
                value = (T)entry.Value;
                return true;
            }

            if (
                heap.FrameScopedUniqueHeap.TryResolveValue<T>(
                    Handle.Value,
                    heap.FixedFrame,
                    out value
                )
            )
            {
                return true;
            }

            value = null;
            return false;
        }

        [MethodImpl(MethodImplOptions.AggressiveInlining)]
        public readonly bool TryGet(WorldAccessor accessor, out T value) =>
            TryGet(accessor.Heap, out value);

        [MethodImpl(MethodImplOptions.AggressiveInlining)]
        internal readonly bool CanGet(World world)
        {
            if (IsNull)
            {
                return false;
            }

            return world.UniqueHeap.TryGetEntry(Handle.Value, out _)
                || world.FrameScopedUniqueHeap.ContainsEntry(Handle.Value);
        }

        [MethodImpl(MethodImplOptions.AggressiveInlining)]
        public readonly bool CanGet(HeapAccessor heap)
        {
            if (IsNull)
            {
                return false;
            }

            return heap.UniqueHeap.TryGetEntry(Handle.Value, out _)
                || heap.FrameScopedUniqueHeap.ContainsEntry(Handle.Value);
        }

        [MethodImpl(MethodImplOptions.AggressiveInlining)]
        public readonly bool CanGet(WorldAccessor accessor) => CanGet(accessor.Heap);

        [MethodImpl(MethodImplOptions.AggressiveInlining)]
        public readonly void Set(UniqueHeap heap, T value)
        {
            heap.SetEntry(Handle.Value, value);
        }

        [MethodImpl(MethodImplOptions.AggressiveInlining)]
        public readonly void Set(HeapAccessor heap, T value)
        {
            Set(heap.UniqueHeap, value);
        }

        [MethodImpl(MethodImplOptions.AggressiveInlining)]
        public readonly void Set(WorldAccessor accessor, T value) => Set(accessor.Heap, value);

        [MethodImpl(MethodImplOptions.AggressiveInlining)]
        public readonly void Set(World world, T value)
        {
            Set(world.UniqueHeap, value);
        }

        internal readonly void Dispose(UniqueHeap heap)
        {
            Assert.That(!IsNull);
            heap.DisposeEntry<T>(Handle.Value);
        }

        internal readonly void Dispose(World world)
        {
            Assert.That(
                !world.FrameScopedUniqueHeap.ContainsEntry(Handle.Value),
                "Frame-scoped UniquePtr must not be manually disposed"
            );
            Dispose(world.UniqueHeap);
        }

        public readonly void Dispose(HeapAccessor heap)
        {
            Assert.That(
                !heap.FrameScopedUniqueHeap.ContainsEntry(Handle.Value),
                "Frame-scoped UniquePtr must not be manually disposed"
            );
            Dispose(heap.UniqueHeap);
        }

        public readonly void Dispose(WorldAccessor accessor) => Dispose(accessor.Heap);

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
}
