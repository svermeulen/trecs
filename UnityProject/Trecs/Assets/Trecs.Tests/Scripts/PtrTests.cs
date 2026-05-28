using System;
using System.Collections.Generic;
using NUnit.Framework;
using Trecs.Internal;
using Trecs.Serialization;
using NAssert = NUnit.Framework.Assert;

namespace Trecs.Tests
{
    [TestFixture]
    public partial class PtrTests
    {
        // ───────────────────────────────────────────────────────────
        // GroupIndex 1: Pure Value-Type Tests (no DI needed)
        // ───────────────────────────────────────────────────────────

        #region PtrHandle

        [Test]
        public void PtrHandle_DefaultIsNull()
        {
            NAssert.IsTrue(default(PtrHandle).IsNull);
        }

        [Test]
        public void PtrHandle_NonZeroIsNotNull()
        {
            NAssert.IsFalse(new PtrHandle(1).IsNull);
        }

        [Test]
        public void PtrHandle_Equality()
        {
            var a = new PtrHandle(42);
            var b = new PtrHandle(42);
            var c = new PtrHandle(99);

            NAssert.IsTrue(a == b);
            NAssert.IsFalse(a != b);
            NAssert.IsTrue(a != c);
            NAssert.IsFalse(a == c);
            NAssert.IsTrue(a.Equals(b));
            NAssert.IsTrue(a.Equals((object)b));
            NAssert.IsFalse(a.Equals((object)c));
            NAssert.IsFalse(a.Equals("not a PtrHandle"));
        }

        [Test]
        public void PtrHandle_HashCode()
        {
            var a = new PtrHandle(42);
            var b = new PtrHandle(42);
            NAssert.AreEqual(a.GetHashCode(), b.GetHashCode());
        }

        #endregion

        #region BlobId

        [Test]
        public void BlobId_DefaultIsNull()
        {
            NAssert.IsTrue(default(BlobId).IsNull);
        }

        [Test]
        public void BlobId_NullFieldIsDefault()
        {
            NAssert.AreEqual(BlobId.Null, default(BlobId));
        }

        [Test]
        public void BlobId_NonZeroIsNotNull()
        {
            NAssert.IsFalse(new BlobId(1).IsNull);
        }

        [Test]
        public void BlobId_Equality()
        {
            var a = new BlobId(100);
            var b = new BlobId(100);
            var c = new BlobId(200);

            NAssert.IsTrue(a == b);
            NAssert.IsFalse(a != b);
            NAssert.IsTrue(a != c);
            NAssert.IsFalse(a == c);
            NAssert.IsTrue(a.Equals(b));
            NAssert.IsTrue(a.Equals((object)b));
            NAssert.IsFalse(a.Equals((object)c));
            NAssert.IsFalse(a.Equals("not a BlobId"));
        }

        [Test]
        public void BlobId_HashCode()
        {
            var a = new BlobId(100);
            var b = new BlobId(100);
            NAssert.AreEqual(a.GetHashCode(), b.GetHashCode());
        }

        #endregion

        #region UniquePtr

        [Test]
        public void UniquePtr_DefaultIsNull()
        {
            var ptr = default(UniquePtr<string>);
            NAssert.IsTrue(ptr.IsNull);
        }

        [Test]
        public void UniquePtr_WithHandle_IsNotNull()
        {
            var ptr = new UniquePtr<string>(new PtrHandle(1));
            NAssert.IsFalse(ptr.IsNull);
        }

        #endregion

        #region SharedPtr

        [Test]
        public void SharedPtr_DefaultIsNull()
        {
            var ptr = default(SharedPtr<string>);
            NAssert.IsTrue(ptr.IsNull);
        }

        [Test]
        public void SharedPtr_WithValues_IsNotNull()
        {
            var ptr = new SharedPtr<string>(new PtrHandle(1), new BlobId(10));
            NAssert.IsFalse(ptr.IsNull);
        }

        [Test]
        public void SharedPtr_Equality()
        {
            var a = new SharedPtr<string>(new PtrHandle(1), new BlobId(10));
            var b = new SharedPtr<string>(new PtrHandle(1), new BlobId(10));
            var c = new SharedPtr<string>(new PtrHandle(2), new BlobId(10));

            NAssert.IsTrue(a.Equals(b));
            NAssert.IsFalse(a.Equals(c));
            NAssert.IsTrue(a.Equals((object)b));
            NAssert.IsFalse(a.Equals((object)c));
        }

        [Test]
        public void SharedPtr_HashCode()
        {
            var a = new SharedPtr<string>(new PtrHandle(1), new BlobId(10));
            var b = new SharedPtr<string>(new PtrHandle(1), new BlobId(10));
            NAssert.AreEqual(a.GetHashCode(), b.GetHashCode());
        }

        #endregion

        #region BlobPtr

        [Test]
        public void BlobPtr_DefaultIsNull()
        {
            var ptr = default(BlobPtr<string>);
            NAssert.IsTrue(ptr.IsNull);
        }

        [Test]
        public void BlobPtr_NullField_IsNull()
        {
            NAssert.IsTrue(BlobPtr<string>.Null.IsNull);
        }

        [Test]
        public void BlobPtr_WithValues_IsNotNull()
        {
            var ptr = new BlobPtr<string>(new PtrHandle(1), new BlobId(10));
            NAssert.IsFalse(ptr.IsNull);
        }

        [Test]
        public void BlobPtr_Equality()
        {
            var a = new BlobPtr<string>(new PtrHandle(1), new BlobId(10));
            var b = new BlobPtr<string>(new PtrHandle(1), new BlobId(10));
            var c = new BlobPtr<string>(new PtrHandle(2), new BlobId(10));

            NAssert.IsTrue(a.Equals(b));
            NAssert.IsFalse(a.Equals(c));
            NAssert.IsTrue(a.Equals((object)b));
            NAssert.IsFalse(a.Equals((object)c));
        }

        [Test]
        public void BlobPtr_HashCode()
        {
            var a = new BlobPtr<string>(new PtrHandle(1), new BlobId(10));
            var b = new BlobPtr<string>(new PtrHandle(1), new BlobId(10));
            NAssert.AreEqual(a.GetHashCode(), b.GetHashCode());
        }

        #endregion

        #region NativeBlobPtr

        [Test]
        public void NativeBlobPtr_DefaultIsNull()
        {
            NAssert.IsTrue(default(NativeBlobPtr<int>).IsNull);
        }

        [Test]
        public void NativeBlobPtr_NullField_IsNull()
        {
            NAssert.IsTrue(NativeBlobPtr<int>.Null.IsNull);
        }

        [Test]
        public void NativeBlobPtr_WithValues_IsNotNull()
        {
            var ptr = new NativeBlobPtr<int>(new PtrHandle(1), new BlobId(10));
            NAssert.IsFalse(ptr.IsNull);
        }

        [Test]
        public void NativeBlobPtr_Equality()
        {
            var a = new NativeBlobPtr<int>(new PtrHandle(1), new BlobId(10));
            var b = new NativeBlobPtr<int>(new PtrHandle(1), new BlobId(10));
            var c = new NativeBlobPtr<int>(new PtrHandle(2), new BlobId(10));

            NAssert.IsTrue(a.Equals(b));
            NAssert.IsFalse(a.Equals(c));
            NAssert.IsTrue(a.Equals((object)b));
            NAssert.IsFalse(a.Equals((object)c));
        }

        [Test]
        public void NativeBlobPtr_HashCode()
        {
            var a = new NativeBlobPtr<int>(new PtrHandle(1), new BlobId(10));
            var b = new NativeBlobPtr<int>(new PtrHandle(1), new BlobId(10));
            NAssert.AreEqual(a.GetHashCode(), b.GetHashCode());
        }

        #endregion

        #region NativeSharedPtr

        [Test]
        public void NativeSharedPtr_DefaultIsNull()
        {
            NAssert.IsTrue(default(NativeSharedPtr<int>).IsNull);
        }

        [Test]
        public void NativeSharedPtr_WithValues_IsNotNull()
        {
            var ptr = new NativeSharedPtr<int>(1);
            NAssert.IsFalse(ptr.IsNull);
        }

        [Test]
        public void NativeSharedPtr_Equality()
        {
            var a = new NativeSharedPtr<int>(1);
            var b = new NativeSharedPtr<int>(1);
            var c = new NativeSharedPtr<int>(2);

            NAssert.IsTrue(a.Equals(b));
            NAssert.IsFalse(a.Equals(c));
            NAssert.IsTrue(a.Equals((object)b));
            NAssert.IsFalse(a.Equals((object)c));
        }

        [Test]
        public void NativeSharedPtr_HashCode()
        {
            var a = new NativeSharedPtr<int>(1);
            var b = new NativeSharedPtr<int>(1);
            NAssert.AreEqual(a.GetHashCode(), b.GetHashCode());
        }

        #endregion

        // ───────────────────────────────────────────────────────────
        // GroupIndex 2: BlobCache + BlobPtr Integration Tests
        // ───────────────────────────────────────────────────────────

