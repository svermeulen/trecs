using System;
using System.ComponentModel;
using System.Runtime.CompilerServices;
using System.Runtime.InteropServices;
using Trecs.Collections;
using Trecs.Internal;
using Unity.Collections.LowLevel.Unsafe;

namespace Trecs.Internal
{
    [EditorBrowsable(EditorBrowsableState.Never)]
    public struct TrecsDictionaryHeader
    {
        public PtrHandle DataHandle;
        public int Count;
        public int EntryCapacity;
        public int BucketCount;
        public int NodeSize;
        public int NodeAlign;
        public int ValueSize;
        public int ValueAlign;
        public int Collisions;
        public ulong FastModMultiplier;
        public ushort Version;
    }

    [EditorBrowsable(EditorBrowsableState.Never)]
    public readonly struct TrecsDictionaryDataMarker<TKey, TValue>
        where TKey : unmanaged, IEquatable<TKey>
        where TValue : unmanaged { }
}

namespace Trecs
{
    public static class TrecsDictionary
    {
        public static TrecsDictionary<TKey, TValue> Alloc<TKey, TValue>(
            WorldAccessor world,
            int initialCapacity = 0,
            [CallerFilePath] string callerFile = null,
            [CallerLineNumber] int callerLine = 0
        )
            where TKey : unmanaged, IEquatable<TKey>
            where TValue : unmanaged
        {
            world.AssertCanMutateHeap();
            return Alloc<TKey, TValue>(
                world.NativeUniqueChunkStore,
                initialCapacity,
                callerFile,
                callerLine
            );
        }

        internal static unsafe TrecsDictionary<TKey, TValue> Alloc<TKey, TValue>(
            NativeHeap chunkStore,
            int initialCapacity = 0,
            [CallerFilePath] string callerFile = null,
            [CallerLineNumber] int callerLine = 0
        )
            where TKey : unmanaged, IEquatable<TKey>
            where TValue : unmanaged
        {
            TrecsDebugAssert.That(initialCapacity >= 0, "initialCapacity must be non-negative");

            var nodeSize = UnsafeUtility.SizeOf<IterableDictionaryNode<TKey>>();
            var nodeAlign = UnsafeUtility.AlignOf<IterableDictionaryNode<TKey>>();
            var valueSize = UnsafeUtility.SizeOf<TValue>();
            var valueAlign = UnsafeUtility.AlignOf<TValue>();

            var handle = chunkStore.Alloc(
                UnsafeUtility.SizeOf<TrecsDictionaryHeader>(),
                UnsafeUtility.AlignOf<TrecsDictionaryHeader>(),
                TypeId<TrecsDictionary<TKey, TValue>>.Value.Value,
                out var headerAddress,
                callerFile,
                callerLine
            );

            var headerPtr = (TrecsDictionaryHeader*)headerAddress.ToPointer();
            headerPtr->DataHandle = default;
            headerPtr->Count = 0;
            headerPtr->EntryCapacity = 0;
            headerPtr->BucketCount = 0;
            headerPtr->NodeSize = nodeSize;
            headerPtr->NodeAlign = nodeAlign;
            headerPtr->ValueSize = valueSize;
            headerPtr->ValueAlign = valueAlign;
            headerPtr->Collisions = 0;
            headerPtr->FastModMultiplier = 0;
            headerPtr->Version = 0;

            if (initialCapacity > 0)
            {
                var bucketCount = HashHelpers.GetPrime(initialCapacity);
                AllocDataSlot<TKey, TValue>(
                    chunkStore,
                    headerPtr,
                    initialCapacity,
                    bucketCount,
                    callerFile,
                    callerLine
                );
            }

            return new TrecsDictionary<TKey, TValue>(handle);
        }

        internal static unsafe void AllocDataSlot<TKey, TValue>(
            NativeHeap chunkStore,
            TrecsDictionaryHeader* headerPtr,
            int entryCapacity,
            int bucketCount,
            string callerFile,
            int callerLine
        )
            where TKey : unmanaged, IEquatable<TKey>
            where TValue : unmanaged
        {
            var byteSize = ComputeDataSlotByteSize(
                entryCapacity,
                bucketCount,
                headerPtr->NodeSize,
                headerPtr->ValueSize,
                headerPtr->ValueAlign
            );
            var alignment = DataSlotAlignment(headerPtr->NodeAlign, headerPtr->ValueAlign);

            var dataHandle = chunkStore.Alloc(
                byteSize,
                alignment,
                TypeId<TrecsDictionaryDataMarker<TKey, TValue>>.Value.Value,
                out _,
                callerFile,
                callerLine
            );
            headerPtr->DataHandle = dataHandle;
            headerPtr->EntryCapacity = entryCapacity;
            headerPtr->BucketCount = bucketCount;
            headerPtr->FastModMultiplier =
                bucketCount > 0 ? HashHelpers.GetFastModMultiplier((uint)bucketCount) : 0;
        }

