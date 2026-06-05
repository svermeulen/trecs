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
        // ─── Registration (mirrored from NativeSharedAnchor so the lifecycle reads on one type) ──
        // Registering a blob source / descriptor builder mutates the BlobFactory registry, which
        // lives outside the snapshotted world — so these gate on AssertBlobRegistrationOpen (a
        // setup-time gate), not AssertCanMutateHeap.

        /// <summary>
        /// Registers a native blob under <paramref name="blobId"/> with a <paramref name="factory"/>
        /// that builds the value on first access. The factory must be a pure function of inputs
        /// captured here, since the cache may evict and re-run it. Acquire a refcounted handle
        /// separately via <see cref="Acquire{T}(WorldAccessor, BlobId)"/>.
        /// </summary>
        public static void Register<T>(WorldAccessor world, BlobId blobId, Func<T> factory)
            where T : unmanaged
        {
            world.AssertBlobRegistrationOpen();
            world.BlobFactory.RegisterNativeBlob<T>(blobId, factory);
        }

        /// <summary>
        /// Registers a native blob from an eager <paramref name="value"/> — sugar for a constant
        /// factory.
        /// </summary>
        public static void Register<T>(WorldAccessor world, BlobId blobId, in T value)
            where T : unmanaged
        {
            world.AssertBlobRegistrationOpen();
            world.BlobFactory.RegisterNativeBlob<T>(blobId, in value);
        }

        /// <summary>
        /// Registers a native blob whose <paramref name="factory"/> allocates a native buffer and
        /// hands ownership to the cache (for variable-sized types whose allocation is larger than
        /// <c>sizeof(T)</c>). The factory must allocate a fresh buffer per call (it may run again
        /// after eviction).
        /// </summary>
        public static void Register<T>(
            WorldAccessor world,
            BlobId blobId,
            Func<NativeBlobAllocation> factory
        )
            where T : unmanaged
        {
            world.AssertBlobRegistrationOpen();
            world.BlobFactory.RegisterNativeBlobTakingOwnership<T>(blobId, factory);
        }

        /// <summary>
        /// Registers the per-descriptor-type builder for a <b>derivable</b> native blob. Call once
        /// at setup; exactly one builder per descriptor type <typeparamref name="TDesc"/>. Acquire a
        /// handle from a descriptor with <see cref="Acquire{TDesc,T}(WorldAccessor, in TDesc)"/>.
        /// <para><b>Determinism contract:</b> the builder must be a pure function of its descriptor.</para>
        /// </summary>
        public static void Register<TDesc, T>(WorldAccessor world, Func<TDesc, T> builder)
            where T : unmanaged
        {
            world.AssertBlobRegistrationOpen();
            world.BlobFactory.AddFactory<TDesc>(new NativeDescriptorBlobFactory<TDesc, T>(builder));
        }

        /// <summary>
        /// Registers the per-descriptor-type builder for a <b>derivable</b> native blob whose builder
        /// allocates its own native buffer and hands ownership to the cache (for variable-sized
        /// types). See the inline-value overload for the determinism contract.
        /// </summary>
        public static void Register<TDesc, T>(
            WorldAccessor world,
            Func<TDesc, NativeBlobAllocation> builder
        )
            where T : unmanaged
        {
            world.AssertBlobRegistrationOpen();
            world.BlobFactory.AddFactory<TDesc>(
                new NativeOwnershipDescriptorBlobFactory<TDesc, T>(builder)
            );
        }

        public static NativeSharedPtr<T> Acquire<T>(WorldAccessor world, BlobId blobId)
            where T : unmanaged
        {
            world.AssertCanMutateHeap();
            return world.NativeSharedHeap.GetBlob<T>(blobId);
        }

        /// <summary>
        /// Interns <paramref name="descriptor"/> — hashing it to a content-derived
        /// <see cref="BlobId"/>, deduplicating against the cache, and running the registered builder
        /// only on a miss — and returns a fresh reference-counted handle in one step. Register the
        /// builder first with the descriptor-taking
        /// <see cref="NativeSharedAnchor.Register{TDesc,T}(WorldAccessor, Func{TDesc,T})"/> (or
        /// <see cref="NativeSharedAnchor.Register{TDesc,T}(WorldAccessor, Func{TDesc,NativeBlobAllocation})"/>
        /// for variable-sized blobs).
        /// <para>
        /// <b>Hot-path discipline:</b> hashing a descriptor is not free (it serializes the
        /// descriptor). Acquire once and <see cref="NativeSharedPtr{T}.Clone(WorldAccessor)"/> the
        /// handle on subsequent frames rather than re-acquiring from the descriptor inside an inner
        /// loop.
        /// </para>
        /// </summary>
        public static NativeSharedPtr<T> Acquire<TDesc, T>(WorldAccessor world, in TDesc descriptor)
            where T : unmanaged
        {
            world.AssertCanMutateHeap();
            var id = world.BlobFactory.Intern(in descriptor);
            return Acquire<T>(world, id);
        }

        /// <summary>
        /// The fallible counterpart to <see cref="Acquire{T}(WorldAccessor, BlobId)"/>: returns true
        /// and a fresh reference-counted handle if a blob is <i>deterministically</i> reachable at
        /// <paramref name="blobId"/> — held by the native shared heap, or backed by a
        /// deterministically registered source (incrementing its refcount — <c>Dispose</c> the
        /// handle when done) — otherwise false. The answer never depends on raw cache residency, so
        /// false is a stable, replayable result; see
        /// <see cref="SharedPtr.TryAcquire{T}(WorldAccessor, BlobId, out SharedPtr{T})"/>.
        /// </summary>
        public static bool TryAcquire<T>(
            WorldAccessor world,
            BlobId blobId,
            out NativeSharedPtr<T> ptr
        )
            where T : unmanaged
        {
            world.AssertCanMutateHeap();
            return world.NativeSharedHeap.TryGetBlob<T>(blobId, out ptr);
        }

        /// <summary>
        /// Converts an in-hand input payload into a simulation-owned refcounted handle — the
        /// blessed bridge from the input domain into simulation state; the unmanaged counterpart
        /// to <see cref="SharedPtr.Acquire{T}(WorldAccessor, InputSharedPtr{T})"/> (see it for the
        /// full determinism rationale). <paramref name="input"/> must be a pointer delivered to the
        /// simulation for the <b>current frame</b> (an <c>[Input]</c> component field).
        /// </summary>
        public static NativeSharedPtr<T> Acquire<T>(
            WorldAccessor world,
            InputNativeSharedPtr<T> input
        )
            where T : unmanaged
        {
            world.AssertCanMutateHeap();
            TrecsAssert.That(!input.IsNull, "Cannot acquire from a null InputNativeSharedPtr");
            world.BlobFactory.PromoteToDeterministic(input.BlobId);
            return world.NativeSharedHeap.PinResident<T>(input.BlobId);
        }

        /// <summary>
        /// Eagerly creates a shared native blob under <paramref name="blobId"/>, taking ownership of
        /// <paramref name="alloc"/>, and returns a pinning handle in one step. Unlike the
        /// taking-ownership <see cref="NativeSharedAnchor.Register{T}(WorldAccessor, BlobId, Func{NativeBlobAllocation})"/>
        /// there is no factory: the bytes already exist, so
        /// the blob is <i>not</i> re-creatable from a registered source — it lingers until the cache
        /// LRU-evicts it and is then forgotten (re-create it the same way on a later miss). Use this
        /// for pre-built native blobs that are not cheaply derivable from a small descriptor —
        /// compound colliders (whose recipe references other blobs) and baked meshes / convex hulls.
        /// Cheap, content-addressed derivable blobs should instead register a descriptor builder via
        /// the descriptor-taking <c>Register</c> overloads and acquire through
        /// <see cref="Acquire{TDesc,T}(WorldAccessor, in TDesc)"/>, which keeps them re-derivable and
        /// journaled.
        /// </summary>
        public static NativeSharedPtr<T> Alloc<T>(
            WorldAccessor world,
            BlobId blobId,
            NativeBlobAllocation alloc
        )
            where T : unmanaged
        {
            world.AssertCanMutateHeap();
            return world.NativeSharedHeap.CreateBlobTakingOwnership<T>(blobId, alloc);
        }

        /// <summary>
        /// Content-addressed eager store: the <see cref="BlobId"/> is the xxHash64 of
        /// <paramref name="value"/>'s bytes, so identical content dedups to one blob and the id is
        /// stable across machines/runs. The blessed default — you never name the blob. Pass an
        /// explicit id only when an external pipeline already has a content-unique id for these bytes.
        /// <para>
        /// <b>Hot-path discipline:</b> deriving the id hashes the bytes (O(size)) even on a dedup hit
        /// — Alloc once and <see cref="NativeSharedPtr{T}.Clone(WorldAccessor)"/> the handle rather
        /// than re-allocing identical content in an inner loop.
        /// </para>
        /// </summary>
        public static NativeSharedPtr<T> Alloc<T>(WorldAccessor world, in T value)
            where T : unmanaged
        {
            world.AssertCanMutateHeap();
            // Insert + pin atomically: the just-allocated value (not cache state) is the
            // justification, so the pin bypasses the deterministic by-id resolve — which would
            // reject the freshly-inserted blob as sourceless.
            var id = world.BlobCache.EnsureNativeBlobContentAddressed<T>(in value);
            return world.NativeSharedHeap.PinResident<T>(id);
        }

        /// <summary>
        /// Content-addressed, taking-ownership counterpart to <see cref="Alloc{T}(WorldAccessor, in T)"/>
        /// for variable-sized blobs: the id is the xxHash64 of <paramref name="alloc"/>'s bytes.
        /// </summary>
        public static NativeSharedPtr<T> Alloc<T>(WorldAccessor world, NativeBlobAllocation alloc)
            where T : unmanaged
        {
            world.AssertCanMutateHeap();
            var id = world.BlobCache.EnsureNativeBlobContentAddressedTakingOwnership<T>(alloc);
            return world.NativeSharedHeap.PinResident<T>(id);
        }
    }

    /// <summary>
    /// Reference-counted pointer to a shared native (unmanaged) heap allocation. Burst-compatible.
    /// Multiple entities can reference the same data via <see cref="BlobId"/>.
    ///
    /// <para>
    /// Register via <see cref="NativeSharedAnchor.Register{T}(WorldAccessor, BlobId, Func{T})"/>;
    /// acquire a refcounted handle via <see cref="NativeSharedPtr.Acquire{T}(WorldAccessor, BlobId)"/>.
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
