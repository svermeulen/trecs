using System;
using System.Runtime.CompilerServices;
using System.Runtime.InteropServices;
using Trecs.Internal;
using Unity.Mathematics;

namespace Trecs
{
    /// <summary>
    /// Static factories for <see cref="NativeSharedAnchor{T}"/> — the non-simulation cache layer for
    /// native blobs. Registering a blob source or descriptor builder mutates the BlobFactory's
    /// source registry, which lives outside the snapshotted world, so these do NOT gate on
    /// AssertCanMutateHeap (a fixed-state guard). Instead, the whole WorldAccessor surface —
    /// lifecycle (Acquire / TryAcquire / Alloc / Clone / Dispose) and reads (Get / TryGetPtr /
    /// CanGet) — gates on the mirror-image AssertAmbientAnchorAccess: anchors are <i>ambient</i>
    /// holds, invisible to snapshots and replay, so Fixed-role (deterministic simulation)
    /// accessors may not use them. Contrast <see cref="NativeSharedPtr"/>, whose Acquire takes a
    /// snapshotted ECS refcount.
    /// </summary>
    public static class NativeSharedAnchor
    {
        /// <summary>
        /// Registers a native blob under <paramref name="blobId"/> with a <paramref name="factory"/>
        /// that builds the value on first access. Registration declares the blob; acquire a handle
        /// separately (a cache pin via the cache layer, or a refcounted
        /// <see cref="NativeSharedPtr.Acquire{T}(WorldAccessor, BlobId)"/>). The factory must be a
        /// pure function of inputs captured here, since the cache may evict and re-run it.
        /// </summary>
        public static void Register<T>(WorldAccessor world, BlobId blobId, Func<T> factory)
            where T : unmanaged
        {
            world.AssertBlobRegistrationOpen();
            world.BlobFactory.RegisterNativeBlob<T>(blobId, factory);
        }

        /// <summary>
        /// Registers a native blob from an eager <paramref name="value"/> — sugar for a constant
        /// factory. See <see cref="Register{T}(WorldAccessor, BlobId, Func{T})"/>.
        /// </summary>
        public static void Register<T>(WorldAccessor world, BlobId blobId, in T value)
            where T : unmanaged
        {
            world.AssertBlobRegistrationOpen();
            world.BlobFactory.RegisterNativeBlob<T>(blobId, in value);
        }

        /// <summary>
        /// Registers a native blob whose <paramref name="factory"/> allocates a native buffer and
        /// hands ownership to the cache — the taking-ownership counterpart to
        /// <see cref="Register{T}(WorldAccessor, BlobId, Func{T})"/>, for variable-sized types whose
        /// allocation is larger than <c>sizeof(T)</c>. The factory must allocate a fresh buffer per
        /// call (it may run again after eviction).
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
        /// Registers the per-descriptor-type builder for a <b>derivable</b> native (unmanaged) blob.
        /// Call once at setup; exactly one builder per descriptor type <typeparamref name="TDesc"/>.
        /// The builder returns the value inline; use the taking-ownership overload
        /// (<see cref="Register{TDesc,T}(WorldAccessor, Func{TDesc,NativeBlobAllocation})"/>) for
        /// variable-sized blobs. Acquire a handle from a descriptor with
        /// <see cref="NativeSharedPtr.Acquire{TDesc,T}(WorldAccessor, in TDesc)"/>.
        /// <para>
        /// <b>Determinism contract:</b> the builder must be a pure function of its descriptor. The
        /// descriptor type must be registered for serialization (hashed via
        /// <see cref="UniqueHashGenerator"/>). Registering is not simulation state; the journaling
        /// that makes a derived blob snapshot-restorable happens at
        /// <see cref="NativeSharedPtr.Acquire{TDesc,T}(WorldAccessor, in TDesc)"/> (intern) time.
        /// </para>
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
        /// types). Call once at setup; the builder must allocate a fresh buffer per call. See the
        /// inline-value overload (<see cref="Register{TDesc,T}(WorldAccessor, Func{TDesc,T})"/>) for
        /// the determinism contract.
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

        /// <summary>
        /// Acquires a pinning <see cref="NativeSharedAnchor{T}"/> for the native blob registered at
        /// <paramref name="blobId"/>, throwing if none is registered. Mirrors
        /// <see cref="SharedAnchor.Acquire{T}(WorldAccessor, BlobId)"/> for the unmanaged case — use it
        /// to pin blob bytes outside the ECS refcount lifetime (e.g. async preload from a non-ECS
        /// subsystem). Dispose the returned handle with <see cref="NativeSharedAnchor{T}.Dispose(WorldAccessor)"/>.
        /// </summary>
        public static NativeSharedAnchor<T> Acquire<T>(WorldAccessor world, BlobId blobId)
            where T : unmanaged
        {
            world.AssertAmbientAnchorAccess();
            return world.BlobFactory.AcquireNativeSharedAnchor<T>(blobId);
        }

