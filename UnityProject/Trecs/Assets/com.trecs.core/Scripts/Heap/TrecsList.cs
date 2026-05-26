using System;
using System.ComponentModel;
using System.Runtime.CompilerServices;
using System.Runtime.InteropServices;
using Trecs.Collections;
using Trecs.Internal;
using Unity.Collections.LowLevel.Unsafe;

namespace Trecs.Internal
{
    /// <summary>
    /// In-memory header for a single <see cref="TrecsList{T}"/> allocation. Lives at a stable
    /// address on the heap for the lifetime of the list; the data buffer it owns lives in a
    /// separate chunk-store slot referenced via <see cref="DataHandle"/>.
    ///
    /// <para>Read/Write wrappers cache the resolved data pointer at Open time, so the hot
    /// path doesn't have to re-resolve through the chunk store on every element access.
    /// Each wrapper also captures <see cref="Version"/> at Open and re-checks on every
    /// data-touching operation; <see cref="Version"/> is bumped whenever the data slot
    /// is reallocated. Any wrapper still holding a stale data pointer will fail loudly
    /// on its next access instead of dereferencing freed memory — this check is plain
    /// integer compare, not gated on <c>ENABLE_UNITY_COLLECTIONS_CHECKS</c>, so it
    /// holds in shipping builds where Unity's safety handle is compiled out.</para>
    /// </summary>
    [EditorBrowsable(EditorBrowsableState.Never)]
    public struct TrecsListHeader
    {
        public PtrHandle DataHandle;
        public int Count;
        public int Capacity;
        public int ElementSize;
        public int ElementAlign;

        // Bumped on every data-slot reallocation. ushort wraps at 65,536 grows — for the
        // hazard to bite, a single wrapper scope (ref struct, so one method) would have to
        // straddle exactly that many grows, which no realistic loop does. ushort costs
        // 2 bytes, uint would cost 4 with no measurable safety upside.
        public ushort Version;
    }

    /// <summary>
    /// Phantom type used as the <c>TypeId</c> tag for <see cref="TrecsList{T}"/> data
    /// buffers. Distinguishes a data-slot chunk-store entry from the header slot
    /// (tagged with <c>TypeId&lt;TrecsList&lt;T&gt;&gt;</c>) and from an unrelated
    /// <see cref="NativeUniquePtr{T}"/> slot (tagged with <c>TypeId&lt;T&gt;</c>).
    /// Never instantiated — only its type identity matters.
    /// </summary>
    [EditorBrowsable(EditorBrowsableState.Never)]
    public readonly struct TrecsListDataMarker<T>
        where T : unmanaged { }
}

namespace Trecs
{
    /// <summary>
    /// Static factories for <see cref="TrecsList{T}"/>. Allocation goes through here;
    /// per-instance operations (<c>Read</c>, <c>Write</c>, <c>EnsureCapacity</c>,
    /// <c>Dispose</c>) live on the struct itself.
    ///
    /// <para>Two slots get allocated in the shared <see cref="NativeHeap"/>:
    /// a stable <see cref="TrecsListHeader"/> tagged with
    /// <c>TypeId&lt;TrecsList&lt;T&gt;&gt;</c>, and (if <paramref name="initialCapacity"/>
    /// &gt; 0) a data buffer tagged with <c>TypeId&lt;TrecsListDataMarker&lt;T&gt;&gt;</c>.
    /// Distinct tags let leak warnings render the slot's role and let resolve-time
    /// checks catch a <see cref="NativeUniquePtr{T}"/> handle handed to a list API.</para>
    /// </summary>
    public static class TrecsList
    {
        public static TrecsList<T> Alloc<T>(
            WorldAccessor world,
            int initialCapacity = 0,
            [CallerFilePath] string callerFile = null,
            [CallerLineNumber] int callerLine = 0
        )
            where T : unmanaged
        {
            world.AssertCanMutateHeap();
            return Alloc<T>(world.NativeUniqueChunkStore, initialCapacity, callerFile, callerLine);
        }

