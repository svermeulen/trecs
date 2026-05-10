using NUnit.Framework;
using NAssert = NUnit.Framework.Assert;
using Trecs.Internal;

namespace Trecs.Tests
{
    [TestFixture]
    public class EntityObserverTests
    {
        #region OnAdded

        [Test]
        public void Observer_OnAdded_FiresOnSubmit()
        {
            using var env = EcsTestHelper.CreateEnvironment(TestTemplates.SimpleAlpha);
            var a = env.Accessor;

            int callCount = 0;
            var sub = a
                .Events.EntitiesWithTags(TestTags.Alpha)
                .OnAdded(
                    (GroupIndex group, EntityRange indices) =>
                    {
                        callCount++;
                    }
                );

            a.AddEntity(TestTags.Alpha).AssertComplete();
            a.SubmitEntities();

            NAssert.Greater(callCount, 0);
            sub.Dispose();
        }

        [Test]
        public void Observer_OnAdded_CorrectCount()
        {
            using var env = EcsTestHelper.CreateEnvironment(TestTemplates.SimpleAlpha);
            var a = env.Accessor;

            int totalAdded = 0;
            var sub = a
                .Events.EntitiesWithTags(TestTags.Alpha)
                .OnAdded(
                    (GroupIndex group, EntityRange indices) =>
                    {
                        totalAdded += indices.End - indices.Start;
                    }
                );

            for (int i = 0; i < 3; i++)
            {
                a.AddEntity(TestTags.Alpha).AssertComplete();
            }
            a.SubmitEntities();

            NAssert.AreEqual(3u, totalAdded);
            sub.Dispose();
        }

        [Test]
        public void Observer_OnAdded_OnlyMatchingGroups()
        {
            using var env = EcsTestHelper.CreateEnvironment(
                TestTemplates.SimpleAlpha,
                TestTemplates.TwoCompBeta
            );
            var a = env.Accessor;

            int alphaCallCount = 0;
            var sub = a
                .Events.EntitiesWithTags(TestTags.Alpha)
                .OnAdded(
                    (GroupIndex group, EntityRange indices) =>
                    {
                        alphaCallCount++;
                    }
                );

            // Add to Beta, not Alpha
            a.AddEntity(TestTags.Beta).Set(new TestFloat { Value = 1.0f }).AssertComplete();
            a.SubmitEntities();

            NAssert.AreEqual(0, alphaCallCount);
            sub.Dispose();
        }

        #endregion

        #region OnRemoved

        [Test]
        public void Observer_OnRemoved_FiresOnSubmit()
        {
            using var env = EcsTestHelper.CreateEnvironment(TestTemplates.SimpleAlpha);
            var a = env.Accessor;

            a.AddEntity(TestTags.Alpha).AssertComplete();
            a.SubmitEntities();

            int callCount = 0;
            var sub = a
                .Events.EntitiesWithTags(TestTags.Alpha)
                .OnRemoved(
                    (GroupIndex group, EntityRange indices) =>
                    {
                        callCount++;
                    }
                );

            var group = a.WorldInfo.GetSingleGroupWithTags(TestTags.Alpha);
            a.RemoveEntity(new EntityIndex(0, group));
            a.SubmitEntities();

            NAssert.Greater(callCount, 0);
            sub.Dispose();
        }

        [Test]
        public void Observer_OnRemoved_CorrectCount()
        {
            using var env = EcsTestHelper.CreateEnvironment(TestTemplates.SimpleAlpha);
            var a = env.Accessor;

            for (int i = 0; i < 3; i++)
            {
                a.AddEntity(TestTags.Alpha).AssertComplete();
            }
            a.SubmitEntities();

            int totalRemoved = 0;
            var sub = a
                .Events.EntitiesWithTags(TestTags.Alpha)
                .OnRemoved(
                    (GroupIndex group, EntityRange indices) =>
                    {
                        totalRemoved += indices.End - indices.Start;
                    }
                );

            a.RemoveEntitiesWithTags(TestTags.Alpha);
            a.SubmitEntities();

            NAssert.AreEqual(3u, totalRemoved);
            sub.Dispose();
        }

        #endregion

        #region OnMoved

        [Test]
        public void Observer_OnMoved_Fires()
        {
            using var env = EcsTestHelper.CreateEnvironment(TestTemplates.WithPartitions);
            var a = env.Accessor;

            var partitionA = TagSet.FromTags(TestTags.Gamma, TestTags.PartitionA);
            var partitionB = TagSet.FromTags(TestTags.Gamma, TestTags.PartitionB);

            a.AddEntity(partitionA).AssertComplete();
            a.SubmitEntities();

            int callCount = 0;
            var sub = a
                .Events.EntitiesWithTags(partitionB)
                .OnMoved(
                    (GroupIndex fromGroup, GroupIndex toGroup, EntityRange indices) =>
                    {
                        callCount++;
                    }
                );

            var groupA = a.WorldInfo.GetSingleGroupWithTags(partitionA);
            a.MoveTo(new EntityIndex(0, groupA), partitionB);
            a.SubmitEntities();

            NAssert.Greater(callCount, 0);
            sub.Dispose();
        }

