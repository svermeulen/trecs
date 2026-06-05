using System;
using System.Runtime.CompilerServices;
using Trecs.Internal;
using Unity.Mathematics;

namespace Trecs
{
    /// <summary>
    /// Static factories for <see cref="SharedAnchor{T}"/>. Per-instance operations
    /// (<c>Get</c>, <c>TryGet</c>, <c>CanGet</c>, <c>Clone</c>, <c>Dispose</c>) live on
    /// the struct itself. The whole WorldAccessor surface gates on
    /// AssertAmbientAnchorAccess: anchors are <i>ambient</i> holds, invisible to snapshots
    /// and replay, so Fixed-role (deterministic simulation) accessors may not use them —
    /// see <see cref="NativeSharedAnchor"/> for the same contract on the unmanaged side.
    /// </summary>
    public static class SharedAnchor
    {
        /// <summary>
        /// Registers a managed blob under <paramref name="blobId"/> in
        /// <paramref name="factory"/> with a builder that produces it on first access.
        /// Registration declares the blob — acquire a pinning handle separately with
        /// <see cref="Acquire{T}(BlobFactory, BlobId)"/>. This is the <see cref="BlobFactory"/>-layer
        /// counterpart to <see cref="Register{T}(WorldAccessor, BlobId, System.Func{T})"/> —
        /// use it when the caller's anchor lifetime is independent of any ECS refcount (e.g.
        /// startup seeders, async preloaders).
        /// </summary>
        internal static void Register<T>(BlobFactory factory, BlobId blobId, Func<T> builder)
            where T : class
        {
            factory.RegisterManagedBlob<T>(blobId, builder);
        }

        /// <summary>
        /// Registers a managed blob from an eager <paramref name="value"/> — sugar for a constant
        /// builder. See <see cref="Register{T}(BlobFactory, BlobId, System.Func{T})"/>.
        /// </summary>
        internal static void Register<T>(BlobFactory factory, BlobId blobId, T value)
            where T : class
        {
            factory.RegisterManagedBlob<T>(blobId, value);
        }

        /// <summary>
        /// Returns a fresh pinning <see cref="SharedAnchor{T}"/> for the managed blob registered at
        /// <paramref name="blobId"/>, throwing if no blob is registered there.
        /// </summary>
        internal static SharedAnchor<T> Acquire<T>(BlobFactory factory, BlobId blobId)
            where T : class
        {
            return factory.AcquireSharedAnchor<T>(blobId);
        }

        /// <summary>
        /// Returns true and a fresh pinning <see cref="SharedAnchor{T}"/> if a managed blob
        /// exists at <paramref name="blobId"/>; otherwise false.
        /// </summary>
        internal static bool TryGet<T>(BlobFactory factory, BlobId blobId, out SharedAnchor<T> ptr)
            where T : class
        {
            return factory.TryAcquireSharedAnchor<T>(blobId, out ptr);
        }

        // ─── WorldAccessor surface (the non-simulation cache layer) ──────
        // Registering a blob source or a descriptor builder is NOT simulation state: it mutates
        // the BlobCache / BlobFactory factory registry, both of which live outside the snapshotted
        // world. So Register does NOT gate on AssertCanMutateHeap (that guard is for fixed-state
        // mutations) — it has its own registration-window seal. Contrast SharedPtr, whose Acquire
        // takes an ECS refcount that IS part of the snapshot. Acquiring a SharedAnchor only pins
        // bytes in the cache (no refcount), so it never lands in a snapshot either — which is
        // exactly why every WorldAccessor anchor entry point below, reads included, gates on
        // AssertAmbientAnchorAccess: an anchor is an *ambient* hold, invisible to snapshots and
        // replay, so Fixed-role (deterministic simulation) accessors may not touch one.
        // Unrestricted / Variable / Input contexts are the intended holders.

        /// <summary>
        /// Registers a managed blob under <paramref name="blobId"/> in <paramref name="world"/>'s
        /// shared heap with a factory that builds it on first access. Registration declares the
        /// blob — acquire a pinning handle separately with
        /// <see cref="Acquire{T}(WorldAccessor, BlobId)"/>. Use it when the caller's anchor
        /// lifetime is independent of any ECS refcount (e.g. startup seeders, async preloaders).
        /// </summary>
        public static void Register<T>(WorldAccessor world, BlobId blobId, Func<T> factory)
            where T : class
        {
            world.AssertBlobRegistrationOpen();
            world.BlobFactory.RegisterManagedBlob<T>(blobId, factory);
        }