        internal static unsafe TrecsList<T> Alloc<T>(
            NativeHeap chunkStore,
            int initialCapacity = 0,
            [CallerFilePath] string callerFile = null,
            [CallerLineNumber] int callerLine = 0
        )
            where T : unmanaged
        {
            TrecsDebugAssert.That(initialCapacity >= 0, "initialCapacity must be non-negative");

            var elementSize = UnsafeUtility.SizeOf<T>();
            var elementAlign = UnsafeUtility.AlignOf<T>();

            var handle = chunkStore.Alloc(
                UnsafeUtility.SizeOf<TrecsListHeader>(),
                UnsafeUtility.AlignOf<TrecsListHeader>(),
                TypeId<TrecsList<T>>.Value.Value,
                out var headerAddress,
                callerFile,
                callerLine
            );

            var headerPtr = (TrecsListHeader*)headerAddress.ToPointer();
            headerPtr->Count = 0;
            headerPtr->Capacity = 0;
            headerPtr->DataHandle = default;
            headerPtr->ElementSize = elementSize;
            headerPtr->ElementAlign = elementAlign;
            headerPtr->Version = 0;

            if (initialCapacity > 0)
            {
                AllocDataSlot<T>(chunkStore, headerPtr, initialCapacity, callerFile, callerLine);
            }

            return new TrecsList<T>(handle);
        }

        // Chunk-store Alloc returns a zeroed slot, so the unused tail past
        // Count*ElementSize is deterministic for snapshots. Tagged with
        // TrecsListDataMarker<T> so a leak warning identifies it as a list backing
        // buffer (distinct from a NativeUniquePtr<T> at the same TypeId<T>).
        static unsafe void AllocDataSlot<T>(
            NativeHeap chunkStore,
            TrecsListHeader* headerPtr,
            int capacity,
            string callerFile,
            int callerLine
        )
            where T : unmanaged
        {
            var byteSize = ByteSizeOrThrow(capacity, headerPtr->ElementSize);
            var dataHandle = chunkStore.Alloc(
                byteSize,
                headerPtr->ElementAlign,
                TypeId<TrecsListDataMarker<T>>.Value.Value,
                out _,
                callerFile,
                callerLine
            );
            headerPtr->DataHandle = dataHandle;
            headerPtr->Capacity = capacity;
        }

        /// <summary>
        /// Computes a doubled capacity that satisfies <paramref name="minCapacity"/>
        /// without overflowing. Clamps the doubling step at <c>int.MaxValue / 2</c>
        /// so a runaway loop can't wrap to negative; verifies the resulting byte
        /// size fits in <c>int</c> (the chunk store's allocation size type).
        ///
        /// <para>Used by both handle-side <c>EnsureCapacity</c> and wrapper-side
        /// auto-grow paths to centralize the doubling policy and the overflow guard.</para>
        /// </summary>
        internal static int ComputeNewCapacity(
            int currentCapacity,
            int minCapacity,
            int elementSize
        )
        {
            var newCap = currentCapacity == 0 ? 4 : currentCapacity;
            while (newCap < minCapacity)
            {
                if (newCap > int.MaxValue / 2)
                {
                    // One more doubling would overflow. Clamp to minCapacity (which
                    // we verify fits below).
                    newCap = minCapacity;
                    break;
                }
                newCap *= 2;
            }
            // Verify byte size fits before handing off to chunk store, which takes int.
            // Clear error here beats the chunk store's generic "size must be positive"
            // assert if it would have silently overflowed.
            TrecsAssert.That(
                (long)newCap * elementSize <= int.MaxValue,
                "TrecsList capacity overflow: {0} elements × {1} bytes per element = "
                    + "{2} bytes exceeds int.MaxValue. Consider a smaller element type or "
                    + "splitting the data across multiple lists.",
                newCap,
                elementSize,
                (long)newCap * elementSize
            );
            return newCap;
        }

