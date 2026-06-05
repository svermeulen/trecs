using System;
using System.Runtime.CompilerServices;
using Trecs.Internal;

namespace Trecs
{
    /// <summary>
    /// Static factories for <see cref="SharedPtr{T}"/>. Per-instance operations
    /// (<c>Get</c>, <c>TryGet</c>, <c>CanGet</c>, <c>Clone</c>, <c>Dispose</c>)
    /// live on the struct itself.
    /// </summary>
    public static class SharedPtr
    {
        // ─── Registration (mirrored from SharedAnchor so the lifecycle reads on one type) ────────
        // Registering a blob source / descriptor builder mutates the BlobFactory registry, which
        // lives outside the snapshotted world — so these gate on AssertBlobRegistrationOpen (a
        // setup-time gate), not AssertCanMutateHeap.

        /// <summary>
        /// Registers a managed blob under <paramref name="blobId"/> with a builder that produces it
        /// on first access. Acquire a refcounted handle separately via
        /// <see cref="Acquire{T}(WorldAccessor, BlobId)"/>.
        /// </summary>
        public static void Register<T>(WorldAccessor world, BlobId blobId, Func<T> factory)
            where T : class
        {
            world.AssertBlobRegistrationOpen();
            world.BlobFactory.RegisterManagedBlob<T>(blobId, factory);
        }

        /// <summary>
        /// Registers a managed blob from an eager <paramref name="value"/> — sugar for a constant
        /// builder.
        /// </summary>
        public static void Register<T>(WorldAccessor world, BlobId blobId, T value)
            where T : class
        {
            world.AssertBlobRegistrationOpen();
            world.BlobFactory.RegisterManagedBlob<T>(blobId, value);
        }

        /// <summary>
        /// Registers the per-descriptor-type builder for a <b>derivable</b> managed blob. Call once
        /// at setup; exactly one builder per descriptor type <typeparamref name="TDesc"/>. Acquire a
        /// handle from a descriptor with <see cref="Acquire{TDesc,T}(WorldAccessor, in TDesc)"/>.
        /// <para><b>Determinism contract:</b> the builder must be a pure function of its descriptor.</para>
        /// </summary>
        public static void Register<TDesc, T>(WorldAccessor world, Func<TDesc, T> builder)
            where T : class
        {
            world.AssertBlobRegistrationOpen();
            world.BlobFactory.AddFactory<TDesc>(
                new ManagedDescriptorBlobFactory<TDesc, T>(builder)
            );
        }

        /// <summary>
        /// Returns a fresh reference-counted handle to the blob registered at
        /// <paramref name="blobId"/>, throwing if no blob is registered there. Materializes the
        /// data lazily on first <c>Get</c>. Register the blob first with
        /// <see cref="Register{T}(WorldAccessor, BlobId, System.Func{T})"/>.
        /// </summary>
        public static SharedPtr<T> Acquire<T>(WorldAccessor world, BlobId blobId)
            where T : class
        {
            world.AssertCanMutateHeap();
            return world.SharedHeap.GetBlobById<T>(blobId);
        }

        /// <summary>
        /// Interns <paramref name="descriptor"/> — hashing it to a content-derived
        /// <see cref="BlobId"/>, deduplicating against the cache, and running the registered builder
        /// only on a miss — and returns a fresh reference-counted handle in one step. Register the
        /// builder first with the descriptor-taking
        /// <see cref="SharedAnchor.Register{TDesc,T}(WorldAccessor, System.Func{TDesc,T})"/> overload.
        /// <para>
        /// <b>Hot-path discipline:</b> hashing a descriptor is not free (it serializes the
        /// descriptor). Acquire once and <see cref="SharedPtr{T}.Clone(WorldAccessor)"/> the handle
        /// on subsequent frames rather than re-acquiring from the descriptor inside an inner loop.
        /// </para>
        /// </summary>
        public static SharedPtr<T> Acquire<TDesc, T>(WorldAccessor world, in TDesc descriptor)
            where T : class
        {
            world.AssertCanMutateHeap();
            var id = world.BlobFactory.Intern(in descriptor);
            return Acquire<T>(world, id);
        }

