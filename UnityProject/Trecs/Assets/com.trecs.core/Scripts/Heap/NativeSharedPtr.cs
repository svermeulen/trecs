using System;
using System.Runtime.CompilerServices;
using System.Runtime.InteropServices;
using Trecs.Internal;

namespace Trecs
{
    /// <summary>
    /// Static factories for <see cref="NativeSharedPtr{T}"/>. Per-instance operations
    /// (<c>Read</c>, <c>Clone</c>, <c>Dispose</c>) live on the struct itself.
    /// </summary>
    public static class NativeSharedPtr
    {
        public static NativeSharedPtr<T> Acquire<T>(WorldAccessor world, BlobId blobId)
            where T : unmanaged
        {
            world.AssertCanMutateHeap();
            return world.NativeSharedHeap.GetBlob<T>(blobId);
        }

        public static NativeSharedPtr<T> Alloc<T>(WorldAccessor world, BlobId blobId, in T value)
            where T : unmanaged
        {
            world.AssertCanMutateHeap();
            return world.NativeSharedHeap.CreateBlob<T>(blobId, in value);
        }

        public static bool TryGet<T>(WorldAccessor world, BlobId blobId, out NativeSharedPtr<T> ptr)
            where T : unmanaged
        {
            world.AssertCanMutateHeap();
            return world.NativeSharedHeap.TryGetBlob<T>(blobId, out ptr);
        }

        public static NativeSharedPtr<T> AllocTakingOwnership<T>(
            WorldAccessor world,
            BlobId blobId,
            NativeBlobAllocation alloc
        )
            where T : unmanaged
        {
            world.AssertCanMutateHeap();
            return world.NativeSharedHeap.CreateBlobTakingOwnership<T>(blobId, alloc);
        }

        public static NativeSharedPtr<T> GetOrAlloc<T>(
            WorldAccessor world,
            BlobId blobId,
            Func<T> factory
        )
            where T : unmanaged
        {
            world.AssertCanMutateHeap();
            if (world.NativeSharedHeap.TryGetBlob<T>(blobId, out var ptr))
            {
                return ptr;
            }
            return world.NativeSharedHeap.CreateBlob<T>(blobId, factory());
        }

        public static NativeSharedPtr<T> GetOrAllocTakingOwnership<T>(
            WorldAccessor world,
            BlobId blobId,
            Func<NativeBlobAllocation> factory
        )
            where T : unmanaged
        {
            world.AssertCanMutateHeap();
            if (world.NativeSharedHeap.TryGetBlob<T>(blobId, out var ptr))
            {
                return ptr;
            }
            return world.NativeSharedHeap.CreateBlobTakingOwnership<T>(blobId, factory());
        }
    }

    /// <summary>
    /// Reference-counted pointer to a shared native (unmanaged) heap allocation. Burst-compatible.
    /// Multiple entities can reference the same data via <see cref="BlobId"/>.
    ///
    /// <para>
    /// Seed via <see cref="NativeSharedPtr.Alloc{T}(WorldAccessor, BlobId, in T)"/>;
    /// look up an already-seeded blob via <see cref="NativeSharedPtr.Acquire{T}(WorldAccessor, BlobId)"/>.
    /// Open a safety-checked view with <see cref="Read(WorldAccessor)"/> on the main thread, or
    /// <see cref="Read(in NativeWorldAccessor)"/> in Burst jobs. <see cref="Clone"/>
    /// increments the reference count; <see cref="Dispose(WorldAccessor)"/> decrements it and
    /// frees on zero.
    /// </para>
    ///
    /// <para>
    /// <b>T must be a <c>readonly struct</c></b> (or a built-in primitive / enum) — enforced by
    /// the TRECS124 analyzer.
    /// </para>
    ///
    /// <para>
    /// The struct stores a single 4-byte handle encoding a generation (8 bits) and blob slot
    /// index (24 bits) into a chunked side-table directory. Freshly-allocated blobs are
    /// immediately visible to Burst jobs — no pending-flush deferral.
    /// </para>
    /// </summary>
    [StructLayout(LayoutKind.Sequential)]
    public readonly unsafe struct NativeSharedPtr<T> : IEquatable<NativeSharedPtr<T>>
        where T : unmanaged
    {
        internal readonly uint Handle;

        internal NativeSharedPtr(uint handle)
        {
            Handle = handle;
        }

        [MethodImpl(MethodImplOptions.AggressiveInlining)]
        public readonly NativeSharedPtr<T> Clone(WorldAccessor world)
        {
            world.AssertCanMutateHeap();
            if (IsNull)
            {
                return default;
            }
            if (!world.NativeSharedHeap.TryClone<T>(Handle, out var result))
            {
                throw TrecsDebugAssert.CreateException(
                    "Failed to clone NativeSharedPtr with handle {0}",
                    Handle
                );
            }
            return result;
        }

        public readonly NativeSharedRead<T> Read(WorldAccessor world)
        {
            return world.NativeSharedHeap.Read(in this);
        }

        public readonly NativeSharedRead<T> Read(in NativeWorldAccessor world)
        {
            var entry = world.SharedPtrResolver.ResolveEntryWithSlotPtr<T>(Handle, out var slotPtr);
#if ENABLE_UNITY_COLLECTIONS_CHECKS
            return new NativeSharedRead<T>(
                entry.Address.ToPointer(),
                slotPtr,
                entry.Generation,
                entry.Safety
            );
#else
            return new NativeSharedRead<T>(entry.Address.ToPointer(), slotPtr, entry.Generation);
#endif
        }

        public readonly void Dispose(WorldAccessor world)
        {
            world.AssertCanMutateHeap();
            world.NativeSharedHeap.DecrementRef(Handle);
        }

        public readonly bool IsNull
        {
            [MethodImpl(MethodImplOptions.AggressiveInlining)]
            get { return Handle == 0; }
        }

        public readonly BlobId GetBlobId(WorldAccessor world)
        {
            return world.NativeSharedHeap.GetBlobId(Handle);
        }

        public readonly bool Equals(NativeSharedPtr<T> other)
        {
            return Handle == other.Handle;
        }

        public override readonly bool Equals(object obj)
        {
            return obj is NativeSharedPtr<T> other && Equals(other);
        }

        public override readonly int GetHashCode()
        {
            return (int)Handle;
        }

        public static bool operator ==(NativeSharedPtr<T> left, NativeSharedPtr<T> right)
        {
            return left.Handle == right.Handle;
        }

        public static bool operator !=(NativeSharedPtr<T> left, NativeSharedPtr<T> right)
        {
            return left.Handle != right.Handle;
        }
    }
}
