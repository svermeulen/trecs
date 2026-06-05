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

        /// <summary>
        /// Interns <paramref name="descriptor"/> — hashing it to a content-derived
        /// <see cref="BlobId"/>, deduplicating against the cache, and running the registered builder
        /// on a miss — and acquires a frame-scoped input handle in one step. Register the builder
        /// once at setup with <see cref="NativeSharedAnchor.Register{TDesc,T}(WorldAccessor, System.Func{TDesc,T})"/>
        /// (the factory registry is shared across the sim and input pointer types).
        /// <para>
        /// Unlike the simulation-side <see cref="NativeSharedPtr.Acquire{TDesc,T}(WorldAccessor, in TDesc)"/>,
        /// the registered source is ambient (input is not simulation state, so the sim cannot
        /// resolve the id until it justifies it — see the input→sim conversion
        /// <see cref="NativeSharedPtr.Acquire{T}(WorldAccessor, InputNativeSharedPtr{T})"/>); the
        /// descriptor is recorded into the input stream so a fresh-process replay can re-derive the
        /// blob.
        /// </para>
        /// </summary>
        public static InputNativeSharedPtr<T> Acquire<TDesc, T>(
            WorldAccessor world,
            in TDesc descriptor
        )
            where T : unmanaged
        {
            world.AssertCanAddInputsHeap();
            return world.InputNativeSharedHeap.AcquireFromDescriptor<TDesc, T>(
                world.FixedFrame,
                in descriptor
            );
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
            return ToRead(world.InputNativeSharedHeap.ResolveEntry<T>(BlobId));
        }

        public readonly NativeSharedRead<T> Read(in NativeWorldAccessor world)
        {
            return ToRead(world.InputSharedPtrResolver.ResolveEntry<T>(BlobId));
        }

        // Input native shared reads pass slot: null / capturedGeneration: 0 — they intentionally opt
        // out of the slot/generation use-after-free check the simulation NativeSharedPtr uses,
        // relying instead on the input phase model (allocation in the input phase, Burst reads in the
        // fixed phase, never concurrent) plus the AtomicSafetyHandle in checks builds.
        [MethodImpl(MethodImplOptions.AggressiveInlining)]
        static NativeSharedRead<T> ToRead(in InputNativeSharedHeapEntry entry)
        {
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
