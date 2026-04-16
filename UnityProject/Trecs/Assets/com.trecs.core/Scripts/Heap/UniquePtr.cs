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
    /// </summary>
    public readonly struct UniquePtr<T> : IEquatable<UniquePtr<T>>
        where T : class
    {
        public readonly PtrHandle Handle;

        public UniquePtr(PtrHandle handle)
        {
            Handle = handle;
        }

        public bool IsNull
        {
            get { return !IsCreated; }
        }
        public bool IsCreated
        {
            get { return !Handle.IsNull; }
        }

        [MethodImpl(MethodImplOptions.AggressiveInlining)]
        internal T Get(UniqueHeap heap)
        {
            return heap.GetEntry<T>(Handle.Value);
        }

        [MethodImpl(MethodImplOptions.AggressiveInlining)]
        internal T Get(World ecs)
        {
            if (ecs.UniqueHeap.TryGetEntry(Handle.Value, out var entry))
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

            return ecs.FrameScopedUniqueHeap.ResolveValue<T>(ecs.FixedFrame, Handle.Value);
        }

        [MethodImpl(MethodImplOptions.AggressiveInlining)]
        public T Get(HeapAccessor heap)
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
        public T Get(WorldAccessor accessor) => Get(accessor.Heap);

        [MethodImpl(MethodImplOptions.AggressiveInlining)]
        internal bool TryGet(UniqueHeap heap, out T value)
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
        internal bool TryGet(World ecs, out T value)
        {
            if (IsNull)
            {
                value = null;
                return false;
            }

            if (ecs.UniqueHeap.TryGetEntry(Handle.Value, out var entry))
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
                ecs.FrameScopedUniqueHeap.TryResolveValue<T>(
                    Handle.Value,
                    ecs.FixedFrame,
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
        public bool TryGet(HeapAccessor heap, out T value)
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
        public bool TryGet(WorldAccessor accessor, out T value) => TryGet(accessor.Heap, out value);

        [MethodImpl(MethodImplOptions.AggressiveInlining)]
        internal bool CanGet(World ecs)
        {
            if (IsNull)
            {
                return false;
            }

            return ecs.UniqueHeap.TryGetEntry(Handle.Value, out _)
                || ecs.FrameScopedUniqueHeap.ContainsEntry(Handle.Value);
        }

        [MethodImpl(MethodImplOptions.AggressiveInlining)]
        public bool CanGet(HeapAccessor heap)
        {
            if (IsNull)
            {
                return false;
            }

            return heap.UniqueHeap.TryGetEntry(Handle.Value, out _)
                || heap.FrameScopedUniqueHeap.ContainsEntry(Handle.Value);
        }

        [MethodImpl(MethodImplOptions.AggressiveInlining)]
        public bool CanGet(WorldAccessor accessor) => CanGet(accessor.Heap);

        [MethodImpl(MethodImplOptions.AggressiveInlining)]
        public void Set(UniqueHeap heap, T value)
        {
            heap.SetEntry(Handle.Value, value);
        }

        [MethodImpl(MethodImplOptions.AggressiveInlining)]
        public void Set(HeapAccessor heap, T value)
        {
            Set(heap.UniqueHeap, value);
        }

        [MethodImpl(MethodImplOptions.AggressiveInlining)]
        public void Set(WorldAccessor accessor, T value) => Set(accessor.Heap, value);

        [MethodImpl(MethodImplOptions.AggressiveInlining)]
        public void Set(World ecs, T value)
        {
            Set(ecs.UniqueHeap, value);
        }

        internal void Dispose(UniqueHeap heap)
        {
            Assert.That(!IsNull);
            heap.DisposeEntry<T>(Handle.Value);
        }

        internal void Dispose(World ecs)
        {
            Assert.That(
                !ecs.FrameScopedUniqueHeap.ContainsEntry(Handle.Value),
                "Frame-scoped UniquePtr must not be manually disposed"
            );
            Dispose(ecs.UniqueHeap);
        }

        public void Dispose(HeapAccessor heap)
        {
            Assert.That(
                !heap.FrameScopedUniqueHeap.ContainsEntry(Handle.Value),
                "Frame-scoped UniquePtr must not be manually disposed"
            );
            Dispose(heap.UniqueHeap);
        }

        public void Dispose(WorldAccessor accessor) => Dispose(accessor.Heap);

        public bool Equals(UniquePtr<T> other)
        {
            return Handle.Equals(other.Handle);
        }

        public override bool Equals(object obj)
        {
            return obj is UniquePtr<T> other && Equals(other);
        }

        public override int GetHashCode()
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
