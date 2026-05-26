using System;
using System.Runtime.CompilerServices;
using System.Runtime.InteropServices;
using Trecs.Internal;
using Unity.Collections.LowLevel.Unsafe;

namespace Trecs
{
    /// <summary>
    /// Static factories for <see cref="TrecsArray{T}"/>. Allocation goes through here;
    /// per-instance operations (<c>Read</c>, <c>Write</c>, <c>Dispose</c>) live on the
    /// struct itself.
    ///
    /// <para>A single chunk-store slot tagged <c>TypeId&lt;TrecsArray&lt;T&gt;&gt;</c>
    /// holds the raw element bytes. <see cref="TrecsArray{T}.Length"/> rides inline on
    /// the handle struct, so <c>Read</c> / <c>Write</c> need only one
    /// <see cref="NativeHeapResolver.ResolveEntryWithSlotPtr"/> + one TypeId
    /// check to open a view. The trade-off is that the handle widens from 4 to 8
    /// bytes — see the <see cref="TrecsArray{T}"/> class doc for the rationale.</para>
    /// </summary>
    public static class TrecsArray
    {
        public static TrecsArray<T> Alloc<T>(
            WorldAccessor world,
            int length,
            [CallerFilePath] string callerFile = null,
            [CallerLineNumber] int callerLine = 0
        )
            where T : unmanaged
        {
            world.AssertCanMutateHeap();
            return Alloc<T>(world.NativeUniqueChunkStore, length, callerFile, callerLine);
        }

        internal static unsafe TrecsArray<T> Alloc<T>(
            NativeHeap chunkStore,
            int length,
            [CallerFilePath] string callerFile = null,
            [CallerLineNumber] int callerLine = 0
        )
            where T : unmanaged
        {
            TrecsDebugAssert.That(length >= 0, "length must be non-negative");

            // length == 0 case: don't allocate at all. default(TrecsArray<T>) already
            // represents the empty array (null handle, Length 0); a zero-length Alloc
            // returns that same shape so the indexer bounds check rejects every access
            // without needing a real slot. Matches what the bounds check would do anyway
            // and avoids spending a chunk-store slot to hold zero bytes.
            if (length == 0)
            {
                return new TrecsArray<T>(default, 0);
            }

            var elementSize = UnsafeUtility.SizeOf<T>();
            var byteSize = ByteSizeOrThrow(length, elementSize);
            var handle = chunkStore.Alloc(
                byteSize,
                UnsafeUtility.AlignOf<T>(),
                TypeId<TrecsArray<T>>.Value.Value,
                out _,
                callerFile,
                callerLine
            );

            return new TrecsArray<T>(handle, length);
        }

        // Computes the byte size for `length` elements of `elementSize` bytes each
        // and asserts it fits in `int` (the chunk store's allocation size type)
        // before returning. Mirrors TrecsList.ByteSizeOrThrow.
        internal static int ByteSizeOrThrow(int length, int elementSize)
        {
            var bytes = (long)length * elementSize;
            TrecsAssert.That(
                bytes <= int.MaxValue,
                "TrecsArray byte size overflow: {0} elements × {1} bytes = {2} exceeds "
                    + "int.MaxValue.",
                length,
                elementSize,
                bytes
            );
            return (int)bytes;
        }
    }