        /// <summary>
        /// The fallible counterpart to <see cref="Acquire{T}(WorldAccessor, BlobId)"/>: returns true
        /// and a fresh reference-counted handle if a blob is <i>deterministically</i> reachable at
        /// <paramref name="blobId"/> — held by the shared heap, or backed by a deterministically
        /// registered source (incrementing its refcount — <c>Dispose</c> the handle when done) —
        /// otherwise false. The answer never depends on raw cache residency, so false is a stable,
        /// replayable result: a blob kept alive only by an ambient anchor (or merely still resident
        /// after eviction pressure elsewhere) answers false in every run of the same timeline.
        /// </summary>
        public static bool TryAcquire<T>(WorldAccessor world, BlobId blobId, out SharedPtr<T> ptr)
            where T : class
        {
            world.AssertCanMutateHeap();
            return world.SharedHeap.TryGetBlobById<T>(blobId, out ptr);
        }

        /// <summary>
        /// Converts an in-hand input payload into a simulation-owned refcounted handle — the
        /// blessed bridge from the input domain into simulation state. <paramref name="input"/>
        /// must be a pointer delivered to the simulation for the <b>current frame</b> (an
        /// <c>[Input]</c> component field): "delivered this frame" is reproduced exactly by a
        /// recording replay, which makes this conversion deterministic where a by-id
        /// <see cref="Acquire{T}(WorldAccessor, BlobId)"/> against an input-heap blob is not
        /// (input-heap <i>retention</i> is history-locker-dependent — a recorder being attached
        /// keeps old frames alive longer — so "currently input-held" is not a deterministic
        /// predicate; only "in my hand this frame" is). A descriptor-backed input blob is promoted
        /// to deterministic here (its recipe is already journaled at intern time), so the sim's
        /// reference re-derives on a fresh-process load; eager input blobs ride the snapshot's
        /// opaque-blob section like any other sim-held eager blob.
        /// </summary>
        public static SharedPtr<T> Acquire<T>(WorldAccessor world, InputSharedPtr<T> input)
            where T : class
        {
            world.AssertCanMutateHeap();
            TrecsAssert.That(!input.IsNull, "Cannot acquire from a null InputSharedPtr");
            world.BlobFactory.PromoteToDeterministic(input.BlobId);
            return world.SharedHeap.PinResident<T>(input.BlobId);
        }

        /// <summary>
        /// Content-addressed eager store: derives the <see cref="BlobId"/> by serializing and hashing
        /// <paramref name="blob"/> (so identical content dedups and the id is stable across
        /// machines/runs), makes it resident, and returns a fresh refcounted <see cref="SharedPtr{T}"/>.
        /// The blessed default for opaque managed content computed on the fly — you never name the
        /// blob. Describe it with a descriptor builder instead if it is cheaply derivable.
        /// <para>
        /// <b>Hot-path discipline:</b> deriving the id serializes the whole blob, so this is not
        /// free even on a dedup hit. Alloc once and <see cref="SharedPtr{T}.Clone(WorldAccessor)"/>
        /// the handle on later frames rather than re-allocing identical content in an inner loop.
        /// </para>
        /// </summary>
        public static SharedPtr<T> Alloc<T>(WorldAccessor world, T blob)
            where T : class
        {
            world.AssertCanMutateHeap();
            // Insert + pin atomically: the just-allocated value (not cache state) is the
            // justification, so the pin bypasses the deterministic by-id resolve — which would
            // reject the freshly-inserted blob as sourceless.
            var id = world.BlobFactory.AllocManagedContentAddressed(blob);
            return world.SharedHeap.PinResident<T>(id);
        }
    }

