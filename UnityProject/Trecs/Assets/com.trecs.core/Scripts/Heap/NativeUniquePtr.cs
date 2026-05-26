using System;
using System.Runtime.CompilerServices;
using System.Runtime.InteropServices;
using Trecs.Collections;
using Trecs.Internal;
using Unity.Collections.LowLevel.Unsafe;

namespace Trecs
{
    /// <summary>
    /// Static factories for <see cref="NativeUniquePtr{T}"/>. Per-instance operations
    /// (<c>Read</c>, <c>Write</c>, <c>Dispose</c>) live on the struct itself.
    ///
    /// <para>Each factory takes <c>[CallerFilePath]</c>/<c>[CallerLineNumber]</c> defaults
    /// the compiler fills at the user's call site; those propagate down to
    /// <see cref="NativeHeap.Alloc"/> so the Dispose-time leak warning can
    /// attribute leaks to the original allocation point.</para>
    /// </summary>
    public static class NativeUniquePtr
    {
        public static NativeUniquePtr<T> Alloc<T>(
            WorldAccessor world,
            in T value,
            [CallerFilePath] string callerFile = null,
            [CallerLineNumber] int callerLine = 0
        )
            where T : unmanaged
        {
            world.AssertCanMutateHeap();
            return Alloc<T>(world.NativeUniqueChunkStore, in value, callerFile, callerLine);
        }

        public static NativeUniquePtr<T> Alloc<T>(
            WorldAccessor world,
            [CallerFilePath] string callerFile = null,
            [CallerLineNumber] int callerLine = 0
        )
            where T : unmanaged
        {
            world.AssertCanMutateHeap();
            return Alloc<T>(world.NativeUniqueChunkStore, default, callerFile, callerLine);
        }

        internal static unsafe NativeUniquePtr<T> Alloc<T>(
            NativeHeap chunkStore,
            in T value,
            [CallerFilePath] string callerFile = null,
            [CallerLineNumber] int callerLine = 0
        )
            where T : unmanaged
        {
            var handle = chunkStore.Alloc(
                UnsafeUtility.SizeOf<T>(),
                UnsafeUtility.AlignOf<T>(),
                TypeId<NativeUniquePtr<T>>.Value.Value,
                out var address,
                callerFile,
                callerLine
            );
            UnsafeUtility.WriteArrayElement(address.ToPointer(), 0, value);
            return new NativeUniquePtr<T>(handle);
        }

        /// <summary>
        /// Takes ownership of an existing native allocation without copying.
        /// See <see cref="NativeUniquePtr{T}"/> docs for the ownership contract.
        /// </summary>
        public static NativeUniquePtr<T> AllocTakingOwnership<T>(
            WorldAccessor world,
            NativeBlobAllocation alloc,
            [CallerFilePath] string callerFile = null,
            [CallerLineNumber] int callerLine = 0
        )
            where T : unmanaged
        {
            world.AssertCanMutateHeap();
            return AllocTakingOwnership<T>(
                world.NativeUniqueChunkStore,
                alloc,
                callerFile,
                callerLine
            );
        }

        internal static NativeUniquePtr<T> AllocTakingOwnership<T>(
            NativeHeap chunkStore,
            NativeBlobAllocation alloc,
            [CallerFilePath] string callerFile = null,
            [CallerLineNumber] int callerLine = 0
        )
            where T : unmanaged
        {
            TrecsDebugAssert.That(alloc.Ptr != IntPtr.Zero, "AllocTakingOwnership: null pointer");
            var handle = chunkStore.AllocExternal(
                alloc.Ptr,
                alloc.AllocSize,
                alloc.Alignment,
                TypeId<NativeUniquePtr<T>>.Value.Value,
                callerFile,
                callerLine
            );
            return new NativeUniquePtr<T>(handle);
        }
    }

