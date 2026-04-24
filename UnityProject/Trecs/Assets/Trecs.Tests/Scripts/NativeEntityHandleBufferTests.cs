using System.Reflection;
using NUnit.Framework;
using Trecs.Internal;
using Unity.Collections.LowLevel.Unsafe;
using NAssert = NUnit.Framework.Assert;

namespace Trecs.Tests
{
    [TestFixture]
    public class NativeEntityHandleBufferTests
    {
        #region Basic Indexing

        [Test]
        public void Buffer_Length_MatchesEntityCount()
        {
            using var env = EcsTestHelper.CreateEnvironment(TestTemplates.SimpleAlpha);
            var a = env.Accessor;

            a.AddEntity(TestTags.Alpha).AssertComplete();
            a.AddEntity(TestTags.Alpha).AssertComplete();
            a.AddEntity(TestTags.Alpha).AssertComplete();
            a.SubmitEntities();

            var group = a.WorldInfo.GetSingleGroupWithTags(TestTags.Alpha);
            var buffer = a.GetEntityHandleBufferForJob(group);

            NAssert.AreEqual(3, buffer.Length);
        }

        [Test]
        public void Buffer_Indexing_RoundTripsViaGetEntityHandle()
        {
            using var env = EcsTestHelper.CreateEnvironment(TestTemplates.SimpleAlpha);
            var a = env.Accessor;

            a.AddEntity(TestTags.Alpha).AssertComplete();
            a.AddEntity(TestTags.Alpha).AssertComplete();
            a.AddEntity(TestTags.Alpha).AssertComplete();
            a.SubmitEntities();

            var group = a.WorldInfo.GetSingleGroupWithTags(TestTags.Alpha);
            var buffer = a.GetEntityHandleBufferForJob(group);

            // Buffer iteration order is group-storage order. For each slot,
            // the handle must match what GetEntityHandle(EntityIndex) reports.
            // This exercises the two-load reconstruction path: reverse map -> uniqueId,
            // then forward map -> Version.
            for (int i = 0; i < buffer.Length; i++)
            {
                var expected = a.GetEntityHandle(new EntityIndex(i, group));
                NAssert.AreEqual(
                    expected,
                    buffer[i],
                    "Buffer handle at index {0} should round-trip via GetEntityHandle",
                    i
                );
            }
        }

        #endregion

        #region Version Reconstruction

        [Test]
        public void Buffer_AfterSlotReuse_ReturnsNewVersion()
        {
            using var env = EcsTestHelper.CreateEnvironment(TestTemplates.SimpleAlpha);
            var a = env.Accessor;

            var init1 = a.AddEntity(TestTags.Alpha).AssertComplete();
            var originalHandle = init1.Handle;
            a.SubmitEntities();

            // Destroy the entity — bumps the forward-map slot's Version on removal.
            a.RemoveEntity(originalHandle);
            a.SubmitEntities();

            // Create a new entity; the submitter should reuse the freed slot.
            var init2 = a.AddEntity(TestTags.Alpha).AssertComplete();
            var newHandle = init2.Handle;
            a.SubmitEntities();

            NAssert.AreEqual(
                originalHandle.UniqueId,
                newHandle.UniqueId,
                "Freed forward-map slot should be reused"
            );
            NAssert.AreNotEqual(
                originalHandle.Version,
                newHandle.Version,
                "Reused slot must carry a bumped Version"
            );

            // The buffer must return the NEW handle (new Version), not the stale one.
            // If the two-load path ever reads a stale Version, this fails.
            var group = a.WorldInfo.GetSingleGroupWithTags(TestTags.Alpha);
            var buffer = a.GetEntityHandleBufferForJob(group);

            NAssert.AreEqual(1, buffer.Length);
            NAssert.AreEqual(newHandle, buffer[0]);
            NAssert.AreNotEqual(
                originalHandle,
                buffer[0],
                "Buffer must not return the stale pre-reuse handle"
            );
        }

        #endregion

        #region Swap-back

        [Test]
        public void Buffer_AfterSwapBack_ContainsRemainingHandles()
        {
            using var env = EcsTestHelper.CreateEnvironment(TestTemplates.SimpleAlpha);
            var a = env.Accessor;

            var init1 = a.AddEntity(TestTags.Alpha).Set(new TestInt { Value = 1 }).AssertComplete();
            var init2 = a.AddEntity(TestTags.Alpha).Set(new TestInt { Value = 2 }).AssertComplete();
            var init3 = a.AddEntity(TestTags.Alpha).Set(new TestInt { Value = 3 }).AssertComplete();
            a.SubmitEntities();

            var handle1 = init1.Handle;
            var handle3 = init3.Handle;

            // Removing the middle entity triggers swap-back of #3 into index 1.
            // The reverse map's int at index 1 gets rewritten with #3's UniqueId.
            a.RemoveEntity(init2.Handle);
            a.SubmitEntities();

            var group = a.WorldInfo.GetSingleGroupWithTags(TestTags.Alpha);
            var buffer = a.GetEntityHandleBufferForJob(group);

            // The reverse-map list is grow-only, so buffer.Length is the high-water
            // mark (3) and the trailing cleared slot returns a null handle. The
            // contract we care about: both survivors are present, and every index
            // in [0, Length) is safe to read.
            NAssert.GreaterOrEqual(buffer.Length, 2);

            bool has1 = false;
            bool has3 = false;
            int liveCount = 0;
            for (int i = 0; i < buffer.Length; i++)
            {
                var h = buffer[i];
                if (h.IsNull)
                {
                    continue;
                }
                liveCount++;
                if (h == handle1)
                {
                    has1 = true;
                }
                if (h == handle3)
                {
                    has3 = true;
                }
            }
            NAssert.AreEqual(2, liveCount, "Buffer should expose exactly two live handles");
            NAssert.IsTrue(has1, "Buffer should still contain handle1 after swap-back");
            NAssert.IsTrue(has3, "Buffer should still contain handle3 after swap-back");
        }

        #endregion

        #region Layout Regression Guards

        // These assertions fail loudly if the reverse-map element type or the
        // forward-map element size is changed without intent. When the follow-up
        // Change A (packing EntityHandleMapElement to 8 bytes) lands, update the
        // sizeof assertion deliberately.

        [Test]
        public void ReverseMap_ElementType_IsInt()
        {
            var field = typeof(EntityHandleMap).GetField(
                "_entityIndexToReferenceMap",
                BindingFlags.Instance | BindingFlags.NonPublic
            );
            NAssert.IsNotNull(field, "Expected internal field _entityIndexToReferenceMap");

            // The field is NativeDenseDictionary<Group, NativeList<int>>. The inner
            // NativeList's element type must be int, not EntityHandle.
            StringAssert.Contains(
                "NativeList`1[System.Int32]",
                field.FieldType.ToString(),
                "Reverse map must store int per entity, not EntityHandle"
            );
        }

        [Test]
        public void EntityHandleMapElement_SizeIs12Bytes()
        {
            NAssert.AreEqual(
                12,
                UnsafeUtility.SizeOf<EntityHandleMapElement>(),
                "EntityHandleMapElement is expected to be 12 bytes. If this changes, "
                    + "update this assertion deliberately — e.g., the planned Change A "
                    + "packs it to 8 bytes."
            );
        }

        #endregion
    }
}
