using NUnit.Framework;
using Trecs.Internal;
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
            var world = env.Accessor;

            var ptr = BlobTestUtil.AllocShared(
                world,
                new BlobId(1),
                new TestHeapObject { Value = 55 }
            );
            NAssert.IsFalse(ptr.IsNull);

            var resolved = ptr.Get(world);
            NAssert.AreEqual(55, resolved.Value);
        }

        [Test]
        public void SharedPtr_DisposeHandle_FreesBlob()
        {
            using var env = EcsTestHelper.CreateEnvironment(TestTemplates.SimpleAlpha);
            var world = env.Accessor;

            var ptr = BlobTestUtil.AllocShared(
                world,
                new BlobId(1),
                new TestHeapObject { Value = 33 }
            );
            NAssert.IsTrue(ptr.CanGet(world));

            ptr.Dispose(world);

            NAssert.IsFalse(ptr.CanGet(world));
        }

        [Test]
        public void SharedPtr_MultipleAllocs_Independent()
        {
            using var env = EcsTestHelper.CreateEnvironment(TestTemplates.SimpleAlpha);
            var world = env.Accessor;

            var ptr1 = BlobTestUtil.AllocShared(
                world,
                new BlobId(1),
                new TestHeapObject { Value = 10 }
            );
            var ptr2 = BlobTestUtil.AllocShared(
                world,
                new BlobId(2),
                new TestHeapObject { Value = 20 }
            );

            NAssert.AreNotEqual(ptr1.Id, ptr2.Id);

            NAssert.AreEqual(10, ptr1.Get(world).Value);
            NAssert.AreEqual(20, ptr2.Get(world).Value);

            // Dispose first, second still valid
            ptr1.Dispose(world);
            NAssert.IsTrue(ptr2.CanGet(world));
            NAssert.IsFalse(ptr1.CanGet(world));

            ptr2.Dispose(world);
        }

        #endregion

        #region NativeSharedPtr

        [Test]
        public void NativeSharedPtr_Alloc_HasValidBlobId()
        {
            using var env = EcsTestHelper.CreateEnvironment(TestTemplates.SimpleAlpha);
            var world = env.Accessor;

            var data = 42;
            var ptr = BlobTestUtil.AllocNativeShared(world, new BlobId(1), in data);

            NAssert.IsFalse(
                ptr.GetBlobId(world).IsNull,
                "NativeSharedPtr should have a valid BlobId"
            );
        }

        [Test]
        public void NativeSharedPtr_ExplicitBlobId_AllocsToSameId()
        {
            using var env = EcsTestHelper.CreateEnvironment(TestTemplates.SimpleAlpha);
            var world = env.Accessor;

            var blobId = new BlobId(99999);
            var data = 123;
            var ptr = BlobTestUtil.AllocNativeShared(world, blobId, in data);

            NAssert.AreEqual(blobId, ptr.GetBlobId(world));
        }

        #endregion

        #region Content-addressed Alloc (no caller-supplied id)

        [Test]
        public void NativeSharedPtr_AllocContentAddressed_EqualValuesDedup()
        {
            using var env = EcsTestHelper.CreateEnvironment(TestTemplates.SimpleAlpha);
            var world = env.Accessor;

            var a = NativeSharedPtr.Alloc<int>(world, 42);
            var b = NativeSharedPtr.Alloc<int>(world, 42);
            var c = NativeSharedPtr.Alloc<int>(world, 99);

            // Equal content -> same content-addressed id (dedup); different content -> different id.
            NAssert.AreEqual(a.GetBlobId(world), b.GetBlobId(world));
            NAssert.AreNotEqual(a.GetBlobId(world), c.GetBlobId(world));

            a.Dispose(world);
            b.Dispose(world);
            c.Dispose(world);
        }

        [Test]
        public void NativeSharedAnchor_AllocContentAddressed_EqualValuesDedup()
        {
            using var env = EcsTestHelper.CreateEnvironment(TestTemplates.SimpleAlpha);
            var world = env.Accessor;

            var a = NativeSharedAnchor.Alloc<int>(world, 7);
            var b = NativeSharedAnchor.Alloc<int>(world, 7);

            NAssert.AreEqual(a.BlobId, b.BlobId);

            a.Dispose(world);
            b.Dispose(world);
        }

        [Test]
        public void BlobIdGenerator_FromContent_IsDeterministicAndContentKeyed()
        {
            using var env = EcsTestHelper.CreateEnvironment(TestTemplates.SimpleAlpha);
            var world = env.Accessor;

            var a = BlobIdGenerator.FromContent(world, 42);
            var b = BlobIdGenerator.FromContent(world, 42);
            var c = BlobIdGenerator.FromContent(world, 99);

            // Equal values hash to the same content id; different values to different ids.
            NAssert.AreEqual(a, b, "equal values hash to the same content id");
            NAssert.AreNotEqual(a, c, "different values hash to different ids");
            NAssert.IsFalse(a.IsNull, "a content id is never the null blob id");
        }

        [Test]
        public void SharedPtr_AllocContentAddressed_EqualValuesDedup()
        {
            using var env = EcsTestHelper.CreateEnvironment(TestTemplates.SimpleAlpha);
            var world = env.Accessor;

            // string is registered for serialization and on the immutable allowlist, so it exercises
            // the managed serialize+hash content-addressing path end to end.
            var a = SharedPtr.Alloc<string>(world, "hello");
            var b = SharedPtr.Alloc<string>(world, "hello");
            var c = SharedPtr.Alloc<string>(world, "world");

            NAssert.AreEqual(a.Id, b.Id);
            NAssert.AreNotEqual(a.Id, c.Id);
            NAssert.AreEqual("hello", a.Get(world));

            a.Dispose(world);
            b.Dispose(world);
            c.Dispose(world);
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