        static BlobCache CreateBlobCache()
        {
            var blobStore = new BlobStoreInMemory(BlobStoreInMemorySettings.Default, null);
            var settings = new BlobCacheSettings { SerializationVersion = 1 };
            // The pool is intentionally not disposed by the test — boxes return their
            // native memory to AllocatorManager on Dispose, and the pool's free-list
            // holds only managed wrapper references that GC reclaims when the test ends.
            return new BlobCache(
                TrecsLog.Default,
                new List<IBlobStore> { blobStore },
                settings,
                new NativeBlobBoxPool()
            );
        }

        [Test]
        public void BlobCache_CreateManagedHandle_ReturnsBlobPtr()
        {
            var blobCache = CreateBlobCache();

            var blob = new List<string> { "hello" };
            var ptr = blobCache.AllocManagedBlob(new BlobId(1), blob);

            NAssert.IsFalse(ptr.IsNull);
            NAssert.IsFalse(ptr.Handle.IsNull);
            NAssert.IsFalse(ptr.BlobId.IsNull);

            ptr.Dispose(blobCache);
            blobCache.Dispose();
        }

        [Test]
        public void BlobCache_GetManagedBlob_ReturnsStoredValue()
        {
            var blobCache = CreateBlobCache();

            var blob = new List<string> { "world" };
            var ptr = blobCache.AllocManagedBlob(new BlobId(1), blob);

            var retrieved = ptr.Get(blobCache);
            NAssert.AreSame(blob, retrieved);

            ptr.Dispose(blobCache);
            blobCache.Dispose();
        }

        [Test]
        public void BlobCache_Contains_ReturnsTrueForExisting()
        {
            var blobCache = CreateBlobCache();

            var blob = new List<string> { "test" };
            var ptr = blobCache.AllocManagedBlob(new BlobId(1), blob);

            NAssert.IsTrue(blobCache.Contains(ptr.BlobId));

            ptr.Dispose(blobCache);
            blobCache.Dispose();
        }

        [Test]
        public void BlobCache_Contains_ReturnsFalseForMissing()
        {
            var blobCache = CreateBlobCache();

            NAssert.IsFalse(blobCache.Contains(new BlobId(999999)));

            blobCache.Dispose();
        }

        [Test]
        public void BlobPtr_Get_ReturnsBlobValue()
        {
            var blobCache = CreateBlobCache();

            var blob = new List<string> { "value" };
            var ptr = blobCache.AllocManagedBlob(new BlobId(1), blob);

            var retrieved = ptr.Get(blobCache);
            NAssert.AreSame(blob, retrieved);

            ptr.Dispose(blobCache);
            blobCache.Dispose();
        }

        [Test]
        public void BlobPtr_TryGet_ReturnsTrueAndValue()
        {
            var blobCache = CreateBlobCache();

            var blob = new List<string> { "tryget" };
            var ptr = blobCache.AllocManagedBlob(new BlobId(1), blob);

            var result = ptr.TryGet(blobCache, out var val);
            NAssert.IsTrue(result);
            NAssert.AreSame(blob, val);

            ptr.Dispose(blobCache);
            blobCache.Dispose();
        }

        [Test]
        public void BlobPtr_CanGet_TrueForValid_FalseForDisposed()
        {
            var blobCache = CreateBlobCache();

            var blob = new List<string> { "canget" };
            var ptr = blobCache.AllocManagedBlob(new BlobId(1), blob);

            NAssert.IsTrue(ptr.CanGet(blobCache));

            ptr.Dispose(blobCache);
            NAssert.IsFalse(ptr.CanGet(blobCache));

            blobCache.Dispose();
        }

        [Test]
        public void BlobPtr_Clone_CreatesIndependentHandle()
        {
            var blobCache = CreateBlobCache();

            var blob = new List<string> { "clone" };
            var ptr = blobCache.AllocManagedBlob(new BlobId(1), blob);
            var clone = ptr.Clone(blobCache);

            NAssert.IsFalse(clone.IsNull);
            NAssert.AreEqual(ptr.BlobId, clone.BlobId);
            NAssert.AreNotEqual(ptr.Handle, clone.Handle);

            ptr.Dispose(blobCache);
            NAssert.IsTrue(clone.CanGet(blobCache));

            var retrieved = clone.Get(blobCache);
            NAssert.AreSame(blob, retrieved);

            clone.Dispose(blobCache);
            blobCache.Dispose();
        }

        [Test]
        public void BlobPtr_Dispose_InvalidatesHandle()
        {
            var blobCache = CreateBlobCache();

            var blob = new List<string> { "dispose" };
            var ptr = blobCache.AllocManagedBlob(new BlobId(1), blob);

            NAssert.IsTrue(ptr.CanGet(blobCache));
            ptr.Dispose(blobCache);
            NAssert.IsFalse(ptr.CanGet(blobCache));

            blobCache.Dispose();
        }

        [Test]
        public void BlobPtr_Clone_OnNull_ReturnsNull()
        {
            var blobCache = CreateBlobCache();

            var result = BlobPtr<string>.Null.Clone(blobCache);
            NAssert.IsTrue(result.IsNull);

            blobCache.Dispose();
        }

        // ───────────────────────────────────────────────────────────
        // GroupIndex 2a: BlobCache.GetStats / GetStatsPerStore
        // ───────────────────────────────────────────────────────────

        [Test]
        public void BlobCache_GetStats_EmptyCache_ReturnsZeros()
        {
            var blobCache = CreateBlobCache();

            var stats = blobCache.GetStats();
            NAssert.AreEqual(0, stats.TotalNativeMemoryBytes);
            NAssert.AreEqual(0, stats.InactiveNativeMemoryBytes);
            NAssert.AreEqual(0, stats.TotalManagedEntries);
            NAssert.AreEqual(0, stats.InactiveManagedEntries);
            NAssert.AreEqual(0, stats.ActiveHandleCount);

            blobCache.Dispose();
        }

        [Test]
        public void BlobCache_GetStats_CountsActiveHandlesAndManagedEntries()
        {
            var blobCache = CreateBlobCache();

            var blobA = new List<string> { "a" };
            var blobB = new List<string> { "b" };
            var ptrA = blobCache.AllocManagedBlob(new BlobId(1), blobA);
            var ptrB = blobCache.AllocManagedBlob(new BlobId(2), blobB);

            var stats = blobCache.GetStats();
            NAssert.AreEqual(2, stats.TotalManagedEntries);
            // Both blobs are pinned, so the inactive count is 0.
            NAssert.AreEqual(0, stats.InactiveManagedEntries);
            NAssert.AreEqual(0, stats.TotalNativeMemoryBytes);
            NAssert.AreEqual(0, stats.InactiveNativeMemoryBytes);
            NAssert.AreEqual(2, stats.ActiveHandleCount);

            ptrA.Dispose(blobCache);
            ptrB.Dispose(blobCache);
            blobCache.Dispose();
        }

        [Test]
        public void BlobCache_GetStats_DisposingHandleMovesEntryToInactive()
        {
            var blobCache = CreateBlobCache();

            var ptr = blobCache.AllocManagedBlob(new BlobId(1), new List<string> { "x" });

            NAssert.AreEqual(1, blobCache.GetStats().ActiveHandleCount);
            NAssert.AreEqual(0, blobCache.GetStats().InactiveManagedEntries);

            ptr.Dispose(blobCache);

            // The blob is still in the in-memory cache (eviction is governed by
            // the high-water mark, which a single entry is far under), but it's
            // no longer pinned by any handle.
            var stats = blobCache.GetStats();
            NAssert.AreEqual(0, stats.ActiveHandleCount);
            NAssert.AreEqual(1, stats.TotalManagedEntries);
            NAssert.AreEqual(1, stats.InactiveManagedEntries);

            blobCache.Dispose();
        }

        [Test]
        public void BlobCache_GetStats_CountsNativeBytesAndManagedEntriesIndependently()
        {
            var blobCache = CreateBlobCache();

            var managedPtr = blobCache.AllocManagedBlob(
                new BlobId(1),
                new List<string> { "managed" }
            );
            var nativePtr = blobCache.AllocNativeBlob<int>(new BlobId(2), 42);

            var stats = blobCache.GetStats();
            NAssert.AreEqual(1, stats.TotalManagedEntries);
            NAssert.AreEqual(0, stats.InactiveManagedEntries);
            // sizeof(int) == 4 on every supported platform.
            NAssert.AreEqual(4, stats.TotalNativeMemoryBytes);
            NAssert.AreEqual(0, stats.InactiveNativeMemoryBytes);
            NAssert.AreEqual(2, stats.ActiveHandleCount);

            managedPtr.Dispose(blobCache);
            nativePtr.Dispose(blobCache);
            blobCache.Dispose();
        }

        [Test]
        public void BlobCache_GetStats_CloneAddsActiveHandleButNotEntry()
        {
            var blobCache = CreateBlobCache();

            var ptr = blobCache.AllocManagedBlob(new BlobId(1), new List<string> { "clone" });
            var clone = ptr.Clone(blobCache);

            var stats = blobCache.GetStats();
            // Both handles point at the same blob — one underlying entry, two
            // outstanding handles, both contribute to ActiveHandleCount.
            NAssert.AreEqual(1, stats.TotalManagedEntries);
            NAssert.AreEqual(0, stats.InactiveManagedEntries);
            NAssert.AreEqual(2, stats.ActiveHandleCount);

            ptr.Dispose(blobCache);
            clone.Dispose(blobCache);
            blobCache.Dispose();
        }