    /// <summary>
    /// Exclusive-ownership pointer to a native (unmanaged) heap allocation. Burst-compatible.
    /// <para>
    /// Allocate via <see cref="NativeUniquePtr.Alloc{T}(WorldAccessor, in T, string, int)"/>.
    /// Open a safety-checked view with <see cref="Read(WorldAccessor)"/> /
    /// <see cref="Write(WorldAccessor)"/> on the main thread, or
    /// <see cref="Read(in NativeHeapResolver)"/> /
    /// <see cref="Write(in NativeHeapResolver)"/> in Burst jobs. Frame-scoped variants
    /// are cleaned up automatically; persistent pointers must be disposed explicitly via
    /// <see cref="Dispose(WorldAccessor)"/>.
    /// </para>
    /// <para>
    /// The persistent struct stores only a <see cref="PtrHandle"/> — it is intentionally cheap
    /// to copy and store on components. Per-allocation <c>AtomicSafetyHandle</c>s live on the
    /// chunk store and are attached to the <see cref="NativeUniqueRead{T}"/> /
    /// <see cref="NativeUniqueWrite{T}"/> wrappers at Open time, so Unity's job-safety walker
    /// can detect cross-job read/write conflicts at schedule time. There is intentionally no
    /// <c>Clone</c> — exclusive ownership means duplicating the ptr would create two owners of
    /// the same heap entry.
    /// </para>
    /// </summary>
    [StructLayout(LayoutKind.Sequential, Pack = 1)]
    public readonly unsafe struct NativeUniquePtr<T> : IEquatable<NativeUniquePtr<T>>
        where T : unmanaged
    {
        public readonly PtrHandle Handle;

        // Internal so external code can't fabricate a handle from an arbitrary uint.
        // Allocation goes through NativeUniquePtr.Alloc; deserialization paths live
        // in InternalsVisibleTo-allowed assemblies.
        internal NativeUniquePtr(PtrHandle handle)
        {
            Handle = handle;
        }

        public readonly void Dispose(WorldAccessor world)
        {
            world.AssertCanMutateHeap();
            Dispose(world.NativeUniqueChunkStore);
        }

        internal readonly void Dispose(NativeHeap chunkStore)
        {
            TrecsDebugAssert.That(Handle.Value != 0);
            // Validate TypeId before freeing — catches accidental cross-heap dispose
            // (e.g. passing a TrecsList handle to NativeUniquePtr.Dispose, or a wrong-T
            // handle).
            var entry = chunkStore.ResolveEntry(Handle);
            AssertPersistentTypeHash(entry.TypeHash);
            chunkStore.Free(Handle);
        }

        /// <summary>
        /// Opens a safety-checked read view. Main-thread only; for in-job access use the
        /// <see cref="Read(in NativeHeapResolver)"/> overload.
        /// </summary>
        public readonly NativeUniqueRead<T> Read(WorldAccessor world)
        {
            return Read(world.NativeUniqueChunkStore.Resolver);
        }

        // Public Write overloads live on NativeUniquePtrExtensions as
        // `this ref NativeUniquePtr<T>` extension methods (bottom of file). The
        // implementation here is `internal readonly` — extensions delegate to it,
        // and the compile-time ref discipline is enforced at the extension's
        // `this ref` receiver. Same pattern as TrecsListExtensions.
        internal readonly NativeUniqueWrite<T> Write(WorldAccessor world)
        {
            world.AssertCanMutateHeap();
            // Pass the chunk store's raw (permissive) resolver since the role
            // check happened above; reaching here means we're already authorized.
            return WriteInternal(world.NativeUniqueChunkStore.Resolver);
        }

        /// <summary>
        /// Burst-compatible read view. Pass a resolver obtained via
        /// <see cref="WorldAccessor.NativeHeapResolver"/> or
        /// <see cref="NativeWorldAccessor.ChunkStoreResolver"/>.
        /// </summary>
        public readonly unsafe NativeUniqueRead<T> Read(in NativeHeapResolver resolver)
        {
            var entry = resolver.ResolveEntryWithSlotPtr(Handle, out var slot);
            AssertPersistentTypeHash(entry.TypeHash);
#if ENABLE_UNITY_COLLECTIONS_CHECKS
            return new NativeUniqueRead<T>(
                entry.Address.ToPointer(),
                slot,
                entry.Generation,
                entry.Safety
            );
#else
            return new NativeUniqueRead<T>(entry.Address.ToPointer(), slot, entry.Generation);
#endif
        }

        // Internal — public access goes through NativeUniquePtrExtensions.Write(this ref,
        // in NativeHeapResolver) so the ref discipline applies. Resolver
        // carries the originating accessor's role flag, so a Variable-role caller
        // fails fast at Open time.
        internal readonly NativeUniqueWrite<T> Write(in NativeHeapResolver resolver)
        {
            resolver.AssertCanMutateHeap();
            return WriteInternal(resolver);
        }

        // Permission-skipping inner Write used by callers that already passed the
        // role check (e.g. the main-thread Write(WorldAccessor) above, which
        // forwarded the chunk store's raw resolver).
        readonly unsafe NativeUniqueWrite<T> WriteInternal(in NativeHeapResolver resolver)
        {
            var entry = resolver.ResolveEntryWithSlotPtr(Handle, out var slot);
            AssertPersistentTypeHash(entry.TypeHash);
#if ENABLE_UNITY_COLLECTIONS_CHECKS
            return new NativeUniqueWrite<T>(
                entry.Address.ToPointer(),
                slot,
                entry.Generation,
                entry.Safety
            );
#else
            return new NativeUniqueWrite<T>(entry.Address.ToPointer(), slot, entry.Generation);
#endif
        }

        readonly void AssertPersistentTypeHash(int storedHash)
        {
            TrecsAssert.That(
                storedHash == TypeId<NativeUniquePtr<T>>.Value.Value,
                "NativeUniquePtr type-hash mismatch for handle {0}: stored {1} "
                    + "!= expected NativeUniquePtr<T>={2}",
                Handle.Value,
                storedHash,
                TypeId<NativeUniquePtr<T>>.Value.Value
            );
        }

        public readonly bool IsNull
        {
            get { return Handle.IsNull; }
        }

        /// <remarks>
        /// Equality compares only <see cref="Handle"/>. <see cref="NativeUniquePtr{T}"/> has no
        /// separate blob ID — the handle <i>is</i> the identity (each handle uniquely owns
        /// one heap entry). The shared variants (<see cref="SharedPtr{T}"/> /
        /// <see cref="NativeSharedPtr{T}"/>) additionally compare a <see cref="BlobId"/>
        /// because multiple handles can reference the same underlying blob; that doesn't
        /// apply here.
        /// </remarks>
        public readonly bool Equals(NativeUniquePtr<T> other)
        {
            return Handle.Equals(other.Handle);
        }

        public override readonly bool Equals(object obj)
        {
            return obj is NativeUniquePtr<T> other && Equals(other);
        }

        public override readonly int GetHashCode()
        {
            return Handle.GetHashCode();
        }

        public static bool operator ==(NativeUniquePtr<T> left, NativeUniquePtr<T> right)
        {
            return left.Equals(right);
        }

        public static bool operator !=(NativeUniquePtr<T> left, NativeUniquePtr<T> right)
        {
            return !left.Equals(right);
        }
    }

