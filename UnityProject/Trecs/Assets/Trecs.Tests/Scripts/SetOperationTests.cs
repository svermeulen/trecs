using System.Collections.Generic;
using NUnit.Framework;
using NAssert = NUnit.Framework.Assert;

namespace Trecs.Tests
{
    // Dedicated set for these tests (scoped to QId1 tag from QueryCompositionTests)
    public struct FiltOpTestSet : IEntitySet<QId1> { }

    public struct FiltOpTestSet2 : IEntitySet<QId1> { }

    [TestFixture]
    public class SetOperationTests
    {
        TestEnvironment CreateEnv() =>
            EcsTestHelper.CreateEnvironment(
                b =>
                {
                    b.AddSet<FiltOpTestSet>();
                    b.AddSet<FiltOpTestSet2>();
                },
                QTestEntityA.Template
            );

        #region Add / Exists

        [Test]
        public void Filter_Add_ExistsReturnsTrue()
        {
            using var env = CreateEnv();
            var a = env.Accessor;

            a.AddEntity(Tag<QId1>.Value)
                .Set(new TestInt { Value = 1 })
                .Set(new TestFloat())
                .AssertComplete();
            a.SubmitEntities();

            var group = a.WorldInfo.GetSingleGroupWithTags(Tag<QId1>.Value);
            var set = a.Set<FiltOpTestSet>();
            a.SetAdd<FiltOpTestSet>(new EntityIndex(0, group));
            a.SubmitEntities(); // flush deferred ops

            NAssert.IsTrue(set.Read.Exists(new EntityIndex(0, group)));
        }

        [Test]
        public void Filter_AddByEntityIndex_ExistsReturnsTrue()
        {
            using var env = CreateEnv();
            var a = env.Accessor;

            a.AddEntity(Tag<QId1>.Value)
                .Set(new TestInt { Value = 1 })
                .Set(new TestFloat())
                .AssertComplete();
            a.SubmitEntities();

            var group = a.WorldInfo.GetSingleGroupWithTags(Tag<QId1>.Value);
            var ei = new EntityIndex(0, group);

            var set = a.Set<FiltOpTestSet>();
            a.SetAdd<FiltOpTestSet>(ei);
            a.SubmitEntities();

            NAssert.IsTrue(set.Read.Exists(ei));
        }

        [Test]
        public void Filter_NotAdded_ExistsReturnsFalse()
        {
            using var env = CreateEnv();
            var a = env.Accessor;

            a.AddEntity(Tag<QId1>.Value)
                .Set(new TestInt { Value = 1 })
                .Set(new TestFloat())
                .AssertComplete();
            a.SubmitEntities();

            var group = a.WorldInfo.GetSingleGroupWithTags(Tag<QId1>.Value);

            var set = a.Set<FiltOpTestSet>();

            NAssert.IsFalse(set.Read.Exists(new EntityIndex(0, group)));
        }

        [Test]
        public void Filter_AddMultiple_AllExist()
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
            var set = a.Set<FiltOpTestSet>();

            // Add entities 0, 2, 4 to set
            a.SetAdd<FiltOpTestSet>(new EntityIndex(0, group));
            a.SetAdd<FiltOpTestSet>(new EntityIndex(2, group));
            a.SetAdd<FiltOpTestSet>(new EntityIndex(4, group));
            a.SubmitEntities();

            var read = set.Read;
            NAssert.IsTrue(read.Exists(new EntityIndex(0, group)));
            NAssert.IsFalse(read.Exists(new EntityIndex(1, group)));
            NAssert.IsTrue(read.Exists(new EntityIndex(2, group)));
            NAssert.IsFalse(read.Exists(new EntityIndex(3, group)));
            NAssert.IsTrue(read.Exists(new EntityIndex(4, group)));
        }

        #endregion

        #region Remove

        [Test]
        public void Filter_Remove_ExistsReturnsFalse()
        {
            using var env = CreateEnv();
            var a = env.Accessor;

            a.AddEntity(Tag<QId1>.Value)
                .Set(new TestInt { Value = 1 })
                .Set(new TestFloat())
                .AssertComplete();
            a.SubmitEntities();

            var group = a.WorldInfo.GetSingleGroupWithTags(Tag<QId1>.Value);
            var set = a.Set<FiltOpTestSet>();

            a.SetAdd<FiltOpTestSet>(new EntityIndex(0, group));
            a.SubmitEntities();
            NAssert.IsTrue(set.Read.Exists(new EntityIndex(0, group)));

            a.SetRemove<FiltOpTestSet>(new EntityIndex(0, group));
            a.SubmitEntities();
            NAssert.IsFalse(set.Read.Exists(new EntityIndex(0, group)));
        }

        [Test]
        public void Filter_RemoveByEntityIndex_ExistsReturnsFalse()
        {
            using var env = CreateEnv();
            var a = env.Accessor;

            a.AddEntity(Tag<QId1>.Value)
                .Set(new TestInt { Value = 1 })
                .Set(new TestFloat())
                .AssertComplete();
            a.SubmitEntities();

            var group = a.WorldInfo.GetSingleGroupWithTags(Tag<QId1>.Value);
            var ei = new EntityIndex(0, group);

            var set = a.Set<FiltOpTestSet>();
            a.SetAdd<FiltOpTestSet>(ei);
            a.SubmitEntities();

            a.SetRemove<FiltOpTestSet>(ei);
            a.SubmitEntities();

            NAssert.IsFalse(set.Read.Exists(ei));
        }