        [Test]
        public void BlobCache_GetStatsPerStore_ReturnsOneEntryPerStore()
        {
            var blobCache = CreateBlobCache();

            var ptr = blobCache.AllocManagedBlob(new BlobId(1), new List<string> { "perstore" });

            var perStore = new List<BlobStoreStats>();
            blobCache.GetStatsPerStore(perStore);

            NAssert.AreEqual(1, perStore.Count);
            NAssert.AreEqual(1, perStore[0].TotalManagedEntries);
            NAssert.AreEqual(0, perStore[0].InactiveManagedEntries);

            ptr.Dispose(blobCache);
            blobCache.Dispose();
        }

        [Test]
        public void BlobCache_GetStatsPerStore_ClearsOutputListBeforePopulating()
        {
            var blobCache = CreateBlobCache();

            var perStore = new List<BlobStoreStats> { new(99, 99, 99, 99), new(99, 99, 99, 99) };

            blobCache.GetStatsPerStore(perStore);

            NAssert.AreEqual(1, perStore.Count);
            NAssert.AreEqual(0, perStore[0].TotalManagedEntries);

            blobCache.Dispose();
        }

        // ───────────────────────────────────────────────────────────
        // GroupIndex 2b: NativeBlobPtr + BlobCache Integration Tests
        // ───────────────────────────────────────────────────────────

        [Test]
        public void NativeBlobPtr_CreateNativeHandle_ReturnsValidPtr()
        {
            var blobCache = CreateBlobCache();

            var ptr = blobCache.AllocNativeBlob<int>(new BlobId(1), 42);

            NAssert.IsFalse(ptr.IsNull);
            NAssert.IsFalse(ptr.Handle.IsNull);
            NAssert.IsFalse(ptr.BlobId.IsNull);

            ptr.Dispose(blobCache);
            blobCache.Dispose();
        }

        [Test]
        public void NativeBlobPtr_Get_ReturnsStoredValue()
        {
            var blobCache = CreateBlobCache();

            var ptr = blobCache.AllocNativeBlob<int>(new BlobId(1), 42);

            ref int value = ref ptr.Get(blobCache);
            NAssert.AreEqual(42, value);

            ptr.Dispose(blobCache);
            blobCache.Dispose();
        }

        [Test]
        public void NativeBlobPtr_TryGetPtr_ReturnsTrueAndPtr()
        {
            var blobCache = CreateBlobCache();

            var ptr = blobCache.AllocNativeBlob<int>(new BlobId(1), 42);

            var result = ptr.TryGetPtr(blobCache, out var nativePtr);
            NAssert.IsTrue(result);
            NAssert.AreNotEqual(IntPtr.Zero, nativePtr);

            ptr.Dispose(blobCache);
            blobCache.Dispose();
        }

        [Test]
        public void NativeBlobPtr_CanGet_TrueForValid_FalseForDisposed()
        {
            var blobCache = CreateBlobCache();

            var ptr = blobCache.AllocNativeBlob<int>(new BlobId(1), 42);

            NAssert.IsTrue(ptr.CanGet(blobCache));

            ptr.Dispose(blobCache);
            NAssert.IsFalse(ptr.CanGet(blobCache));

            blobCache.Dispose();
        }

        [Test]
        public void NativeBlobPtr_Clone_CreatesIndependentHandle()
        {
            var blobCache = CreateBlobCache();

            var ptr = blobCache.AllocNativeBlob<int>(new BlobId(1), 42);
            var clone = ptr.Clone(blobCache);

            NAssert.IsFalse(clone.IsNull);
            NAssert.AreEqual(ptr.BlobId, clone.BlobId);
            NAssert.AreNotEqual(ptr.Handle, clone.Handle);

            ptr.Dispose(blobCache);
            NAssert.IsTrue(clone.CanGet(blobCache));

            ref int value = ref clone.Get(blobCache);
            NAssert.AreEqual(42, value);

            clone.Dispose(blobCache);
            blobCache.Dispose();
        }

        [Test]
        public void NativeBlobPtr_Clone_OnNull_ReturnsNull()
        {
            var blobCache = CreateBlobCache();

            var result = NativeBlobPtr<int>.Null.Clone(blobCache);
            NAssert.IsTrue(result.IsNull);

            blobCache.Dispose();
        }

        [Test]
        public void NativeBlobPtr_Dispose_InvalidatesHandle()
        {
            var blobCache = CreateBlobCache();

            var ptr = blobCache.AllocNativeBlob<int>(new BlobId(1), 42);

            NAssert.IsTrue(ptr.CanGet(blobCache));
            ptr.Dispose(blobCache);
            NAssert.IsFalse(ptr.CanGet(blobCache));

            blobCache.Dispose();
        }

        // ───────────────────────────────────────────────────────────
        // GroupIndex 3: SharedHeap Tests
        // ───────────────────────────────────────────────────────────

        static (SharedHeap heap, BlobCache blobCache) CreateSharedHeap()
        {
            var blobCache = CreateBlobCache();
            var heap = new SharedHeap(TrecsLog.Default, blobCache);
            return (heap, blobCache);
        }

        [Test]
        public void SharedHeap_CreateBlob_ReturnsValidPtr()
        {
            var (heap, blobCache) = CreateSharedHeap();

            var blob = new List<object> { "shared" };
            var ptr = heap.CreateBlob(new BlobId(1), blob);

            NAssert.IsFalse(ptr.IsNull);
            NAssert.IsFalse(ptr.Handle.IsNull);
            NAssert.IsFalse(ptr.Id.IsNull);

            heap.Dispose();
            blobCache.Dispose();
        }

        [Test]
        public void SharedHeap_GetBlob_ReturnsSameValue()
        {
            var (heap, blobCache) = CreateSharedHeap();

            var blob = new List<object> { "getblob" };
            var ptr = heap.CreateBlob(new BlobId(1), blob);

            var retrieved = heap.GetBlob<List<object>>(ptr.Handle);
            NAssert.AreSame(blob, retrieved);

            heap.Dispose();
            blobCache.Dispose();
        }

        [Test]
        public void SharedHeap_DisposeHandle_RemovesBlob()
        {
            var (heap, blobCache) = CreateSharedHeap();

            var blob = new List<object> { "remove" };
            var ptr = heap.CreateBlob(new BlobId(1), blob);

            NAssert.AreEqual(1, heap.NumEntries);

            heap.DisposeHandle(ptr.Handle);
            NAssert.AreEqual(0, heap.NumEntries);

            heap.Dispose();
            blobCache.Dispose();
        }

        [Test]
        public void SharedHeap_Clone_SharesBlob()
        {
            var (heap, blobCache) = CreateSharedHeap();

            var blob = new List<object> { "cloneshared" };
            var ptr = heap.CreateBlob(new BlobId(1), blob);

            heap.TryClone<List<object>>(ptr.Handle, out var clone);
            NAssert.IsFalse(clone.IsNull);

            heap.DisposeHandle(ptr.Handle);
            NAssert.IsTrue(heap.CanGetBlob(clone.Handle));

            var retrieved = heap.GetBlob<List<object>>(clone.Handle);
            NAssert.AreSame(blob, retrieved);

            heap.Dispose();
            blobCache.Dispose();
        }

        [Test]
        public void SharedHeap_NumEntries_TracksActiveBlobs()
        {
            var (heap, blobCache) = CreateSharedHeap();

            NAssert.AreEqual(0, heap.NumEntries);

            var ptr1 = heap.CreateBlob(new BlobId(1), new List<object> { "a" });
            NAssert.AreEqual(1, heap.NumEntries);

            var ptr2 = heap.CreateBlob(new BlobId(2), new List<object> { "b" });
            NAssert.AreEqual(2, heap.NumEntries);

            heap.DisposeHandle(ptr1.Handle);
            NAssert.AreEqual(1, heap.NumEntries);

            heap.DisposeHandle(ptr2.Handle);
            NAssert.AreEqual(0, heap.NumEntries);

            heap.Dispose();
            blobCache.Dispose();
        }

        // ───────────────────────────────────────────────────────────
        // GroupIndex 3b: UniqueHeap Tests
        // ───────────────────────────────────────────────────────────

        static UniqueHeap CreateUniqueHeap()
        {
            return new UniqueHeap(TrecsLog.Default, null);
        }

        [Test]
        public void UniqueHeap_AllocUnique_ReturnsValidPtr()
        {
            var heap = CreateUniqueHeap();

            var value = new List<string> { "hello" };
            var ptr = heap.AllocUnique(value);

            NAssert.IsFalse(ptr.IsNull);

            heap.Dispose();
        }

        [Test]
        public void UniqueHeap_AllocUnique_WithNull_ReturnsValidPtr()
        {
            var heap = CreateUniqueHeap();

            var ptr = heap.AllocUnique<List<string>>(null);

            NAssert.IsFalse(ptr.IsNull);
            NAssert.IsNull(heap.TryGetPtrValue(ptr.Handle.Value));

            heap.Dispose();
        }

