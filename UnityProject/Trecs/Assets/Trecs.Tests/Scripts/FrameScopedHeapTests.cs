using NUnit.Framework;
using NAssert = NUnit.Framework.Assert;

namespace Trecs.Tests
{
    [TestFixture]
    public class FrameScopedHeapTests
    {
        #region FrameScopedSharedHeap via World

        [Test]
        public void FrameScopedShared_Alloc_IncreasesCount()
        {
            using var env = EcsTestHelper.CreateEnvironment(TestTemplates.SimpleAlpha);
            var heap = env.Accessor.Heap.FrameScopedSharedHeap;

            heap.CreateBlob(frame: 0, new BlobId(10), new TestHeapObject { Value = 10 });

            NAssert.AreEqual(1, heap.NumEntries);
        }

        [Test]
        public void FrameScopedShared_AllocMultipleFrames_AllPresent()
        {
            using var env = EcsTestHelper.CreateEnvironment(TestTemplates.SimpleAlpha);
            var heap = env.Accessor.Heap.FrameScopedSharedHeap;

            heap.CreateBlob(frame: 0, new BlobId(10), new TestHeapObject { Value = 10 });
            heap.CreateBlob(frame: 1, new BlobId(20), new TestHeapObject { Value = 20 });
            heap.CreateBlob(frame: 2, new BlobId(30), new TestHeapObject { Value = 30 });

            NAssert.AreEqual(3, heap.NumEntries);
        }

        [Test]
        public void FrameScopedShared_ClearAtOrBeforeFrame_RemovesOldEntries()
        {
            using var env = EcsTestHelper.CreateEnvironment(TestTemplates.SimpleAlpha);
            var heap = env.Accessor.Heap.FrameScopedSharedHeap;

            heap.CreateBlob(frame: 0, new BlobId(10), new TestHeapObject { Value = 10 });
            heap.CreateBlob(frame: 1, new BlobId(20), new TestHeapObject { Value = 20 });
            heap.CreateBlob(frame: 2, new BlobId(30), new TestHeapObject { Value = 30 });
            heap.CreateBlob(frame: 3, new BlobId(40), new TestHeapObject { Value = 40 });

            NAssert.AreEqual(4, heap.NumEntries);

            heap.ClearAtOrBeforeFrame(1);

            NAssert.AreEqual(
                2,
                heap.NumEntries,
                "Should have 2 entries after clearing frames <= 1"
            );
        }

        [Test]
        public void FrameScopedShared_ClearAtOrAfterFrame_RemovesNewEntries()
        {
            using var env = EcsTestHelper.CreateEnvironment(TestTemplates.SimpleAlpha);
            var heap = env.Accessor.Heap.FrameScopedSharedHeap;

            heap.CreateBlob(frame: 0, new BlobId(10), new TestHeapObject { Value = 10 });
            heap.CreateBlob(frame: 1, new BlobId(20), new TestHeapObject { Value = 20 });
            heap.CreateBlob(frame: 2, new BlobId(30), new TestHeapObject { Value = 30 });
            heap.CreateBlob(frame: 3, new BlobId(40), new TestHeapObject { Value = 40 });

            NAssert.AreEqual(4, heap.NumEntries);

            heap.ClearAtOrAfterFrame(2);

            NAssert.AreEqual(
                2,
                heap.NumEntries,
                "Should have 2 entries after clearing frames >= 2"
            );
        }

        [Test]
        public void FrameScopedShared_ClearAll_RemovesEverything()
        {
            using var env = EcsTestHelper.CreateEnvironment(TestTemplates.SimpleAlpha);
            var heap = env.Accessor.Heap.FrameScopedSharedHeap;

            heap.CreateBlob(frame: 0, new BlobId(10), new TestHeapObject { Value = 10 });
            heap.CreateBlob(frame: 1, new BlobId(20), new TestHeapObject { Value = 20 });

            heap.ClearAll();

            NAssert.AreEqual(0, heap.NumEntries);
        }

        [Test]
        public void FrameScopedShared_ClearBeyondRange_NoEffect()
        {
            using var env = EcsTestHelper.CreateEnvironment(TestTemplates.SimpleAlpha);
            var heap = env.Accessor.Heap.FrameScopedSharedHeap;

            heap.CreateBlob(frame: 5, new BlobId(10), new TestHeapObject { Value = 10 });
            heap.CreateBlob(frame: 6, new BlobId(20), new TestHeapObject { Value = 20 });

            // Clear frames <= 3 (before any allocation)
            heap.ClearAtOrBeforeFrame(3);

            NAssert.AreEqual(2, heap.NumEntries, "No entries should be cleared");
        }

        [Test]
        public void FrameScopedShared_MultipleAllocsInSameFrame_AllPresent()
        {
            using var env = EcsTestHelper.CreateEnvironment(TestTemplates.SimpleAlpha);
            var heap = env.Accessor.Heap.FrameScopedSharedHeap;

            heap.CreateBlob(frame: 5, new BlobId(1), new TestHeapObject { Value = 1 });
            heap.CreateBlob(frame: 5, new BlobId(2), new TestHeapObject { Value = 2 });
            heap.CreateBlob(frame: 5, new BlobId(3), new TestHeapObject { Value = 3 });

            NAssert.AreEqual(3, heap.NumEntries);

            heap.ClearAtOrAfterFrame(5);

            NAssert.AreEqual(0, heap.NumEntries, "All entries in frame 5 should be cleared");
        }

        [Test]
        public void FrameScopedShared_Resolve_ReturnsCorrectValue()
        {
            using var env = EcsTestHelper.CreateEnvironment(TestTemplates.SimpleAlpha);
            var heap = env.Accessor.Heap.FrameScopedSharedHeap;

            var ptr = heap.CreateBlob(frame: 0, new BlobId(42), new TestHeapObject { Value = 42 });

            var resolved = heap.ResolveValue<TestHeapObject>(0, ptr.Handle.Value);
            NAssert.AreEqual(42, resolved.Value);
        }

        #endregion
    }
}
