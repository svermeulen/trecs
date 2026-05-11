using System.Collections.Generic;
using NUnit.Framework;
using Trecs.Internal;
using NAssert = NUnit.Framework.Assert;

namespace Trecs.Tests
{
    [TestFixture]
    public class AdvancedEntityTests
    {
        static readonly TagSet PartitionA = TagSet.FromTags(TestTags.Gamma, TestTags.PartitionA);
        static readonly TagSet PartitionB = TagSet.FromTags(TestTags.Gamma, TestTags.PartitionB);

        #region Heavy Churn - Entity Ref Stability

        [Test]
        public void HeavyChurn_CreateDestroyCreate_RefsNeverCollide()
        {
            using var env = EcsTestHelper.CreateEnvironment(TestTemplates.SimpleAlpha);
            var a = env.Accessor;

            var allRefs = new HashSet<EntityHandle>();

            for (int round = 0; round < 10; round++)
            {
                var roundRefs = new EntityHandle[10];
                for (int i = 0; i < 10; i++)
                {
                    var init = a.AddEntity(TestTags.Alpha)
                        .Set(new TestInt { Value = round * 100 + i })
                        .AssertComplete();
                    roundRefs[i] = init.Handle;
                    NAssert.IsTrue(
                        allRefs.Add(roundRefs[i]),
                        "EntityHandle collision in round {0}, entity {1}",
                        round,
                        i
                    );
                }
                a.SubmitEntities();

                // Remove all entities
                for (int i = 0; i < 10; i++)
                {
                    a.RemoveEntity(roundRefs[i]);
                }
                a.SubmitEntities();
            }
        }

        [Test]
        public void HeavyChurn_InterleavedAddRemove_CountsCorrect()
        {
            using var env = EcsTestHelper.CreateEnvironment(TestTemplates.SimpleAlpha);
            var a = env.Accessor;

            var activeRefs = new List<EntityHandle>();

            for (int round = 0; round < 5; round++)
            {
                // Add 10 entities
                for (int i = 0; i < 10; i++)
                {
                    var init = a.AddEntity(TestTags.Alpha)
                        .Set(new TestInt { Value = round * 100 + i })
                        .AssertComplete();
                    activeRefs.Add(init.Handle);
                }
                a.SubmitEntities();

                // Remove half (every other)
                var toRemove = new List<EntityHandle>();
                for (int i = 0; i < activeRefs.Count; i += 2)
                {
                    toRemove.Add(activeRefs[i]);
                }
                foreach (var r in toRemove)
                {
                    a.RemoveEntity(r);
                    activeRefs.Remove(r);
                }
                a.SubmitEntities();
            }

            // Verify all remaining refs are valid and the count matches
            NAssert.AreEqual(activeRefs.Count, a.CountEntitiesWithTags(TestTags.Alpha));
            foreach (var r in activeRefs)
            {
                NAssert.IsTrue(a.EntityExists(r));
            }
        }

        #endregion

        #region GetSingleEntityIndex

        [Test]
        public void GetSingleEntityIndex_OneEntity_ReturnsCorrectIndex()
        {
            using var env = EcsTestHelper.CreateEnvironment(TestTemplates.SimpleAlpha);
            var a = env.Accessor;

            var init = a.AddEntity(TestTags.Alpha).Set(new TestInt { Value = 42 }).AssertComplete();
            a.SubmitEntities();

            var entity = a.Query().WithTags(TestTags.Alpha).Single();
            ref readonly var comp = ref entity.Get<TestInt>().Read;
            NAssert.AreEqual(42, comp.Value);
        }

        [Test]
        public void TryGetSingleEntityIndex_NoEntities_ReturnsFalse()
        {
            using var env = EcsTestHelper.CreateEnvironment(TestTemplates.SimpleAlpha);
            var a = env.Accessor;

            bool found = a.Query().WithTags(TestTags.Alpha).TrySingle(out _);
            NAssert.IsFalse(found);
        }

