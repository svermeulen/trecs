using System;
using System.Runtime.CompilerServices;
using System.Runtime.InteropServices;
using Trecs.Internal;

namespace Trecs
{
    /// <summary>
    /// Static factories for <see cref="InputNativeSharedPtr{T}"/>. Allocation
    /// is gated to input-role accessors; the heap that tracks refcount handles
    /// releases the blob's refcount when the input queue trims the allocating
    /// frame.
    /// </summary>
    public static class InputNativeSharedPtr
    {
        public static InputNativeSharedPtr<T> Alloc<T>(
            WorldAccessor world,
            BlobId blobId,
            in T value
        )
            where T : unmanaged
        {
            world.AssertCanAddInputsHeap();
            return world.InputNativeSharedHeap.Alloc<T>(world.FixedFrame, blobId, in value);
        }

        public static InputNativeSharedPtr<T> Acquire<T>(WorldAccessor world, BlobId blobId)
            where T : unmanaged
        {
            world.AssertCanAddInputsHeap();
            return world.InputNativeSharedHeap.Acquire<T>(world.FixedFrame, blobId);
        }

        public static bool TryAcquire<T>(
            WorldAccessor world,
            BlobId blobId,
            out InputNativeSharedPtr<T> ptr
        )
            where T : unmanaged
        {
            world.AssertCanAddInputsHeap();
            return world.InputNativeSharedHeap.TryAcquire<T>(world.FixedFrame, blobId, out ptr);
        }
    }

    /// <summary>
    /// Reference-counted pointer to an unmanaged shared blob, allocated through
    /// the input pipeline. The struct stores a <see cref="BlobId"/> (8 bytes); reads
    /// go through <see cref="InputNativeSharedPtrResolver"/> which is a separate
    /// BlobId-keyed map independent from the simulation-state
    /// <see cref="NativeSharedPtrResolver"/>.
    /// </summary>
    [StructLayout(LayoutKind.Sequential, Pack = 1)]
    public readonly unsafe struct InputNativeSharedPtr<T> : IEquatable<InputNativeSharedPtr<T>>
        where T : unmanaged
    {
        public readonly BlobId BlobId;

        internal InputNativeSharedPtr(BlobId blobId)
        {
            BlobId = blobId;
        }

        public readonly bool IsNull
        {
            [MethodImpl(MethodImplOptions.AggressiveInlining)]
            get { return BlobId.IsNull; }
        }

        public readonly NativeSharedRead<T> Read(WorldAccessor world)
        {
            var entry = world.InputNativeSharedHeap.ResolveEntry<T>(BlobId);
#if ENABLE_UNITY_COLLECTIONS_CHECKS
            return new NativeSharedRead<T>(
                entry.Ptr.ToPointer(),
                slot: null,
                capturedGeneration: 0,
                entry.Safety
            );
#else
            return new NativeSharedRead<T>(
                entry.Ptr.ToPointer(),
                slot: null,
                capturedGeneration: 0
            );
#endif
        }

        public readonly NativeSharedRead<T> Read(in NativeWorldAccessor world)
        {
            var entry = world.InputSharedPtrResolver.ResolveEntry<T>(BlobId);
#if ENABLE_UNITY_COLLECTIONS_CHECKS
            return new NativeSharedRead<T>(
                entry.Ptr.ToPointer(),
                slot: null,
                capturedGeneration: 0,
                entry.Safety
            );
#else
            return new NativeSharedRead<T>(
                entry.Ptr.ToPointer(),
                slot: null,
                capturedGeneration: 0
            );
#endif
        }

        public readonly bool Equals(InputNativeSharedPtr<T> other) => BlobId.Equals(other.BlobId);

        public override readonly bool Equals(object obj) =>
            obj is InputNativeSharedPtr<T> other && Equals(other);

        public override readonly int GetHashCode() => BlobId.GetHashCode();

        public static bool operator ==(InputNativeSharedPtr<T> l, InputNativeSharedPtr<T> r) =>
            l.Equals(r);

        public static bool operator !=(InputNativeSharedPtr<T> l, InputNativeSharedPtr<T> r) =>
            !l.Equals(r);
    }
}