        /// <summary>
        /// Interns <paramref name="descriptor"/> — hashing it to a content-derived
        /// <see cref="BlobId"/>, deduplicating against the cache, and running the registered builder
        /// only on a miss — and returns a fresh pinning <see cref="NativeSharedAnchor{T}"/> in one
        /// step. The anchor counterpart to
        /// <see cref="NativeSharedPtr.Acquire{TDesc,T}(WorldAccessor, in TDesc)"/>: use it when a
        /// non-ECS holder (a startup seeder, an async preloader) pins a <i>derivable</i> native blob.
        /// Register the builder first with one of the descriptor-taking <c>Register&lt;TDesc, T&gt;</c>
        /// overloads.
        /// <para>
        /// <b>Hot-path discipline:</b> hashing a descriptor serializes it, so acquire once and
        /// <see cref="NativeSharedAnchor{T}.Clone(WorldAccessor)"/> the handle rather than re-acquiring
        /// from the descriptor in an inner loop.
        /// </para>
        /// </summary>
        public static NativeSharedAnchor<T> Acquire<TDesc, T>(
            WorldAccessor world,
            in TDesc descriptor
        )
            where T : unmanaged
        {
            world.AssertAmbientAnchorAccess();
            // The intern's source tag follows the accessor context — see
            // SharedAnchor.Acquire<TDesc,T> for the rationale.
            var id = world.BlobFactory.Intern(in descriptor, world.CanMutateHeap);
            return Acquire<T>(world, id);
        }

        /// <summary>
        /// The fallible counterpart to <see cref="Acquire{T}(WorldAccessor, BlobId)"/>: returns true
        /// and a fresh pinning <see cref="NativeSharedAnchor{T}"/> if a native blob exists at
        /// <paramref name="blobId"/>; otherwise false. The unmanaged counterpart to
        /// <see cref="SharedAnchor.TryAcquire{T}(WorldAccessor, BlobId, out SharedAnchor{T})"/>.
        /// </summary>
        public static bool TryAcquire<T>(
            WorldAccessor world,
            BlobId blobId,
            out NativeSharedAnchor<T> ptr
        )
            where T : unmanaged
        {
            // Role-gated like every anchor Acquire/Alloc, but within ambient contexts this keeps
            // its residency-probing semantics ("is it resident right now?" — the async-readiness
            // primitive), in deliberate contrast to the deterministic NativeSharedPtr.TryAcquire.
            world.AssertAmbientAnchorAccess();
            return world.BlobFactory.TryAcquireNativeSharedAnchor<T>(blobId, out ptr);
        }

        /// <summary>
        /// Eagerly stores a caller-allocated native buffer as a resident blob under
        /// <paramref name="blobId"/> (taking ownership of the allocation) and returns a pinning
        /// <see cref="NativeSharedAnchor{T}"/>. The runtime alloc path for unmanaged content computed on
        /// the fly (e.g. baked colliders); the unmanaged counterpart to
        /// <see cref="SharedAnchor.Alloc{T}(WorldAccessor, BlobId, T)"/>.
        /// </summary>
        public static NativeSharedAnchor<T> Alloc<T>(
            WorldAccessor world,
            BlobId blobId,
            NativeBlobAllocation alloc
        )
            where T : unmanaged
        {
            world.AssertAmbientAnchorAccess();
            return world.BlobCache.AllocNativeBlobTakingOwnership<T>(blobId, alloc);
        }

        /// <summary>
        /// Content-addressed eager store: the <see cref="BlobId"/> is the xxHash64 of
        /// <paramref name="value"/>'s bytes, so identical content dedups to one blob and the id is
        /// stable across machines/runs. The blessed default — you never name the blob.
        /// <para>
        /// <b>Hot-path discipline:</b> deriving the id hashes the bytes (O(size)) even on a dedup hit
        /// — Alloc once and clone the handle rather than re-allocing identical content in a loop.
        /// </para>
        /// </summary>
        public static NativeSharedAnchor<T> Alloc<T>(WorldAccessor world, in T value)
            where T : unmanaged
        {
            world.AssertAmbientAnchorAccess();
            return world.BlobCache.AllocNativeBlob<T>(in value);
        }

        /// <summary>
        /// Content-addressed, taking-ownership counterpart to <see cref="Alloc{T}(WorldAccessor, in T)"/>
        /// for variable-sized blobs: the id is the xxHash64 of <paramref name="alloc"/>'s bytes.
        /// </summary>
        public static NativeSharedAnchor<T> Alloc<T>(
            WorldAccessor world,
            NativeBlobAllocation alloc
        )
            where T : unmanaged
        {
            world.AssertAmbientAnchorAccess();
            return world.BlobCache.AllocNativeBlobTakingOwnership<T>(alloc);
        }
    }