        [Test]
        public void UniqueHeap_GetEntry_ReturnsStoredValue()
        {
            var heap = CreateUniqueHeap();

            var value = new List<string> { "stored" };
            var ptr = heap.AllocUnique(value);

            var retrieved = heap.GetEntry<List<string>>(ptr.Handle.Value);
            NAssert.AreSame(value, retrieved);

            heap.Dispose();
        }

        [Test]
        public void UniqueHeap_SetEntry_UpdatesValue()
        {
            var heap = CreateUniqueHeap();

            var original = new List<string> { "original" };
            var ptr = heap.AllocUnique(original);

            var updated = new List<string> { "updated" };
            heap.SetEntry(ptr.Handle.Value, updated);

            var retrieved = heap.GetEntry<List<string>>(ptr.Handle.Value);
            NAssert.AreSame(updated, retrieved);

            heap.Dispose();
        }

        [Test]
        public void UniqueHeap_TryGetEntry_ReturnsTrueForValid()
        {
            var heap = CreateUniqueHeap();

            var value = new List<string> { "tryget" };
            var ptr = heap.AllocUnique(value);

            var result = heap.TryGetEntry(ptr.Handle.Value, out var entry);
            NAssert.IsTrue(result);
            NAssert.AreEqual(typeof(List<string>), entry.Type);
            NAssert.AreSame(value, entry.Value);

            heap.Dispose();
        }

        [Test]
        public void UniqueHeap_TryGetEntry_ReturnsFalseForInvalid()
        {
            var heap = CreateUniqueHeap();

            var result = heap.TryGetEntry(999999, out _);
            NAssert.IsFalse(result);

            heap.Dispose();
        }

        [Test]
        public void UniqueHeap_DisposeEntry_RemovesFromHeap()
        {
            var heap = CreateUniqueHeap();

            var value = new List<string> { "dispose" };
            var ptr = heap.AllocUnique(value);
            NAssert.AreEqual(1, heap.NumEntries);

            heap.DisposeEntry<List<string>>(ptr.Handle.Value);
            NAssert.AreEqual(0, heap.NumEntries);

            heap.Dispose();
        }

        [Test]
        public void UniqueHeap_NumEntries_TracksAllocations()
        {
            var heap = CreateUniqueHeap();

            NAssert.AreEqual(0, heap.NumEntries);

            var ptr1 = heap.AllocUnique(new List<string> { "a" });
            NAssert.AreEqual(1, heap.NumEntries);

            var ptr2 = heap.AllocUnique(new List<string> { "b" });
            NAssert.AreEqual(2, heap.NumEntries);

            heap.DisposeEntry<List<string>>(ptr1.Handle.Value);
            NAssert.AreEqual(1, heap.NumEntries);

            heap.DisposeEntry<List<string>>(ptr2.Handle.Value);
            NAssert.AreEqual(0, heap.NumEntries);

            heap.Dispose();
        }

        // ───────────────────────────────────────────────────────────
        // GroupIndex 3c: UniquePtr + UniqueHeap Integration Tests
        // ───────────────────────────────────────────────────────────

        [Test]
        public void UniquePtr_Get_ReturnsAllocatedValue()
        {
            var heap = CreateUniqueHeap();

            var value = new List<string> { "get" };
            var ptr = heap.AllocUnique(value);

            var retrieved = ptr.Get(heap);
            NAssert.AreSame(value, retrieved);

            heap.Dispose();
        }

        [Test]
        public void UniquePtr_TryGet_ReturnsFalseForNullPtr()
        {
            var heap = CreateUniqueHeap();

            var ptr = default(UniquePtr<List<string>>);
            var result = ptr.TryGet(heap, out var val);
            NAssert.IsFalse(result);
            NAssert.IsNull(val);

            heap.Dispose();
        }

        [Test]
        public void UniquePtr_Set_UpdatesValue()
        {
            var heap = CreateUniqueHeap();

            var original = new List<string> { "original" };
            var ptr = heap.AllocUnique(original);

            var updated = new List<string> { "updated" };
            ptr.Set(heap, updated);

            var retrieved = ptr.Get(heap);
            NAssert.AreSame(updated, retrieved);

            heap.Dispose();
        }

        [Test]
        public void UniquePtr_Dispose_InvalidatesEntry()
        {
            var heap = CreateUniqueHeap();

            var value = new List<string> { "dispose" };
            var ptr = heap.AllocUnique(value);
            NAssert.AreEqual(1, heap.NumEntries);

            ptr.Dispose(heap);
            NAssert.AreEqual(0, heap.NumEntries);

            heap.Dispose();
        }

        // ───────────────────────────────────────────────────────────
        // GroupIndex 3b: NativeUniquePtr Value-Type Tests
        // ───────────────────────────────────────────────────────────

        [Test]
        public void NativeUniquePtr_DefaultIsNull()
        {
            var ptr = default(NativeUniquePtr<int>);
            NAssert.IsTrue(ptr.IsNull);
        }

        [Test]
        public void NativeUniquePtr_WithHandle_IsNotNull()
        {
            var ptr = new NativeUniquePtr<int>(new PtrHandle(1));
            NAssert.IsFalse(ptr.IsNull);
        }

        [Test]
        public void NativeUniquePtr_Equality()
        {
            var a = new NativeUniquePtr<int>(new PtrHandle(1));
            var b = new NativeUniquePtr<int>(new PtrHandle(1));
            var c = new NativeUniquePtr<int>(new PtrHandle(2));

            NAssert.IsTrue(a.Equals(b));
            NAssert.IsFalse(a.Equals(c));
        }

        [Test]
        public void NativeUniquePtr_HashCode()
        {
            var a = new NativeUniquePtr<int>(new PtrHandle(1));
            var b = new NativeUniquePtr<int>(new PtrHandle(1));

            NAssert.AreEqual(a.GetHashCode(), b.GetHashCode());
        }

        // ───────────────────────────────────────────────────────────
        // GroupIndex 4: NativeSharedHeap Tests
        // ───────────────────────────────────────────────────────────

        static (NativeSharedHeap heap, BlobCache blobCache) CreateNativeSharedHeap()
        {
            var blobCache = CreateBlobCache();
            var heap = new NativeSharedHeap(TrecsLog.Default, blobCache);
            return (heap, blobCache);
        }

        [Test]
        public void NativeSharedHeap_CreateBlob_ReturnsValidPtr()
        {
            var (heap, blobCache) = CreateNativeSharedHeap();

            var ptr = heap.CreateBlob<int>(new BlobId(42), 42);

            NAssert.IsFalse(ptr.IsNull);
            NAssert.AreNotEqual(0u, ptr.Handle);
            NAssert.IsFalse(heap.GetBlobId(ptr.Handle).IsNull);

            heap.Dispose();
            blobCache.Dispose();
        }

        [Test]
        public void NativeSharedHeap_GetBlob_ReturnsSameValue()
        {
            var (heap, blobCache) = CreateNativeSharedHeap();

            var ptr = heap.CreateBlob<int>(new BlobId(42), 42);

            ref readonly int value = ref heap.Read(in ptr).Value;
            NAssert.AreEqual(42, value);

            heap.Dispose();
            blobCache.Dispose();
        }

        [Test]
        public void NativeSharedHeap_DisposeHandle_RemovesBlob()
        {
            var (heap, blobCache) = CreateNativeSharedHeap();

            var ptr = heap.CreateBlob<int>(new BlobId(42), 42);

            NAssert.AreEqual(1, heap.NumEntries);

            heap.DecrementRef(ptr.Handle);

            NAssert.AreEqual(0, heap.NumEntries);

            heap.Dispose();
            blobCache.Dispose();
        }

        [Test]
        public void NativeSharedHeap_Clone_SharesBlob()
        {
            var (heap, blobCache) = CreateNativeSharedHeap();

            var ptr = heap.CreateBlob<int>(new BlobId(42), 42);

            heap.TryClone<int>(ptr.Handle, out var clone);
            NAssert.IsFalse(clone.IsNull);

            heap.DecrementRef(ptr.Handle);

            ref readonly int value = ref heap.Read(in clone).Value;
            NAssert.AreEqual(42, value);

            heap.Dispose();
            blobCache.Dispose();
        }

        [Test]
        public void NativeSharedHeap_CreateAndDispose_BeforeFlush()
        {
            var (heap, blobCache) = CreateNativeSharedHeap();

            var ptr = heap.CreateBlob<int>(new BlobId(42), 42);
            NAssert.AreEqual(1, heap.NumEntries);

            heap.DecrementRef(ptr.Handle);
            NAssert.AreEqual(0, heap.NumEntries);

            heap.Dispose();
            blobCache.Dispose();
        }

        [Test]
        public void NativeSharedHeap_DisposeHandle_UnknownHandleThrows()
        {
            var (heap, blobCache) = CreateNativeSharedHeap();

            NAssert.Throws<TrecsException>(() => heap.DecrementRef(12345));

            heap.Dispose();
            blobCache.Dispose();
        }

