using System.Collections.Generic;
using NUnit.Framework;
using Trecs.Internal;
using NAssert = NUnit.Framework.Assert;

namespace Trecs.Tests
{
    public struct TFJTestTransientSet : IEntitySet<QId1> { }

    [TestFixture]
    public partial class TransientSetJobWriteTests
    {
        TestEnvironment CreateEnv() =>
            EcsTestHelper.CreateEnvironment(
                b => b.AddSet<TFJTestTransientSet>(),
                QTestEntityA.Template
            );

        partial struct FlagEntitiesJob
        {
            [FromWorld]
            public NativeSetWrite<TFJTestTransientSet> Writer;

            [ForEachEntity(Tag = typeof(QId1))]
            void Execute(in TestInt value, EntityIndex entityIndex)
            {
                // Flag every other entity
                if (entityIndex.Index % 2 == 0)
                {
                    Writer.AddImmediate(entityIndex);
                }
            }
        }

        partial struct FlagAllEntitiesJob
        {
            [FromWorld]
            public NativeSetWrite<TFJTestTransientSet> Writer;

            [ForEachEntity(Tag = typeof(QId1))]
            void Execute(in TestInt value, EntityIndex entityIndex)
            {
                Writer.AddImmediate(entityIndex);
            }
        }

        [Test]
        public void JobWrite_EntitiesAppearAfterMainThreadRead()
        {
            using var env = CreateEnv();
            var a = env.Accessor;

            // Add entities
            for (int i = 0; i < 10; i++)
            {
                a.AddEntity(Tag<QId1>.Value)
                    .Set(new TestInt { Value = i })
                    .Set(new TestFloat())
                    .AssertComplete();
            }
            a.SubmitEntities();

            // Schedule a job that writes to the transient set
            new FlagEntitiesJob().ScheduleParallel(a);

            var group = a.WorldInfo.GetSingleGroupWithTags(Tag<QId1>.Value);

            // Reading the set on main thread should sync + flush
            var set = a.Set<TFJTestTransientSet>();
            var read = set.Read;

            // Every other entity (0, 2, 4, 6, 8) should be in the set
            NAssert.AreEqual(5, read.Count, "Job should have flagged 5 entities (every other one)");

            NAssert.IsTrue(read.Exists(new EntityIndex(0, group)));
            NAssert.IsTrue(read.Exists(new EntityIndex(2, group)));
            NAssert.IsTrue(read.Exists(new EntityIndex(4, group)));
            NAssert.IsFalse(read.Exists(new EntityIndex(1, group)));
            NAssert.IsFalse(read.Exists(new EntityIndex(3, group)));
        }

        [Test]
        public void JobWrite_FlushedAtPhaseBoundary_ThenManualClear()
        {
            using var env = CreateEnv();
            var a = env.Accessor;

            for (int i = 0; i < 4; i++)
            {
                a.AddEntity(Tag<QId1>.Value)
                    .Set(new TestInt { Value = i })
                    .Set(new TestFloat())
                    .AssertComplete();
            }
            a.SubmitEntities();

            // Schedule a job that writes to the set
            new FlagAllEntitiesJob().ScheduleParallel(a);

            // Simulate phase boundary: CompleteAllOutstanding + FlushJobWrites
            a.JobScheduler.CompleteAllOutstanding();
            env.World.GetSetStore().FlushAllSetJobWrites();

            // Set should contain entries after flush (no auto-clear)
            var set = a.Set<TFJTestTransientSet>();
            NAssert.Greater(set.Read.Count, 0, "Set should have entries after job flush");

            // Manual clear
            set.Write.Clear();
            NAssert.AreEqual(0, set.Read.Count, "Set should be empty after manual clear");
        }

        [Test]
        public void QueryInSet_TransientSet_IteratesCorrectEntities()
        {
            using var env = CreateEnv();
            var a = env.Accessor;

            for (int i = 0; i < 6; i++)
            {
                a.AddEntity(Tag<QId1>.Value)
                    .Set(new TestInt { Value = i })
                    .Set(new TestFloat())
                    .AssertComplete();
            }
            a.SubmitEntities();

            var group = a.WorldInfo.GetSingleGroupWithTags(Tag<QId1>.Value);

            // Add entities 1, 3, 5 to the transient set directly
            var set = a.Set<TFJTestTransientSet>();
            var write = set.Write;
            write.AddImmediate(new EntityIndex(1, group));
            write.AddImmediate(new EntityIndex(3, group));
            write.AddImmediate(new EntityIndex(5, group));

            // Query using InSet<TFJTestTransientSet> via QueryBuilder
            var result = new List<int>();
            foreach (var ei in a.Query().InSet<TFJTestTransientSet>().EntityIndices())
            {
                result.Add(ei.Index);
            }

            result.Sort();
            NAssert.AreEqual(3, result.Count, "Should iterate exactly the 3 flagged entities");
            NAssert.AreEqual(1, result[0]);
            NAssert.AreEqual(3, result[1]);
            NAssert.AreEqual(5, result[2]);
        }