        [Test]
        public void Filter_RemoveAndReAddSameFrame_EntityInSet()
        {
            using var env = CreateEnv();
            var a = env.Accessor;

            a.AddEntity(Tag<QId1>.Value)
                .Set(new TestInt { Value = 1 })
                .Set(new TestFloat())
                .AssertComplete();
            a.SubmitEntities();

            var group = a.WorldInfo.GetSingleGroupWithTags(Tag<QId1>.Value);
            var set = a.Set<FiltOpTestSet>();

            a.SetAdd<FiltOpTestSet>(new EntityIndex(0, group));
            a.SubmitEntities();

            // Remove and re-add in the same frame
            a.SetRemove<FiltOpTestSet>(new EntityIndex(0, group));
            a.SetAdd<FiltOpTestSet>(new EntityIndex(0, group));
            a.SubmitEntities();

            NAssert.IsTrue(set.Read.Exists(new EntityIndex(0, group)));
        }

        #endregion

        #region Clear

        [Test]
        public void Filter_Clear_RemovesAllEntities()
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
            var set = a.Set<FiltOpTestSet>();

            a.SetAdd<FiltOpTestSet>(new EntityIndex(0, group));
            a.SetAdd<FiltOpTestSet>(new EntityIndex(1, group));
            a.SetAdd<FiltOpTestSet>(new EntityIndex(2, group));
            a.SubmitEntities();

            set.Write.Clear();