        internal static int ComputeDataSlotByteSize(
            int entryCapacity,
            int bucketCount,
            int nodeSize,
            int valueSize,
            int valueAlign
        )
        {
            long nodesBytes = (long)entryCapacity * nodeSize;
            long valuesOffset = AlignUpLong(nodesBytes, valueAlign);
            long valuesBytes = (long)entryCapacity * valueSize;
            long bucketsOffset = AlignUpLong(valuesOffset + valuesBytes, sizeof(int));
            long total = bucketsOffset + (long)bucketCount * sizeof(int);

            TrecsAssert.That(
                total > 0 && total <= int.MaxValue,
                "TrecsDictionary data slot byte size overflow: {0}",
                total
            );
            return (int)total;
        }

        internal static int ValuesOffset(int entryCapacity, int nodeSize, int valueAlign)
        {
            return AlignUp(entryCapacity * nodeSize, valueAlign);
        }

        internal static int BucketsOffset(
            int entryCapacity,
            int nodeSize,
            int valueAlign,
            int valueSize
        )
        {
            var valuesOff = ValuesOffset(entryCapacity, nodeSize, valueAlign);
            return AlignUp(valuesOff + entryCapacity * valueSize, sizeof(int));
        }

        internal static int DataSlotAlignment(int nodeAlign, int valueAlign)
        {
            var a = Math.Max(nodeAlign, valueAlign);
            return Math.Max(a, sizeof(int));
        }

        internal static int ComputeNewEntryCapacity(
            int currentCapacity,
            int minCapacity,
            int nodeSize,
            int valueSize
        )
        {
            var newCap = currentCapacity == 0 ? 4 : currentCapacity;
            while (newCap < minCapacity)
            {
                if (newCap > int.MaxValue / 2)
                {
                    newCap = minCapacity;
                    break;
                }
                newCap *= 2;
            }
            var approxBytes = (long)newCap * (nodeSize + valueSize);
            TrecsAssert.That(
                approxBytes <= int.MaxValue,
                "TrecsDictionary capacity overflow: {0} entries × ({1}+{2}) bytes = {3} exceeds int.MaxValue",
                newCap,
                nodeSize,
                valueSize,
                approxBytes
            );
            return newCap;
        }

        [MethodImpl(MethodImplOptions.AggressiveInlining)]
        internal static int Reduce(uint hashcode, uint N, ulong fastModBucketsMultiplier)
        {
            if (hashcode >= N)
                return (int)HashHelpers.FastMod(hashcode, N, fastModBucketsMultiplier);
            return (int)hashcode;
        }

        [MethodImpl(MethodImplOptions.AggressiveInlining)]
        internal static int AlignUp(int value, int alignment)
        {
            return (value + alignment - 1) & ~(alignment - 1);
        }

        [MethodImpl(MethodImplOptions.AggressiveInlining)]
        static long AlignUpLong(long value, int alignment)
        {
            return (value + alignment - 1) & ~((long)alignment - 1);
        }
    }