        /// <summary>
        /// Registers a managed blob from an eager <paramref name="value"/> — sugar for a constant
        /// factory. See <see cref="Register{T}(WorldAccessor, BlobId, System.Func{T})"/>.
        /// </summary>
        public static void Register<T>(WorldAccessor world, BlobId blobId, T value)
            where T : class
        {
            world.AssertBlobRegistrationOpen();
            world.BlobFactory.RegisterManagedBlob<T>(blobId, value);
        }

        /// <summary>
        /// Registers the per-descriptor-type builder for a <b>derivable</b> managed blob: data
        /// computed from a small descriptor. Call once at setup; exactly one builder may be
        /// registered per descriptor type <typeparamref name="TDesc"/>. Acquire a handle from a
        /// descriptor with <see cref="SharedPtr.Acquire{TDesc,T}(WorldAccessor, in TDesc)"/>, which
        /// hashes the descriptor to a content-derived id, deduplicates against the cache, and runs
        /// this builder only on a miss.
        /// <para>
        /// <b>Determinism contract:</b> the builder must be a pure function of its descriptor — it
        /// must not read mutable world state — because the cache may evict a blob and re-run the
        /// builder. The descriptor type must be registered for serialization (it is hashed via
        /// <see cref="UniqueHashGenerator"/>). Registering the builder is not simulation state; the
        /// journaling that makes a derived blob snapshot-restorable happens at
        /// <see cref="SharedPtr.Acquire{TDesc,T}(WorldAccessor, in TDesc)"/> (intern) time.
        /// </para>
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
        /// Returns a fresh pinning <see cref="SharedAnchor{T}"/> for the managed blob registered at
        /// <paramref name="blobId"/>, throwing if no blob is registered there.
        /// </summary>
        public static SharedAnchor<T> Acquire<T>(WorldAccessor world, BlobId blobId)
            where T : class
        {
            world.AssertAmbientAnchorAccess();
            return world.BlobFactory.AcquireSharedAnchor<T>(blobId);
        }

        /// <summary>
        /// Interns <paramref name="descriptor"/> — hashing it to a content-derived
        /// <see cref="BlobId"/>, deduplicating against the cache, and running the registered builder
        /// only on a miss — and returns a fresh pinning <see cref="SharedAnchor{T}"/> in one step.
        /// The anchor counterpart to <see cref="SharedPtr.Acquire{TDesc,T}(WorldAccessor, in TDesc)"/>:
        /// use it when a non-ECS holder (a startup seeder, an async preloader) pins a <i>derivable</i>
        /// blob. Register the builder first with the descriptor-taking
        /// <see cref="Register{TDesc,T}(WorldAccessor, System.Func{TDesc,T})"/> overload.
        /// <para>
        /// <b>Hot-path discipline:</b> hashing a descriptor serializes it, so acquire once and
        /// <see cref="SharedAnchor{T}.Clone(WorldAccessor)"/> the handle rather than re-acquiring from
        /// the descriptor in an inner loop.
        /// </para>
        /// </summary>
        public static SharedAnchor<T> Acquire<TDesc, T>(WorldAccessor world, in TDesc descriptor)
            where T : class
        {
            world.AssertAmbientAnchorAccess();
            // The intern's source tag follows the accessor context: an Unrestricted (setup/editor)
            // intern is deterministic — it happens identically every run, which is what makes the
            // seeder/warm-up pattern's blobs acquirable by id from simulation — while a
            // Variable/Input-context intern is ambient (sim-invisible until the sim justifies the
            // same descriptor itself; see BlobFactory._ambientSources).
            var id = world.BlobFactory.Intern(in descriptor, world.CanMutateHeap);
            return Acquire<T>(world, id);
        }

        /// <summary>
        /// The fallible counterpart to <see cref="Acquire{T}(WorldAccessor, BlobId)"/>: returns true
        /// and a fresh pinning <see cref="SharedAnchor{T}"/> if a managed blob exists at
        /// <paramref name="blobId"/>; otherwise false.
        /// </summary>
        public static bool TryAcquire<T>(
            WorldAccessor world,
            BlobId blobId,
            out SharedAnchor<T> ptr
        )
            where T : class
        {
            // No AssertCanMutateHeap (that gate is for the snapshotted ECS refcount taken by
            // SharedPtr), but the ambient-anchor role gate applies like every other anchor
            // Acquire/Alloc. Within ambient contexts this keeps its residency-probing semantics —
            // "is it resident right now?" is the intended primitive for async-readiness polling —
            // in deliberate contrast to the deterministic SharedPtr.TryAcquire.
            world.AssertAmbientAnchorAccess();
            return world.BlobFactory.TryAcquireSharedAnchor<T>(blobId, out ptr);
        }