    /// <summary>
    /// Reference-counted pointer to a shared managed (class) heap allocation. Multiple entities
    /// can hold a <see cref="SharedPtr{T}"/> referencing the same underlying object, identified
    /// by a <see cref="BlobId"/>. Register via <see cref="SharedAnchor.Register{T}(WorldAccessor, BlobId, System.Func{T})"/>;
    /// acquire a refcounted handle via <see cref="SharedPtr.Acquire{T}(WorldAccessor, BlobId)"/>.
    /// <para>
    /// Resolve the value with <see cref="Get(WorldAccessor)"/>.
    /// Cloning increments the reference count; disposing decrements it and frees when zero.
    /// </para>
    /// <para>
    /// <b>Identity is the <see cref="BlobId"/>.</b> The struct stores only its blob id; two
    /// <see cref="SharedPtr{T}"/>s that point at the same blob (e.g. one cloned from the other)
    /// are equal. This matches <see cref="NativeSharedPtr{T}"/>, whose equality is likewise
    /// blob-scoped. Use <see cref="Id"/> directly when you need the underlying id.
    /// </para>
    /// <para>
    /// <b>T must be marked <see cref="ImmutableAttribute"/></b> (or be one of the implicitly-
    /// allowed types like <c>string</c>) — enforced by the TRECS125 analyzer. Managed shared
    /// blobs live in the BlobCache, which is not snapshotted alongside game-state snapshots,
    /// so any post-Alloc mutation silently desyncs determinism. See <see cref="ImmutableAttribute"/>
    /// for the full contract and TRECS126 for what gets validated.
    /// </para>
    /// <para>
    /// Public verb set: <c>Get</c>, <c>TryGet</c>, <c>CanGet</c>, <c>Clone</c>,
    /// <c>Dispose</c>, <c>IsNull</c> (read the blob id directly via <see cref="Id"/>). The struct is
    /// itself <c>readonly</c>; all instance methods
    /// are marked <c>readonly</c> to match the rest of the pointer family — none of them mutate
    /// the ptr struct (heap state mutates instead).
    /// </para>
    /// </summary>
    public readonly struct SharedPtr<T> : IEquatable<SharedPtr<T>>
        where T : class
    {
        /// <summary>
        /// The <see cref="BlobId"/> of the underlying shared allocation, and the pointer's identity.
        /// </summary>
        public readonly BlobId Id;

        // Internal so external code can't fabricate a pointer to an arbitrary blob.
        // Allocation goes through SharedPtr.Acquire / Clone; deserialization paths
        // live in InternalsVisibleTo-allowed assemblies.
        internal SharedPtr(BlobId blobId)
        {
            Id = blobId;
        }

        [MethodImpl(MethodImplOptions.AggressiveInlining)]
        public readonly T Get(WorldAccessor world)
        {
            TrecsDebugAssert.That(!IsNull);

            if (world.SharedHeap.TryGetBlobDirect<T>(Id, out var result))
            {
                return result;
            }

            throw TrecsDebugAssert.CreateException("Failed to resolve SharedPtr with id {0}", Id);
        }

        [MethodImpl(MethodImplOptions.AggressiveInlining)]
        public readonly bool TryGet(WorldAccessor world, out T value)
        {
            if (IsNull)
            {
                value = null;
                return false;
            }

            if (world.SharedHeap.TryGetBlobDirect<T>(Id, out value))
            {
                return true;
            }

            value = null;
            return false;
        }

        public readonly bool CanGet(WorldAccessor world)
        {
            if (IsNull)
            {
                return false;
            }

            return world.SharedHeap.ContainsBlobDirect(Id);
        }

        [MethodImpl(MethodImplOptions.AggressiveInlining)]
        public readonly SharedPtr<T> Clone(WorldAccessor world)
        {
            world.AssertCanMutateHeap();
            if (IsNull)
            {
                return default;
            }
            if (!world.SharedHeap.TryClone<T>(Id, out var result))
            {
                throw TrecsDebugAssert.CreateException("Failed to clone SharedPtr with id {0}", Id);
            }
            return result;
        }

        public readonly void Dispose(WorldAccessor world)
        {
            world.AssertCanMutateHeap();
            world.SharedHeap.DecrementRef(Id);
        }

        public readonly bool IsNull
        {
            get { return Id.IsNull; }
        }

        /// <remarks>
        /// Equality is blob-scoped: two <see cref="SharedPtr{T}"/>s with the same
        /// <see cref="Id"/> are equal, including one cloned from the other. This is the same
        /// "same blob ⇒ equal" semantics as <see cref="NativeSharedPtr{T}"/>.
        /// </remarks>
        public readonly bool Equals(SharedPtr<T> other)
        {
            return Id.Equals(other.Id);
        }

        public override readonly bool Equals(object obj)
        {
            return obj is SharedPtr<T> other && Equals(other);
        }

        public override readonly int GetHashCode()
        {
            return Id.GetHashCode();
        }

        public static bool operator ==(SharedPtr<T> left, SharedPtr<T> right)
        {
            return left.Equals(right);
        }

        public static bool operator !=(SharedPtr<T> left, SharedPtr<T> right)
        {
            return !left.Equals(right);
        }
    }
}
