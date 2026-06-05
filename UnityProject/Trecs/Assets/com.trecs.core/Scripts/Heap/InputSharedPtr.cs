using System;
using System.Runtime.CompilerServices;
using Trecs.Internal;

namespace Trecs
{
    /// <summary>
    /// Static factories for <see cref="InputSharedPtr{T}"/>. Allocation is
    /// gated to input-role accessors; the input heap releases the blob's
    /// refcount when the allocating frame is trimmed.
    /// </summary>
    public static class InputSharedPtr
    {
        /// <summary>
        /// Content-addressed eager input alloc: derives the <see cref="BlobId"/> by serializing and
        /// hashing <paramref name="value"/> (so identical content dedups and the id is stable across
        /// machines/runs), makes it resident, and returns a frame-scoped <see cref="InputSharedPtr{T}"/>.
        /// The input-pipeline mirror of <see cref="SharedPtr.Alloc{T}(WorldAccessor, T)"/> — you never
        /// name the blob. Describe it with a descriptor builder instead (and
        /// <see cref="Acquire{TDesc,T}(WorldAccessor, in TDesc)"/>) if it is cheaply derivable; the
        /// blob has no source, so it serializes into a recording as an opaque (eager) blob whose bytes
        /// are persisted.
        /// </summary>
        public static InputSharedPtr<T> Alloc<T>(WorldAccessor world, T value)
            where T : class
        {
            world.AssertCanAddInputsHeap();
            return world.InputSharedHeap.Alloc<T>(world.FixedFrame, value);
        }

        public static InputSharedPtr<T> Acquire<T>(WorldAccessor world, BlobId blobId)
            where T : class
        {
            world.AssertCanAddInputsHeap();
            return world.InputSharedHeap.Acquire<T>(world.FixedFrame, blobId);
        }

        /// <summary>
        /// Interns <paramref name="descriptor"/> — hashing it to a content-derived
        /// <see cref="BlobId"/>, deduplicating against the cache, and running the registered builder
        /// on a miss — and acquires a frame-scoped input handle in one step. Register the builder
        /// once at setup with <see cref="SharedAnchor.Register{TDesc,T}(WorldAccessor, System.Func{TDesc,T})"/>
        /// (the factory registry is shared across the sim and input pointer types).
        /// <para>
        /// Unlike the simulation-side <see cref="SharedPtr.Acquire{TDesc,T}(WorldAccessor, in TDesc)"/>,
        /// the registered source is ambient (input is not simulation state, so the sim cannot
        /// resolve the id until it justifies it — see the input→sim conversion
        /// <see cref="SharedPtr.Acquire{T}(WorldAccessor, InputSharedPtr{T})"/>); the descriptor is
        /// recorded into the input stream so a fresh-process replay can re-derive the blob.
        /// </para>
        /// </summary>
        public static InputSharedPtr<T> Acquire<TDesc, T>(WorldAccessor world, in TDesc descriptor)
            where T : class
        {
            world.AssertCanAddInputsHeap();
            return world.InputSharedHeap.AcquireFromDescriptor<TDesc, T>(
                world.FixedFrame,
                in descriptor
            );
        }

        public static bool TryAcquire<T>(
            WorldAccessor world,
            BlobId blobId,
            out InputSharedPtr<T> ptr
        )
            where T : class
        {
            world.AssertCanAddInputsHeap();
            return world.InputSharedHeap.TryAcquire<T>(world.FixedFrame, blobId, out ptr);
        }
    }

    /// <summary>
    /// Reference-counted pointer to a managed shared blob, allocated through
    /// the input pipeline. The object lives in the shared <see cref="BlobCache"/>;
    /// the lifetime of this reference is bound to the allocating input frame.
    /// When the frame is trimmed, the input heap releases its refcount and the
    /// cache evicts the object if no other reference exists.
    ///
    /// <para>Distinct from <see cref="SharedPtr{T}"/>: the type-level split
    /// encodes the lifetime contract — input pointers can only be allocated
    /// from input-role accessors, cannot be manually disposed, and source-gen
    /// rejects them in <c>[Input(MissingInputBehavior.Retain)]</c> fields.</para>
    /// </summary>
    public readonly struct InputSharedPtr<T> : IEquatable<InputSharedPtr<T>>
        where T : class
    {
        public readonly BlobId BlobId;

        internal InputSharedPtr(BlobId blobId)
        {
            BlobId = blobId;
        }

        public readonly bool IsNull
        {
            [MethodImpl(MethodImplOptions.AggressiveInlining)]
            get { return BlobId.IsNull; }
        }

        /// <summary>
        /// Resolves the managed value. Throws if the blob has been evicted
        /// (a Retain-style misuse pattern that source-gen catches at compile
        /// time, but checked at runtime too as defense in depth).
        /// </summary>
        [MethodImpl(MethodImplOptions.AggressiveInlining)]
        public readonly T Get(WorldAccessor world)
        {
            TrecsDebugAssert.That(!IsNull, "Cannot Get on a null InputSharedPtr");
            return world.BlobCache.GetManagedBlob<T>(BlobId);
        }

        [MethodImpl(MethodImplOptions.AggressiveInlining)]
        public readonly bool TryGet(WorldAccessor world, out T value)
        {
            if (IsNull)
            {
                value = null;
                return false;
            }
            return world.BlobCache.TryGetManagedBlob<T>(BlobId, out value);
        }

        public readonly bool Equals(InputSharedPtr<T> other) => BlobId.Equals(other.BlobId);

        public override readonly bool Equals(object obj) =>
            obj is InputSharedPtr<T> other && Equals(other);

        public override readonly int GetHashCode() => BlobId.GetHashCode();

        public static bool operator ==(InputSharedPtr<T> l, InputSharedPtr<T> r) => l.Equals(r);

        public static bool operator !=(InputSharedPtr<T> l, InputSharedPtr<T> r) => !l.Equals(r);
    }
}