        /// <summary>
        /// Computes the byte size for <paramref name="capacity"/> elements of size
        /// <paramref name="elementSize"/> and asserts it fits in <c>int</c> before
        /// returning. Used at the boundary where chunk-store allocation happens.
        /// </summary>
        internal static int ByteSizeOrThrow(int capacity, int elementSize)
        {
            var bytes = (long)capacity * elementSize;
            TrecsAssert.That(
                bytes <= int.MaxValue,
                "TrecsList byte size overflow: {0} elements × {1} bytes = {2} exceeds "
                    + "int.MaxValue.",
                capacity,
                elementSize,
                bytes
            );
            return (int)bytes;
        }
    }

    /// <summary>
    /// A growable list of unmanaged values whose storage is owned by the world's shared
    /// <see cref="NativeHeap"/>. Designed to live as a 4-byte value on ECS
    /// components, so a component can carry a dynamically-sized collection without going
    /// through <c>NativeUniquePtr</c>.
    ///
    /// <para>The struct itself only carries a <see cref="PtrHandle"/>;
    /// allocating, growing, reading, and writing all go through
    /// <see cref="WorldAccessor"/> / <see cref="NativeWorldAccessor"/>. Read/write
    /// surfaces come in two flavors:</para>
    ///
    /// <list type="bullet">
    /// <item><description><b>Managed</b> <see cref="TrecsListRead{T}"/> /
    ///   <see cref="TrecsListWrite{T}"/> — main-thread <c>ref struct</c> views
    ///   returned by the <see cref="WorldAccessor"/>
    ///   overloads. <c>TrecsListWrite.Add</c> auto-grows.</description></item>
    /// <item><description><b>Native</b> <see cref="NativeTrecsListRead{T}"/> /
    ///   <see cref="NativeTrecsListWrite{T}"/> — Burst-safe views returned by the
    ///   <see cref="NativeWorldAccessor"/> and <see cref="NativeHeapResolver"/>
    ///   overloads. Usable inside <c>[BurstCompile]</c> jobs; do not auto-grow
    ///   (pre-size with
    ///   <see cref="TrecsListExtensions.EnsureCapacity{T}(ref TrecsList{T}, WorldAccessor, int, string, int)"/>
    ///   on the main thread first). The per-allocation <c>AtomicSafetyHandle</c>
    ///   lets Unity's job walker detect cross-job conflicts at schedule
    ///   time.</description></item>
    /// </list>
    ///
    /// <para><see cref="TrecsList.Alloc{T}(WorldAccessor, int, string, int)"/>
    /// creates the list with an initial capacity; <see cref="Dispose(WorldAccessor)"/>
    /// frees the backing storage. The struct is exclusive-ownership: copying it
    /// produces two handles that both refer to the same backing storage, and the
    /// per-handle safety identity catches concurrent writers on those copies.</para>
    ///
    /// <para><b>Write discipline.</b> <c>Write</c> and <c>EnsureCapacity</c> are
    /// extension methods on <see cref="TrecsListExtensions"/> declared with
    /// <c>this ref TrecsList&lt;T&gt;</c>, so the caller must hold writable access to
    /// the handle struct — calling them through an <c>in</c> parameter or an
    /// <c>IRead&lt;...&gt;</c> aspect field is a compile error. <c>Read</c> stays as
    /// a <c>readonly</c> instance method so it's callable through readonly receivers.
    /// Same pattern as <see cref="FixedList16Extensions"/>.</para>
    ///
    /// <para><b>Growth.</b> Both auto-grow (<c>TrecsListWrite.Add</c>) and explicit
    /// (<c>EnsureCapacity</c>) paths double geometrically. The header struct that
    /// holds the backing pointer lives at a stable address on the heap — the data
    /// buffer reallocates, the header doesn't move.</para>
    /// </summary>
    [StructLayout(LayoutKind.Sequential, Pack = 1)]
    public readonly struct TrecsList<T> : IEquatable<TrecsList<T>>
        where T : unmanaged
    {
        public readonly PtrHandle Handle;

        // Internal so external code can't fabricate a TrecsList<T> from an arbitrary
        // uint. Allocation goes through TrecsList.Alloc; deserialization paths
        // (e.g. snapshot restore) live in InternalsVisibleTo-allowed assemblies and
        // construct via this ctor with a known-valid handle.
        internal TrecsList(PtrHandle handle)
        {
            Handle = handle;
        }

        public readonly bool IsNull => Handle.IsNull;

        // Dispose is a `readonly` instance method so the OnRemoved-observer pattern
        // (which receives the component as `in`) can still tear down the list. Write
        // and EnsureCapacity live on TrecsListExtensions as `this ref TrecsList<T>`
        // extension methods, which require writable access to the handle struct —
        // the compile-time gate against mutation through an IRead aspect field or
        // an `in` parameter. Same pattern as FixedList16Extensions.
        public readonly void Dispose(WorldAccessor world)
        {
            world.AssertCanMutateHeap();
            Dispose(world.NativeUniqueChunkStore);
        }

        internal readonly unsafe void Dispose(NativeHeap chunkStore)
        {
            TrecsDebugAssert.That(Handle.Value != 0);
            // Validate TypeId before freeing — catches accidental cross-heap dispose
            // (e.g. passing a NativeUniquePtr handle to TrecsList.Dispose).
            var headerEntry = ResolveHeaderEntry(chunkStore);
            var dataHandle = ((TrecsListHeader*)headerEntry.Address.ToPointer())->DataHandle;
            // Free header first so its CheckDeallocateAndThrow runs before we touch
            // the data slot. If a job still holds the header's safety handle, Free
            // throws and the data slot stays intact — caller can retry after job
            // completion without orphaning the buffer. Wrappers carry the header's
            // safety handle, not the data's, so once the header has been freed there
            // is no live wrapper that could be holding the data buffer.
            chunkStore.Free(Handle);
            if (!dataHandle.IsNull)
            {
                chunkStore.Free(dataHandle);
            }
        }

        // Public EnsureCapacity overloads live on TrecsListExtensions as
        // `this ref TrecsList<T>` extension methods (see bottom of file). The
        // implementation here is `internal readonly` — extensions delegate to it,
        // and the compile-time ref discipline is enforced at the extension's
        // `this ref` receiver, not at this internal method.
        internal readonly unsafe void EnsureCapacity(
            NativeHeap chunkStore,
            int minCapacity,
            [CallerFilePath] string callerFile = null,
            [CallerLineNumber] int callerLine = 0
        )
        {
            TrecsDebugAssert.That(minCapacity >= 0, "minCapacity must be non-negative");

            var entry = ResolveHeaderEntry(chunkStore);
            var headerPtr = (TrecsListHeader*)entry.Address.ToPointer();

            if (minCapacity <= headerPtr->Capacity)
            {
                return;
            }

#if ENABLE_UNITY_COLLECTIONS_CHECKS
            AtomicSafetyHandle.CheckDeallocateAndThrow(entry.Safety);
#endif

            var elementSize = headerPtr->ElementSize;
            var newCapacity = TrecsList.ComputeNewCapacity(
                headerPtr->Capacity,
                minCapacity,
                elementSize
            );

            var oldDataHandle = headerPtr->DataHandle;
            var liveBytes = (long)headerPtr->Count * elementSize;

            var newByteSize = TrecsList.ByteSizeOrThrow(newCapacity, elementSize);
            var newDataHandle = chunkStore.Alloc(
                newByteSize,
                headerPtr->ElementAlign,
                TypeId<TrecsListDataMarker<T>>.Value.Value,
                out var newDataAddress,
                callerFile,
                callerLine
            );

            if (liveBytes > 0)
            {
                var oldDataEntry = chunkStore.ResolveEntry(oldDataHandle);
                UnsafeUtility.MemCpy(
                    newDataAddress.ToPointer(),
                    oldDataEntry.Address.ToPointer(),
                    liveBytes
                );
            }
            // No tail MemClear needed — chunk-store Alloc already zeroed the slot.

            headerPtr->DataHandle = newDataHandle;
            headerPtr->Capacity = newCapacity;
            // Bump after the swap so any held wrapper detects the new data pointer.
            // Wrap at 65,536 is benign — see TrecsListHeader.Version.
            unchecked
            {
                headerPtr->Version++;
            }

            if (!oldDataHandle.IsNull)
            {
                chunkStore.Free(oldDataHandle);
            }
        }

        /// <summary>
        /// Opens a main-thread read view. For in-job access use the
        /// <see cref="Read(in NativeWorldAccessor)"/> or
        /// <see cref="Read(in NativeHeapResolver)"/> overload, which returns
        /// the Burst-safe <see cref="NativeTrecsListRead{T}"/>.
        /// </summary>
        public readonly TrecsListRead<T> Read(WorldAccessor world) =>
            Read(world.NativeUniqueChunkStore);

        // Public Write overloads live on TrecsListExtensions (this-ref extension
        // methods; see bottom of file). The implementations below are `internal
        // readonly` instance methods — extensions delegate to them. The compile-time
        // ref discipline is enforced at the extension's `this ref` receiver.

        internal readonly unsafe TrecsListRead<T> Read(NativeHeap chunkStore)
        {
            ResolveHeaderAndData(
                in chunkStore.Resolver,
                out var headerPtr,
                out var dataPtr,
                out var headerEntry,
                out var headerSlot
            );
#if ENABLE_UNITY_COLLECTIONS_CHECKS
            return new TrecsListRead<T>(
                headerPtr,
                dataPtr,
                headerSlot,
                headerEntry.Generation,
                headerEntry.Safety
            );
#else
            return new TrecsListRead<T>(headerPtr, dataPtr, headerSlot, headerEntry.Generation);
#endif
        }

        internal readonly unsafe TrecsListWrite<T> Write(NativeHeap chunkStore)
        {
            ResolveHeaderAndData(
                in chunkStore.Resolver,
                out var headerPtr,
                out var dataPtr,
                out var headerEntry,
                out var headerSlot
            );
#if ENABLE_UNITY_COLLECTIONS_CHECKS
            return new TrecsListWrite<T>(
                headerPtr,
                dataPtr,
                chunkStore,
                headerSlot,
                headerEntry.Generation,
                headerEntry.Safety
            );
#else
            return new TrecsListWrite<T>(
                headerPtr,
                dataPtr,
                chunkStore,
                headerSlot,
                headerEntry.Generation
            );
#endif
        }

        /// <summary>
        /// Burst-compatible read view. Convenience overload that pulls the resolver out
        /// of <paramref name="nativeWorld"/>; equivalent to
        /// <c>Read(nativeWorld.ChunkStoreResolver)</c>.
        /// </summary>
        public readonly NativeTrecsListRead<T> Read(in NativeWorldAccessor nativeWorld) =>
            Read(nativeWorld.ChunkStoreResolver);

        // Native Write via NativeWorldAccessor lives on TrecsListExtensions
        // (this-ref); this internal helper is the actual implementation. Both
        // the world-flag check and the resolver-flag check pass through —
        // belt-and-suspenders, since either alone would catch a Variable-role
        // caller, but the world-side flag gives a clearer error message before
        // the resolver-side fallback fires.
        internal readonly NativeTrecsListWrite<T> Write(in NativeWorldAccessor nativeWorld)
        {
            nativeWorld.AssertCanMutateHeap();
            return Write(nativeWorld.ChunkStoreResolver);
        }

        /// <summary>
        /// Burst-compatible read view. Pass a resolver obtained via
        /// <see cref="WorldAccessor.NativeHeapResolver"/> or
        /// <see cref="NativeWorldAccessor.ChunkStoreResolver"/>. Validates the header slot
        /// is tagged <c>TrecsList&lt;T&gt;</c> and the data slot is tagged
        /// <c>TrecsListDataMarker&lt;T&gt;</c>; throws on mismatch (e.g. a
        /// <see cref="NativeUniquePtr{T}"/> handle, or a wrong-T list handle).
        /// </summary>
        public readonly unsafe NativeTrecsListRead<T> Read(in NativeHeapResolver resolver)
        {
            ResolveHeaderAndData(
                in resolver,
                out var headerPtr,
                out var dataPtr,
                out var headerEntry,
                out var headerSlot
            );
#if ENABLE_UNITY_COLLECTIONS_CHECKS
            return new NativeTrecsListRead<T>(
                headerPtr,
                dataPtr,
                headerSlot,
                headerEntry.Generation,
                headerEntry.Safety
            );
#else
            return new NativeTrecsListRead<T>(
                headerPtr,
                dataPtr,
                headerSlot,
                headerEntry.Generation
            );
#endif
        }

        // Internal — public access goes through TrecsListExtensions.Write(this ref,
        // in NativeHeapResolver) so the ref discipline applies. This is the
        // actual implementation. The resolver carries the originating accessor's
        // role flag, so a Variable-role caller fails fast here.
        internal readonly unsafe NativeTrecsListWrite<T> Write(in NativeHeapResolver resolver)
        {
            resolver.AssertCanMutateHeap();
            ResolveHeaderAndData(
                in resolver,
                out var headerPtr,
                out var dataPtr,
                out var headerEntry,
                out var headerSlot
            );
#if ENABLE_UNITY_COLLECTIONS_CHECKS
            return new NativeTrecsListWrite<T>(
                headerPtr,
                dataPtr,
                headerSlot,
                headerEntry.Generation,
                headerEntry.Safety
            );
#else
            return new NativeTrecsListWrite<T>(
                headerPtr,
                dataPtr,
                headerSlot,
                headerEntry.Generation
            );
#endif
        }

        /// <summary>
        /// Resolves the header slot, validates its TypeId, then resolves the data slot
        /// (if non-null) and validates its TypeId. Used by all four Read/Write opening
        /// paths so the resolve + typecheck pattern lives in one place.
        ///
        /// <para>Also surfaces the address of the header slot's side-table entry
        /// (<paramref name="headerSlot"/>). That pointer is stable for the chunk
        /// store's lifetime — wrappers cache it so they can re-check the slot's
        /// <c>Generation</c> on every access (shipping-build use-after-dispose
        /// guard).</para>
        /// </summary>
        readonly unsafe void ResolveHeaderAndData(
            in NativeHeapResolver resolver,
            out TrecsListHeader* headerPtr,
            out T* dataPtr,
            out NativeHeapEntry headerEntry,
            out NativeHeapEntry* headerSlot
        )
        {
            headerEntry = resolver.ResolveEntryWithSlotPtr(Handle, out headerSlot);
            AssertHeaderTypeHash(headerEntry.TypeHash);
            headerPtr = (TrecsListHeader*)headerEntry.Address.ToPointer();
            dataPtr = null;
            if (!headerPtr->DataHandle.IsNull)
            {
                var dataEntry = resolver.ResolveEntry(headerPtr->DataHandle);
                AssertDataTypeHash(headerPtr->DataHandle.Value, dataEntry.TypeHash);
                dataPtr = (T*)dataEntry.Address.ToPointer();
            }
        }

        readonly NativeHeapEntry ResolveHeaderEntry(NativeHeap chunkStore)
        {
            TrecsDebugAssert.That(Handle.Value != 0, "Attempted to resolve null TrecsList handle");
            var entry = chunkStore.ResolveEntry(Handle);
            AssertHeaderTypeHash(entry.TypeHash);
            return entry;
        }

        readonly void AssertHeaderTypeHash(int storedHash)
        {
            TrecsAssert.That(
                storedHash == TypeId<TrecsList<T>>.Value.Value,
                "TrecsList header type-hash mismatch for handle {0}: stored {1} != expected {2}",
                Handle.Value,
                storedHash,
                TypeId<TrecsList<T>>.Value.Value
            );
        }

        static void AssertDataTypeHash(uint dataHandleValue, int storedHash)
        {
            TrecsAssert.That(
                storedHash == TypeId<TrecsListDataMarker<T>>.Value.Value,
                "TrecsList data type-hash mismatch for handle {0}: stored {1} != expected {2}",
                dataHandleValue,
                storedHash,
                TypeId<TrecsListDataMarker<T>>.Value.Value
            );
        }

        public readonly bool Equals(TrecsList<T> other) => Handle.Equals(other.Handle);

        public override readonly bool Equals(object obj) =>
            obj is TrecsList<T> other && Equals(other);

        public override readonly int GetHashCode() => Handle.GetHashCode();

        public static bool operator ==(TrecsList<T> left, TrecsList<T> right) => left.Equals(right);

        public static bool operator !=(TrecsList<T> left, TrecsList<T> right) =>
            !left.Equals(right);
    }