        [Test]
        public void TryGetSingleEntityIndex_OneEntity_ReturnsTrue()
        {
            using var env = EcsTestHelper.CreateEnvironment(TestTemplates.SimpleAlpha);
            var a = env.Accessor;

            a.AddEntity(TestTags.Alpha).Set(new TestInt { Value = 77 }).AssertComplete();
            a.SubmitEntities();

            bool found = a.Query().WithTags(TestTags.Alpha).TrySingle(out var entity);
            NAssert.IsTrue(found);
            NAssert.AreEqual(77, entity.Get<TestInt>().Read.Value);
        }

        #endregion

        #region QueryEntityIndices

        [Test]
        public void QueryEntityIndices_ReturnsAllEntities()
        {
            using var env = EcsTestHelper.CreateEnvironment(TestTemplates.SimpleAlpha);
            var a = env.Accessor;

            for (int i = 0; i < 5; i++)
            {
                a.AddEntity(TestTags.Alpha).Set(new TestInt { Value = i * 10 }).AssertComplete();
            }
            a.SubmitEntities();

            int count = 0;
            foreach (var ei in a.Query().WithTags(TestTags.Alpha).EntityIndices())
            {
                count++;
            }
            NAssert.AreEqual(5, count);
        }

        [Test]
        public void QueryEntityIndices_AcrossStates_ReturnsAll()
        {
            using var env = EcsTestHelper.CreateEnvironment(TestTemplates.WithPartitions);
            var a = env.Accessor;

            for (int i = 0; i < 3; i++)
            {
                a.AddEntity(PartitionA).AssertComplete();
            }
            for (int i = 0; i < 2; i++)
            {
                a.AddEntity(PartitionB).AssertComplete();
            }
            a.SubmitEntities();

            int count = 0;
            foreach (var ei in a.Query().WithTags(TestTags.Gamma).EntityIndices())
            {
                count++;
            }
            NAssert.AreEqual(5, count);
        }

        #endregion

        #region GetEntityHandle Round-trip

        [Test]
        public void GetEntityHandle_RoundTrip_Consistent()
        {
            using var env = EcsTestHelper.CreateEnvironment(TestTemplates.SimpleAlpha);
            var a = env.Accessor;

            var init = a.AddEntity(TestTags.Alpha)
                .Set(new TestInt { Value = 123 })
                .AssertComplete();
            var originalRef = init.Handle;
            a.SubmitEntities();

            // Ref -> Index -> Ref
            var entityIndex = originalRef.ToIndex(a);
            var roundTripRef = a.GetEntityHandle(entityIndex);
            NAssert.AreEqual(originalRef, roundTripRef);
        }

        [Test]
        public void GetEntityHandle_AfterMove_StillCorrect()
        {
            using var env = EcsTestHelper.CreateEnvironment(TestTemplates.WithPartitions);
            var a = env.Accessor;

            var init = a.AddEntity(PartitionA).Set(new TestInt { Value = 50 }).AssertComplete();
            var entityHandle = init.Handle;
            a.SubmitEntities();

            // Move to PartitionB
            a.SetTag<TestPartitionB>(entityHandle.ToIndex(a));
            a.SubmitEntities();

            // Get the new index and convert back to ref
            var newIndex = entityHandle.ToIndex(a);
            var roundTripRef = a.GetEntityHandle(newIndex);
            NAssert.AreEqual(entityHandle, roundTripRef);
        }

        #endregion

        #region Component Modification via Different Access Patterns

        [Test]
        public void ComponentModify_ViaQueryEntity_VisibleViaQueryEntitiesSingle()
        {
            using var env = EcsTestHelper.CreateEnvironment(TestTemplates.SimpleAlpha);
            var a = env.Accessor;

            a.AddEntity(TestTags.Alpha).Set(new TestInt { Value = 10 }).AssertComplete();
            a.SubmitEntities();

            // Modify via QueryEntity
            var group = a.WorldInfo.GetSingleGroupWithTags(TestTags.Alpha);
            a.Component<TestInt>(new EntityIndex(0, group)).Write.Value = 99;

            // Read via QueryEntitiesSingle
            var comp = a.Query().WithTags(TestTags.Alpha).Single().Get<TestInt>();
            NAssert.AreEqual(99, comp.Read.Value);
        }

