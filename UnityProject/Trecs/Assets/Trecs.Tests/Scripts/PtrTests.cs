using System;
using System.Collections.Generic;
using NUnit.Framework;
using Trecs.Internal;
using NAssert = NUnit.Framework.Assert;

namespace Trecs.Tests
{
    [TestFixture]
    public partial class PtrTests
    {
        // ───────────────────────────────────────────────────────────
        // Group 1: Pure Value-Type Tests (no DI needed)
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
            NAssert.IsFalse(ptr.IsCreated);
        }

        [Test]
        public void UniquePtr_WithHandle_IsCreated()
        {
            var ptr = new UniquePtr<string>(new PtrHandle(1));
            NAssert.IsTrue(ptr.IsCreated);
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
            var ptr = new NativeSharedPtr<int>(new PtrHandle(1), new BlobId(10));
            NAssert.IsFalse(ptr.IsNull);
            NAssert.IsTrue(ptr.IsCreated);
        }

        [Test]
        public void NativeSharedPtr_Equality()
        {
            var a = new NativeSharedPtr<int>(new PtrHandle(1), new BlobId(10));
            var b = new NativeSharedPtr<int>(new PtrHandle(1), new BlobId(10));
            var c = new NativeSharedPtr<int>(new PtrHandle(2), new BlobId(10));

            NAssert.IsTrue(a.Equals(b));
            NAssert.IsFalse(a.Equals(c));
            NAssert.IsTrue(a.Equals((object)b));
            NAssert.IsFalse(a.Equals((object)c));
        }

        [Test]
        public void NativeSharedPtr_HashCode()
        {
            var a = new NativeSharedPtr<int>(new PtrHandle(1), new BlobId(10));
            var b = new NativeSharedPtr<int>(new PtrHandle(1), new BlobId(10));
            NAssert.AreEqual(a.GetHashCode(), b.GetHashCode());
        }

        #endregion

        // ───────────────────────────────────────────────────────────
        // Group 2: BlobCache + BlobPtr Integration Tests
        // ───────────────────────────────────────────────────────────

        static BlobCache CreateBlobCache()
        {
            var blobStore = new BlobStoreInMemory(
                new BlobStoreInMemorySettings { MaxMemoryCacheMb = 100 },
                null
            );
            var settings = new BlobCacheSettings
            {
                CleanIntervalSeconds = 99999,
                SerializationVersion = 1,
            };
            return new BlobCache(new List<IBlobStore> { blobStore }, settings);
        }

        [Test]
        public void BlobCache_CreateManagedHandle_ReturnsBlobPtr()
        {
            var blobCache = CreateBlobCache();

            var blob = new List<string> { "hello" };
            var ptr = blobCache.CreateBlobPtr(blob);

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
            var ptr = blobCache.CreateBlobPtr(blob);

            var retrieved = ptr.Get(blobCache);
            NAssert.AreSame(blob, retrieved);

            ptr.Dispose(blobCache);
            blobCache.Dispose();
        }

        [Test]
        public void BlobCache_HasBlob_ReturnsTrueForExisting()
        {
            var blobCache = CreateBlobCache();

            var blob = new List<string> { "test" };
            var ptr = blobCache.CreateBlobPtr(blob);

            NAssert.IsTrue(blobCache.HasBlob(ptr.BlobId));

            ptr.Dispose(blobCache);
            blobCache.Dispose();
        }

        [Test]
        public void BlobCache_HasBlob_ReturnsFalseForMissing()
        {
            var blobCache = CreateBlobCache();

            NAssert.IsFalse(blobCache.HasBlob(new BlobId(999999)));

            blobCache.Dispose();
        }

