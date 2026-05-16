using System.Collections.Generic;
using NUnit.Framework;
using NAssert = NUnit.Framework.Assert;

namespace Trecs.Tests
{
    // Template with partitions for set+move testing
    public struct FMTag : ITag { }

    public struct FMPartitionA : ITag { }

    public struct FMPartitionB : ITag { }

    public partial class FMTestEntity
        : ITemplate,
            ITagged<FMTag>,
            IPartitionedBy<FMPartitionA, FMPartitionB>
    {
        TestInt TestInt;
        TestFloat TestFloat;
    }

    // Set scoped to the FMTag (valid for both partition groups)
    public struct FMSet : IEntitySet<FMTag> { }

    [TestFixture]
    public class SetMoveTests
    {
        static readonly TagSet FMPartitionASet = TagSet.FromTags(
            Tag<FMTag>.Value,
            Tag<FMPartitionA>.Value
        );
        static readonly TagSet FMPartitionBSet = TagSet.FromTags(
            Tag<FMTag>.Value,
            Tag<FMPartitionB>.Value
        );

        TestEnvironment CreateEnv() =>
            EcsTestHelper.CreateEnvironment(b => b.AddSet<FMSet>(), FMTestEntity.Template);

        #region Set Tracks Entity Through Move

        [Test]
        public void FilterMove_EntityMovedToNewState_StillInSet()
        {
            using var env = CreateEnv();
            var a = env.Accessor;

            var init = a.AddEntity(FMPartitionASet)
                .Set(new TestInt { Value = 42 })
                .Set(new TestFloat { Value = 1.0f })
                .AssertComplete();
            var entityHandle = init.Handle;
            a.SubmitEntities();

            var groupA = a.WorldInfo.GetSingleGroupWithTags(FMPartitionASet);
            var set = a.Set<FMSet>();

            set.Write.Add(new EntityIndex(0, groupA));
            a.SubmitEntities();

            NAssert.AreEqual(1, set.Read.Count);

            // Move entity to PartitionB
            a.SetTag<FMPartitionB>(entityHandle.ToIndex(a));
            a.SubmitEntities();

            // Entity should still be in the set, but now under the PartitionB group
            NAssert.AreEqual(
                1,
                set.Read.Count,
                "Entity should remain in set after move between partitions"
            );
        }

        [Test]
        public void FilterMove_EntityMovedToNewState_QueryThroughSetFindsIt()
        {
            using var env = CreateEnv();
            var a = env.Accessor;

            var init = a.AddEntity(FMPartitionASet)
                .Set(new TestInt { Value = 99 })
                .Set(new TestFloat())
                .AssertComplete();
            var entityHandle = init.Handle;
            a.SubmitEntities();

            var groupA = a.WorldInfo.GetSingleGroupWithTags(FMPartitionASet);
            var set = a.Set<FMSet>();

            set.Write.Add(new EntityIndex(0, groupA));
            a.SubmitEntities();

            // Move to PartitionB
            a.SetTag<FMPartitionB>(entityHandle.ToIndex(a));
            a.SubmitEntities();

            // Query through set should find the entity
            int queryCount = a.Query().InSet<FMSet>().Count();
            NAssert.AreEqual(1, queryCount, "Query through set should find moved entity");

            // Verify data is correct
            var results = new List<int>();
            foreach (var ei in a.Query().InSet<FMSet>().Indices())
            {
                results.Add(a.Component<TestInt>(ei).Read.Value);
            }
            NAssert.AreEqual(1, results.Count);
            NAssert.AreEqual(99, results[0]);
        }

        [Test]
        public void FilterMove_EntityNotInSet_StaysOutAfterMove()
        {
            using var env = CreateEnv();
            var a = env.Accessor;

            // Add 2 entities, only put entity 0 in set
            var init0 = a.AddEntity(FMPartitionASet)
                .Set(new TestInt { Value = 10 })
                .Set(new TestFloat())
                .AssertComplete();
            var init1 = a.AddEntity(FMPartitionASet)
                .Set(new TestInt { Value = 20 })
                .Set(new TestFloat())
                .AssertComplete();
            a.SubmitEntities();

            var groupA = a.WorldInfo.GetSingleGroupWithTags(FMPartitionASet);
            var set = a.Set<FMSet>();

            set.Write.Add(new EntityIndex(0, groupA));
            a.SubmitEntities();

            // Move entity 1 (NOT in set) to PartitionB
            a.SetTag<FMPartitionB>(init1.Handle.ToIndex(a));
            a.SubmitEntities();

            // Set should still have exactly 1 entity
            NAssert.AreEqual(1, set.Read.Count);
            NAssert.AreEqual(1, a.Query().InSet<FMSet>().Count());
        }