    /// <summary>
    /// A fixed-size array of unmanaged values whose storage is owned by the world's
    /// shared <see cref="NativeHeap"/>. Designed to live on ECS components, so a
    /// component can carry a variably-sized buffer (size chosen at allocation time, but
    /// fixed thereafter) without going through <see cref="NativeUniquePtr{T}"/>.
    ///
    /// <para>Use this when the size is known at allocation time and won't change. If
    /// the size needs to grow, use <see cref="TrecsList{T}"/> instead — or
    /// <see cref="Dispose(WorldAccessor)"/> this array and allocate a new one. For
    /// small (≤256) compile-time-fixed sizes, prefer <c>FixedArray2..256</c> which
    /// live inline on the component and avoid the chunk-store allocation entirely.</para>
    ///
    /// <para><b>Layout.</b> The handle is an 8-byte struct: a 4-byte
    /// <see cref="PtrHandle"/> pointing at the element-bytes slot, plus a 4-byte
    /// <see cref="Length"/>. Both fields are set at allocation time and never change,
    /// so the handle as a whole is immutable — no version stamp, no second resolve.
    /// Carrying <c>Length</c> on the handle (rather than in an in-slot header) means
    /// <c>Read</c> and <c>Write</c> perform a single <c>ResolveEntry</c> + one TypeId
    /// check to open a view, with the cached element pointer stable for the wrapper's
    /// lifetime.</para>
    ///
    /// <para>Read/write surfaces are a single Burst-safe wrapper pair, usable both
    /// on the main thread and inside <c>[BurstCompile]</c> jobs:</para>
    /// <list type="bullet">
    /// <item><description><see cref="TrecsArrayRead{T}"/> — read-only view, multiple
    ///   concurrent readers allowed.</description></item>
    /// <item><description><see cref="TrecsArrayWrite{T}"/> — write view, exclusive
    ///   access enforced by Unity's job-safety walker at <c>Schedule</c> time.</description></item>
    /// </list>
    ///
    /// <para>Because the buffer never reallocates, no version stamp is needed — the
    /// wrapper's cached data pointer is stable for the allocation's lifetime. Use-
    /// after-dispose is caught by the per-slot <c>AtomicSafetyHandle</c> in editor /
    /// dev builds and by the side-table generation re-check in shipping builds (see
    /// <see cref="TrecsArrayRead{T}"/>).</para>
    ///
    /// <para><b>Write discipline.</b> <c>Write</c> is an extension method on
    /// <see cref="TrecsArrayExtensions"/> declared with <c>this ref TrecsArray&lt;T&gt;</c>,
    /// so the caller must hold writable access to the handle struct — calling it
    /// through an <c>in</c> parameter or an <c>IRead&lt;...&gt;</c> aspect field is a
    /// compile error. <c>Read</c> stays as a <c>readonly</c> instance method so it's
    /// callable through readonly receivers. Same pattern as
    /// <see cref="TrecsListExtensions"/>.</para>
    /// </summary>
    [StructLayout(LayoutKind.Sequential, Pack = 1)]
    public readonly struct TrecsArray<T> : IEquatable<TrecsArray<T>>
        where T : unmanaged
    {
        public readonly PtrHandle Handle;

        /// <summary>
        /// Element count, fixed at <see cref="TrecsArray.Alloc{T}(WorldAccessor, int, string, int)"/>
        /// time and immutable for the array's lifetime. Stored inline on the handle so
        /// <c>Read</c> / <c>Write</c> can skip an extra slot resolve.
        /// </summary>
        public readonly int Length;

        // Internal so external code can't fabricate a TrecsArray<T> from an arbitrary
        // uint+int pair. Allocation goes through TrecsArray.Alloc; deserialization
        // paths live in InternalsVisibleTo-allowed assemblies and construct via this
        // ctor. Tests in the same Trecs assembly use it to forge cross-type-confusion
        // handles for the type-hash safety tests.
        internal TrecsArray(PtrHandle handle, int length)
        {
            Handle = handle;
            Length = length;
        }

        public readonly bool IsNull => Handle.IsNull;

        // Dispose is a `readonly` instance method so the OnRemoved-observer pattern
        // (which receives the component as `in`) can still tear down the array. Write
        // lives on TrecsArrayExtensions as a `this ref TrecsArray<T>` extension method,
        // which requires writable access to the handle struct — the compile-time gate
        // against mutation through an IRead aspect field or an `in` parameter.
        public readonly void Dispose(WorldAccessor world) => Dispose(world.NativeUniqueChunkStore);

        internal readonly unsafe void Dispose(NativeHeap chunkStore)
        {
            // Null-handle Dispose is a no-op (no slot was ever acquired). Lets
            // default(TrecsArray<T>) and Alloc<T>(_, 0) be treated uniformly: both
            // are valid empty arrays that Dispose silently. The bounds check on
            // Length=0 already prevents accidental data access.
            if (Handle.IsNull)
            {
                TrecsDebugAssert.That(
                    Length == 0,
                    "TrecsArray handle is null but Length is {0}; expected 0",
                    Length
                );
                return;
            }
            // Validate TypeId before freeing — catches accidental cross-heap dispose
            // (e.g. passing a TrecsList handle to TrecsArray.Dispose).
            var entry = chunkStore.ResolveEntry(Handle);
            AssertTypeHash(entry.TypeHash);
            chunkStore.Free(Handle);
        }

        /// <summary>
        /// Opens a read view. Works both on the main thread and inside Burst jobs.
        /// </summary>
        public readonly TrecsArrayRead<T> Read(WorldAccessor world) =>
            Read(world.NativeUniqueChunkStore);

        public readonly TrecsArrayRead<T> Read(in NativeWorldAccessor nativeWorld) =>
            Read(nativeWorld.ChunkStoreResolver);

        /// <summary>
        /// Burst-compatible read view. Pass a resolver obtained via
        /// <see cref="WorldAccessor.NativeHeapResolver"/> or
        /// <see cref="NativeWorldAccessor.ChunkStoreResolver"/>.
        /// </summary>
        public readonly unsafe TrecsArrayRead<T> Read(in NativeHeapResolver resolver)
        {
            ResolveData(in resolver, out var dataPtr, out var entry, out var slot);
#if ENABLE_UNITY_COLLECTIONS_CHECKS
            return new TrecsArrayRead<T>(dataPtr, Length, slot, entry.Generation, entry.Safety);
#else
            return new TrecsArrayRead<T>(dataPtr, Length, slot, entry.Generation);
#endif
        }

        internal readonly unsafe TrecsArrayRead<T> Read(NativeHeap chunkStore) =>
            Read(chunkStore.Resolver);

        // Public Write overloads live on TrecsArrayExtensions (this-ref extension
        // methods; see bottom of file). The implementations below are `internal
        // readonly` instance methods — extensions delegate to them. The compile-time
        // ref discipline is enforced at the extension's `this ref` receiver.

        internal readonly unsafe TrecsArrayWrite<T> Write(in NativeHeapResolver resolver)
        {
            ResolveData(in resolver, out var dataPtr, out var entry, out var slot);
#if ENABLE_UNITY_COLLECTIONS_CHECKS
            return new TrecsArrayWrite<T>(dataPtr, Length, slot, entry.Generation, entry.Safety);
#else
            return new TrecsArrayWrite<T>(dataPtr, Length, slot, entry.Generation);
#endif
        }

        internal readonly TrecsArrayWrite<T> Write(NativeHeap chunkStore) =>
            Write(chunkStore.Resolver);

        internal readonly TrecsArrayWrite<T> Write(in NativeWorldAccessor nativeWorld) =>
            Write(nativeWorld.ChunkStoreResolver);

        // Resolves the single data slot and validates its TypeId. Used by both Read
        // and Write opening paths so the resolve + typecheck pattern lives in one
        // place. Also surfaces the slot pointer — wrappers cache it so they can
        // re-check the slot's Generation on every access (shipping-build
        // use-after-dispose guard).
        //
        // For the Length=0 / default(TrecsArray<T>) shape, Handle is null and no
        // resolve is performed: the wrapper carries a null data pointer and a Length
        // of 0, so any indexed access trips the bounds check before dereferencing.
        readonly unsafe void ResolveData(
            in NativeHeapResolver resolver,
            out T* dataPtr,
            out NativeHeapEntry entry,
            out NativeHeapEntry* slot
        )
        {
            if (Handle.IsNull)
            {
                TrecsDebugAssert.That(
                    Length == 0,
                    "TrecsArray handle is null but Length is {0}; expected 0",
                    Length
                );
                // No slot to resolve. The wrapper still constructs with a null
                // data pointer; the bounds check guards every access.
                entry = default;
                slot = null;
                dataPtr = null;
                return;
            }
            entry = resolver.ResolveEntryWithSlotPtr(Handle, out slot);
            AssertTypeHash(entry.TypeHash);
            dataPtr = (T*)entry.Address.ToPointer();
        }

        readonly void AssertTypeHash(int storedHash)
        {
            TrecsAssert.That(
                storedHash == TypeId<TrecsArray<T>>.Value.Value,
                "TrecsArray type-hash mismatch for handle {0}: stored {1} != expected {2}",
                Handle.Value,
                storedHash,
                TypeId<TrecsArray<T>>.Value.Value
            );
        }

        public readonly bool Equals(TrecsArray<T> other) =>
            Handle.Equals(other.Handle) && Length == other.Length;

        public override readonly bool Equals(object obj) =>
            obj is TrecsArray<T> other && Equals(other);

        public override readonly int GetHashCode() =>
            unchecked((Handle.GetHashCode() * 397) ^ Length);

        public static bool operator ==(TrecsArray<T> left, TrecsArray<T> right) =>
            left.Equals(right);

        public static bool operator !=(TrecsArray<T> left, TrecsArray<T> right) =>
            !left.Equals(right);
    }

