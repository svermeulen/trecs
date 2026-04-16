using NUnit.Framework;
using NAssert = NUnit.Framework.Assert;

namespace Trecs.Tests
{
    /// <summary>
    /// Tests for edge cases in move+remove interactions during entity submission.
    /// </summary>
    [TestFixture]
    public class MoveRemoveEdgeCaseTests
    {
        static readonly TagSet PartitionASet = TagSet.FromTags(TestTags.Gamma, TestTags.PartitionA);
        static readonly TagSet PartitionBSet = TagSet.FromTags(TestTags.Gamma, TestTags.PartitionB);

        #region QueueMoveOperation missing _entitiesRemoved check

        [Test]
        public void RemoveThenMove_SameEntity_EntityIsRemoved()
        {
            // QueueMoveOperation (main-thread) doesn't check _entitiesRemoved,
            // so a remove-then-move sequence could leave the move un-reverted.
            // The entity should be removed (remove supersedes move).
            using var env = EcsTestHelper.CreateEnvironment(TestTemplates.WithPartitions);
            var a = env.Accessor;

            var init = a.AddEntity(PartitionASet)
                .Set(new TestInt { Value = 1 })
                .Set(new TestVec())
                .AssertComplete();
            var handle = init.Handle;
            a.SubmitEntities();

            NAssert.AreEqual(1, a.CountEntitiesWithTags(TestTags.Gamma));

            // Remove first, then move — remove should win
            a.RemoveEntity(handle.ToIndex(a));
            a.MoveTo(handle.ToIndex(a), PartitionBSet);
            a.SubmitEntities();

            NAssert.AreEqual(
                0,
                a.CountEntitiesWithTags(TestTags.Gamma),
                "Entity should be removed — remove supersedes move"
            );
        }

        [Test]
        public void MoveThenRemove_SameEntity_EntityIsRemoved()
        {
            // Move-then-remove: QueueRemoveOperation calls RevertMoveOperationIfPreviouslyQueued,
            // so the move is reverted and the entity is removed. This should work.
            using var env = EcsTestHelper.CreateEnvironment(TestTemplates.WithPartitions);
            var a = env.Accessor;

            var init = a.AddEntity(PartitionASet)
                .Set(new TestInt { Value = 1 })
                .Set(new TestVec())
                .AssertComplete();
            var handle = init.Handle;
            a.SubmitEntities();

            // Move first, then remove — remove should win
            a.MoveTo(handle.ToIndex(a), PartitionBSet);
            a.RemoveEntity(handle.ToIndex(a));
            a.SubmitEntities();

            NAssert.AreEqual(
                0,
                a.CountEntitiesWithTags(TestTags.Gamma),
                "Entity should be removed — remove reverts prior move"
            );
        }

        #endregion

        #region Move + remove in same group — swap-back invalidation

        [Test]
        public void MoveAndRemove_DifferentEntities_SameGroup_BothApplied()
        {
            // Entity A is moved out of group, entity B is removed from same group.
            // The move causes swap-back which could invalidate B's removal index.
            using var env = EcsTestHelper.CreateEnvironment(TestTemplates.WithPartitions);
            var a = env.Accessor;

            var handles = new EntityHandle[5];
            for (int i = 0; i < 5; i++)
            {
                handles[i] = a.AddEntity(PartitionASet)
                    .Set(new TestInt { Value = i })
                    .Set(new TestVec())
                    .AssertComplete()
                    .Handle;
            }
            a.SubmitEntities();

            NAssert.AreEqual(5, a.CountEntitiesWithTags(PartitionASet));

            // Move entity 1 to PartitionB, remove entity 4 (at the tail — will be swap-back source)
            a.MoveTo(handles[1].ToIndex(a), PartitionBSet);
            a.RemoveEntity(handles[4]);
            a.SubmitEntities();

            NAssert.AreEqual(
                3,
                a.CountEntitiesWithTags(PartitionASet),
                "PartitionA should have 3 entities (5 - 1 moved - 1 removed)"
            );
            NAssert.AreEqual(
                1,
                a.CountEntitiesWithTags(PartitionBSet),
                "PartitionB should have the 1 moved entity"
            );

            // Verify the moved entity has correct data
            var movedEntity = a.Query().WithTags(PartitionBSet).Single();
            NAssert.AreEqual(1, movedEntity.Get<TestInt>().Read.Value);

            // Verify removed entity no longer exists
            NAssert.IsFalse(a.EntityExists(handles[4]));
        }

