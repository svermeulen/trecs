using NUnit.Framework;
using Trecs.Internal;
using Unity.Collections;
using Unity.Collections.LowLevel.Unsafe;
using NAssert = NUnit.Framework.Assert;

namespace Trecs.Tests
{
    [TestFixture]
    public class NativeComponentLookupTests
    {
        #region Read Lookup — Single GroupIndex

        [Test]
        public void ReadLookup_SingleGroup_ReadsCorrectValues()
        {
            using var env = EcsTestHelper.CreateEnvironment(TestTemplates.SimpleAlpha);
            var a = env.Accessor;

            for (int i = 0; i < 3; i++)
            {
                a.AddEntity(TestTags.Alpha)
                    .Set(new TestInt { Value = (i + 1) * 10 })
                    .AssertComplete();
            }
            a.Submit();

            using var lookup = a.CreateNativeComponentLookupRead<TestInt>(
                TestTags.Alpha,
                Allocator.TempJob
            );

            NAssert.IsTrue(lookup.IsCreated);

            var group = a.WorldInfo.GetSingleGroupWithTags(TestTags.Alpha);
            NAssert.AreEqual(10, lookup[new EntityIndex(0, group)].Value);
            NAssert.AreEqual(20, lookup[new EntityIndex(1, group)].Value);
            NAssert.AreEqual(30, lookup[new EntityIndex(2, group)].Value);
        }

        #endregion

        #region Job Lookup — Shared Entries

        [Test]
        public void JobLookup_RebuildsAfterStructuralChange_SkipsEmptyGroups()
        {
            using var env = EcsTestHelper.CreateEnvironment(TestTemplates.SimpleAlpha);
            var a = env.Accessor;
            var groups = a.WorldInfo.GetGroupsWithTagsAndComponents<TestInt>(TestTags.Alpha);
            var group = a.WorldInfo.GetSingleGroupWithTags(TestTags.Alpha);

            NAssert.AreEqual(0, a.GetLookupEntriesForJob<TestInt>(groups).Groups.Count);

            a.AddEntity(TestTags.Alpha).Set(new TestInt { Value = 10 }).AssertComplete();
            a.Submit();

            NAssert.AreEqual(1, a.GetLookupEntriesForJob<TestInt>(groups).Groups.Count);
            using (var lookup = a.CreateNativeComponentLookupReadForJob<TestInt>(groups))
            {
                NAssert.AreEqual(10, lookup[new EntityIndex(0, group)].Value);
            }

            // Enough to move the buffer.
            for (int i = 0; i < 1000; i++)
            {
                a.AddEntity(TestTags.Alpha).Set(new TestInt { Value = i }).AssertComplete();
            }
            a.Submit();

            using var moved = a.CreateNativeComponentLookupReadForJob<TestInt>(groups);
            NAssert.AreEqual(10, moved[new EntityIndex(0, group)].Value);
            NAssert.AreEqual(999, moved[new EntityIndex(1000, group)].Value);
        }

        #endregion

        #region Dispose

        [Test]
        public void EmptyLookup_ReleasesSafetyHandleOnDispose()
        {
            using var env = EcsTestHelper.CreateEnvironment(TestTemplates.SimpleAlpha);
            var lookup = env.Accessor.CreateNativeComponentLookupRead<TestInt>(
                TestTags.Alpha,
                Allocator.TempJob
            );
            NAssert.IsFalse(lookup.IsCreated, "no entities gives no entries");

            lookup.Dispose();

            NAssert.Throws<System.ObjectDisposedException>(() =>
                AtomicSafetyHandle.CheckExistsAndThrow(lookup.SafetyHandle)
            );
        }

        #endregion

        #region Read Lookup — Multiple Groups

        [Test]
        public void ReadLookup_MultipleGroups_ReadsFromBoth()
        {
            using var env = EcsTestHelper.CreateEnvironment(TestTemplates.WithPartitions);
            var a = env.Accessor;

            var partitionA = TagSet.FromTags(TestTags.Gamma, TestTags.PartitionA);
            var partitionB = TagSet.FromTags(TestTags.Gamma, TestTags.PartitionB);

            a.AddEntity(partitionA)
                .Set(new TestInt { Value = 100 })
                .Set(new TestVec())
                .AssertComplete();
            a.AddEntity(partitionB)
                .Set(new TestInt { Value = 200 })
                .Set(new TestVec())
                .AssertComplete();
            a.Submit();

            // Lookup by Gamma tag — matches both state groups
            using var lookup = a.CreateNativeComponentLookupRead<TestInt>(
                TagSet.FromTags(TestTags.Gamma),
                Allocator.TempJob
            );

            var groupA = a.WorldInfo.GetSingleGroupWithTags(partitionA);
            var groupB = a.WorldInfo.GetSingleGroupWithTags(partitionB);

            NAssert.AreEqual(100, lookup[new EntityIndex(0, groupA)].Value);
            NAssert.AreEqual(200, lookup[new EntityIndex(0, groupB)].Value);
        }

        #endregion

        #region Write Lookup