        [Test]
        public void ComponentModify_ViaEntityHandle_VisibleViaEntityIndex()
        {
            using var env = EcsTestHelper.CreateEnvironment(TestTemplates.SimpleAlpha);
            var a = env.Accessor;

            var init = a.AddEntity(TestTags.Alpha).Set(new TestInt { Value = 10 }).AssertComplete();
            var entityHandle = init.Handle;
            a.SubmitEntities();

            // Modify via EntityHandle
            a.Component<TestInt>(entityHandle).Write.Value = 77;

            // Read via EntityIndex
            var group = a.WorldInfo.GetSingleGroupWithTags(TestTags.Alpha);
            var comp = a.Component<TestInt>(new EntityIndex(0, group));
            NAssert.AreEqual(77, comp.Read.Value);
        }

        [Test]
        public void ComponentModify_ViaEntityIndex_VisibleViaRef()
        {
            using var env = EcsTestHelper.CreateEnvironment(TestTemplates.SimpleAlpha);
            var a = env.Accessor;

            var init = a.AddEntity(TestTags.Alpha).Set(new TestInt { Value = 10 }).AssertComplete();
            var entityHandle = init.Handle;
            a.SubmitEntities();

            // Modify via EntityIndex write access
            var group = a.WorldInfo.GetSingleGroupWithTags(TestTags.Alpha);
            a.Component<TestInt>(new EntityIndex(0, group)).Write.Value = 55;

            // Read via EntityHandle
            NAssert.AreEqual(55, a.Component<TestInt>(entityHandle).Read.Value);
        }

        #endregion

        #region Tag-Based Removal Across Multiple Groups

        [Test]
        public void RemoveWithTags_AcrossMultipleGroups_RemovesAll()
        {
            using var env = EcsTestHelper.CreateEnvironment(TestTemplates.WithPartitions);
            var a = env.Accessor;

            for (int i = 0; i < 3; i++)
            {
                a.AddEntity(PartitionA).AssertComplete();
            }
            for (int i = 0; i < 2; i++)
            {
                a.AddEntity(PartitionB).AssertComplete();
            }
            a.SubmitEntities();

            NAssert.AreEqual(5, a.Query().WithTags(TestTags.Gamma).Count());

            // Remove all entities with Gamma tag (both PartitionA and PartitionB)
            a.RemoveEntitiesWithTags(TestTags.Gamma);
            a.SubmitEntities();

            NAssert.AreEqual(0, a.CountEntitiesWithTags(PartitionA));
            NAssert.AreEqual(0, a.CountEntitiesWithTags(PartitionB));
            NAssert.AreEqual(0, a.Query().WithTags(TestTags.Gamma).Count());
        }

        #endregion

        #region Observer AllEntities

        [Test]
        public void Observer_AllEntities_FiresForAnyGroup()
        {
            using var env = EcsTestHelper.CreateEnvironment(
                TestTemplates.SimpleAlpha,
                TestTemplates.TwoCompBeta
            );
            var a = env.Accessor;

            int callCount = 0;
            var sub = a
                .Events.AllEntities()
                .OnAdded(
                    (GroupIndex group, EntityRange indices) =>
                    {
                        callCount++;
                    }
                );

            a.AddEntity(TestTags.Alpha).AssertComplete();
            a.SubmitEntities();
            NAssert.AreEqual(1, callCount, "Should fire for Alpha group");

            a.AddEntity(TestTags.Beta).Set(new TestFloat { Value = 1.0f }).AssertComplete();
            a.SubmitEntities();
            NAssert.AreEqual(2, callCount, "Should fire for Beta group too");

            sub.Dispose();
        }

        #endregion

        #region Multiple Observer Subscriptions

        [Test]
        public void Observer_MultipleSubscriptions_AllFire()
        {
            using var env = EcsTestHelper.CreateEnvironment(TestTemplates.SimpleAlpha);
            var a = env.Accessor;

            int count1 = 0,
                count2 = 0,
                count3 = 0;

            var sub1 = a
                .Events.EntitiesWithTags(TestTags.Alpha)
                .OnAdded((GroupIndex g, EntityRange i) => count1++);
            var sub2 = a
                .Events.EntitiesWithTags(TestTags.Alpha)
                .OnAdded((GroupIndex g, EntityRange i) => count2++);
            var sub3 = a
                .Events.EntitiesWithTags(TestTags.Alpha)
                .OnAdded((GroupIndex g, EntityRange i) => count3++);

            a.AddEntity(TestTags.Alpha).AssertComplete();
            a.SubmitEntities();

            NAssert.AreEqual(1, count1);
            NAssert.AreEqual(1, count2);
            NAssert.AreEqual(1, count3);

            sub1.Dispose();
            sub2.Dispose();
            sub3.Dispose();
        }