        [Test]
        public void MoveAndRemove_TailEntityMovedAndOtherRemoved_DataIntact()
        {
            // Move entity at the tail, remove entity in the middle.
            // The tail entity is the swap-back source for the removal.
            using var env = EcsTestHelper.CreateEnvironment(TestTemplates.WithPartitions);
            var a = env.Accessor;

            var handles = new EntityHandle[5];
            for (int i = 0; i < 5; i++)
            {
                handles[i] = a.AddEntity(PartitionASet)
                    .Set(new TestInt { Value = i * 10 })
                    .Set(new TestVec())
                    .AssertComplete()
                    .Handle;
            }
            a.SubmitEntities();

            // Move tail entity (4) to PartitionB, remove middle entity (2)
            a.MoveTo(handles[4].ToIndex(a), PartitionBSet);
            a.RemoveEntity(handles[2]);
            a.SubmitEntities();

            NAssert.AreEqual(3, a.CountEntitiesWithTags(PartitionASet));
            NAssert.AreEqual(1, a.CountEntitiesWithTags(PartitionBSet));

            // All surviving entities should still be accessible
            NAssert.IsTrue(a.EntityExists(handles[0]));
            NAssert.IsTrue(a.EntityExists(handles[1]));
            NAssert.IsFalse(a.EntityExists(handles[2])); // removed
            NAssert.IsTrue(a.EntityExists(handles[3]));
            NAssert.IsTrue(a.EntityExists(handles[4])); // moved

            // Data should be intact
            NAssert.AreEqual(0, a.Component<TestInt>(handles[0]).Read.Value);
            NAssert.AreEqual(10, a.Component<TestInt>(handles[1]).Read.Value);
            NAssert.AreEqual(30, a.Component<TestInt>(handles[3]).Read.Value);
            NAssert.AreEqual(40, a.Component<TestInt>(handles[4]).Read.Value);
        }

        [Test]
        public void MoveAndRemove_MultipleMovesAndRemoves_SameGroup()
        {
            // Multiple entities moved and multiple removed from same group.
            using var env = EcsTestHelper.CreateEnvironment(TestTemplates.WithPartitions);
            var a = env.Accessor;

            var handles = new EntityHandle[10];
            for (int i = 0; i < 10; i++)
            {
                handles[i] = a.AddEntity(PartitionASet)
                    .Set(new TestInt { Value = i })
                    .Set(new TestVec())
                    .AssertComplete()
                    .Handle;
            }
            a.SubmitEntities();

            // Move entities 2, 5 to PartitionB. Remove entities 7, 8, 9 (tail entities).
            a.MoveTo(handles[2].ToIndex(a), PartitionBSet);
            a.MoveTo(handles[5].ToIndex(a), PartitionBSet);
            a.RemoveEntity(handles[7]);
            a.RemoveEntity(handles[8]);
            a.RemoveEntity(handles[9]);
            a.SubmitEntities();

            NAssert.AreEqual(5, a.CountEntitiesWithTags(PartitionASet));
            NAssert.AreEqual(2, a.CountEntitiesWithTags(PartitionBSet));

            // Verify all surviving entities are accessible and have correct data
            for (int i = 0; i < 10; i++)
            {
                if (i == 7 || i == 8 || i == 9)
                {
                    NAssert.IsFalse(a.EntityExists(handles[i]), $"Entity {i} should be removed");
                }
                else
                {
                    NAssert.IsTrue(a.EntityExists(handles[i]), $"Entity {i} should still exist");
                    NAssert.AreEqual(
                        i,
                        a.Component<TestInt>(handles[i]).Read.Value,
                        $"Entity {i} data should be intact"
                    );
                }
            }
        }