        [Test]
        public void NativeSharedHeap_DisposeHandle_DoubleDisposeThrows()
        {
            var (heap, blobCache) = CreateNativeSharedHeap();

            var ptr = heap.CreateBlob<int>(new BlobId(42), 42);
            heap.DecrementRef(ptr.Handle);

            NAssert.Throws<TrecsException>(() => heap.DecrementRef(ptr.Handle));

            heap.Dispose();
            blobCache.Dispose();
        }

        [Test]
        public void NativeSharedHeap_NumEntries_TracksActiveBlobs()
        {
            var (heap, blobCache) = CreateNativeSharedHeap();

            NAssert.AreEqual(0, heap.NumEntries);

            var ptr1 = heap.CreateBlob<int>(new BlobId(10), 10);
            NAssert.AreEqual(1, heap.NumEntries);

            var ptr2 = heap.CreateBlob<int>(new BlobId(20), 20);
            NAssert.AreEqual(2, heap.NumEntries);

            heap.DecrementRef(ptr1.Handle);

            NAssert.AreEqual(1, heap.NumEntries);

            heap.DecrementRef(ptr2.Handle);

            NAssert.AreEqual(0, heap.NumEntries);

            heap.Dispose();
            blobCache.Dispose();
        }

        // ───────────────────────────────────────────────────────────
        // GroupIndex 5: NativeUniquePtr Tests (chunk-store-backed)
        // ───────────────────────────────────────────────────────────

        static NativeHeap CreateNativeUniqueHeap()
        {
            return new NativeHeap(TrecsLog.Default);
        }

        [Test]
        public void NativeUniquePtr_Alloc_ReturnsValidPtr()
        {
            var chunkStore = CreateNativeUniqueHeap();
            var ptr = NativeUniquePtr.Alloc<int>(chunkStore, 42);
            NAssert.IsFalse(ptr.IsNull);
            ptr.Dispose(chunkStore);
            chunkStore.Dispose();
        }

        [Test]
        public void NativeUniquePtr_Get_ReturnsSameValue()
        {
            var chunkStore = CreateNativeUniqueHeap();
            var ptr = NativeUniquePtr.Alloc<int>(chunkStore, 42);
            ref readonly int value = ref ptr.Read(chunkStore.Resolver).Value;
            NAssert.AreEqual(42, value);
            ptr.Dispose(chunkStore);
            chunkStore.Dispose();
        }

        [Test]
        public void NativeUniquePtr_GetMut_SupportsWrite()
        {
            var chunkStore = CreateNativeUniqueHeap();
            var ptr = NativeUniquePtr.Alloc<int>(chunkStore, 42);
            ref int value = ref ptr.Write(chunkStore.Resolver).Value;
            NAssert.AreEqual(42, value);
            value = 99;
            ref readonly int readBack = ref ptr.Read(chunkStore.Resolver).Value;
            NAssert.AreEqual(99, readBack);
            ptr.Dispose(chunkStore);
            chunkStore.Dispose();
        }

        [Test]
        public void NativeUniquePtr_Set_OverwritesValue()
        {
            var chunkStore = CreateNativeUniqueHeap();
            var ptr = NativeUniquePtr.Alloc<int>(chunkStore, 42);
            ptr.Write(chunkStore.Resolver).Set(123);
            ref readonly int readBack = ref ptr.Read(chunkStore.Resolver).Value;
            NAssert.AreEqual(123, readBack);
            ptr.Dispose(chunkStore);
            chunkStore.Dispose();
        }

        [Test]
        public void NativeUniquePtr_Dispose_RemovesEntry()
        {
            var chunkStore = CreateNativeUniqueHeap();
            var ptr = NativeUniquePtr.Alloc<int>(chunkStore, 42);
            NAssert.AreEqual(1, chunkStore.NumLiveAllocations);
            ptr.Dispose(chunkStore);
            NAssert.AreEqual(0, chunkStore.NumLiveAllocations);
            chunkStore.Dispose();
        }

        [Test]
        public void NativeUniquePtr_Alloc_ImmediatelyVisibleToResolver()
        {
            var chunkStore = CreateNativeUniqueHeap();
            var ptr = NativeUniquePtr.Alloc<int>(chunkStore, 42);
            // Under the immediate-write model, the new entry is in the side table
            // before Alloc returns — resolver can see it without any flush.
            ref readonly int readValue = ref ptr.Read(chunkStore.Resolver).Value;
            NAssert.AreEqual(42, readValue);
            ptr.Dispose(chunkStore);
            chunkStore.Dispose();
        }

        [Test]
        public void NativeUniquePtr_AllocAndDispose_TracksLiveSlotsViaChunkStore()
        {
            var chunkStore = CreateNativeUniqueHeap();
            NAssert.AreEqual(0, chunkStore.NumLiveAllocations);

            var ptr1 = NativeUniquePtr.Alloc<int>(chunkStore, 10);
            NAssert.AreEqual(1, chunkStore.NumLiveAllocations);
            var ptr2 = NativeUniquePtr.Alloc<int>(chunkStore, 20);
            NAssert.AreEqual(2, chunkStore.NumLiveAllocations);

            ptr1.Dispose(chunkStore);
            NAssert.AreEqual(1, chunkStore.NumLiveAllocations);
            ptr2.Dispose(chunkStore);
            NAssert.AreEqual(0, chunkStore.NumLiveAllocations);

            chunkStore.Dispose();
        }

        // ───────────────────────────────────────────────────────────
        // GroupIndex 6: InputNativeUniqueHeap Tests
        // ───────────────────────────────────────────────────────────

        [Test]
        public void InputNativeUniqueHeap_Alloc_ReturnsValidPtr()
        {
            var heap = new InputNativeUniqueHeap(TrecsLog.Default);
            var ptr = heap.Alloc<int>(1, 42);
            NAssert.IsFalse(ptr.IsNull);
            NAssert.AreEqual(1, heap.NumLiveFrames);
            heap.Dispose();
        }

        [Test]
        public void InputNativeUniqueHeap_Read_ViaResolver()
        {
            var heap = new InputNativeUniqueHeap(TrecsLog.Default);
            var ptr = heap.Alloc<int>(1, 42);
            ref readonly int value = ref ptr.Read(heap.Resolver);
            NAssert.AreEqual(42, value);
            heap.Dispose();
        }

        [Test]
        public void InputNativeUniqueHeap_ClearAtOrAfterFrame_RemovesFrames()
        {
            var heap = new InputNativeUniqueHeap(TrecsLog.Default);
            heap.Alloc<int>(1, 10);
            heap.Alloc<int>(2, 20);
            heap.Alloc<int>(3, 30);
            NAssert.AreEqual(3, heap.NumLiveFrames);
            heap.ClearAtOrAfterFrame(2);
            NAssert.AreEqual(1, heap.NumLiveFrames);
            heap.Dispose();
        }

        [Test]
        public void InputNativeUniqueHeap_ClearAtOrBeforeFrame_RemovesFrames()
        {
            var heap = new InputNativeUniqueHeap(TrecsLog.Default);
            heap.Alloc<int>(1, 10);
            heap.Alloc<int>(2, 20);
            heap.Alloc<int>(3, 30);
            NAssert.AreEqual(3, heap.NumLiveFrames);
            heap.ClearAtOrBeforeFrame(2);
            NAssert.AreEqual(1, heap.NumLiveFrames);
            heap.Dispose();
        }

        [Test]
        public void InputNativeUniqueHeap_SerializeDeserialize_CleanState_RoundTrips()
        {
            var ptrA = default(InputNativeUniquePtr<int>);
            var ptrB = default(InputNativeUniquePtr<int>);
            var ptrC = default(InputNativeUniquePtr<int>);

            using var buffer = new SerializationBuffer(MakeRegistry());

            // Write
            var src = new InputNativeUniqueHeap(TrecsLog.Default);
            ptrA = src.Alloc<int>(10, 100);
            ptrB = src.Alloc<int>(11, 200);
            ptrC = src.Alloc<int>(12, 300);
            buffer.StartWrite(version: 1, includeTypeChecks: true);
            src.Serialize(buffer.Writer);
            buffer.EndWrite();
            src.Dispose();

            // Read
            buffer.ResetMemoryPosition();
            var dst = new InputNativeUniqueHeap(TrecsLog.Default);
            buffer.StartRead();
            dst.Deserialize(buffer.Reader);
            buffer.StopRead(verifySentinel: false);

            NAssert.AreEqual(3, dst.NumLiveFrames);
            NAssert.AreEqual(100, ptrA.Read(dst.Resolver));
            NAssert.AreEqual(200, ptrB.Read(dst.Resolver));
            NAssert.AreEqual(300, ptrC.Read(dst.Resolver));
            dst.Dispose();
        }