        [Test]
        public void Observer_DisposeOne_OthersStillFire()
        {
            using var env = EcsTestHelper.CreateEnvironment(TestTemplates.SimpleAlpha);
            var a = env.Accessor;

            int count1 = 0,
                count2 = 0;

            var sub1 = a
                .Events.EntitiesWithTags(TestTags.Alpha)
                .OnAdded((GroupIndex g, EntityRange i) => count1++);
            var sub2 = a
                .Events.EntitiesWithTags(TestTags.Alpha)
                .OnAdded((GroupIndex g, EntityRange i) => count2++);

            a.AddEntity(TestTags.Alpha).AssertComplete();
            a.SubmitEntities();

            sub1.Dispose(); // Dispose first sub

            a.AddEntity(TestTags.Alpha).AssertComplete();
            a.SubmitEntities();

            NAssert.AreEqual(1, count1, "Disposed sub should not fire again");
            NAssert.AreEqual(2, count2, "Active sub should still fire");

            sub2.Dispose();
        }

        #endregion

        #region Query Consistency

        [Test]
        public void QueryCount_MatchesManualIterationCount()
        {
            using var env = EcsTestHelper.CreateEnvironment(TestTemplates.WithPartitions);
            var a = env.Accessor;

            for (int i = 0; i < 7; i++)
            {
                a.AddEntity(PartitionA).AssertComplete();
            }
            for (int i = 0; i < 3; i++)
            {
                a.AddEntity(PartitionB).AssertComplete();
            }
            a.SubmitEntities();

            int manualCount = 0;
            foreach (var _ in a.Query().WithTags(TestTags.Gamma).EntityIndices())
            {
                manualCount++;
            }

            int apiCount = a.Query().WithTags(TestTags.Gamma).Count();
            NAssert.AreEqual(
                manualCount,
                apiCount,
                "Query().Count() should match manual iteration count"
            );
            NAssert.AreEqual(10, apiCount);
        }

        [Test]
        public void QueryGroupSlices_TotalCountMatchesQueryCount()
        {
            using var env = EcsTestHelper.CreateEnvironment(TestTemplates.WithPartitions);
            var a = env.Accessor;

            for (int i = 0; i < 4; i++)
            {
                a.AddEntity(PartitionA).AssertComplete();
            }
            for (int i = 0; i < 6; i++)
            {
                a.AddEntity(PartitionB).AssertComplete();
            }
            a.SubmitEntities();

            int sliceTotal = 0;
            foreach (var slice in a.Query().WithTags(TestTags.Gamma).GroupSlices())
            {
                sliceTotal += (int)slice.Count;
            }

            int queryCount = a.Query().WithTags(TestTags.Gamma).Count();
            NAssert.AreEqual(queryCount, sliceTotal);
        }

        #endregion

        #region WorldInfo Template Queries

        [Test]
        public void WorldInfo_ResolvedTemplates_NonEmpty()
        {
            using var env = EcsTestHelper.CreateEnvironment(TestTemplates.SimpleAlpha);
            var a = env.Accessor;

            NAssert.IsNotNull(a.WorldInfo.AllTemplates);
            NAssert.Greater(a.WorldInfo.AllTemplates.Count, 0);
        }

        [Test]
        public void WorldInfo_GroupHasComponents_CorrectForTemplate()
        {
            using var env = EcsTestHelper.CreateEnvironment(TestTemplates.TwoCompBeta);
            var a = env.Accessor;

            var group = a.WorldInfo.GetSingleGroupWithTags(TestTags.Beta);
            NAssert.IsTrue(a.WorldInfo.GroupHasComponent<TestInt>(group));
            NAssert.IsTrue(a.WorldInfo.GroupHasComponent<TestFloat>(group));
        }

        #endregion
    }
}
