using NUnit.Framework;
using Trecs.Internal;
using Trecs.Serialization;
using NAssert = NUnit.Framework.Assert;

namespace Trecs.Tests
{
    class TestHeapObject
    {
        public int Value;
    }

    [TestFixture]
    public class HeapMemoryTests
    {
        #region UniquePtr

        [Test]
        public void UniquePtr_Alloc_IsValid()
        {
            var heap = new UniqueHeap(TrecsLog.Default, null);
            var ptr = heap.AllocUnique(new TestHeapObject { Value = 42 });

            NAssert.IsFalse(ptr.Handle.IsNull);
            NAssert.AreEqual(1, heap.NumEntries);

            heap.Dispose();
        }

        [Test]
        public void UniquePtr_GetEntry_ReturnsCorrectValue()
        {
            var heap = new UniqueHeap(TrecsLog.Default, null);
            var ptr = heap.AllocUnique(new TestHeapObject { Value = 99 });

            var resolved = heap.GetEntry<TestHeapObject>(ptr.Handle.Value);
            NAssert.AreEqual(99, resolved.Value);

            heap.Dispose();
        }

        [Test]
        public void UniquePtr_DisposeEntry_ReducesCount()
        {
            var heap = new UniqueHeap(TrecsLog.Default, null);
            var ptr = heap.AllocUnique(new TestHeapObject { Value = 1 });

            NAssert.AreEqual(1, heap.NumEntries);

            heap.DisposeEntry<TestHeapObject>(ptr.Handle.Value);

            NAssert.AreEqual(0, heap.NumEntries);

            heap.Dispose();
        }

        [Test]
        public void UniquePtr_MultipleAllocs_IndependentHandles()
        {
            var heap = new UniqueHeap(TrecsLog.Default, null);
            var ptr1 = heap.AllocUnique(new TestHeapObject { Value = 10 });
            var ptr2 = heap.AllocUnique(new TestHeapObject { Value = 20 });

            NAssert.AreNotEqual(ptr1.Handle, ptr2.Handle);
            NAssert.AreEqual(2, heap.NumEntries);

            NAssert.AreEqual(10, heap.GetEntry<TestHeapObject>(ptr1.Handle.Value).Value);
            NAssert.AreEqual(20, heap.GetEntry<TestHeapObject>(ptr2.Handle.Value).Value);

            heap.Dispose();
        }

        [Test]
        public void UniquePtr_DisposeOne_OtherStillValid()
        {
            var heap = new UniqueHeap(TrecsLog.Default, null);
            var ptr1 = heap.AllocUnique(new TestHeapObject { Value = 10 });
            var ptr2 = heap.AllocUnique(new TestHeapObject { Value = 20 });

            heap.DisposeEntry<TestHeapObject>(ptr1.Handle.Value);

            NAssert.AreEqual(1, heap.NumEntries);
            NAssert.AreEqual(20, heap.GetEntry<TestHeapObject>(ptr2.Handle.Value).Value);

            heap.Dispose();
        }

        #endregion

        #region SharedPtr via World

        [Test]
        public void SharedPtr_AllocAndResolve_ViaWorld()
        {
            using var env = EcsTestHelper.CreateEnvironment(TestTemplates.SimpleAlpha);
            var heap = env.Accessor.Heap;

            var ptr = heap.AllocShared(
                BlobIdGenerator.FromKey(1),
                new TestHeapObject { Value = 55 }
            );
            NAssert.IsFalse(ptr.Handle.IsNull);

            var sharedHeap = heap.SharedHeap;
            var resolved = sharedHeap.GetBlob<TestHeapObject>(ptr.Handle);
            NAssert.AreEqual(55, resolved.Value);
        }

        [Test]
        public void SharedPtr_DisposeHandle_FreesBlob()
        {
            using var env = EcsTestHelper.CreateEnvironment(TestTemplates.SimpleAlpha);
            var heap = env.Accessor.Heap;

            var ptr = heap.AllocShared(
                BlobIdGenerator.FromKey(1),
                new TestHeapObject { Value = 33 }
            );
            var sharedHeap = heap.SharedHeap;

            NAssert.IsTrue(sharedHeap.CanGetBlob(ptr.Handle));

            sharedHeap.DisposeHandle(ptr.Handle);

            NAssert.IsFalse(sharedHeap.CanGetBlob(ptr.Handle));
        }

        [Test]
        public void SharedPtr_MultipleAllocs_Independent()
        {
            using var env = EcsTestHelper.CreateEnvironment(TestTemplates.SimpleAlpha);
            var heap = env.Accessor.Heap;

            var ptr1 = heap.AllocShared(
                BlobIdGenerator.FromKey(1),
                new TestHeapObject { Value = 10 }
            );
            var ptr2 = heap.AllocShared(
                BlobIdGenerator.FromKey(2),
                new TestHeapObject { Value = 20 }
            );

            NAssert.AreNotEqual(ptr1.Handle, ptr2.Handle);

            var sharedHeap = heap.SharedHeap;
            NAssert.AreEqual(10, sharedHeap.GetBlob<TestHeapObject>(ptr1.Handle).Value);
            NAssert.AreEqual(20, sharedHeap.GetBlob<TestHeapObject>(ptr2.Handle).Value);

            // Dispose first, second still valid
            sharedHeap.DisposeHandle(ptr1.Handle);
            NAssert.IsTrue(sharedHeap.CanGetBlob(ptr2.Handle));
            NAssert.IsFalse(sharedHeap.CanGetBlob(ptr1.Handle));

            sharedHeap.DisposeHandle(ptr2.Handle);
        }

        #endregion

        #region NativeSharedPtr

        [Test]
        public void NativeSharedPtr_Alloc_HasValidBlobId()
        {
            using var env = EcsTestHelper.CreateEnvironment(TestTemplates.SimpleAlpha);
            var heap = env.Accessor.Heap;

            var data = 42;
            var ptr = heap.AllocNativeShared(BlobIdGenerator.FromKey(1), in data);

            NAssert.IsFalse(ptr.BlobId.IsNull, "NativeSharedPtr should have a valid BlobId");
        }

        [Test]
        public void NativeSharedPtr_ExplicitBlobId_AllocsToSameId()
        {
            using var env = EcsTestHelper.CreateEnvironment(TestTemplates.SimpleAlpha);
            var heap = env.Accessor.Heap;

            var blobId = new BlobId(99999);
            var data = 123;
            var ptr = heap.AllocNativeShared(blobId, in data);

            NAssert.AreEqual(blobId, ptr.BlobId);
        }

        #endregion

        #region PtrHandle Value Type

        [Test]
        public void PtrHandle_Default_IsNull()
        {
            NAssert.IsTrue(default(PtrHandle).IsNull);
        }

        [Test]
        public void PtrHandle_NonDefault_IsNotNull()
        {
            NAssert.IsFalse(new PtrHandle(1).IsNull);
        }

        [Test]
        public void PtrHandle_SameValue_AreEqual()
        {
            var a = new PtrHandle(42);
            var b = new PtrHandle(42);
            NAssert.AreEqual(a, b);
            NAssert.IsTrue(a == b);
        }

        [Test]
        public void PtrHandle_DifferentValues_NotEqual()
        {
            var a = new PtrHandle(1);
            var b = new PtrHandle(2);
            NAssert.AreNotEqual(a, b);
            NAssert.IsTrue(a != b);
        }

        #endregion
    }
}
