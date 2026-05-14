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
            // This exercises the two-load reconstruction path: reverse map -> id,
            // then forward map -> Version.
            for (int i = 0; i < buffer.Length; i++)
            {
                var expected = new EntityIndex(i, group).ToHandle(a);
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
                originalHandle.Id,
                newHandle.Id,
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
            // The reverse map's int at index 1 gets rewritten with #3's Id.
            a.RemoveEntity(init2.Handle);
            a.SubmitEntities();

            var group = a.WorldInfo.GetSingleGroupWithTags(TestTags.Alpha);
            var buffer = a.GetEntityHandleBufferForJob(group);

            NAssert.AreEqual(2, buffer.Length);

            bool has1 = false;
            bool has3 = false;
            for (int i = 0; i < buffer.Length; i++)
            {
                if (buffer[i] == handle1)
                {
                    has1 = true;
                }
                if (buffer[i] == handle3)
                {
                    has3 = true;
                }
            }
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

            // The field is NativeList<UnsafeList<int>>, indexed by GroupIndex.Index.
            // Inner is UnsafeList<int> (not NativeList) so the overall type is a single
            // NativeContainer wrapping non-NativeContainers — legal inside jobs.
            StringAssert.Contains(
                "UnsafeList`1[System.Int32]",
                field.FieldType.ToString(),
                "Reverse map must store int per entity, not EntityHandle"
            );
        }

        [Test]
        public void EntityHandleMapElement_SizeIs8Bytes()
        {
            NAssert.AreEqual(
                8,
                UnsafeUtility.SizeOf<EntityHandleMapElement>(),
                "EntityHandleMapElement is expected to be 8 bytes "
                    + "(int Index + ushort GroupIndex + ushort Version). If this changes, "
                    + "update this assertion deliberately."
            );
        }

        #endregion
    }
}