        [Test]
        public void QueryGroupSlices_TransientSet_IteratesCorrectEntities()
        {
            using var env = CreateEnv();
            var a = env.Accessor;

            for (int i = 0; i < 6; i++)
            {
                a.AddEntity(Tag<QId1>.Value)
                    .Set(new TestInt { Value = i })
                    .Set(new TestFloat())
                    .AssertComplete();
            }
            a.SubmitEntities();

            var group = a.WorldInfo.GetSingleGroupWithTags(Tag<QId1>.Value);

            // Add entities 0, 2, 4 to the transient set
            var set = a.Set<TFJTestTransientSet>();
            var write = set.Write;
            write.AddImmediate(new EntityIndex(0, group));
            write.AddImmediate(new EntityIndex(2, group));
            write.AddImmediate(new EntityIndex(4, group));

            // Query using GroupSlices path
            int count = 0;
            foreach (var slice in a.Query().InSet<TFJTestTransientSet>().GroupSlices())
            {
                foreach (var _ in slice.Indices)
                    count++;
            }

            NAssert.AreEqual(
                3,
                count,
                "GroupSlices should yield 3 entities from the transient set"
            );
        }

        [Test]
        public void GetSet_TransientSet_ReturnsCorrectLookup()
        {
            using var env = CreateEnv();
            var a = env.Accessor;

            for (int i = 0; i < 4; i++)
            {
                a.AddEntity(Tag<QId1>.Value)
                    .Set(new TestInt { Value = i })
                    .Set(new TestFloat())
                    .AssertComplete();
            }
            a.SubmitEntities();

            var group = a.WorldInfo.GetSingleGroupWithTags(Tag<QId1>.Value);

            // Add to transient set
            var set = a.Set<TFJTestTransientSet>();
            var write = set.Write;
            write.AddImmediate(new EntityIndex(0, group));
            write.AddImmediate(new EntityIndex(2, group));

            // Verify via SetAccessor
            var read = set.Read;
            NAssert.IsTrue(read.Exists(new EntityIndex(0, group)), "Entity 0 should be in set");
            NAssert.IsTrue(read.Exists(new EntityIndex(2, group)), "Entity 2 should be in set");
            NAssert.AreEqual(2, read.Count, "Should have 2 entities total");
        }

        [Test]
        public void TransientSet_DirectAdd_ExistsAndCountCorrect()
        {
            using var env = CreateEnv();
            var a = env.Accessor;

            for (int i = 0; i < 5; i++)
            {
                a.AddEntity(Tag<QId1>.Value)
                    .Set(new TestInt { Value = i })
                    .Set(new TestFloat())
                    .AssertComplete();
            }
            a.SubmitEntities();

            var group = a.WorldInfo.GetSingleGroupWithTags(Tag<QId1>.Value);

            var set = a.Set<TFJTestTransientSet>();
            var write = set.Write;
            write.AddImmediate(new EntityIndex(0, group));
            write.AddImmediate(new EntityIndex(3, group));

            var read = set.Read;
            NAssert.AreEqual(2, read.Count);
            NAssert.IsTrue(read.Exists(new EntityIndex(0, group)));
            NAssert.IsFalse(read.Exists(new EntityIndex(1, group)));
            NAssert.IsTrue(read.Exists(new EntityIndex(3, group)));
        }

        [Test]
        public void Set_ManualClear_IsEmpty()
        {
            using var env = CreateEnv();
            var a = env.Accessor;

            for (int i = 0; i < 3; i++)
            {
                a.AddEntity(Tag<QId1>.Value)
                    .Set(new TestInt { Value = i })
                    .Set(new TestFloat())
                    .AssertComplete();
            }
            a.SubmitEntities();

            var group = a.WorldInfo.GetSingleGroupWithTags(Tag<QId1>.Value);

            var set = a.Set<TFJTestTransientSet>();
            var write = set.Write;
            write.AddImmediate(new EntityIndex(0, group));
            write.AddImmediate(new EntityIndex(2, group));

            NAssert.AreEqual(2, set.Read.Count);

            // Manual clear
            set.Write.Clear();

            var setAfter = a.Set<TFJTestTransientSet>();
            NAssert.AreEqual(0, setAfter.Read.Count, "Set should be empty after manual clear");
        }

        [Test]
        public void TransientSet_AddSameEntityTwice_CountIsOne()
        {
            using var env = CreateEnv();
            var a = env.Accessor;

            a.AddEntity(Tag<QId1>.Value)
                .Set(new TestInt { Value = 1 })
                .Set(new TestFloat())
                .AssertComplete();
            a.SubmitEntities();

            var group = a.WorldInfo.GetSingleGroupWithTags(Tag<QId1>.Value);

            var set = a.Set<TFJTestTransientSet>();
            var write = set.Write;
            write.AddImmediate(new EntityIndex(0, group));
            write.AddImmediate(new EntityIndex(0, group));

            NAssert.AreEqual(
                1,
                set.Read.Count,
                "Adding same entity twice to transient set should count as one"
            );
        }

        [Test]
        public void JobWrite_SchedulerTracksWriteDependency()
        {
            using var env = CreateEnv();
            var a = env.Accessor;

            for (int i = 0; i < 4; i++)
            {
                a.AddEntity(Tag<QId1>.Value)
                    .Set(new TestInt { Value = i })
                    .Set(new TestFloat())
                    .AssertComplete();
            }
            a.SubmitEntities();

            // Schedule a job that writes to the transient set
            new FlagAllEntitiesJob().ScheduleParallel(a);

            // Verify scheduler has outstanding jobs
            NAssert.IsTrue(
                a.JobScheduler.HasOutstandingJobs,
                "Scheduler should track the writing job"
            );

            // Reading the set should complete the writing job (sync point)
            var set = a.Set<TFJTestTransientSet>();

            // All entities should be flagged (proves the write job completed before read)
            NAssert.AreEqual(4, set.Read.Count, "All entities should be in set after sync");
        }
    }
}