        [Test]
        public void BlobPtr_Get_ReturnsBlobValue()
        {
            var blobCache = CreateBlobCache();

            var blob = new List<string> { "value" };
            var ptr = blobCache.CreateBlobPtr(blob);

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
            var ptr = blobCache.CreateBlobPtr(blob);

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
            var ptr = blobCache.CreateBlobPtr(blob);

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
            var ptr = blobCache.CreateBlobPtr(blob);
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
            var ptr = blobCache.CreateBlobPtr(blob);

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
        // Group 2b: NativeBlobPtr + BlobCache Integration Tests
        // ───────────────────────────────────────────────────────────

        [Test]
        public void NativeBlobPtr_CreateNativeHandle_ReturnsValidPtr()
        {
            var blobCache = CreateBlobCache();

            var ptr = blobCache.CreateNativeBlobPtr<int>(42);

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

            var ptr = blobCache.CreateNativeBlobPtr<int>(42);

            ref int value = ref ptr.Get(blobCache);
            NAssert.AreEqual(42, value);

            ptr.Dispose(blobCache);
            blobCache.Dispose();
        }

        [Test]
        public void NativeBlobPtr_TryGetPtr_ReturnsTrueAndPtr()
        {
            var blobCache = CreateBlobCache();

            var ptr = blobCache.CreateNativeBlobPtr<int>(42);

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

            var ptr = blobCache.CreateNativeBlobPtr<int>(42);

            NAssert.IsTrue(ptr.CanGet(blobCache));

            ptr.Dispose(blobCache);
            NAssert.IsFalse(ptr.CanGet(blobCache));

            blobCache.Dispose();
        }

        [Test]
        public void NativeBlobPtr_Clone_CreatesIndependentHandle()
        {
            var blobCache = CreateBlobCache();

            var ptr = blobCache.CreateNativeBlobPtr<int>(42);
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

            var ptr = blobCache.CreateNativeBlobPtr<int>(42);

            NAssert.IsTrue(ptr.CanGet(blobCache));
            ptr.Dispose(blobCache);
            NAssert.IsFalse(ptr.CanGet(blobCache));

            blobCache.Dispose();
        }

        // ───────────────────────────────────────────────────────────
        // Group 3: SharedHeap Tests
        // ───────────────────────────────────────────────────────────

        static (SharedHeap heap, BlobCache blobCache) CreateSharedHeap()
        {
            var blobCache = CreateBlobCache();
            var heap = new SharedHeap(blobCache);
            return (heap, blobCache);
        }

        [Test]
        public void SharedHeap_CreateBlob_ReturnsValidPtr()
        {
            var (heap, blobCache) = CreateSharedHeap();

            var blob = new List<object> { "shared" };
            var ptr = heap.CreateBlob(blob);

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
            var ptr = heap.CreateBlob(blob);

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
            var ptr = heap.CreateBlob(blob);

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
            var ptr = heap.CreateBlob(blob);

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

            var ptr1 = heap.CreateBlob(new List<object> { "a" });
            NAssert.AreEqual(1, heap.NumEntries);

            var ptr2 = heap.CreateBlob(new List<object> { "b" });
            NAssert.AreEqual(2, heap.NumEntries);

            heap.DisposeHandle(ptr1.Handle);
            NAssert.AreEqual(1, heap.NumEntries);

            heap.DisposeHandle(ptr2.Handle);
            NAssert.AreEqual(0, heap.NumEntries);

            heap.Dispose();
            blobCache.Dispose();
        }

        // ───────────────────────────────────────────────────────────
        // Group 3b: UniqueHeap Tests
        // ───────────────────────────────────────────────────────────

        static UniqueHeap CreateUniqueHeap()
        {
            return new UniqueHeap(null);
        }

        [Test]
        public void UniqueHeap_AllocUnique_ReturnsValidPtr()
        {
            var heap = CreateUniqueHeap();

            var value = new List<string> { "hello" };
            var ptr = heap.AllocUnique(value);

            NAssert.IsTrue(ptr.IsCreated);
            NAssert.IsFalse(ptr.IsNull);

            heap.Dispose();
        }

        [Test]
        public void UniqueHeap_AllocUnique_WithNull_ReturnsValidPtr()
        {
            var heap = CreateUniqueHeap();

            var ptr = heap.AllocUnique<List<string>>(null);

            NAssert.IsTrue(ptr.IsCreated);
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
        // Group 3c: UniquePtr + UniqueHeap Integration Tests
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
        // Group 3b: NativeUniquePtr Value-Type Tests
        // ───────────────────────────────────────────────────────────

        [Test]
        public void NativeUniquePtr_DefaultIsNull()
        {
            var ptr = default(NativeUniquePtr<int>);
            NAssert.IsTrue(ptr.IsNull);
            NAssert.IsFalse(ptr.IsCreated);
        }

        [Test]
        public void NativeUniquePtr_WithHandle_IsCreated()
        {
            var ptr = new NativeUniquePtr<int>(new PtrHandle(1));
            NAssert.IsFalse(ptr.IsNull);
            NAssert.IsTrue(ptr.IsCreated);
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
        // Group 4: NativeSharedHeap Tests
        // ───────────────────────────────────────────────────────────

        static (NativeSharedHeap heap, BlobCache blobCache) CreateNativeSharedHeap()
        {
            var blobCache = CreateBlobCache();
            var heap = new NativeSharedHeap(blobCache);
            return (heap, blobCache);
        }

        [Test]
        public void NativeSharedHeap_CreateBlob_ReturnsValidPtr()
        {
            var (heap, blobCache) = CreateNativeSharedHeap();

            var ptr = heap.CreateBlob<int>(42);

            NAssert.IsFalse(ptr.IsNull);
            NAssert.IsFalse(ptr.Handle.IsNull);
            NAssert.IsFalse(ptr.BlobId.IsNull);

            heap.Dispose();
            blobCache.Dispose();
        }

        [Test]
        public void NativeSharedHeap_GetBlob_ReturnsSameValue()
        {
            var (heap, blobCache) = CreateNativeSharedHeap();

            var ptr = heap.CreateBlob<int>(42);

            // Native resolve (NativeSharedPtrResolver) requires flush first
            heap.FlushPendingOperations();
            ref int value = ref ptr.Get(heap.Resolver);
            NAssert.AreEqual(42, value);

            heap.Dispose();
            blobCache.Dispose();
        }

        [Test]
        public void NativeSharedHeap_DisposeHandle_RemovesBlob()
        {
            var (heap, blobCache) = CreateNativeSharedHeap();

            var ptr = heap.CreateBlob<int>(42);

            NAssert.AreEqual(1, heap.NumEntries);

            heap.DisposeHandle(ptr.Handle);
            NAssert.AreEqual(0, heap.NumEntries);

            heap.Dispose();
            blobCache.Dispose();
        }

        [Test]
        public void NativeSharedHeap_Clone_SharesBlob()
        {
            var (heap, blobCache) = CreateNativeSharedHeap();

            var ptr = heap.CreateBlob<int>(42);

            heap.TryClone<int>(ptr.Handle, out var clone);
            NAssert.IsFalse(clone.IsNull);

            heap.DisposeHandle(ptr.Handle);

            heap.FlushPendingOperations();
            ref int value = ref clone.Get(heap.Resolver);
            NAssert.AreEqual(42, value);

            heap.Dispose();
            blobCache.Dispose();
        }

        [Test]
        public void NativeSharedHeap_CreateAndDispose_BeforeFlush()
        {
            var (heap, blobCache) = CreateNativeSharedHeap();

            var ptr = heap.CreateBlob<int>(42);
            NAssert.AreEqual(1, heap.NumEntries);

            heap.DisposeHandle(ptr.Handle);
            NAssert.AreEqual(0, heap.NumEntries);

            // Both pending add and pending remove for the same blob —
            // flush should process adds first so the remove finds the entry
            heap.FlushPendingOperations();
            NAssert.AreEqual(0, heap.NumEntries);

            heap.Dispose();
            blobCache.Dispose();
        }

        [Test]
        public void NativeSharedHeap_NumEntries_TracksActiveBlobs()
        {
            var (heap, blobCache) = CreateNativeSharedHeap();

            NAssert.AreEqual(0, heap.NumEntries);

            var ptr1 = heap.CreateBlob<int>(10);
            NAssert.AreEqual(1, heap.NumEntries);

            var ptr2 = heap.CreateBlob<int>(20);
            NAssert.AreEqual(2, heap.NumEntries);

            heap.DisposeHandle(ptr1.Handle);
            NAssert.AreEqual(1, heap.NumEntries);

            heap.DisposeHandle(ptr2.Handle);
            NAssert.AreEqual(0, heap.NumEntries);

            heap.Dispose();
            blobCache.Dispose();
        }

        // ───────────────────────────────────────────────────────────
        // Group 5: NativeUniqueHeap Tests
        // ───────────────────────────────────────────────────────────

        static (
            NativeUniqueHeap heap,
            FrameScopedNativeUniqueHeap frameScopedHeap
        ) CreateNativeUniqueHeap()
        {
            var heap = new NativeUniqueHeap();
            var frameScopedHeap = new FrameScopedNativeUniqueHeap();
            heap.SetFrameScopedEntries(frameScopedHeap.AllEntries);
            return (heap, frameScopedHeap);
        }

        [Test]
        public void NativeUniqueHeap_Alloc_ReturnsValidPtr()
        {
            var (heap, frameScopedHeap) = CreateNativeUniqueHeap();

            var ptr = heap.Alloc<int>(42);

            NAssert.IsFalse(ptr.IsNull);
            NAssert.IsTrue(ptr.IsCreated);

            heap.Dispose();
            frameScopedHeap.Dispose();
        }

        [Test]
        public void NativeUniqueHeap_Get_ReturnsSameValue()
        {
            var (heap, frameScopedHeap) = CreateNativeUniqueHeap();

            var ptr = heap.Alloc<int>(42);

            // Resolver requires flush first
            heap.FlushPendingOperations();
            ref readonly int value = ref ptr.Get(heap.Resolver);
            NAssert.AreEqual(42, value);

            heap.Dispose();
            frameScopedHeap.Dispose();
        }

        [Test]
        public void NativeUniqueHeap_GetMut_SupportsWrite()
        {
            var (heap, frameScopedHeap) = CreateNativeUniqueHeap();

            var ptr = heap.Alloc<int>(42);

            heap.FlushPendingOperations();
            ref int value = ref ptr.GetMut(heap.Resolver);
            NAssert.AreEqual(42, value);

            value = 99;
            ref readonly int readBack = ref ptr.Get(heap.Resolver);
            NAssert.AreEqual(99, readBack);

            heap.Dispose();
            frameScopedHeap.Dispose();
        }

        [Test]
        public void NativeUniqueHeap_Set_OverwritesValue()
        {
            var (heap, frameScopedHeap) = CreateNativeUniqueHeap();

            var ptr = heap.Alloc<int>(42);

            heap.FlushPendingOperations();
            ptr.Set(heap.Resolver, 123);

            ref readonly int readBack = ref ptr.Get(heap.Resolver);
            NAssert.AreEqual(123, readBack);

            heap.Dispose();
            frameScopedHeap.Dispose();
        }

        [Test]
        public void NativeUniqueHeap_DisposeEntry_RemovesEntry()
        {
            var (heap, frameScopedHeap) = CreateNativeUniqueHeap();

            var ptr = heap.Alloc<int>(42);
            NAssert.AreEqual(1, heap.NumEntries);

            heap.DisposeEntry(ptr.Handle.Value);
            NAssert.AreEqual(0, heap.NumEntries);

            heap.Dispose();
            frameScopedHeap.Dispose();
        }

        [Test]
        public void NativeUniqueHeap_FlushPendingOperations_MakesEntriesVisibleToResolver()
        {
            var (heap, frameScopedHeap) = CreateNativeUniqueHeap();

            var ptr = heap.Alloc<int>(42);

            // Before flush, resolver can't see the entry
            // After flush, resolver can
            heap.FlushPendingOperations();
            ref readonly int readValue = ref ptr.Get(heap.Resolver);
            NAssert.AreEqual(42, readValue);

            heap.Dispose();
            frameScopedHeap.Dispose();
        }

        [Test]
        public void NativeUniqueHeap_CreateAndDispose_BeforeFlush()
        {
            var (heap, frameScopedHeap) = CreateNativeUniqueHeap();

            var ptr = heap.Alloc<int>(42);
            NAssert.AreEqual(1, heap.NumEntries);

            // Dispose before flush — should immediately free since never visible to jobs
            heap.DisposeEntry(ptr.Handle.Value);
            NAssert.AreEqual(0, heap.NumEntries);

            heap.FlushPendingOperations();
            NAssert.AreEqual(0, heap.NumEntries);

            heap.Dispose();
            frameScopedHeap.Dispose();
        }

        [Test]
        public void NativeUniqueHeap_NumEntries_TracksActiveEntries()
        {
            var (heap, frameScopedHeap) = CreateNativeUniqueHeap();

            NAssert.AreEqual(0, heap.NumEntries);

            var ptr1 = heap.Alloc<int>(10);
            NAssert.AreEqual(1, heap.NumEntries);

            var ptr2 = heap.Alloc<int>(20);
            NAssert.AreEqual(2, heap.NumEntries);

            heap.DisposeEntry(ptr1.Handle.Value);
            NAssert.AreEqual(1, heap.NumEntries);

            heap.DisposeEntry(ptr2.Handle.Value);
            NAssert.AreEqual(0, heap.NumEntries);

            heap.Dispose();
            frameScopedHeap.Dispose();
        }

        // ───────────────────────────────────────────────────────────
        // Group 6: FrameScopedNativeUniqueHeap Tests
        // ───────────────────────────────────────────────────────────

        [Test]
        public void FrameScopedNativeUniqueHeap_Alloc_ReturnsValidPtr()
        {
            var (heap, frameScopedHeap) = CreateNativeUniqueHeap();

            var ptr = frameScopedHeap.Alloc<int>(1, 42);

            NAssert.IsFalse(ptr.IsNull);
            NAssert.IsTrue(ptr.IsCreated);
            NAssert.AreEqual(1, frameScopedHeap.NumEntries);

            heap.Dispose();
            frameScopedHeap.Dispose();
        }

        [Test]
        public void FrameScopedNativeUniqueHeap_Get_ViaResolver()
        {
            var (heap, frameScopedHeap) = CreateNativeUniqueHeap();

            var ptr = frameScopedHeap.Alloc<int>(1, 42);

            // Frame-scoped entries go directly into NativeDenseDictionary, no flush needed
            ref readonly int value = ref ptr.Get(heap.Resolver);
            NAssert.AreEqual(42, value);

            heap.Dispose();
            frameScopedHeap.Dispose();
        }

        [Test]
        public void FrameScopedNativeUniqueHeap_ClearAtOrAfterFrame_RemovesEntries()
        {
            var (heap, frameScopedHeap) = CreateNativeUniqueHeap();

            frameScopedHeap.Alloc<int>(1, 10);
            frameScopedHeap.Alloc<int>(2, 20);
            frameScopedHeap.Alloc<int>(3, 30);

            NAssert.AreEqual(3, frameScopedHeap.NumEntries);

            frameScopedHeap.ClearAtOrAfterFrame(2);
            NAssert.AreEqual(1, frameScopedHeap.NumEntries);

            heap.Dispose();
            frameScopedHeap.Dispose();
        }

        [Test]
        public void FrameScopedNativeUniqueHeap_ClearAtOrBeforeFrame_RemovesEntries()
        {
            var (heap, frameScopedHeap) = CreateNativeUniqueHeap();

            frameScopedHeap.Alloc<int>(1, 10);
            frameScopedHeap.Alloc<int>(2, 20);
            frameScopedHeap.Alloc<int>(3, 30);

            NAssert.AreEqual(3, frameScopedHeap.NumEntries);

            frameScopedHeap.ClearAtOrBeforeFrame(2);
            NAssert.AreEqual(1, frameScopedHeap.NumEntries);

            heap.Dispose();
            frameScopedHeap.Dispose();
        }
    }
}
