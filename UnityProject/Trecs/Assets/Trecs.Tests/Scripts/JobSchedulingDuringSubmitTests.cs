using NUnit.Framework;
using Trecs.Internal;
using NAssert = NUnit.Framework.Assert;

namespace Trecs.Tests
{
    [TestFixture]
    public class JobSchedulingDuringSubmitTests
    {
        [Test]
        public void TrackJob_DuringOnAddedCallback_Throws()
        {
            using var env = EcsTestHelper.CreateEnvironment(TestTemplates.SimpleAlpha);
            var a = env.Accessor;
            var scheduler = env.World.GetSystemRunner().JobScheduler;

            var sub = a
                .Events.EntitiesWithTags(TestTags.Alpha)
                .OnAdded(
                    (GroupIndex group, EntityRange indices) =>
                    {
                        scheduler.TrackJob(default);
                    }
                );

            a.AddEntity(TestTags.Alpha).AssertComplete();

            NAssert.Throws<TrecsException>(() => a.World.Submit());
            sub.Dispose();
        }

        [Test]
        public void TrackJob_DuringOnRemovedCallback_Throws()
        {
            using var env = EcsTestHelper.CreateEnvironment(TestTemplates.SimpleAlpha);
            var a = env.Accessor;
            var scheduler = env.World.GetSystemRunner().JobScheduler;

            a.AddEntity(TestTags.Alpha).AssertComplete();
            a.World.Submit();

            var sub = a
                .Events.EntitiesWithTags(TestTags.Alpha)
                .OnRemoved(
                    (GroupIndex group, EntityRange indices) =>
                    {
                        scheduler.TrackJob(default);
                    }
                );

            var group = a.WorldInfo.GetSingleGroupWithTags(TestTags.Alpha);
            a.RemoveEntity(new EntityIndex(0, group));

            NAssert.Throws<TrecsException>(() => a.World.Submit());
            sub.Dispose();
        }

        [Test]
        public void TrackJob_DuringOnMovedCallback_Throws()
        {
            using var env = EcsTestHelper.CreateEnvironment(TestTemplates.WithPartitions);
            var a = env.Accessor;
            var scheduler = env.World.GetSystemRunner().JobScheduler;

            var partitionA = TagSet.FromTags(TestTags.Gamma, TestTags.PartitionA);
            var partitionB = TagSet.FromTags(TestTags.Gamma, TestTags.PartitionB);

            a.AddEntity(partitionA).AssertComplete();
            a.World.Submit();

            var sub = a
                .Events.EntitiesWithTags(partitionB)
                .OnMoved(
                    (GroupIndex fromGroup, GroupIndex toGroup, EntityRange indices) =>
                    {
                        scheduler.TrackJob(default);
                    }
                );

            var groupA = a.WorldInfo.GetSingleGroupWithTags(partitionA);
            a.SetTag<TestPartitionB>(new EntityIndex(0, groupA));

            NAssert.Throws<TrecsException>(() => a.World.Submit());
            sub.Dispose();
        }

        [Test]
        public void TrackJobRead_DuringCallback_Throws()
        {
            using var env = EcsTestHelper.CreateEnvironment(TestTemplates.SimpleAlpha);
            var a = env.Accessor;
            var scheduler = env.World.GetSystemRunner().JobScheduler;

            var sub = a
                .Events.EntitiesWithTags(TestTags.Alpha)
                .OnAdded(
                    (GroupIndex group, EntityRange indices) =>
                    {
                        scheduler.TrackJobRead(
                            default,
                            ResourceId.Component(TypeId<TestInt>.Value),
                            group
                        );
                    }
                );

            a.AddEntity(TestTags.Alpha).AssertComplete();

            NAssert.Throws<TrecsException>(() => a.World.Submit());
            sub.Dispose();
        }

        [Test]
        public void TrackJobWrite_DuringCallback_Throws()
        {
            using var env = EcsTestHelper.CreateEnvironment(TestTemplates.SimpleAlpha);
            var a = env.Accessor;
            var scheduler = env.World.GetSystemRunner().JobScheduler;

            var sub = a
                .Events.EntitiesWithTags(TestTags.Alpha)
                .OnAdded(
                    (GroupIndex group, EntityRange indices) =>
                    {
                        scheduler.TrackJobWrite(
                            default,
                            ResourceId.Component(TypeId<TestInt>.Value),
                            group
                        );
                    }
                );

            a.AddEntity(TestTags.Alpha).AssertComplete();

            NAssert.Throws<TrecsException>(() => a.World.Submit());
            sub.Dispose();
        }

        [Test]
        public void TrackJob_OutsideSubmission_DoesNotThrow()
        {
            using var env = EcsTestHelper.CreateEnvironment(TestTemplates.SimpleAlpha);
            var scheduler = env.World.GetSystemRunner().JobScheduler;

            NAssert.DoesNotThrow(() => scheduler.TrackJob(default));

            scheduler.CompleteAllOutstanding();
        }

        [Test]
        public void TrackJob_DuringOnSubmissionCompleted_DoesNotThrow()
        {
            using var env = EcsTestHelper.CreateEnvironment(TestTemplates.SimpleAlpha);
            var a = env.Accessor;
            var scheduler = env.World.GetSystemRunner().JobScheduler;

            bool callbackFired = false;
            var sub = a.Events.OnSubmissionCompleted(() =>
            {
                scheduler.TrackJob(default);
                callbackFired = true;
            });

            a.AddEntity(TestTags.Alpha).AssertComplete();

            NAssert.DoesNotThrow(() => a.World.Submit());
            NAssert.IsTrue(callbackFired);

            scheduler.CompleteAllOutstanding();
            sub.Dispose();
        }
    }
}