    [StructLayout(LayoutKind.Sequential, Pack = 1)]
    public readonly struct TrecsDictionary<TKey, TValue> : IEquatable<TrecsDictionary<TKey, TValue>>
        where TKey : unmanaged, IEquatable<TKey>
        where TValue : unmanaged
    {
        public readonly PtrHandle Handle;

        internal TrecsDictionary(PtrHandle handle)
        {
            Handle = handle;
        }

        public readonly bool IsNull => Handle.IsNull;

        public readonly void Dispose(WorldAccessor world)
        {
            world.AssertCanMutateHeap();
            Dispose(world.NativeUniqueChunkStore);
        }

        internal readonly unsafe void Dispose(NativeHeap chunkStore)
        {
            TrecsDebugAssert.That(Handle.Value != 0);
            var headerEntry = ResolveHeaderEntry(chunkStore);
            var dataHandle = ((TrecsDictionaryHeader*)headerEntry.Address.ToPointer())->DataHandle;
            chunkStore.Free(Handle);
            if (!dataHandle.IsNull)
            {
                chunkStore.Free(dataHandle);
            }
        }

        internal readonly unsafe void EnsureCapacity(
            NativeHeap chunkStore,
            int minCapacity,
            [CallerFilePath] string callerFile = null,
            [CallerLineNumber] int callerLine = 0
        )
        {
            TrecsDebugAssert.That(minCapacity >= 0, "minCapacity must be non-negative");

            var entry = ResolveHeaderEntry(chunkStore);
            var headerPtr = (TrecsDictionaryHeader*)entry.Address.ToPointer();

            if (minCapacity <= headerPtr->EntryCapacity)
                return;

#if ENABLE_UNITY_COLLECTIONS_CHECKS
            AtomicSafetyHandle.CheckDeallocateAndThrow(entry.Safety);
#endif

            var newEntryCapacity = TrecsDictionary.ComputeNewEntryCapacity(
                headerPtr->EntryCapacity,
                minCapacity,
                headerPtr->NodeSize,
                headerPtr->ValueSize
            );
            var newBucketCount = HashHelpers.GetPrime(newEntryCapacity);

            GrowDataSlot<TKey, TValue>(
                chunkStore,
                headerPtr,
                newEntryCapacity,
                newBucketCount,
                callerFile,
                callerLine
            );
            unchecked
            {
                headerPtr->Version++;
            }
        }

        internal static unsafe void GrowDataSlot<TK, TV>(
            NativeHeap chunkStore,
            TrecsDictionaryHeader* headerPtr,
            int newEntryCapacity,
            int newBucketCount,
            string callerFile = null,
            int callerLine = 0
        )
            where TK : unmanaged, IEquatable<TK>
            where TV : unmanaged
        {
            var oldDataHandle = headerPtr->DataHandle;
            var count = headerPtr->Count;

            var newByteSize = TrecsDictionary.ComputeDataSlotByteSize(
                newEntryCapacity,
                newBucketCount,
                headerPtr->NodeSize,
                headerPtr->ValueSize,
                headerPtr->ValueAlign
            );
            var alignment = TrecsDictionary.DataSlotAlignment(
                headerPtr->NodeAlign,
                headerPtr->ValueAlign
            );

            var newDataHandle = chunkStore.Alloc(
                newByteSize,
                alignment,
                TypeId<TrecsDictionaryDataMarker<TK, TV>>.Value.Value,
                out var newDataAddress,
                callerFile,
                callerLine
            );

            var newBase = (byte*)newDataAddress.ToPointer();

            if (count > 0 && !oldDataHandle.IsNull)
            {
                var oldDataEntry = chunkStore.ResolveEntry(oldDataHandle);
                var oldBase = (byte*)oldDataEntry.Address.ToPointer();

                var oldValuesOffset = TrecsDictionary.ValuesOffset(
                    headerPtr->EntryCapacity,
                    headerPtr->NodeSize,
                    headerPtr->ValueAlign
                );
                var newValuesOffset = TrecsDictionary.ValuesOffset(
                    newEntryCapacity,
                    headerPtr->NodeSize,
                    headerPtr->ValueAlign
                );

                // Copy nodes
                UnsafeUtility.MemCpy(newBase, oldBase, (long)count * headerPtr->NodeSize);
                // Copy values
                UnsafeUtility.MemCpy(
                    newBase + newValuesOffset,
                    oldBase + oldValuesOffset,
                    (long)count * headerPtr->ValueSize
                );
            }

            headerPtr->DataHandle = newDataHandle;
            headerPtr->EntryCapacity = newEntryCapacity;
            headerPtr->BucketCount = newBucketCount;
            headerPtr->FastModMultiplier =
                newBucketCount > 0 ? HashHelpers.GetFastModMultiplier((uint)newBucketCount) : 0;

            // Rebuild buckets from scratch
            var newBucketsOffset = TrecsDictionary.BucketsOffset(
                newEntryCapacity,
                headerPtr->NodeSize,
                headerPtr->ValueAlign,
                headerPtr->ValueSize
            );
            var newBuckets = (int*)(newBase + newBucketsOffset);
            var newNodes = (IterableDictionaryNode<TK>*)newBase;
            headerPtr->Collisions = 0;
            var bucketsCapacity = (uint)newBucketCount;
            var fmm = headerPtr->FastModMultiplier;

            for (int i = 0; i < count; i++)
            {
                ref var node = ref newNodes[i];
                var bi = TrecsDictionary.Reduce((uint)node.HashCode, bucketsCapacity, fmm);
                int existingValueIndex = newBuckets[bi] - 1;
                newBuckets[bi] = i + 1;

                if (existingValueIndex == -1)
                {
                    node.Previous = -1;
                }
                else
                {
                    headerPtr->Collisions++;
                    node.Previous = existingValueIndex;
                }
            }

            if (!oldDataHandle.IsNull)
            {
                chunkStore.Free(oldDataHandle);
            }
        }

        public readonly TrecsDictionaryRead<TKey, TValue> Read(WorldAccessor world) =>
            Read(world.NativeUniqueChunkStore);

        internal readonly unsafe TrecsDictionaryRead<TKey, TValue> Read(NativeHeap chunkStore)
        {
            ResolveHeaderAndData(
                in chunkStore.Resolver,
                out var headerPtr,
                out var nodes,
                out var values,
                out var buckets,
                out var headerEntry,
                out var headerSlot
            );
#if ENABLE_UNITY_COLLECTIONS_CHECKS
            return new TrecsDictionaryRead<TKey, TValue>(
                headerPtr,
                nodes,
                values,
                buckets,
                headerSlot,
                headerEntry.Generation,
                headerEntry.Safety
            );
#else
            return new TrecsDictionaryRead<TKey, TValue>(
                headerPtr,
                nodes,
                values,
                buckets,
                headerSlot,
                headerEntry.Generation
            );
#endif
        }

        internal readonly unsafe TrecsDictionaryWrite<TKey, TValue> Write(NativeHeap chunkStore)
        {
            ResolveHeaderAndData(
                in chunkStore.Resolver,
                out var headerPtr,
                out var nodes,
                out var values,
                out var buckets,
                out var headerEntry,
                out var headerSlot
            );
#if ENABLE_UNITY_COLLECTIONS_CHECKS
            return new TrecsDictionaryWrite<TKey, TValue>(
                headerPtr,
                nodes,
                values,
                buckets,
                chunkStore,
                headerSlot,
                headerEntry.Generation,
                headerEntry.Safety
            );
#else
            return new TrecsDictionaryWrite<TKey, TValue>(
                headerPtr,
                nodes,
                values,
                buckets,
                chunkStore,
                headerSlot,
                headerEntry.Generation
            );
#endif
        }

        public readonly NativeTrecsDictionaryRead<TKey, TValue> Read(
            in NativeWorldAccessor nativeWorld
        ) => Read(nativeWorld.ChunkStoreResolver);

        internal readonly NativeTrecsDictionaryWrite<TKey, TValue> Write(
            in NativeWorldAccessor nativeWorld
        )
        {
            nativeWorld.AssertCanMutateHeap();
            return Write(nativeWorld.ChunkStoreResolver);
        }

        public readonly unsafe NativeTrecsDictionaryRead<TKey, TValue> Read(
            in NativeHeapResolver resolver
        )
        {
            ResolveHeaderAndData(
                in resolver,
                out var headerPtr,
                out var nodes,
                out var values,
                out var buckets,
                out var headerEntry,
                out var headerSlot
            );
#if ENABLE_UNITY_COLLECTIONS_CHECKS
            return new NativeTrecsDictionaryRead<TKey, TValue>(
                headerPtr,
                nodes,
                values,
                buckets,
                headerSlot,
                headerEntry.Generation,
                headerEntry.Safety
            );
#else
            return new NativeTrecsDictionaryRead<TKey, TValue>(
                headerPtr,
                nodes,
                values,
                buckets,
                headerSlot,
                headerEntry.Generation
            );
#endif
        }

        internal readonly unsafe NativeTrecsDictionaryWrite<TKey, TValue> Write(
            in NativeHeapResolver resolver
        )
        {
            resolver.AssertCanMutateHeap();
            ResolveHeaderAndData(
                in resolver,
                out var headerPtr,
                out var nodes,
                out var values,
                out var buckets,
                out var headerEntry,
                out var headerSlot
            );
#if ENABLE_UNITY_COLLECTIONS_CHECKS
            return new NativeTrecsDictionaryWrite<TKey, TValue>(
                headerPtr,
                nodes,
                values,
                buckets,
                headerSlot,
                headerEntry.Generation,
                headerEntry.Safety
            );
#else
            return new NativeTrecsDictionaryWrite<TKey, TValue>(
                headerPtr,
                nodes,
                values,
                buckets,
                headerSlot,
                headerEntry.Generation
            );
#endif
        }

        readonly unsafe void ResolveHeaderAndData(
            in NativeHeapResolver resolver,
            out TrecsDictionaryHeader* headerPtr,
            out IterableDictionaryNode<TKey>* nodes,
            out TValue* values,
            out int* buckets,
            out NativeHeapEntry headerEntry,
            out NativeHeapEntry* headerSlot
        )
        {
            headerEntry = resolver.ResolveEntryWithSlotPtr(Handle, out headerSlot);
            AssertHeaderTypeHash(headerEntry.TypeHash);
            headerPtr = (TrecsDictionaryHeader*)headerEntry.Address.ToPointer();
            nodes = null;
            values = null;
            buckets = null;
            if (!headerPtr->DataHandle.IsNull)
            {
                var dataEntry = resolver.ResolveEntry(headerPtr->DataHandle);
                AssertDataTypeHash(headerPtr->DataHandle.Value, dataEntry.TypeHash);
                var dataBase = (byte*)dataEntry.Address.ToPointer();
                nodes = (IterableDictionaryNode<TKey>*)dataBase;
                var valuesOffset = TrecsDictionary.ValuesOffset(
                    headerPtr->EntryCapacity,
                    headerPtr->NodeSize,
                    headerPtr->ValueAlign
                );
                values = (TValue*)(dataBase + valuesOffset);
                var bucketsOffset = TrecsDictionary.BucketsOffset(
                    headerPtr->EntryCapacity,
                    headerPtr->NodeSize,
                    headerPtr->ValueAlign,
                    headerPtr->ValueSize
                );
                buckets = (int*)(dataBase + bucketsOffset);
            }
        }

        readonly NativeHeapEntry ResolveHeaderEntry(NativeHeap chunkStore)
        {
            TrecsDebugAssert.That(
                Handle.Value != 0,
                "Attempted to resolve null TrecsDictionary handle"
            );
            var entry = chunkStore.ResolveEntry(Handle);
            AssertHeaderTypeHash(entry.TypeHash);
            return entry;
        }

        readonly void AssertHeaderTypeHash(int storedHash)
        {
            TrecsAssert.That(
                storedHash == TypeId<TrecsDictionary<TKey, TValue>>.Value.Value,
                "TrecsDictionary header type-hash mismatch for handle {0}: stored {1} != expected {2}",
                Handle.Value,
                storedHash,
                TypeId<TrecsDictionary<TKey, TValue>>.Value.Value
            );
        }

        static void AssertDataTypeHash(uint dataHandleValue, int storedHash)
        {
            TrecsAssert.That(
                storedHash == TypeId<TrecsDictionaryDataMarker<TKey, TValue>>.Value.Value,
                "TrecsDictionary data type-hash mismatch for handle {0}: stored {1} != expected {2}",
                dataHandleValue,
                storedHash,
                TypeId<TrecsDictionaryDataMarker<TKey, TValue>>.Value.Value
            );
        }

        public readonly bool Equals(TrecsDictionary<TKey, TValue> other) =>
            Handle.Equals(other.Handle);

        public override readonly bool Equals(object obj) =>
            obj is TrecsDictionary<TKey, TValue> other && Equals(other);

        public override readonly int GetHashCode() => Handle.GetHashCode();

        public static bool operator ==(
            TrecsDictionary<TKey, TValue> left,
            TrecsDictionary<TKey, TValue> right
        ) => left.Equals(right);

        public static bool operator !=(
            TrecsDictionary<TKey, TValue> left,
            TrecsDictionary<TKey, TValue> right
        ) => !left.Equals(right);
    }