        /// <summary>
        /// Eagerly stores <paramref name="blob"/> as a resident managed blob under
        /// <paramref name="blobId"/> and returns a pinning <see cref="SharedAnchor{T}"/> to it. Unlike
        /// <see cref="Register{T}(WorldAccessor, BlobId, T)"/>, this works after blob registration is
        /// sealed (post first Tick) — it's the runtime alloc path for content computed on the fly
        /// (e.g. a background task result), not a re-derivable registered source.
        /// </summary>
        public static SharedAnchor<T> Alloc<T>(WorldAccessor world, BlobId blobId, T blob)
            where T : class
        {
            world.AssertAmbientAnchorAccess();
            return world.BlobCache.AllocManagedBlob(blobId, blob);
        }

        /// <summary>
        /// Content-addressed eager store: derives the <see cref="BlobId"/> by serializing and hashing
        /// <paramref name="blob"/> (so identical content dedups to one blob and the id is stable
        /// across machines/runs), makes it resident, and returns a pinning <see cref="SharedAnchor{T}"/>.
        /// This is the blessed default — you never name the blob. Pass an explicit id only when an
        /// external pipeline already has a content-unique id for these bytes. The serialize cost is
        /// the price of an opaque blob; describe it with a descriptor builder instead if you can.
        /// <para>
        /// <b>Hot-path discipline:</b> deriving the id serializes the whole blob, so this is not free
        /// even on a dedup hit — Alloc once and clone the handle rather than re-allocing identical
        /// content in a loop.
        /// </para>
        /// </summary>
        public static SharedAnchor<T> Alloc<T>(WorldAccessor world, T blob)
            where T : class
        {
            world.AssertAmbientAnchorAccess();
            var id = world.BlobFactory.AllocManagedContentAddressed(blob);
            return new SharedAnchor<T>(world.BlobCache.CreateHandle(id), id);
        }

        /// <summary>
        /// True if a blob is currently resident in the cache under <paramref name="blobId"/>
        /// (any type, managed or native). Replaces the removed type-erased
        /// <c>BlobCache.Contains(BlobId)</c> for callers that only need a residency check.
        /// Role-gated like the rest of the anchor surface: residency is non-deterministic state,
        /// so probing it from a Fixed-role accessor throws — simulation code must never branch on
        /// what happens to be in the cache.
        /// </summary>
        public static bool IsResident(WorldAccessor world, BlobId blobId)
        {
            world.AssertAmbientAnchorAccess();
            return world.BlobCache.IsResident(blobId);
        }
    }