    /// <summary>
    /// Mutating operations on <see cref="TrecsArray{T}"/>. Each method takes
    /// <c>this ref TrecsArray&lt;T&gt;</c>, so the caller must hold writable access
    /// to the handle struct — calling <c>Write</c> through an <c>in</c> parameter, a
    /// <c>readonly</c> field, or an <c>IRead&lt;...&gt;</c> aspect field is a compile
    /// error. Same pattern as <see cref="TrecsListExtensions"/>.
    /// </summary>
    public static class TrecsArrayExtensions
    {
        public static TrecsArrayWrite<T> Write<T>(this ref TrecsArray<T> array, WorldAccessor world)
            where T : unmanaged => array.Write(world.NativeUniqueChunkStore);

        /// <summary>
        /// Convenience overload that pulls the resolver out of
        /// <paramref name="nativeWorld"/>.
        /// </summary>
        public static TrecsArrayWrite<T> Write<T>(
            this ref TrecsArray<T> array,
            in NativeWorldAccessor nativeWorld
        )
            where T : unmanaged => array.Write(in nativeWorld);

        /// <summary>
        /// Pass a resolver obtained via <see cref="WorldAccessor.NativeHeapResolver"/>
        /// or <see cref="NativeWorldAccessor.ChunkStoreResolver"/>.
        /// </summary>
        public static TrecsArrayWrite<T> Write<T>(
            this ref TrecsArray<T> array,
            in NativeHeapResolver resolver
        )
            where T : unmanaged => array.Write(resolver);
    }
}