    /// <summary>
    /// Mutating operations on <see cref="TrecsList{T}"/>. Each method takes
    /// <c>this ref TrecsList&lt;T&gt;</c>, so the caller must hold writable access to the
    /// handle struct — calling <c>Write</c> or <c>EnsureCapacity</c> through an
    /// <c>in</c> parameter, a <c>readonly</c> field, or an <c>IRead&lt;...&gt;</c>
    /// aspect field is a compile error. Same pattern as
    /// <see cref="FixedList16Extensions"/>.
    /// </summary>
    public static class TrecsListExtensions
    {
        // ── Write ────────────────────────────────────────────────────────────

        /// <summary>
        /// Opens a main-thread write view. <see cref="TrecsListWrite{T}.Add"/>
        /// auto-grows. For in-job access use the
        /// <see cref="Write{T}(ref TrecsList{T}, in NativeWorldAccessor)"/> or
        /// <see cref="Write{T}(ref TrecsList{T}, in NativeHeapResolver)"/>
        /// overload, which returns the Burst-safe <see cref="NativeTrecsListWrite{T}"/>
        /// (does not auto-grow).
        /// </summary>
        public static TrecsListWrite<T> Write<T>(this ref TrecsList<T> list, WorldAccessor world)
            where T : unmanaged
        {
            world.AssertCanMutateHeap();
            return list.Write(world.NativeUniqueChunkStore);
        }