        [Test]
        public void WriteLookup_ModifyComponent_PersistsValue()
        {
            using var env = EcsTestHelper.CreateEnvironment(TestTemplates.SimpleAlpha);
            var a = env.Accessor;

            a.AddEntity(TestTags.Alpha).Set(new TestInt { Value = 10 }).AssertComplete();
            a.Submit();

            var group = a.WorldInfo.GetSingleGroupWithTags(TestTags.Alpha);

            using var lookup = a.CreateNativeComponentLookupWrite<TestInt>(
                TestTags.Alpha,
                Allocator.TempJob
            );

            lookup[new EntityIndex(0, group)] = new TestInt { Value = 99 };

            // Verify via regular accessor
            NAssert.AreEqual(99, a.Component<TestInt>(new EntityIndex(0, group)).Read.Value);
        }

        #endregion

        #region TryGet

        [Test]
        public void ReadLookup_TryGet_ReturnsTrueForMatchingGroup()
        {
            using var env = EcsTestHelper.CreateEnvironment(TestTemplates.SimpleAlpha);
            var a = env.Accessor;

            a.AddEntity(TestTags.Alpha).Set(new TestInt { Value = 42 }).AssertComplete();
            a.Submit();

            using var lookup = a.CreateNativeComponentLookupRead<TestInt>(
                TestTags.Alpha,
                Allocator.TempJob
            );

            var group = a.WorldInfo.GetSingleGroupWithTags(TestTags.Alpha);
            bool found = lookup.TryGet(new EntityIndex(0, group), out var value);

            NAssert.IsTrue(found);
            NAssert.AreEqual(42, value.Value);
        }

        [Test]
        public void ReadLookup_TryGet_ReturnsFalseForMissingGroup()
        {
            using var env = EcsTestHelper.CreateEnvironment(
                TestTemplates.SimpleAlpha,
                TestTemplates.TwoCompBeta
            );
            var a = env.Accessor;

            a.AddEntity(TestTags.Alpha).Set(new TestInt { Value = 1 }).AssertComplete();
            a.AddEntity(TestTags.Beta)
                .Set(new TestInt { Value = 2 })
                .Set(new TestFloat())
                .AssertComplete();
            a.Submit();

            // Lookup only covers Alpha group
            using var lookup = a.CreateNativeComponentLookupRead<TestInt>(
                TestTags.Alpha,
                Allocator.TempJob
            );

            var betaGroup = a.WorldInfo.GetSingleGroupWithTags(TestTags.Beta);
            bool found = lookup.TryGet(new EntityIndex(0, betaGroup), out _);

            NAssert.IsFalse(found, "TryGet should return false for group not in lookup");
        }

        #endregion

        #region Exists

        [Test]
        public void ReadLookup_Exists_TrueForMatchingGroup()
        {
            using var env = EcsTestHelper.CreateEnvironment(TestTemplates.SimpleAlpha);
            var a = env.Accessor;

            a.AddEntity(TestTags.Alpha).Set(new TestInt { Value = 1 }).AssertComplete();
            a.Submit();

            using var lookup = a.CreateNativeComponentLookupRead<TestInt>(
                TestTags.Alpha,
                Allocator.TempJob
            );

            var group = a.WorldInfo.GetSingleGroupWithTags(TestTags.Alpha);
            NAssert.IsTrue(lookup.Exists(new EntityIndex(0, group)));
        }

        [Test]
        public void ReadLookup_Exists_FalseForMissingGroup()
        {
            using var env = EcsTestHelper.CreateEnvironment(
                TestTemplates.SimpleAlpha,
                TestTemplates.TwoCompBeta
            );
            var a = env.Accessor;

            a.AddEntity(TestTags.Alpha).Set(new TestInt { Value = 1 }).AssertComplete();
            a.AddEntity(TestTags.Beta)
                .Set(new TestInt { Value = 2 })
                .Set(new TestFloat())
                .AssertComplete();
            a.Submit();

            using var lookup = a.CreateNativeComponentLookupRead<TestInt>(
                TestTags.Alpha,
                Allocator.TempJob
            );

            var betaGroup = a.WorldInfo.GetSingleGroupWithTags(TestTags.Beta);
            NAssert.IsFalse(lookup.Exists(new EntityIndex(0, betaGroup)));
        }

        #endregion

        #region Lookup After Structural Changes

        [Test]
        public void ReadLookup_AfterEntityRemoved_StillWorksForRemaining()
        {
            using var env = EcsTestHelper.CreateEnvironment(TestTemplates.SimpleAlpha);
            var a = env.Accessor;

            var handle = a.AddEntity(TestTags.Alpha)
                .Set(new TestInt { Value = 10 })
                .AssertComplete()
                .Handle;
            a.AddEntity(TestTags.Alpha).Set(new TestInt { Value = 20 }).AssertComplete();
            a.Submit();

            // Remove first entity
            a.RemoveEntity(handle);
            a.Submit();

            using var lookup = a.CreateNativeComponentLookupRead<TestInt>(
                TestTags.Alpha,
                Allocator.TempJob
            );

            NAssert.IsTrue(lookup.IsCreated);
            var group = a.WorldInfo.GetSingleGroupWithTags(TestTags.Alpha);
            // After swap-back, entity at index 0 should have value 20
            NAssert.AreEqual(20, lookup[new EntityIndex(0, group)].Value);
        }

        #endregion
    }
}
