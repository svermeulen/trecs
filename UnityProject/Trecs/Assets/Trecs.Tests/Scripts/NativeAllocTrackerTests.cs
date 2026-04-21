using NUnit.Framework;
using Trecs.Internal;
using NAssert = NUnit.Framework.Assert;

namespace Trecs.Tests
{
    [TestFixture]
    public class NativeAllocTrackerTests
    {
        // NativeAllocTracker is a process-global counter. We snapshot before/after
        // rather than assert against zero so unrelated outstanding allocations in
        // the test runner don't make these tests brittle.

        [Test]
        public void Alloc_Dispose_ReturnsToStartingCount()
        {
            int start = NativeAllocTracker.OutstandingCount;

            var box = NativeBlobBox.AllocFromValue<int>(42);
            NAssert.AreEqual(start + 1, NativeAllocTracker.OutstandingCount);

            box.Dispose();
            NAssert.AreEqual(start, NativeAllocTracker.OutstandingCount);
        }

        [Test]
        public void MultipleAllocs_EachIncrementsCounter()
        {
            int start = NativeAllocTracker.OutstandingCount;

            var a = NativeBlobBox.AllocFromValue<int>(1);
            var b = NativeBlobBox.AllocFromValue<long>(2L);
            var c = NativeBlobBox.AllocUninitialized(
                size: 16,
                alignment: 8,
                innerType: typeof(float)
            );

            NAssert.AreEqual(start + 3, NativeAllocTracker.OutstandingCount);

            a.Dispose();
            b.Dispose();
            c.Dispose();

            NAssert.AreEqual(start, NativeAllocTracker.OutstandingCount);
        }

        [Test]
        public void World_DisposeWithEmptyHeaps_DoesNotTripLeakAssertion()
        {
            // Regression guard on the World.Dispose leak assertion: with no
            // allocations in flight, teardown must not assert.
            var env = EcsTestHelper.CreateEnvironment(TestTemplates.SimpleAlpha);
            var a = env.Accessor;

            a.AddEntity(TestTags.Alpha).Set(new TestInt { Value = 1 }).AssertComplete();
            a.SubmitEntities();

            // Dispose should succeed without throwing from the NativeAllocTracker
            // assertion we added to World.Dispose.
            env.Dispose();
        }
    }
}
