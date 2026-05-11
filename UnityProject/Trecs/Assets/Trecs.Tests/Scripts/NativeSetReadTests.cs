using NUnit.Framework;
using Trecs.Internal;
using Unity.Collections;
using NAssert = NUnit.Framework.Assert;

namespace Trecs.Tests
{
    public struct NSRTestSet : IEntitySet<QId1> { }

    [TestFixture]
    public partial class NativeSetReadTests
    {
        TestEnvironment CreateEnv() =>
            EcsTestHelper.CreateEnvironment(b => b.AddSet<NSRTestSet>(), QTestEntityA.Template);

        // Job that consults NativeSetRead.Exists per entity, recording results into a buffer.
        partial struct CheckMembershipJob
        {
            [FromWorld]
            public NativeSetRead<NSRTestSet> Reader;

            // Index i records 1 if entity i is in the set, 0 otherwise. Parallel
            // workers within one ScheduleParallel call write distinct slots — keyed
            // by entityIndex.Index — so the parallel-for restriction is safe to lift.
            [NativeDisableParallelForRestriction]
            public NativeArray<int> Results;

            [ForEachEntity(Tag = typeof(QId1))]
            void Execute(in TestInt value, EntityIndex entityIndex)
            {
                Results[entityIndex.Index] = Reader.Exists(entityIndex) ? 1 : 0;
            }
        }

        [Test]
        public void NativeSetRead_Exists_AgreesWithMainThreadRead()
        {
            using var env = CreateEnv();
            var a = env.Accessor;

            for (int i = 0; i < 6; i++)
                a.AddEntity(Tag<QId1>.Value)
                    .Set(new TestInt { Value = i })
                    .Set(new TestFloat())
                    .AssertComplete();
            a.SubmitEntities();

            var group = a.WorldInfo.GetSingleGroupWithTags(Tag<QId1>.Value);
            var set = a.Set<NSRTestSet>();
            // Membership: 0, 2, 4 in the set.
            set.Write.Add(new EntityIndex(0, group));
            set.Write.Add(new EntityIndex(2, group));
            set.Write.Add(new EntityIndex(4, group));

            using var results = new NativeArray<int>(6, Allocator.TempJob);
            new CheckMembershipJob { Results = results }
                .ScheduleParallel(a)
                .Complete();

            NAssert.AreEqual(1, results[0]);
            NAssert.AreEqual(0, results[1]);
            NAssert.AreEqual(1, results[2]);
            NAssert.AreEqual(0, results[3]);
            NAssert.AreEqual(1, results[4]);
            NAssert.AreEqual(0, results[5]);
        }

        [Test]
        public void NativeSetRead_AfterMainThreadWrite_ReaderJobSeesUpdates()
        {
            using var env = CreateEnv();
            var a = env.Accessor;

            for (int i = 0; i < 4; i++)
                a.AddEntity(Tag<QId1>.Value)
                    .Set(new TestInt { Value = i })
                    .Set(new TestFloat())
                    .AssertComplete();
            a.SubmitEntities();

            var group = a.WorldInfo.GetSingleGroupWithTags(Tag<QId1>.Value);
            var set = a.Set<NSRTestSet>();
            // Initial state: only 1 in set.
            set.Write.Add(new EntityIndex(1, group));

            using var resultsA = new NativeArray<int>(4, Allocator.TempJob);
            new CheckMembershipJob { Results = resultsA }
                .ScheduleParallel(a)
                .Complete();

            NAssert.AreEqual(0, resultsA[0]);
            NAssert.AreEqual(1, resultsA[1]);
            NAssert.AreEqual(0, resultsA[2]);

            // Mutate on main thread (the .Write here syncs the previous reader job).
            set.Write.Remove(new EntityIndex(1, group));
            set.Write.Add(new EntityIndex(3, group));

            // A second reader job should see the new state — IncludeReadDep waits for
            // the previous main-thread write to be visible (job graph captures the
            // implicit barrier).
            using var resultsB = new NativeArray<int>(4, Allocator.TempJob);
            new CheckMembershipJob { Results = resultsB }
                .ScheduleParallel(a)
                .Complete();

            NAssert.AreEqual(0, resultsB[0]);
            NAssert.AreEqual(0, resultsB[1]);
            NAssert.AreEqual(0, resultsB[2]);
            NAssert.AreEqual(1, resultsB[3]);
        }
    }
}
