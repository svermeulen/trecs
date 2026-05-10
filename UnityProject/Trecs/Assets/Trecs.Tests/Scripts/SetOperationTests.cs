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
            a.Set<FiltOpTestSet>().Defer.Add(new EntityIndex(0, group));
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
            a.Set<FiltOpTestSet>().Defer.Add(ei);
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
            a.Set<FiltOpTestSet>().Defer.Add(new EntityIndex(0, group));
            a.Set<FiltOpTestSet>().Defer.Add(new EntityIndex(2, group));
            a.Set<FiltOpTestSet>().Defer.Add(new EntityIndex(4, group));
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

            a.Set<FiltOpTestSet>().Defer.Add(new EntityIndex(0, group));
            a.SubmitEntities();
            NAssert.IsTrue(set.Read.Exists(new EntityIndex(0, group)));

            a.Set<FiltOpTestSet>().Defer.Remove(new EntityIndex(0, group));
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
            a.Set<FiltOpTestSet>().Defer.Add(ei);
            a.SubmitEntities();

            a.Set<FiltOpTestSet>().Defer.Remove(ei);
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

            a.Set<FiltOpTestSet>().Defer.Add(new EntityIndex(0, group));
            a.SubmitEntities();

            // Remove and re-add in the same frame
            a.Set<FiltOpTestSet>().Defer.Remove(new EntityIndex(0, group));
            a.Set<FiltOpTestSet>().Defer.Add(new EntityIndex(0, group));
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

            a.Set<FiltOpTestSet>().Defer.Add(new EntityIndex(0, group));
            a.Set<FiltOpTestSet>().Defer.Add(new EntityIndex(1, group));
            a.Set<FiltOpTestSet>().Defer.Add(new EntityIndex(2, group));
            a.SubmitEntities();

            set.Write.Clear();

            var read = set.Read;
            NAssert.IsFalse(read.Exists(new EntityIndex(0, group)));
            NAssert.IsFalse(read.Exists(new EntityIndex(1, group)));
            NAssert.IsFalse(read.Exists(new EntityIndex(2, group)));
        }

        [Test]
        public void Filter_DeferredClear_AppliedAtSubmission()
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

            a.Set<FiltOpTestSet>().Defer.Add(new EntityIndex(0, group));
            a.Set<FiltOpTestSet>().Defer.Add(new EntityIndex(1, group));
            a.SubmitEntities();
            NAssert.AreEqual(2, set.Read.Count);

            a.Set<FiltOpTestSet>().Defer.Clear();
            // Still populated until submission lands.
            NAssert.AreEqual(2, set.Read.Count);

            a.SubmitEntities();
            NAssert.AreEqual(0, set.Read.Count);
        }

        [Test]
        public void Filter_DeferredClear_SupersedesPendingAddInSameSubmission()
        {
            // Adds queued before Clear in the same submission window are
            // dropped. Mirrors remove-supersedes-move on entity ops.
            using var env = CreateEnv();
            var a = env.Accessor;

            a.AddEntity(Tag<QId1>.Value).Set(new TestInt()).Set(new TestFloat()).AssertComplete();
            a.AddEntity(Tag<QId1>.Value).Set(new TestInt()).Set(new TestFloat()).AssertComplete();
            a.SubmitEntities();

            var group = a.WorldInfo.GetSingleGroupWithTags(Tag<QId1>.Value);

            a.Set<FiltOpTestSet>().Defer.Add(new EntityIndex(0, group));
            a.Set<FiltOpTestSet>().Defer.Add(new EntityIndex(1, group));
            a.Set<FiltOpTestSet>().Defer.Clear();
            a.SubmitEntities();

            NAssert.AreEqual(0, a.Set<FiltOpTestSet>().Read.Count);
        }

        [Test]
        public void Filter_DeferredClear_SupersedesPendingAddRegardlessOfOrder()
        {
            // Adds queued *after* Clear are also dropped — Clear wins by rule,
            // not by call order. Use Clear if you want sequential
            // semantics within a single system.
            using var env = CreateEnv();
            var a = env.Accessor;

            a.AddEntity(Tag<QId1>.Value).Set(new TestInt()).Set(new TestFloat()).AssertComplete();
            a.AddEntity(Tag<QId1>.Value).Set(new TestInt()).Set(new TestFloat()).AssertComplete();
            a.SubmitEntities();

            var group = a.WorldInfo.GetSingleGroupWithTags(Tag<QId1>.Value);

            a.Set<FiltOpTestSet>().Defer.Clear();
            a.Set<FiltOpTestSet>().Defer.Add(new EntityIndex(0, group));
            a.Set<FiltOpTestSet>().Defer.Add(new EntityIndex(1, group));
            a.SubmitEntities();

            NAssert.AreEqual(0, a.Set<FiltOpTestSet>().Read.Count);
        }

        [Test]
        public void Filter_DeferredClear_SupersedesPendingRemove()
        {
            // Remove of an entity not in the set would be a no-op anyway, but
            // the clear path should still drain it correctly without crashing.
            using var env = CreateEnv();
            var a = env.Accessor;

            for (int i = 0; i < 3; i++)
                a.AddEntity(Tag<QId1>.Value)
                    .Set(new TestInt())
                    .Set(new TestFloat())
                    .AssertComplete();
            a.SubmitEntities();

            var group = a.WorldInfo.GetSingleGroupWithTags(Tag<QId1>.Value);

            // Pre-populate
            a.Set<FiltOpTestSet>().Defer.Add(new EntityIndex(0, group));
            a.Set<FiltOpTestSet>().Defer.Add(new EntityIndex(1, group));
            a.Set<FiltOpTestSet>().Defer.Add(new EntityIndex(2, group));
            a.SubmitEntities();
            NAssert.AreEqual(3, a.Set<FiltOpTestSet>().Read.Count);

            a.Set<FiltOpTestSet>().Defer.Remove(new EntityIndex(1, group));
            a.Set<FiltOpTestSet>().Defer.Clear();
            a.SubmitEntities();

            NAssert.AreEqual(0, a.Set<FiltOpTestSet>().Read.Count);
        }

        [Test]
        public void Filter_DeferredClear_OnlyAffectsTargetSet()
        {
            using var env = CreateEnv();
            var a = env.Accessor;

            for (int i = 0; i < 2; i++)
                a.AddEntity(Tag<QId1>.Value)
                    .Set(new TestInt())
                    .Set(new TestFloat())
                    .AssertComplete();
            a.SubmitEntities();

            var group = a.WorldInfo.GetSingleGroupWithTags(Tag<QId1>.Value);

            a.Set<FiltOpTestSet>().Defer.Add(new EntityIndex(0, group));
            a.Set<FiltOpTestSet2>().Defer.Add(new EntityIndex(0, group));
            a.Set<FiltOpTestSet2>().Defer.Add(new EntityIndex(1, group));
            a.SubmitEntities();

            a.Set<FiltOpTestSet>().Defer.Clear();
            a.SubmitEntities();

            NAssert.AreEqual(0, a.Set<FiltOpTestSet>().Read.Count);
            NAssert.AreEqual(2, a.Set<FiltOpTestSet2>().Read.Count);
        }

        [Test]
        public void Filter_DeferredClear_ConsumedAfterFlush()
        {
            // Clear flag must reset after flush so a subsequent Add takes
            // effect on the next submission.
            using var env = CreateEnv();
            var a = env.Accessor;

            a.AddEntity(Tag<QId1>.Value).Set(new TestInt()).Set(new TestFloat()).AssertComplete();
            a.SubmitEntities();

            var group = a.WorldInfo.GetSingleGroupWithTags(Tag<QId1>.Value);

            a.Set<FiltOpTestSet>().Defer.Clear();
            a.SubmitEntities();

            a.Set<FiltOpTestSet>().Defer.Add(new EntityIndex(0, group));
            a.SubmitEntities();

            NAssert.AreEqual(1, a.Set<FiltOpTestSet>().Read.Count);
        }

        [Test]
        public void Filter_DeferredClear_FromNativeAccessor_SupersedesNativeAdd()
        {
            using var env = CreateEnv();
            var a = env.Accessor;

            for (int i = 0; i < 3; i++)
                a.AddEntity(Tag<QId1>.Value)
                    .Set(new TestInt())
                    .Set(new TestFloat())
                    .AssertComplete();
            a.SubmitEntities();

            var group = a.WorldInfo.GetSingleGroupWithTags(Tag<QId1>.Value);
            var nativeEcs = a.ToNative();

            // Both NativeWorldAccessor.SetAdd and SetClear hit the same per-set
            // deferred queues / clear flag. Clear should win regardless of order.
            nativeEcs.SetAdd<FiltOpTestSet>(new EntityIndex(0, group));
            nativeEcs.SetAdd<FiltOpTestSet>(new EntityIndex(1, group));
            nativeEcs.SetClear<FiltOpTestSet>();
            nativeEcs.SetAdd<FiltOpTestSet>(new EntityIndex(2, group));
            a.SubmitEntities();

            NAssert.AreEqual(0, a.Set<FiltOpTestSet>().Read.Count);
        }

        [Test]
        public void Filter_DeferredClear_FromNativeAccessor_SupersedesMainThreadDeferAdd()
        {
            using var env = CreateEnv();
            var a = env.Accessor;

            for (int i = 0; i < 2; i++)
                a.AddEntity(Tag<QId1>.Value)
                    .Set(new TestInt())
                    .Set(new TestFloat())
                    .AssertComplete();
            a.SubmitEntities();

            var group = a.WorldInfo.GetSingleGroupWithTags(Tag<QId1>.Value);

            // Main-thread Defer enqueues into the same per-set queues that the native
            // accessor uses. Clear from the native accessor must wipe both.
            a.Set<FiltOpTestSet>().Defer.Add(new EntityIndex(0, group));
            a.Set<FiltOpTestSet>().Defer.Add(new EntityIndex(1, group));

            a.ToNative().SetClear<FiltOpTestSet>();
            a.SubmitEntities();

            NAssert.AreEqual(0, a.Set<FiltOpTestSet>().Read.Count);
        }

        [Test]
        public void Filter_DeferredClear_FromNativeAccessor_AppliedAtSubmission()
        {
            using var env = CreateEnv();
            var a = env.Accessor;

            for (int i = 0; i < 2; i++)
                a.AddEntity(Tag<QId1>.Value)
                    .Set(new TestInt())
                    .Set(new TestFloat())
                    .AssertComplete();
            a.SubmitEntities();

            var group = a.WorldInfo.GetSingleGroupWithTags(Tag<QId1>.Value);
            a.Set<FiltOpTestSet>().Defer.Add(new EntityIndex(0, group));
            a.Set<FiltOpTestSet>().Defer.Add(new EntityIndex(1, group));
            a.SubmitEntities();
            NAssert.AreEqual(2, a.Set<FiltOpTestSet>().Read.Count);

            var nativeEcs = a.ToNative();
            nativeEcs.SetClear<FiltOpTestSet>();
            a.SubmitEntities();

            NAssert.AreEqual(0, a.Set<FiltOpTestSet>().Read.Count);
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

            a.Set<FiltOpTestSet>().Defer.Add(new EntityIndex(0, group));
            a.Set<FiltOpTestSet>().Defer.Add(new EntityIndex(2, group));
            a.Set<FiltOpTestSet>().Defer.Add(new EntityIndex(4, group));
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

            a.Set<FiltOpTestSet>().Defer.Add(new EntityIndex(0, group));
            a.Set<FiltOpTestSet>().Defer.Add(new EntityIndex(1, group));
            a.Set<FiltOpTestSet>().Defer.Add(new EntityIndex(2, group));
            a.SubmitEntities();

            NAssert.AreEqual(3, set.Read.Count);

            a.Set<FiltOpTestSet>().Defer.Remove(new EntityIndex(1, group));
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
            a.Set<FiltOpTestSet>().Defer.Add(new EntityIndex(0, group));
            a.Set<FiltOpTestSet>().Defer.Add(new EntityIndex(2, group));
            a.Set<FiltOpTestSet2>().Defer.Add(new EntityIndex(1, group));
            a.Set<FiltOpTestSet2>().Defer.Add(new EntityIndex(2, group));
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

            a.Set<FiltOpTestSet>().Defer.Add(new EntityIndex(0, group));
            a.Set<FiltOpTestSet>().Defer.Add(new EntityIndex(1, group));
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

            a.Set<FiltOpTestSet>().Defer.Add(new EntityIndex(0, group));
            a.Set<FiltOpTestSet>().Defer.Add(new EntityIndex(0, group));
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
            a.Set<FiltOpTestSet>().Defer.Add(new EntityIndex(1, group));
            a.Set<FiltOpTestSet>().Defer.Add(new EntityIndex(3, group));
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
                a.Set<FiltOpTestSet>().Defer.Add(new EntityIndex(i, group));
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

        #region TryGetGroupEntry

        [Test]
        public void TryGetGroupEntry_ValidGroupOfRegisteredSet_ReturnsTrueWithEntry()
        {
            using var env = CreateEnv();
            var a = env.Accessor;

            a.AddEntity(Tag<QId1>.Value).Set(new TestInt()).Set(new TestFloat()).AssertComplete();
            a.SubmitEntities();

            var group = a.WorldInfo.GetSingleGroupWithTags(Tag<QId1>.Value);
            var read = a.Set<FiltOpTestSet>().Read;

            NAssert.IsTrue(read.TryGetGroupEntry(group, out var entry));
            NAssert.IsTrue(entry.IsValid);
        }

        [Test]
        public void TryGetGroupEntry_NullGroup_ReturnsFalse()
        {
            using var env = CreateEnv();
            var a = env.Accessor;

            var read = a.Set<FiltOpTestSet>().Read;
            NAssert.IsFalse(read.TryGetGroupEntry(GroupIndex.Null, out _));
        }

        [Test]
        public void TryGetGroupEntry_GroupNotInSetTemplate_ReturnsFalse()
        {
            // FiltOpTestSet is scoped to QId1 (via IEntitySet<QId1>); a QId2 group
            // exists in this world but isn't covered by the set's template.
            using var env = EcsTestHelper.CreateEnvironment(
                b =>
                {
                    b.AddSet<FiltOpTestSet>();
                },
                QTestEntityA.Template,
                QTestEntityAB.Template
            );
            var a = env.Accessor;

            a.AddEntity(Tag<QId1>.Value).Set(new TestInt()).Set(new TestFloat()).AssertComplete();
            a.AddEntity(Tag<QId2>.Value).Set(new TestInt()).Set(new TestFloat()).AssertComplete();
            a.SubmitEntities();

            var qId2Group = a.WorldInfo.GetSingleGroupWithTags(Tag<QId2>.Value);
            var read = a.Set<FiltOpTestSet>().Read;

            NAssert.IsFalse(
                read.TryGetGroupEntry(qId2Group, out _),
                "QId2 group is not in FiltOpTestSet's template — entry should be invalid"
            );
        }

        #endregion

        #region SetWrite group-validity safety net

        [Test]
        public void SetWrite_AddFromGroupOutsideTemplate_Throws()
        {
            using var env = EcsTestHelper.CreateEnvironment(
                b =>
                {
                    b.AddSet<FiltOpTestSet>();
                },
                QTestEntityA.Template,
                QTestEntityAB.Template
            );
            var a = env.Accessor;

            a.AddEntity(Tag<QId2>.Value).Set(new TestInt()).Set(new TestFloat()).AssertComplete();
            a.SubmitEntities();

            var qId2Group = a.WorldInfo.GetSingleGroupWithTags(Tag<QId2>.Value);

            // FiltOpTestSet's template is QId1; writing to a QId2 group must surface
            // the AssertValidGroup safety net (DEBUG-only). SetWrite is a ref struct
            // so we re-acquire it inside the lambda — ref locals can't be captured.
            NAssert.Catch<System.Exception>(() =>
                a.Set<FiltOpTestSet>().Write.Add(new EntityIndex(0, qId2Group))
            );
        }

        #endregion

        #region Untyped Set(SetId) gateway

        [Test]
        public void SetById_Read_Count_MatchesTypedRead()
        {
            using var env = CreateEnv();
            var a = env.Accessor;

            for (int i = 0; i < 3; i++)
                a.AddEntity(Tag<QId1>.Value)
                    .Set(new TestInt { Value = i })
                    .Set(new TestFloat())
                    .AssertComplete();
            a.SubmitEntities();

            var group = a.WorldInfo.GetSingleGroupWithTags(Tag<QId1>.Value);
            var typedSet = a.Set<FiltOpTestSet>();
            typedSet.Write.Add(new EntityIndex(0, group));
            typedSet.Write.Add(new EntityIndex(2, group));

            var setId = EntitySet<FiltOpTestSet>.Value.Id;
            NAssert.AreEqual(2, a.Set(setId).Read.Count);
            NAssert.AreEqual(typedSet.Read.Count, a.Set(setId).Read.Count);
        }

        [Test]
        public void SetById_Read_Exists_AgreesWithTypedRead()
        {
            using var env = CreateEnv();
            var a = env.Accessor;

            for (int i = 0; i < 3; i++)
                a.AddEntity(Tag<QId1>.Value)
                    .Set(new TestInt { Value = i })
                    .Set(new TestFloat())
                    .AssertComplete();
            a.SubmitEntities();

            var group = a.WorldInfo.GetSingleGroupWithTags(Tag<QId1>.Value);
            var typedSet = a.Set<FiltOpTestSet>();
            typedSet.Write.Add(new EntityIndex(1, group));

            var setId = EntitySet<FiltOpTestSet>.Value.Id;
            var untypedRead = a.Set(setId).Read;
            NAssert.IsTrue(untypedRead.Exists(new EntityIndex(1, group)));
            NAssert.IsFalse(untypedRead.Exists(new EntityIndex(0, group)));
            NAssert.IsFalse(untypedRead.Exists(new EntityIndex(2, group)));
        }

        [Test]
        public void SetById_Read_Exists_NullGroupReturnsFalse()
        {
            using var env = CreateEnv();
            var a = env.Accessor;

            var setId = EntitySet<FiltOpTestSet>.Value.Id;
            var read = a.Set(setId).Read;
            NAssert.IsFalse(read.Exists(EntityIndex.Null));
        }

        [Test]
        public void SetById_Read_EmptySetCountIsZero()
        {
            using var env = CreateEnv();
            var a = env.Accessor;

            var setId = EntitySet<FiltOpTestSet>.Value.Id;
            NAssert.AreEqual(0, a.Set(setId).Read.Count);
        }

        #endregion

        #region EntityHandle overloads

        [Test]
        public void Filter_Defer_AddByHandle_AppearsInSet()
        {
            using var env = CreateEnv();
            var a = env.Accessor;

            var handle = a.AddEntity(Tag<QId1>.Value)
                .Set(new TestInt { Value = 7 })
                .Set(new TestFloat())
                .AssertComplete()
                .Handle;
            a.SubmitEntities();

            a.Set<FiltOpTestSet>().Defer.Add(handle);
            a.SubmitEntities();

            NAssert.IsTrue(a.Set<FiltOpTestSet>().Read.Exists(handle));
        }

        [Test]
        public void Filter_Defer_RemoveByHandle_RemovedFromSet()
        {
            using var env = CreateEnv();
            var a = env.Accessor;

            var handle = a.AddEntity(Tag<QId1>.Value)
                .Set(new TestInt { Value = 7 })
                .Set(new TestFloat())
                .AssertComplete()
                .Handle;
            a.SubmitEntities();

            var set = a.Set<FiltOpTestSet>();
            set.Defer.Add(handle);
            a.SubmitEntities();
            NAssert.IsTrue(set.Read.Exists(handle));

            set.Defer.Remove(handle);
            a.SubmitEntities();
            NAssert.IsFalse(set.Read.Exists(handle));
        }

        [Test]
        public void Filter_Write_AddByHandle_AppearsInSet()
        {
            using var env = CreateEnv();
            var a = env.Accessor;

            var handle = a.AddEntity(Tag<QId1>.Value)
                .Set(new TestInt())
                .Set(new TestFloat())
                .AssertComplete()
                .Handle;
            a.SubmitEntities();

            var set = a.Set<FiltOpTestSet>();
            set.Write.Add(handle);

            NAssert.IsTrue(set.Read.Exists(handle));
        }

        [Test]
        public void Filter_Write_RemoveByHandle_RemovedFromSet()
        {
            using var env = CreateEnv();
            var a = env.Accessor;

            var handle = a.AddEntity(Tag<QId1>.Value)
                .Set(new TestInt())
                .Set(new TestFloat())
                .AssertComplete()
                .Handle;
            a.SubmitEntities();

            var set = a.Set<FiltOpTestSet>();
            set.Write.Add(handle);
            NAssert.IsTrue(set.Read.Exists(handle));

            set.Write.Remove(handle);
            NAssert.IsFalse(set.Read.Exists(handle));
        }

        [Test]
        public void Filter_Read_ExistsByHandle_MatchesEntityIndexResult()
        {
            using var env = CreateEnv();
            var a = env.Accessor;

            var handleA = a.AddEntity(Tag<QId1>.Value)
                .Set(new TestInt { Value = 1 })
                .Set(new TestFloat())
                .AssertComplete()
                .Handle;
            var handleB = a.AddEntity(Tag<QId1>.Value)
                .Set(new TestInt { Value = 2 })
                .Set(new TestFloat())
                .AssertComplete()
                .Handle;
            a.SubmitEntities();

            var set = a.Set<FiltOpTestSet>();
            set.Write.Add(handleA);

            // Each Read access syncs and returns a fresh view; check both handles
            // resolve to the right answers.
            NAssert.IsTrue(set.Read.Exists(handleA));
            NAssert.IsFalse(set.Read.Exists(handleB));
        }

        #endregion
    }
}