        [Test]
        public void InputNativeUniqueHeap_SerializeDeserialize_AfterFrameTrim_RoundTrips()
        {
            // Round-trip after a trim+realloc sequence. Handles are content-
            // addressed monotonic IDs, so survivors and post-trim allocs each
            // carry their own ID into the wire format. Deserialize re-mints
            // an allocation per ID and bumps the counter past the max so a
            // subsequent live Alloc doesn't collide.
            using var buffer = new SerializationBuffer(MakeRegistry());

            var src = new InputNativeUniqueHeap(TrecsLog.Default);
            src.Alloc<int>(10, 100);
            src.Alloc<int>(11, 200);
            var ptr12 = src.Alloc<int>(12, 300);
            src.ClearAtOrBeforeFrame(11);
            var ptr13 = src.Alloc<int>(13, 400);
            var ptr14 = src.Alloc<int>(14, 500);

            buffer.StartWrite(version: 1, includeTypeChecks: true);
            src.Serialize(buffer.Writer);
            buffer.EndWrite();
            src.Dispose();

            buffer.ResetMemoryPosition();
            var dst = new InputNativeUniqueHeap(TrecsLog.Default);
            buffer.StartRead();
            dst.Deserialize(buffer.Reader);
            buffer.StopRead(verifySentinel: false);

            NAssert.AreEqual(3, dst.NumLiveFrames);
            NAssert.AreEqual(300, ptr12.Read(dst.Resolver));
            NAssert.AreEqual(400, ptr13.Read(dst.Resolver));
            NAssert.AreEqual(500, ptr14.Read(dst.Resolver));

            // A fresh Alloc after Deserialize must get an ID past any restored
            // one, so the new entry coexists with the survivors in _allocations.
            var ptrA = dst.Alloc<int>(20, 1000);
            NAssert.AreNotEqual(ptr14.Handle.Value, ptrA.Handle.Value);
            NAssert.AreEqual(1000, ptrA.Read(dst.Resolver));
            NAssert.AreEqual(300, ptr12.Read(dst.Resolver));
            dst.Dispose();
        }

        [Test]
        public unsafe void InputNativeUniqueHeap_ResolveUnsafePtr_WrongT_Throws()
        {
            // Wrong-T read fails the structural TypeHash check on the
            // main-thread Resolve path. The check is now always-on (uses
            // TrecsAssert, not TrecsDebugAssert), so this test runs in both
            // DEBUG and release builds.
            var heap = new InputNativeUniqueHeap(TrecsLog.Default);
            var ptr = heap.Alloc<int>(1, 42);
            NAssert.Throws<TrecsException>(() => heap.ResolveUnsafePtr<float>(ptr.Handle));
            heap.Dispose();
        }

        [Test]
        public unsafe void InputNativeUniqueHeap_ResolveUnsafePtr_SameT_DoesNotThrow()
        {
            var heap = new InputNativeUniqueHeap(TrecsLog.Default);
            var ptr = heap.Alloc<int>(1, 42);
            NAssert.DoesNotThrow(() => heap.ResolveUnsafePtr<int>(ptr.Handle));
            heap.Dispose();
        }

        [Test]
        public unsafe void InputNativeUniqueHeap_ResolveUnsafePtr_AfterFrameTrim_StaleHandleThrows()
        {
            // alloc<int> at frame 1. Trim frame 1, freeing the allocation.
            // Resolving the now-stale handle must throw — the handle is no
            // longer present in _allocations. A subsequent alloc<float> at
            // frame 2 gets a fresh handle ID; resolving its handle as float
            // succeeds.
            var heap = new InputNativeUniqueHeap(TrecsLog.Default);
            var intPtr = heap.Alloc<int>(1, 42);
            heap.ClearAtOrBeforeFrame(1);
            NAssert.Throws<TrecsException>(() => heap.ResolveUnsafePtr<int>(intPtr.Handle));
            var floatPtr = heap.Alloc<float>(2, 3.14f);
            NAssert.AreNotEqual(intPtr.Handle.Value, floatPtr.Handle.Value);
            NAssert.DoesNotThrow(() => heap.ResolveUnsafePtr<float>(floatPtr.Handle));
            heap.Dispose();
        }

        [Test]
        public unsafe void InputNativeUniqueHeap_ResolveUnsafePtr_AfterDeserialize_TagsRoundTrip()
        {
            // Type tags round-trip through Serialize/Deserialize as part of
            // InputAllocation. After deserialize, same-T reads succeed and
            // wrong-T reads must throw — i.e. the deserialized heap behaves
            // identically to a live heap with respect to the type-check.
            using var buffer = new SerializationBuffer(MakeRegistry());
            var src = new InputNativeUniqueHeap(TrecsLog.Default);
            var ptr = src.Alloc<int>(1, 42);
            buffer.StartWrite(version: 1, includeTypeChecks: true);
            src.Serialize(buffer.Writer);
            buffer.EndWrite();
            src.Dispose();

            buffer.ResetMemoryPosition();
            var dst = new InputNativeUniqueHeap(TrecsLog.Default);
            buffer.StartRead();
            dst.Deserialize(buffer.Reader);
            buffer.StopRead(verifySentinel: false);

            NAssert.DoesNotThrow(() => dst.ResolveUnsafePtr<int>(ptr.Handle));
            NAssert.Throws<TrecsException>(() => dst.ResolveUnsafePtr<float>(ptr.Handle));
            dst.Dispose();
        }

        [Test]
        public unsafe void InputNativeUniqueHeap_Resolver_WrongT_Throws()
        {
            // Wrong-T cast on the Burst-readable resolver path also fails the
            // structural TypeHash check. (We exercise the resolver from
            // managed code here — Burst-compiled code paths are exercised by
            // higher-level tests.)
            var heap = new InputNativeUniqueHeap(TrecsLog.Default);
            var ptr = heap.Alloc<int>(1, 42);
            // Reinterpret as InputNativeUniquePtr<float> via the internal
            // ctor — same handle, different declared T — and read through
            // the resolver.
            var wrongPtr = new InputNativeUniquePtr<float>(ptr.Handle);
            NAssert.Throws<TrecsException>(() =>
            {
                ref readonly var _ = ref wrongPtr.Read(heap.Resolver);
            });
            heap.Dispose();
        }

        [Test]
        public void InputNativeUniqueHeap_ManyAllocations_RoundTripPreservesAllValues()
        {
            // Many allocations on a single frame round-trip correctly. Each
            // allocation is its own native buffer with its own handle ID;
            // Serialize walks the per-frame bucket and emits them in order.
            const int Count = 5000;
            var ptrs = new InputNativeUniquePtr<int>[Count];

            using var buffer = new SerializationBuffer(MakeRegistry());

            var src = new InputNativeUniqueHeap(TrecsLog.Default);
            for (int i = 0; i < Count; i++)
            {
                ptrs[i] = src.Alloc<int>(7, i * 7);
            }
            NAssert.AreEqual(1, src.NumLiveFrames);
            NAssert.AreEqual(Count, src.NumLiveAllocations);
            for (int i = 0; i < Count; i++)
            {
                NAssert.AreEqual(i * 7, ptrs[i].Read(src.Resolver));
            }

            buffer.StartWrite(version: 1, includeTypeChecks: true);
            src.Serialize(buffer.Writer);
            buffer.EndWrite();
            src.Dispose();

            buffer.ResetMemoryPosition();
            var dst = new InputNativeUniqueHeap(TrecsLog.Default);
            buffer.StartRead();
            dst.Deserialize(buffer.Reader);
            buffer.StopRead(verifySentinel: false);

            NAssert.AreEqual(Count, dst.NumLiveAllocations);
            for (int i = 0; i < Count; i++)
            {
                NAssert.AreEqual(i * 7, ptrs[i].Read(dst.Resolver));
            }
            dst.Dispose();
        }

        static SerializerRegistry MakeRegistry()
        {
            var registry = new SerializerRegistry();
            DefaultTrecsSerializers.RegisterCommonTrecsSerializers(registry);
            return registry;
        }

        // ───────────────────────────────────────────────────────────
        // GroupIndex 7: InputNativeSharedHeap Tests
        // ───────────────────────────────────────────────────────────

        [Test]
        public void InputNativeSharedHeap_SerializeDeserialize_CleanState_RoundTrips()
        {
            // The blob bytes live in BlobCache; the heap only tracks refcount
            // handles. Both src and dst share the same BlobCache so the blobs
            // survive across the round-trip — Deserialize's job is to re-mint
            // one refcount handle per (frame, BlobId) entry so the blob stays
            // pinned for the lifetime of the input frame.
            var blobCache = CreateBlobCache();
            using var buffer = new SerializationBuffer(MakeRegistry());

            var src = new InputNativeSharedHeap(TrecsLog.Default, blobCache);
            var ptrA = src.Alloc<int>(10, new BlobId(1), 100);
            var ptrB = src.Alloc<int>(11, new BlobId(2), 200);
            var ptrC = src.Alloc<int>(12, new BlobId(3), 300);

            NAssert.AreEqual(3, src.NumLiveFrames);

            buffer.StartWrite(version: 1, includeTypeChecks: true);
            src.Serialize(buffer.Writer);
            buffer.EndWrite();
            src.Dispose();

            buffer.ResetMemoryPosition();
            var dst = new InputNativeSharedHeap(TrecsLog.Default, blobCache);
            buffer.StartRead();
            dst.Deserialize(buffer.Reader);
            buffer.StopRead(verifySentinel: false);

            NAssert.AreEqual(3, dst.NumLiveFrames);
            // The blob bytes were never serialized — they're still in the
            // BlobCache. A regression in the refcount-rewind path would leave
            // dangling BlobIds in the wire format that fail re-resolution here.
            NAssert.AreEqual(100, blobCache.GetNativeBlobRef<int>(ptrA.BlobId));
            NAssert.AreEqual(200, blobCache.GetNativeBlobRef<int>(ptrB.BlobId));
            NAssert.AreEqual(300, blobCache.GetNativeBlobRef<int>(ptrC.BlobId));
            dst.Dispose();
            blobCache.Dispose();
        }