    /// <summary>
    /// Mutating operations on <see cref="NativeUniquePtr{T}"/>. Each <c>Write</c>
    /// takes <c>this ref NativeUniquePtr&lt;T&gt;</c>, so the caller must hold
    /// writable access to the handle struct — calling <c>Write</c> through an
    /// <c>in</c> parameter, a <c>readonly</c> field, or an <c>IRead&lt;...&gt;</c>
    /// aspect field is a compile error. Same pattern as
    /// <see cref="TrecsListExtensions"/> and
    /// <see cref="FixedList16Extensions"/>.
    /// </summary>
    public static class NativeUniquePtrExtensions
    {
        /// <summary>
        /// Opens a main-thread write view. For in-job access use the
        /// <see cref="Write{T}(ref NativeUniquePtr{T}, in NativeHeapResolver)"/>
        /// overload.
        /// </summary>
        public static NativeUniqueWrite<T> Write<T>(
            this ref NativeUniquePtr<T> ptr,
            WorldAccessor world
        )
            where T : unmanaged => ptr.Write(world);

        /// <summary>
        /// Burst-compatible write view. Pass a resolver obtained via
        /// <see cref="WorldAccessor.NativeHeapResolver"/> or
        /// <see cref="NativeWorldAccessor.ChunkStoreResolver"/>.
        /// </summary>
        public static NativeUniqueWrite<T> Write<T>(
            this ref NativeUniquePtr<T> ptr,
            in NativeHeapResolver resolver
        )
            where T : unmanaged => ptr.Write(in resolver);
    }
}