        /// <summary>
        /// Burst-compatible write view. Convenience overload that pulls the resolver
        /// out of <paramref name="nativeWorld"/>.
        /// </summary>
        public static NativeTrecsListWrite<T> Write<T>(
            this ref TrecsList<T> list,
            in NativeWorldAccessor nativeWorld
        )
            where T : unmanaged => list.Write(in nativeWorld);

        /// <summary>
        /// Burst-compatible write view. Pass a resolver obtained via
        /// <see cref="WorldAccessor.NativeHeapResolver"/> or
        /// <see cref="NativeWorldAccessor.ChunkStoreResolver"/>.
        /// </summary>
        public static NativeTrecsListWrite<T> Write<T>(
            this ref TrecsList<T> list,
            in NativeHeapResolver resolver
        )
            where T : unmanaged => list.Write(in resolver);

        // ── EnsureCapacity ───────────────────────────────────────────────────

        /// <summary>
        /// Grows this list's backing data buffer so it can hold at least
        /// <paramref name="minCapacity"/> elements without further reallocation. Doubles
        /// geometrically past the existing capacity. Main-thread only — call this
        /// before scheduling any job that appends via <see cref="NativeTrecsListWrite{T}"/>,
        /// which does not auto-grow.
        ///
        /// <para>Rejects the grow via the header's <c>AtomicSafetyHandle</c> if any job
        /// is still holding a <see cref="NativeTrecsListRead{T}"/> /
        /// <see cref="NativeTrecsListWrite{T}"/> over this list.</para>
        /// </summary>
        public static void EnsureCapacity<T>(
            this ref TrecsList<T> list,
            WorldAccessor world,
            int minCapacity,
            [CallerFilePath] string callerFile = null,
            [CallerLineNumber] int callerLine = 0
        )
            where T : unmanaged
        {
            world.AssertCanMutateHeap();
            list.EnsureCapacity(world.NativeUniqueChunkStore, minCapacity, callerFile, callerLine);
        }
    }
}