        [Test]
        public void InputNativeSharedHeap_SerializeDeserialize_AfterTryAcquireAndTrim_RoundTrips()
        {
            // Multiple TryAcquire calls bump the refcount on the same BlobId;
            // ClearAtOrBefore releases the per-frame bucket only, not the blob
            // itself. The round-trip must preserve the surviving (frame, BlobId)
            // pairs and re-mint one refcount handle per pair on Deserialize.
            var blobCache = CreateBlobCache();
            using var buffer = new SerializationBuffer(MakeRegistry());

            var src = new InputNativeSharedHeap(TrecsLog.Default, blobCache);
            src.Alloc<int>(10, new BlobId(1), 100);
            src.Alloc<int>(11, new BlobId(2), 200);
            // TryAcquire bumps refcount on BlobId(1) under a new frame.
            NAssert.IsTrue(src.TryAcquire<int>(12, new BlobId(1), out var ptr12));
            src.Alloc<int>(13, new BlobId(3), 300);
            // TryAcquire under the same frame appends another entry — refcount
            // accounting must survive the round-trip per-entry.
            NAssert.IsTrue(src.TryAcquire<int>(13, new BlobId(2), out var ptr13b));
            // Trim early frames; survivors are frames 12 & 13.
            src.ClearAtOrBeforeFrame(11);
            NAssert.AreEqual(2, src.NumLiveFrames);

            buffer.StartWrite(version: 1, includeTypeChecks: true);
            src.Serialize(buffer.Writer);
            buffer.EndWrite();
            src.Dispose();

            buffer.ResetMemoryPosition();
            var dst = new InputNativeSharedHeap(TrecsLog.Default, blobCache);
            buffer.StartRead();
            dst.Deserialize(buffer.Reader);
            buffer.StopRead(verifySentinel: false);

            NAssert.AreEqual(2, dst.NumLiveFrames);
            NAssert.AreEqual(100, blobCache.GetNativeBlobRef<int>(ptr12.BlobId));
            NAssert.AreEqual(200, blobCache.GetNativeBlobRef<int>(ptr13b.BlobId));
            NAssert.AreEqual(300, blobCache.GetNativeBlobRef<int>(new BlobId(3)));

            // Trim one frame on dst — only the entries for that frame should
            // release. If Deserialize re-minted handles incorrectly (e.g. lost
            // one), this trim would either skip a release (warning on dispose)
            // or release a stale handle (RemoveMustExist throws).
            dst.ClearAtOrBeforeFrame(12);
            NAssert.AreEqual(1, dst.NumLiveFrames);

            dst.Dispose();
            blobCache.Dispose();
        }

        [Test]
        public void InputNativeSharedHeap_Deserialize_OnDirtyHeap_DefensiveClearAll()
        {
            // The Deserialize contract is ClearAll-then-Deserialize. The
            // defensive ClearAll() at the top of Deserialize must scrub a
            // dirty heap so a missed caller-side ClearAll doesn't double-mint
            // refcount handles for entries from the prior session.
            var blobCache = CreateBlobCache();
            using var buffer = new SerializationBuffer(MakeRegistry());

            var src = new InputNativeSharedHeap(TrecsLog.Default, blobCache);
            src.Alloc<int>(10, new BlobId(1), 100);
            src.Alloc<int>(11, new BlobId(2), 200);

            buffer.StartWrite(version: 1, includeTypeChecks: true);
            src.Serialize(buffer.Writer);
            buffer.EndWrite();
            src.Dispose();

            // dst already has unrelated state — Alloc<int>(99, …) — when
            // Deserialize is called. The defensive ClearAll inside Deserialize
            // must release that handle before re-minting from the wire.
            buffer.ResetMemoryPosition();
            var dst = new InputNativeSharedHeap(TrecsLog.Default, blobCache);
            dst.Alloc<int>(99, new BlobId(999), 9999);
            NAssert.AreEqual(1, dst.NumLiveFrames);

            buffer.StartRead();
            dst.Deserialize(buffer.Reader);
            buffer.StopRead(verifySentinel: false);

            // Dirty entry got wiped; only the deserialized frames are live.
            NAssert.AreEqual(2, dst.NumLiveFrames);
            NAssert.AreEqual(100, blobCache.GetNativeBlobRef<int>(new BlobId(1)));
            NAssert.AreEqual(200, blobCache.GetNativeBlobRef<int>(new BlobId(2)));

            dst.Dispose();
            blobCache.Dispose();
        }

        // ───────────────────────────────────────────────────────────
        // GroupIndex 8: InputSharedHeap Tests
        // ───────────────────────────────────────────────────────────

        static SerializerRegistry MakeRegistryWithManagedListInt()
        {
            // InputUniqueHeap.Serialize / Deserialize round-trip arbitrary
            // managed values via WriteObject / ReadObject, which needs a
            // type-resolved serializer in the registry. ListSerializer<int>
            // covers our test payloads (List<int>) without dragging the full
            // TestSerializerInstaller surface area into this fixture.
            var registry = MakeRegistry();
            registry.RegisterSerializer(new ListSerializer<int>());
            return registry;
        }

        [Test]
        public void InputSharedHeap_SerializeDeserialize_CleanState_RoundTrips()
        {
            // Managed-side analog of the InputNativeSharedHeap round-trip:
            // BlobCache owns the object reference, the heap owns refcount
            // handles only, and Deserialize re-mints one handle per
            // (frame, BlobId) entry.
            var blobCache = CreateBlobCache();
            using var buffer = new SerializationBuffer(MakeRegistry());

            var src = new InputSharedHeap(TrecsLog.Default, blobCache);
            var listA = new List<string> { "a" };
            var listB = new List<string> { "b" };
            var listC = new List<string> { "c" };
            var ptrA = src.Alloc<List<string>>(10, new BlobId(1), listA);
            var ptrB = src.Alloc<List<string>>(11, new BlobId(2), listB);
            var ptrC = src.Alloc<List<string>>(12, new BlobId(3), listC);

            NAssert.AreEqual(3, src.NumLiveFrames);

            buffer.StartWrite(version: 1, includeTypeChecks: true);
            src.Serialize(buffer.Writer);
            buffer.EndWrite();
            src.Dispose();

            buffer.ResetMemoryPosition();
            var dst = new InputSharedHeap(TrecsLog.Default, blobCache);
            buffer.StartRead();
            dst.Deserialize(buffer.Reader);
            buffer.StopRead(verifySentinel: false);

            NAssert.AreEqual(3, dst.NumLiveFrames);
            NAssert.AreSame(listA, blobCache.GetManagedBlob<List<string>>(ptrA.BlobId));
            NAssert.AreSame(listB, blobCache.GetManagedBlob<List<string>>(ptrB.BlobId));
            NAssert.AreSame(listC, blobCache.GetManagedBlob<List<string>>(ptrC.BlobId));
            dst.Dispose();
            blobCache.Dispose();
        }

        [Test]
        public void InputSharedHeap_SerializeDeserialize_AfterTryAcquireAndTrim_RoundTrips()
        {
            var blobCache = CreateBlobCache();
            using var buffer = new SerializationBuffer(MakeRegistry());

            var src = new InputSharedHeap(TrecsLog.Default, blobCache);
            var listA = new List<string> { "a" };
            var listB = new List<string> { "b" };
            var listC = new List<string> { "c" };
            src.Alloc<List<string>>(10, new BlobId(1), listA);
            src.Alloc<List<string>>(11, new BlobId(2), listB);
            NAssert.IsTrue(src.TryAcquire<List<string>>(12, new BlobId(1), out var ptr12));
            src.Alloc<List<string>>(13, new BlobId(3), listC);
            NAssert.IsTrue(src.TryAcquire<List<string>>(13, new BlobId(2), out var ptr13b));
            src.ClearAtOrBeforeFrame(11);
            NAssert.AreEqual(2, src.NumLiveFrames);

            buffer.StartWrite(version: 1, includeTypeChecks: true);
            src.Serialize(buffer.Writer);
            buffer.EndWrite();
            src.Dispose();

            buffer.ResetMemoryPosition();
            var dst = new InputSharedHeap(TrecsLog.Default, blobCache);
            buffer.StartRead();
            dst.Deserialize(buffer.Reader);
            buffer.StopRead(verifySentinel: false);

            NAssert.AreEqual(2, dst.NumLiveFrames);
            NAssert.AreSame(listA, blobCache.GetManagedBlob<List<string>>(ptr12.BlobId));
            NAssert.AreSame(listB, blobCache.GetManagedBlob<List<string>>(ptr13b.BlobId));
            NAssert.AreSame(listC, blobCache.GetManagedBlob<List<string>>(new BlobId(3)));

            dst.ClearAtOrBeforeFrame(12);
            NAssert.AreEqual(1, dst.NumLiveFrames);

            dst.Dispose();
            blobCache.Dispose();
        }