            var read = set.Read;
            NAssert.IsFalse(read.Exists(new EntityIndex(0, group)));
            NAssert.IsFalse(read.Exists(new EntityIndex(1, group)));
            NAssert.IsFalse(read.Exists(new EntityIndex(2, group)));
        }

        #endregion

        #region Count

        [Test]
        public void Filter_ComputeFinalCount_Accurate()
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
            var set = a.Set<FiltOpTestSet>();

            a.SetAdd<FiltOpTestSet>(new EntityIndex(0, group));
            a.SetAdd<FiltOpTestSet>(new EntityIndex(2, group));
            a.SetAdd<FiltOpTestSet>(new EntityIndex(4, group));
            a.SubmitEntities();

            NAssert.AreEqual(3, set.Read.Count);
        }

        [Test]
        public void Filter_ComputeFinalCount_EmptyIsZero()
        {
            using var env = CreateEnv();
            var a = env.Accessor;

            a.AddEntity(Tag<QId1>.Value)
                .Set(new TestInt { Value = 1 })
                .Set(new TestFloat())
                .AssertComplete();
            a.SubmitEntities();

            var set = a.Set<FiltOpTestSet>();

            NAssert.AreEqual(0, set.Read.Count);
        }

        [Test]
        public void Filter_ComputeFinalCount_AfterRemove_Decreases()
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
            var set = a.Set<FiltOpTestSet>();

            a.SetAdd<FiltOpTestSet>(new EntityIndex(0, group));
            a.SetAdd<FiltOpTestSet>(new EntityIndex(1, group));
            a.SetAdd<FiltOpTestSet>(new EntityIndex(2, group));
            a.SubmitEntities();

            NAssert.AreEqual(3, set.Read.Count);

            a.SetRemove<FiltOpTestSet>(new EntityIndex(1, group));
            a.SubmitEntities();

            NAssert.AreEqual(2, set.Read.Count);
        }

        #endregion

        #region Two Independent Sets

        [Test]
        public void TwoSets_IndependentMembership()
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
            var set1 = a.Set<FiltOpTestSet>();
            var set2 = a.Set<FiltOpTestSet2>();

            // Entity 0 in filter1 only, entity 1 in filter2 only, entity 2 in both
            a.SetAdd<FiltOpTestSet>(new EntityIndex(0, group));
            a.SetAdd<FiltOpTestSet>(new EntityIndex(2, group));
            a.SetAdd<FiltOpTestSet2>(new EntityIndex(1, group));
            a.SetAdd<FiltOpTestSet2>(new EntityIndex(2, group));
            a.SubmitEntities();

            var read1 = set1.Read;
            NAssert.IsTrue(read1.Exists(new EntityIndex(0, group)));
            NAssert.IsFalse(read1.Exists(new EntityIndex(1, group)));
            NAssert.IsTrue(read1.Exists(new EntityIndex(2, group)));

            var read2 = set2.Read;
            NAssert.IsFalse(read2.Exists(new EntityIndex(0, group)));
            NAssert.IsTrue(read2.Exists(new EntityIndex(1, group)));
            NAssert.IsTrue(read2.Exists(new EntityIndex(2, group)));

            NAssert.AreEqual(2, read1.Count);
            NAssert.AreEqual(2, read2.Count);
        }

        #endregion

        #region Set + Entity Remove Interaction

        [Test]
        public void Filter_EntityRemoved_FilterReflectsRemoval()
        {
            using var env = CreateEnv();
            var a = env.Accessor;

            var init = a.AddEntity(Tag<QId1>.Value)
                .Set(new TestInt { Value = 1 })
                .Set(new TestFloat())
                .AssertComplete();
            var entityHandle = init.Handle;

            a.AddEntity(Tag<QId1>.Value)
                .Set(new TestInt { Value = 2 })
                .Set(new TestFloat())
                .AssertComplete();
            a.SubmitEntities();

            var group = a.WorldInfo.GetSingleGroupWithTags(Tag<QId1>.Value);
            var set = a.Set<FiltOpTestSet>();

            a.SetAdd<FiltOpTestSet>(new EntityIndex(0, group));
            a.SetAdd<FiltOpTestSet>(new EntityIndex(1, group));
            a.SubmitEntities();

            NAssert.AreEqual(2, set.Read.Count);

            // Remove entity 0
            a.RemoveEntity(entityHandle);
            a.SubmitEntities();

            // After removal with swap-back, the set should have been updated
            // Entity count in group should be 1
            NAssert.AreEqual(1, a.CountEntitiesWithTags(Tag<QId1>.Value));
            // Set should also reflect the removal (surviving entity should still be in set)
            NAssert.AreEqual(
                1,
                set.Read.Count,
                "Set should have 1 entry after removing one of two set entities"
            );
        }

        #endregion

        #region Set + Add Same Entity Twice

        [Test]
        public void Filter_AddSameEntityTwice_OnlyOnce()
        {
            using var env = CreateEnv();
            var a = env.Accessor;

            a.AddEntity(Tag<QId1>.Value)
                .Set(new TestInt { Value = 1 })
                .Set(new TestFloat())
                .AssertComplete();
            a.SubmitEntities();

            var group = a.WorldInfo.GetSingleGroupWithTags(Tag<QId1>.Value);
            var set = a.Set<FiltOpTestSet>();

            a.SetAdd<FiltOpTestSet>(new EntityIndex(0, group));
            a.SetAdd<FiltOpTestSet>(new EntityIndex(0, group));
            a.SubmitEntities();

            var read = set.Read;
            NAssert.AreEqual(1, read.Count);
            NAssert.IsTrue(read.Exists(new EntityIndex(0, group)));
        }

        #endregion

        #region Set Query Data Iteration

        [Test]
        public void Filter_QueryInSet_ReturnsCorrectComponentValues()
        {
            using var env = CreateEnv();
            var a = env.Accessor;

            for (int i = 0; i < 5; i++)
            {
                a.AddEntity(Tag<QId1>.Value)
                    .Set(new TestInt { Value = i * 10 })
                    .Set(new TestFloat())
                    .AssertComplete();
            }
            a.SubmitEntities();

            var group = a.WorldInfo.GetSingleGroupWithTags(Tag<QId1>.Value);

            // Add entities 1 and 3 to set (values 10 and 30)
            a.SetAdd<FiltOpTestSet>(new EntityIndex(1, group));
            a.SetAdd<FiltOpTestSet>(new EntityIndex(3, group));
            a.SubmitEntities();

            var values = new List<int>();
            foreach (var ei in a.Query().InSet<FiltOpTestSet>().EntityIndices())
            {
                values.Add(a.Component<TestInt>(ei).Read.Value);
            }

            values.Sort();
            NAssert.AreEqual(2, values.Count);
            NAssert.AreEqual(10, values[0]);
            NAssert.AreEqual(30, values[1]);
        }

        [Test]
        public void Filter_QueryInSet_AfterRemoval_ReturnsUpdatedData()
        {
            using var env = CreateEnv();
            var a = env.Accessor;

            var refs = new EntityHandle[4];
            for (int i = 0; i < 4; i++)
            {
                var init = a.AddEntity(Tag<QId1>.Value)
                    .Set(new TestInt { Value = (i + 1) * 100 })
                    .Set(new TestFloat())
                    .AssertComplete();
                refs[i] = init.Handle;
            }
            a.SubmitEntities();

            var group = a.WorldInfo.GetSingleGroupWithTags(Tag<QId1>.Value);
            var set = a.Set<FiltOpTestSet>();

            // Add all to set
            for (int i = 0; i < 4; i++)
            {
                a.SetAdd<FiltOpTestSet>(new EntityIndex(i, group));
            }
            a.SubmitEntities();

            NAssert.AreEqual(4, set.Read.Count);

            // Remove entity 1 (value 200) from the world
            a.RemoveEntity(refs[1]);
            a.SubmitEntities();

            // Iterate set - should have 3 entities, none with value 200
            var values = new List<int>();
            foreach (var ei in a.Query().InSet<FiltOpTestSet>().EntityIndices())
            {
                values.Add(a.Component<TestInt>(ei).Read.Value);
            }

            NAssert.AreEqual(3, values.Count, "Set should have 3 entries after entity removal");
            NAssert.IsFalse(values.Contains(200), "Removed entity's value should not appear");
        }

        #endregion
    }
}