        #endregion

        #region Cascading removes across submission iterations

        [Test]
        public void RemoveCallback_RemovesAnotherEntity_BothRemoved()
        {
            // When entity A is removed and its OnRemoved callback removes entity B,
            // both should be properly removed across submission iterations.
            using var env = EcsTestHelper.CreateEnvironment(TestTemplates.SimpleAlpha);
            var a = env.Accessor;

            var handleA = a.AddEntity(TestTags.Alpha)
                .Set(new TestInt { Value = 1 })
                .AssertComplete()
                .Handle;
            var handleB = a.AddEntity(TestTags.Alpha)
                .Set(new TestInt { Value = 2 })
                .AssertComplete()
                .Handle;
            a.SubmitEntities();

            // Register callback: when any entity is removed, remove entity B
            var subscription = a
                .Events.InGroupsWithTags(TestTags.Alpha)
                .OnRemoved(
                    (group, indices) =>
                    {
                        if (a.EntityExists(handleB))
                        {
                            a.RemoveEntity(handleB);
                        }
                    }
                );

            a.RemoveEntity(handleA);
            a.SubmitEntities();

            NAssert.IsFalse(a.EntityExists(handleA), "Entity A should be removed");
            NAssert.IsFalse(a.EntityExists(handleB), "Entity B should be removed by callback");
            NAssert.AreEqual(0, a.CountEntitiesWithTags(TestTags.Alpha));

            subscription.Dispose();
        }

        [Test]
        public void RemoveCallback_SwapBackDoesNotCorruptSurvivors()
        {
            // Remove several entities. OnRemoved callback removes more.
            // Remaining entities should have correct data after all swap-backs.
            using var env = EcsTestHelper.CreateEnvironment(TestTemplates.SimpleAlpha);
            var a = env.Accessor;

            var handles = new EntityHandle[8];
            for (int i = 0; i < 8; i++)
            {
                handles[i] = a.AddEntity(TestTags.Alpha)
                    .Set(new TestInt { Value = i * 100 })
                    .AssertComplete()
                    .Handle;
            }
            a.SubmitEntities();

            // Callback: when entities are removed, also remove entity 6
            var subscription = a
                .Events.InGroupsWithTags(TestTags.Alpha)
                .OnRemoved(
                    (group, indices) =>
                    {
                        if (a.EntityExists(handles[6]))
                        {
                            a.RemoveEntity(handles[6]);
                        }
                    }
                );

            // Remove entities 0 and 3 directly
            a.RemoveEntity(handles[0]);
            a.RemoveEntity(handles[3]);
            a.SubmitEntities();

            // 0, 3, and 6 should be removed (6 by callback)
            NAssert.AreEqual(5, a.CountEntitiesWithTags(TestTags.Alpha));
            NAssert.IsFalse(a.EntityExists(handles[0]));
            NAssert.IsFalse(a.EntityExists(handles[3]));
            NAssert.IsFalse(a.EntityExists(handles[6]));

            // Surviving entities should have correct data
            NAssert.AreEqual(100, a.Component<TestInt>(handles[1]).Read.Value);
            NAssert.AreEqual(200, a.Component<TestInt>(handles[2]).Read.Value);
            NAssert.AreEqual(400, a.Component<TestInt>(handles[4]).Read.Value);
            NAssert.AreEqual(500, a.Component<TestInt>(handles[5]).Read.Value);
            NAssert.AreEqual(700, a.Component<TestInt>(handles[7]).Read.Value);

            subscription.Dispose();
        }

        #endregion
    }
}