        [Test]
        public void InputSharedHeap_Deserialize_OnDirtyHeap_DefensiveClearAll()
        {
            var blobCache = CreateBlobCache();
            using var buffer = new SerializationBuffer(MakeRegistry());

            var src = new InputSharedHeap(TrecsLog.Default, blobCache);
            var listA = new List<string> { "a" };
            var listB = new List<string> { "b" };
            src.Alloc<List<string>>(10, new BlobId(1), listA);
            src.Alloc<List<string>>(11, new BlobId(2), listB);

            buffer.StartWrite(version: 1, includeTypeChecks: true);
            src.Serialize(buffer.Writer);
            buffer.EndWrite();
            src.Dispose();

            buffer.ResetMemoryPosition();
            var dst = new InputSharedHeap(TrecsLog.Default, blobCache);
            dst.Alloc<List<string>>(99, new BlobId(999), new List<string> { "dirty" });
            NAssert.AreEqual(1, dst.NumLiveFrames);

            buffer.StartRead();
            dst.Deserialize(buffer.Reader);
            buffer.StopRead(verifySentinel: false);

            NAssert.AreEqual(2, dst.NumLiveFrames);
            NAssert.AreSame(listA, blobCache.GetManagedBlob<List<string>>(new BlobId(1)));
            NAssert.AreSame(listB, blobCache.GetManagedBlob<List<string>>(new BlobId(2)));

            dst.Dispose();
            blobCache.Dispose();
        }

        // ───────────────────────────────────────────────────────────
        // GroupIndex 9: InputUniqueHeap Tests
        // ───────────────────────────────────────────────────────────

        sealed class StubPoolManager : ITrecsPoolManager
        {
            // The InputUniqueHeap tests below only use the value-passing
            // Alloc<T>(frame, T value) overload, so Spawn<T>() is never
            // reached. HasPool* must return true to clear the Alloc<T>
            // precondition. Despawn is a no-op — the heap owns the object
            // for the lifetime of the allocating frame and hands it off
            // to us on trim; we just drop it on the floor.
            public bool HasPool(Type type) => true;

            public bool HasPool<T>()
                where T : class => true;

            public T Spawn<T>()
                where T : class
            {
                throw new InvalidOperationException(
                    "StubPoolManager.Spawn was not expected to be called by these tests"
                );
            }

            public void Despawn(object value) { }

            public void Despawn(Type type, object value) { }
        }

        [Test]
        public void InputUniqueHeap_SerializeDeserialize_CleanState_RoundTrips()
        {
            // InputUniqueHeap owns the values themselves (not refcounts into a
            // BlobCache), so the wire format carries the objects via
            // WriteObject / ReadObject. The pool manager is only consulted on
            // Despawn during frame trim — Deserialize re-instantiates each
            // entry from the wire and rebuilds _handlesByFrame from each
            // entry's Frame field.
            var pool = new StubPoolManager();
            using var buffer = new SerializationBuffer(MakeRegistryWithManagedListInt());

            var src = new InputUniqueHeap(TrecsLog.Default, pool);
            var ptrA = src.Alloc<List<int>>(10, new List<int> { 100 });
            var ptrB = src.Alloc<List<int>>(11, new List<int> { 200, 201 });
            var ptrC = src.Alloc<List<int>>(12, new List<int> { 300, 301, 302 });

            NAssert.AreEqual(3, src.NumLiveAllocations);

            buffer.StartWrite(version: 1, includeTypeChecks: true);
            src.Serialize(buffer.Writer);
            buffer.EndWrite();
            src.Dispose();

            buffer.ResetMemoryPosition();
            var dst = new InputUniqueHeap(TrecsLog.Default, pool);
            buffer.StartRead();
            dst.Deserialize(buffer.Reader);
            buffer.StopRead(verifySentinel: false);

            NAssert.AreEqual(3, dst.NumLiveAllocations);
            // Each handle resolves back to a deserialized List<int> with the
            // expected contents. The objects themselves are fresh instances —
            // ReferenceEquals against the originals would not hold across the
            // round-trip — so we compare values.
            var listA = dst.ResolveValue<List<int>>(ptrA.Handle);
            var listB = dst.ResolveValue<List<int>>(ptrB.Handle);
            var listC = dst.ResolveValue<List<int>>(ptrC.Handle);
            CollectionAssert.AreEqual(new[] { 100 }, listA);
            CollectionAssert.AreEqual(new[] { 200, 201 }, listB);
            CollectionAssert.AreEqual(new[] { 300, 301, 302 }, listC);

            // _handlesByFrame is reconstructed from each entry's Frame field.
            // Trimming a single frame must release only that frame's handles
            // and leave the others resolvable.
            dst.ClearAtOrBeforeFrame(10);
            NAssert.AreEqual(2, dst.NumLiveAllocations);
            NAssert.IsFalse(dst.ContainsEntry(ptrA.Handle));
            NAssert.IsTrue(dst.ContainsEntry(ptrB.Handle));
            NAssert.IsTrue(dst.ContainsEntry(ptrC.Handle));

            dst.Dispose();
        }

        [Test]
        public void InputUniqueHeap_SerializeDeserialize_AfterTrim_RestoresIdCounter()
        {
            // Round-trip after a partial trim must preserve survivor handle IDs
            // *and* restore the id counter so a fresh Alloc on dst doesn't
            // collide with any survivor — analogous to the InputNativeUniqueHeap
            // AfterFrameTrim test. This is the path the task description calls
            // out as the "load-on-top-of-running-game" scenario.
            var pool = new StubPoolManager();
            using var buffer = new SerializationBuffer(MakeRegistryWithManagedListInt());

            var src = new InputUniqueHeap(TrecsLog.Default, pool);
            src.Alloc<List<int>>(10, new List<int> { 100 });
            src.Alloc<List<int>>(11, new List<int> { 200 });
            var ptr12 = src.Alloc<List<int>>(12, new List<int> { 300 });
            src.ClearAtOrBeforeFrame(11);
            var ptr13 = src.Alloc<List<int>>(13, new List<int> { 400 });
            var ptr14 = src.Alloc<List<int>>(14, new List<int> { 500 });

            buffer.StartWrite(version: 1, includeTypeChecks: true);
            src.Serialize(buffer.Writer);
            buffer.EndWrite();
            src.Dispose();

            buffer.ResetMemoryPosition();
            var dst = new InputUniqueHeap(TrecsLog.Default, pool);
            buffer.StartRead();
            dst.Deserialize(buffer.Reader);
            buffer.StopRead(verifySentinel: false);

            NAssert.AreEqual(3, dst.NumLiveAllocations);
            CollectionAssert.AreEqual(new[] { 300 }, dst.ResolveValue<List<int>>(ptr12.Handle));
            CollectionAssert.AreEqual(new[] { 400 }, dst.ResolveValue<List<int>>(ptr13.Handle));
            CollectionAssert.AreEqual(new[] { 500 }, dst.ResolveValue<List<int>>(ptr14.Handle));

            // Fresh Alloc on dst must get an ID past any restored handle so
            // the new entry coexists with the survivors. A regression in the
            // id-counter rewind path would surface here as a duplicate-key
            // failure inside _entries.Add.
            var ptrFresh = dst.Alloc<List<int>>(20, new List<int> { 1000 });
            NAssert.AreNotEqual(ptr14.Handle.Value, ptrFresh.Handle.Value);
            NAssert.AreNotEqual(ptr13.Handle.Value, ptrFresh.Handle.Value);
            NAssert.AreNotEqual(ptr12.Handle.Value, ptrFresh.Handle.Value);
            CollectionAssert.AreEqual(new[] { 1000 }, dst.ResolveValue<List<int>>(ptrFresh.Handle));

            dst.Dispose();
        }

        [Test]
        public void InputUniqueHeap_Deserialize_OnDirtyHeap_DefensiveClearAll()
        {
            var pool = new StubPoolManager();
            using var buffer = new SerializationBuffer(MakeRegistryWithManagedListInt());

            var src = new InputUniqueHeap(TrecsLog.Default, pool);
            src.Alloc<List<int>>(10, new List<int> { 100 });
            src.Alloc<List<int>>(11, new List<int> { 200 });

            buffer.StartWrite(version: 1, includeTypeChecks: true);
            src.Serialize(buffer.Writer);
            buffer.EndWrite();
            src.Dispose();

            buffer.ResetMemoryPosition();
            var dst = new InputUniqueHeap(TrecsLog.Default, pool);
            dst.Alloc<List<int>>(99, new List<int> { 9999 });
            NAssert.AreEqual(1, dst.NumLiveAllocations);

            buffer.StartRead();
            dst.Deserialize(buffer.Reader);
            buffer.StopRead(verifySentinel: false);

            // Dirty entry was scrubbed by the defensive ClearAll at the top
            // of Deserialize.
            NAssert.AreEqual(2, dst.NumLiveAllocations);

            dst.Dispose();
        }
    }
}