    public static class TrecsDictionaryExtensions
    {
        public static TrecsDictionaryWrite<TKey, TValue> Write<TKey, TValue>(
            this ref TrecsDictionary<TKey, TValue> dict,
            WorldAccessor world
        )
            where TKey : unmanaged, IEquatable<TKey>
            where TValue : unmanaged
        {
            world.AssertCanMutateHeap();
            return dict.Write(world.NativeUniqueChunkStore);
        }

        public static NativeTrecsDictionaryWrite<TKey, TValue> Write<TKey, TValue>(
            this ref TrecsDictionary<TKey, TValue> dict,
            in NativeWorldAccessor nativeWorld
        )
            where TKey : unmanaged, IEquatable<TKey>
            where TValue : unmanaged => dict.Write(in nativeWorld);

        public static NativeTrecsDictionaryWrite<TKey, TValue> Write<TKey, TValue>(
            this ref TrecsDictionary<TKey, TValue> dict,
            in NativeHeapResolver resolver
        )
            where TKey : unmanaged, IEquatable<TKey>
            where TValue : unmanaged => dict.Write(in resolver);

        public static void EnsureCapacity<TKey, TValue>(
            this ref TrecsDictionary<TKey, TValue> dict,
            WorldAccessor world,
            int minCapacity,
            [CallerFilePath] string callerFile = null,
            [CallerLineNumber] int callerLine = 0
        )
            where TKey : unmanaged, IEquatable<TKey>
            where TValue : unmanaged
        {
            world.AssertCanMutateHeap();
            dict.EnsureCapacity(world.NativeUniqueChunkStore, minCapacity, callerFile, callerLine);
        }
    }
}