    /// <summary>
    /// Lower-level pinning pointer for a managed (class) blob in <see cref="BlobCache"/>.
    /// Most game code should use <see cref="SharedPtr{T}"/> — register the blob with
    /// <see cref="Register{T}(WorldAccessor, BlobId, System.Func{T})"/> and acquire via
    /// <see cref="SharedPtr.Acquire{T}(WorldAccessor, BlobId)"/>, which adds the ECS-side
    /// refcount layer on top of the cache. Reach for <see cref="SharedAnchor{T}"/> directly
    /// when you need to pin blob bytes outside the ECS refcount lifetime — for example,
    /// startup seeders that anchor blobs before any entity references them, or async
    /// preload from a non-ECS subsystem.
    /// </summary>
    public readonly struct SharedAnchor<T> : IEquatable<SharedAnchor<T>>, IBlobAnchor
        where T : class
    {
        public readonly PtrHandle Handle;
        public readonly BlobId BlobId;

        public SharedAnchor(PtrHandle handle, BlobId blobId)
        {
            Handle = handle;
            BlobId = blobId;
        }

        public static readonly SharedAnchor<T> Null = default;

        public readonly bool IsNull
        {
            get { return Handle.IsNull && BlobId == default; }
        }

        [MethodImpl(MethodImplOptions.AggressiveInlining)]
        internal T Get(BlobCache blobCache)
        {
            TrecsDebugAssert.That(!IsNull);
            TrecsDebugAssert.That(
                blobCache.ContainsHandle(Handle),
                "Attempted to Get from a disposed SharedAnchor"
            );
            return blobCache.GetManagedBlob<T>(BlobId);
        }

        [MethodImpl(MethodImplOptions.AggressiveInlining)]
        internal bool TryGet(BlobCache blobCache, out T value)
        {
            if (IsNull || !blobCache.ContainsHandle(Handle))
            {
                value = null;
                return false;
            }
            return blobCache.TryGetManagedBlob<T>(BlobId, out value);
        }

        internal bool CanGet(BlobCache blobCache)
        {
            if (IsNull)
                return false;
            return blobCache.ContainsHandle(Handle);
        }

        [MethodImpl(MethodImplOptions.AggressiveInlining)]
        internal SharedAnchor<T> Clone(BlobCache blobCache)
        {
            if (IsNull)
                return Null;
            var newHandle = blobCache.CreateHandle(BlobId);
            return new SharedAnchor<T>(newHandle, BlobId);
        }

        internal SharedAnchor<TTarget> Cast<TTarget>(BlobCache blobCache)
            where TTarget : class
        {
            if (IsNull)
            {
                return SharedAnchor<TTarget>.Null;
            }

            TrecsDebugAssert.That(
                blobCache.ContainsHandle(Handle),
                "Attempted to Cast a disposed SharedAnchor"
            );

#if DEBUG
            var actualType = blobCache.GetManagedBlobType(BlobId);
            TrecsDebugAssert.That(
                typeof(TTarget).IsAssignableFrom(actualType),
                "SharedAnchor cast failed: expected blob assignable to type {0} but found type {1}",
                typeof(TTarget),
                actualType
            );
#endif
            return new SharedAnchor<TTarget>(Handle, BlobId);
        }

        internal readonly void Dispose(BlobCache blobCache)
        {
            TrecsDebugAssert.That(!IsNull);
            blobCache.DisposeHandle(Handle);
        }

        // ─── WorldAccessor overloads ────────────────────────────────────
        // Mirror the BlobCache instance methods above, routed through the world so callers
        // never have to name the cache. The whole surface — reads included — gates on
        // AssertAmbientAnchorAccess: CanGet/TryGet are handle-liveness probes (non-deterministic
        // state), and a Fixed-role accessor holding an anchor at all means data crossed a domain
        // boundary outside the input stream.

        [MethodImpl(MethodImplOptions.AggressiveInlining)]
        public T Get(WorldAccessor world)
        {
            world.AssertAmbientAnchorAccess();
            return Get(world.BlobCache);
        }

        [MethodImpl(MethodImplOptions.AggressiveInlining)]
        public bool TryGet(WorldAccessor world, out T value)
        {
            world.AssertAmbientAnchorAccess();
            return TryGet(world.BlobCache, out value);
        }

        public bool CanGet(WorldAccessor world)
        {
            world.AssertAmbientAnchorAccess();
            return CanGet(world.BlobCache);
        }

        [MethodImpl(MethodImplOptions.AggressiveInlining)]
        public SharedAnchor<T> Clone(WorldAccessor world)
        {
            // Gated like the anchor Acquire/Alloc/TryAcquire factories: cloning creates an ambient
            // pin, which is legal from any phase *except* deterministic simulation (Fixed) — see
            // AssertAmbientAnchorAccess. Input-phase use (e.g. an async level loader managing
            // persistent-blob anchors) remains allowed.
            world.AssertAmbientAnchorAccess();
            return Clone(world.BlobCache);
        }

        public SharedAnchor<TTarget> Cast<TTarget>(WorldAccessor world)
            where TTarget : class
        {
            world.AssertAmbientAnchorAccess();
            return Cast<TTarget>(world.BlobCache);
        }

        public readonly void Dispose(WorldAccessor world)
        {
            // Gated for the same reason as Clone / the anchor factories: releasing an ambient pin
            // is an anchor lifecycle operation, off-limits to Fixed-role accessors.
            world.AssertAmbientAnchorAccess();
            Dispose(world.BlobCache);
        }

        public bool Equals(SharedAnchor<T> other)
        {
            return Handle.Equals(other.Handle) && BlobId.Equals(other.BlobId);
        }

        public override bool Equals(object obj)
        {
            return obj is SharedAnchor<T> other && Equals(other);
        }

        public override int GetHashCode()
        {
            return unchecked((int)math.hash(new int2(Handle.GetHashCode(), BlobId.GetHashCode())));
        }
    }
}