        [Test]
        public void FilterMove_MoveMultiple_SetCountPreserved()
        {
            using var env = CreateEnv();
            var a = env.Accessor;

            var refs = new EntityHandle[6];
            for (int i = 0; i < 6; i++)
            {
                var init = a.AddEntity(FMPartitionASet)
                    .Set(new TestInt { Value = i * 10 })
                    .Set(new TestFloat())
                    .AssertComplete();
                refs[i] = init.Handle;
            }
            a.SubmitEntities();

            var groupA = a.WorldInfo.GetSingleGroupWithTags(FMPartitionASet);
            var set = a.Set<FMSet>();

            // Add entities 0, 2, 4 to set
            var write = set.Write;
            write.Add(new EntityIndex(0, groupA));
            write.Add(new EntityIndex(2, groupA));
            write.Add(new EntityIndex(4, groupA));
            a.SubmitEntities();
            NAssert.AreEqual(3, set.Read.Count);

            // Move entities 0 and 2 (in set) plus entity 1 (not in set) to PartitionB
            a.SetTag<FMPartitionB>(refs[0].ToIndex(a));
            a.SetTag<FMPartitionB>(refs[1].ToIndex(a));
            a.SetTag<FMPartitionB>(refs[2].ToIndex(a));
            a.SubmitEntities();

            // Set should still have 3 entities (0 and 2 moved but still tracked, 4 stayed)
            NAssert.AreEqual(3, set.Read.Count, "All 3 set entities should be tracked after moves");

            // Verify query through set gets correct values
            var values = new List<int>();
            foreach (var ei in a.Query().InSet<FMSet>().Indices())
            {
                values.Add(a.Component<TestInt>(ei).Read.Value);
            }
            values.Sort();
            NAssert.AreEqual(3, values.Count);
            NAssert.Contains(0, values);
            NAssert.Contains(20, values);
            NAssert.Contains(40, values);
        }

        #endregion

        #region Set + Move + Remove Combined

        [Test]
        public void FilterMoveRemove_MoveOneRemoveAnother_SetCorrect()
        {
            using var env = CreateEnv();
            var a = env.Accessor;

            var refs = new EntityHandle[3];
            for (int i = 0; i < 3; i++)
            {
                var init = a.AddEntity(FMPartitionASet)
                    .Set(new TestInt { Value = i * 10 })
                    .Set(new TestFloat())
                    .AssertComplete();
                refs[i] = init.Handle;
            }
            a.SubmitEntities();

            var groupA = a.WorldInfo.GetSingleGroupWithTags(FMPartitionASet);
            var set = a.Set<FMSet>();

            // Add all 3 to set
            var write = set.Write;
            write.Add(new EntityIndex(0, groupA));
            write.Add(new EntityIndex(1, groupA));
            write.Add(new EntityIndex(2, groupA));
            a.SubmitEntities();
            NAssert.AreEqual(3, set.Read.Count);

            // Move entity 0 to PartitionB, remove entity 1
            a.SetTag<FMPartitionB>(refs[0].ToIndex(a));
            a.RemoveEntity(refs[1]);
            a.SubmitEntities();

            // Entity 0: moved (still in set at new group)
            // Entity 1: removed (gone from set)
            // Entity 2: stayed (still in set at possibly new index due to swap-back)
            NAssert.AreEqual(2, set.Read.Count, "Should have 2 entities in set after move+remove");
            NAssert.AreEqual(2, a.Query().InSet<FMSet>().Count());
        }

        [Test]
        public void FilterMoveRemove_RemoveSetMoveUnfiltered_SetCorrect()
        {
            using var env = CreateEnv();
            var a = env.Accessor;

            var refs = new EntityHandle[4];
            for (int i = 0; i < 4; i++)
            {
                var init = a.AddEntity(FMPartitionASet)
                    .Set(new TestInt { Value = i * 10 })
                    .Set(new TestFloat())
                    .AssertComplete();
                refs[i] = init.Handle;
            }
            a.SubmitEntities();

            var groupA = a.WorldInfo.GetSingleGroupWithTags(FMPartitionASet);
            var set = a.Set<FMSet>();

            // Only entity 0 and 2 in set
            var write = set.Write;
            write.Add(new EntityIndex(0, groupA));
            write.Add(new EntityIndex(2, groupA));
            a.SubmitEntities();

            // Remove entity 0 (in set), move entity 1 (NOT in set) to PartitionB
            a.RemoveEntity(refs[0]);
            a.SetTag<FMPartitionB>(refs[1].ToIndex(a));
            a.SubmitEntities();

            // Entity 0 removed -> set loses it
            // Entity 1 moved but wasn't in set -> set unchanged for it
            // Entity 2 still in set (at possibly new index)
            NAssert.AreEqual(1, set.Read.Count, "Only entity 2 should remain in set");
        }

        #endregion

        #region Set + Move Back and Forth

        [Test]
        public void FilterMoveBackForth_EntityTrackedThroughMultipleMoves()
        {
            using var env = CreateEnv();
            var a = env.Accessor;

            var init = a.AddEntity(FMPartitionASet)
                .Set(new TestInt { Value = 77 })
                .Set(new TestFloat())
                .AssertComplete();
            var entityHandle = init.Handle;
            a.SubmitEntities();

            var groupA = a.WorldInfo.GetSingleGroupWithTags(FMPartitionASet);
            var set = a.Set<FMSet>();

            set.Write.Add(new EntityIndex(0, groupA));
            a.SubmitEntities();
            NAssert.AreEqual(1, set.Read.Count);

            // Move A -> B
            a.SetTag<FMPartitionB>(entityHandle.ToIndex(a));
            a.SubmitEntities();
            NAssert.AreEqual(1, set.Read.Count, "After A->B");

            // Move B -> A
            a.SetTag<FMPartitionA>(entityHandle.ToIndex(a));
            a.SubmitEntities();
            NAssert.AreEqual(1, set.Read.Count, "After B->A");

            // Move A -> B again
            a.SetTag<FMPartitionB>(entityHandle.ToIndex(a));
            a.SubmitEntities();
            NAssert.AreEqual(1, set.Read.Count, "After A->B again");

            // Verify data is still correct
            var comp = a.Component<TestInt>(entityHandle);
            NAssert.AreEqual(77, comp.Read.Value);
        }

        #endregion
    }
}