        [Test]
        public void Observer_OnMoved_CorrectGroups()
        {
            using var env = EcsTestHelper.CreateEnvironment(TestTemplates.WithPartitions);
            var a = env.Accessor;

            var partitionA = TagSet.FromTags(TestTags.Gamma, TestTags.PartitionA);
            var partitionB = TagSet.FromTags(TestTags.Gamma, TestTags.PartitionB);

            a.AddEntity(partitionA).AssertComplete();
            a.SubmitEntities();

            var expectedGroupA = a.WorldInfo.GetSingleGroupWithTags(partitionA);
            var expectedGroupB = a.WorldInfo.GetSingleGroupWithTags(partitionB);

            GroupIndex observedFrom = default;
            GroupIndex observedTo = default;

            var sub = a
                .Events.EntitiesWithTags(partitionB)
                .OnMoved(
                    (GroupIndex fromGroup, GroupIndex toGroup, EntityRange indices) =>
                    {
                        observedFrom = fromGroup;
                        observedTo = toGroup;
                    }
                );

            a.MoveTo(new EntityIndex(0, expectedGroupA), partitionB);
            a.SubmitEntities();

            NAssert.AreEqual(expectedGroupA, observedFrom);
            NAssert.AreEqual(expectedGroupB, observedTo);
            sub.Dispose();
        }

        #endregion

        #region Data Access In Callbacks

        [Test]
        public void Observer_OnAdded_CanReadComponentData()
        {
            using var env = EcsTestHelper.CreateEnvironment(TestTemplates.SimpleAlpha);
            var a = env.Accessor;

            int observedValue = -1;
            var sub = a
                .Events.EntitiesWithTags(TestTags.Alpha)
                .OnAdded(
                    (GroupIndex group, EntityRange indices) =>
                    {
                        for (int i = indices.Start; i < indices.End; i++)
                        {
                            observedValue = a.Component<TestInt>(
                                new EntityIndex(i, group)
                            ).Read.Value;
                        }
                    }
                );

            a.AddEntity(TestTags.Alpha).Set(new TestInt { Value = 42 }).AssertComplete();
            a.SubmitEntities();

            NAssert.AreEqual(
                42,
                observedValue,
                "OnAdded callback should be able to read component data of newly added entity"
            );
            sub.Dispose();
        }

        [Test]
        public void Observer_OnMoved_CanReadComponentDataInNewGroup()
        {
            using var env = EcsTestHelper.CreateEnvironment(TestTemplates.WithPartitions);
            var a = env.Accessor;

            var partitionA = TagSet.FromTags(TestTags.Gamma, TestTags.PartitionA);
            var partitionB = TagSet.FromTags(TestTags.Gamma, TestTags.PartitionB);

            a.AddEntity(partitionA).Set(new TestInt { Value = 88 }).AssertComplete();
            a.SubmitEntities();

            int observedValue = -1;
            var sub = a
                .Events.EntitiesWithTags(partitionB)
                .OnMoved(
                    (GroupIndex fromGroup, GroupIndex toGroup, EntityRange indices) =>
                    {
                        for (int i = indices.Start; i < indices.End; i++)
                        {
                            observedValue = a.Component<TestInt>(
                                new EntityIndex(i, toGroup)
                            ).Read.Value;
                        }
                    }
                );

            var groupA = a.WorldInfo.GetSingleGroupWithTags(partitionA);
            a.MoveTo(new EntityIndex(0, groupA), partitionB);
            a.SubmitEntities();

            NAssert.AreEqual(
                88,
                observedValue,
                "OnMoved callback should be able to read component data in the destination group"
            );
            sub.Dispose();
        }

        #endregion

        #region Lifecycle

        [Test]
        public void Observer_Dispose_StopsCallbacks()
        {
            using var env = EcsTestHelper.CreateEnvironment(TestTemplates.SimpleAlpha);
            var a = env.Accessor;

            int callCount = 0;
            var sub = a
                .Events.EntitiesWithTags(TestTags.Alpha)
                .OnAdded(
                    (GroupIndex group, EntityRange indices) =>
                    {
                        callCount++;
                    }
                );

            a.AddEntity(TestTags.Alpha).AssertComplete();
            a.SubmitEntities();

            NAssert.AreEqual(1, callCount);

            // Dispose the subscription
            sub.Dispose();

            // Add another entity, should not fire
            a.AddEntity(TestTags.Alpha).AssertComplete();
            a.SubmitEntities();

            NAssert.AreEqual(1, callCount);
        }

        #endregion
    }
}