    /// <summary>
    /// Lower-level pinning pointer for a native (unmanaged) blob in <see cref="BlobCache"/>.
    /// Resolves to a <c>ref readonly T</c> for direct read access — shared native blobs are
    /// immutable once materialized (mutating one would desync determinism, since the
    /// <see cref="BlobCache"/> is not snapshotted with game state). Most game code should use
    /// <see cref="NativeSharedPtr{T}"/> — register the blob with
    /// <see cref="Register{T}(WorldAccessor, BlobId, Func{T})"/> and acquire via
    /// <see cref="NativeSharedPtr.Acquire{T}(WorldAccessor, BlobId)"/>, which adds the
    /// ECS-side refcount layer (and Burst-resolvable lookup) on top of the cache. Reach
    /// for <see cref="NativeSharedAnchor{T}"/> directly when you need to pin blob bytes
    /// outside the ECS refcount lifetime — for example, async preload from a non-ECS
    /// subsystem.
    /// </summary>
    [StructLayout(LayoutKind.Sequential, Pack = 1)]
    public readonly struct NativeSharedAnchor<T> : IEquatable<NativeSharedAnchor<T>>, IBlobAnchor
        where T : unmanaged
    {
        public readonly PtrHandle Handle;
        public readonly BlobId BlobId;

        public NativeSharedAnchor(PtrHandle handle, BlobId blobId)
        {
            Handle = handle;
            BlobId = blobId;
        }

        public static readonly NativeSharedAnchor<T> Null = default;

        public readonly bool IsNull
        {
            get { return Handle.IsNull && BlobId == default; }
        }

        [MethodImpl(MethodImplOptions.AggressiveInlining)]
        internal ref readonly T Get(BlobCache blobCache)
        {
            TrecsDebugAssert.That(!IsNull);
            TrecsDebugAssert.That(
                blobCache.ContainsHandle(Handle),
                "Attempted to Get from a disposed NativeSharedAnchor"
            );
            return ref blobCache.GetNativeBlobRef<T>(BlobId);
        }

        [MethodImpl(MethodImplOptions.AggressiveInlining)]
        internal bool TryGetPtr(BlobCache blobCache, out IntPtr ptr)
        {
            if (IsNull || !blobCache.ContainsHandle(Handle))
            {
                ptr = IntPtr.Zero;
                return false;
            }
            return blobCache.TryGetNativeBlobPtr<T>(BlobId, out ptr);
        }

        internal bool CanGet(BlobCache blobCache)
        {
            if (IsNull)
                return false;
            return blobCache.ContainsHandle(Handle);
        }

        [MethodImpl(MethodImplOptions.AggressiveInlining)]
        internal NativeSharedAnchor<T> Clone(BlobCache blobCache)
        {
            if (IsNull)
                return Null;
            var newHandle = blobCache.CreateHandle(BlobId);
            return new NativeSharedAnchor<T>(newHandle, BlobId);
        }

        internal readonly void Dispose(BlobCache blobCache)
        {
            TrecsDebugAssert.That(!IsNull);
            blobCache.DisposeHandle(Handle);
        }

        // ─── WorldAccessor overloads ────────────────────────────────────
        // Mirror the BlobCache instance methods above, routed through the world so callers
        // never have to name the cache. The whole surface — reads included — gates on
        // AssertAmbientAnchorAccess: CanGet/TryGetPtr are handle-liveness probes
        // (non-deterministic state), and a Fixed-role accessor holding an anchor at all means
        // data crossed a domain boundary outside the input stream.

        [MethodImpl(MethodImplOptions.AggressiveInlining)]
        public ref readonly T Get(WorldAccessor world)
        {
            world.AssertAmbientAnchorAccess();
            return ref Get(world.BlobCache);
        }

        [MethodImpl(MethodImplOptions.AggressiveInlining)]
        public bool TryGetPtr(WorldAccessor world, out IntPtr ptr)
        {
            world.AssertAmbientAnchorAccess();
            return TryGetPtr(world.BlobCache, out ptr);
        }

        public bool CanGet(WorldAccessor world)
        {
            world.AssertAmbientAnchorAccess();
            return CanGet(world.BlobCache);
        }

        [MethodImpl(MethodImplOptions.AggressiveInlining)]
        public NativeSharedAnchor<T> Clone(WorldAccessor world)
        {
            // Gated like the anchor Acquire/Alloc/TryAcquire factories: cloning creates an ambient
            // pin, which is legal from any phase *except* deterministic simulation (Fixed) — see
            // AssertAmbientAnchorAccess. Input-phase use (e.g. an async level loader managing
            // persistent-blob anchors) remains allowed.
            world.AssertAmbientAnchorAccess();
            return Clone(world.BlobCache);
        }

        public readonly void Dispose(WorldAccessor world)
        {
            // Gated for the same reason as Clone / the anchor factories: releasing an ambient pin
            // is an anchor lifecycle operation, off-limits to Fixed-role accessors.
            world.AssertAmbientAnchorAccess();
            Dispose(world.BlobCache);
        }

        public bool Equals(NativeSharedAnchor<T> other)
        {
            return Handle.Equals(other.Handle) && BlobId.Equals(other.BlobId);
        }

        public override bool Equals(object obj)
        {
            return obj is NativeSharedAnchor<T> other && Equals(other);
        }

        public override int GetHashCode()
        {
            return unchecked((int)math.hash(new int2(Handle.GetHashCode(), BlobId.GetHashCode())));
        }
    }
}
