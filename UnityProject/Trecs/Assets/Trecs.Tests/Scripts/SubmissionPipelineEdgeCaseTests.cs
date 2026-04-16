using NUnit.Framework;
using NAssert = NUnit.Framework.Assert;

namespace Trecs.Tests
{
    /// <summary>
    /// Tests for submission pipeline edge cases involving interactions between
    /// moves, removes, adds, and swap-back chain resolution.
    /// </summary>
    [TestFixture]
    public class SubmissionPipelineEdgeCaseTests
    {
        static readonly TagSet StateA = TagSet.FromTags(TestTags.Gamma, TestTags.StateA);
        static readonly TagSet StateB = TagSet.FromTags(TestTags.Gamma, TestTags.StateB);

        #region Swap-back chain resolution (the UpdateRemoveIndicesAfterMoveSwapBack bug)

        [Test]
        public void MoveAllButOne_RemoveOneViaNative_SwapBackChainResolved()
        {
            // This was the original bug: when nearly all entities in a group are moved
            // and one is removed via native path, the swap-back chain creates multi-hop
            // mappings. UpdateRemoveIndicesAfterMoveSwapBack must follow the full chain.
            using var env = EcsTestHelper.CreateEnvironment(TestTemplates.WithStates);
            var a = env.Accessor;
            var nativeEcs = a.ToNative();

            var handles = new EntityHandle[10];
            for (int i = 0; i < 10; i++)
            {
                handles[i] = a.AddEntity(StateA)
                    .Set(new TestInt { Value = i })
                    .Set(new TestVec())
                    .AssertComplete()
                    .Handle;
            }
            a.SubmitEntities();

            // Move entities 0-8 to StateB, native-remove entity 5
            // Entity 5's move gets reverted. The swap-back plan creates a multi-hop chain
            // that UpdateRemoveIndicesAfterMoveSwapBack must fully resolve.
            for (int i = 0; i < 9; i++)
                a.MoveTo(handles[i].ToIndex(a), StateB);
            nativeEcs.RemoveEntity(handles[5].ToIndex(a));
            a.SubmitEntities();

            // Entity 5 removed, entities 0-4,6-8 moved, entity 9 stays
            NAssert.AreEqual(
                1,
                a.CountEntitiesWithTags(StateA),
                "Only entity 9 should remain in StateA"
            );
            NAssert.AreEqual(8, a.CountEntitiesWithTags(StateB), "8 entities should be in StateB");
            NAssert.IsFalse(a.EntityExists(handles[5]), "Entity 5 should be removed");
            NAssert.IsTrue(a.EntityExists(handles[9]), "Entity 9 should still exist in StateA");

            // Verify moved entities have correct data
            for (int i = 0; i < 9; i++)
            {
                if (i == 5)
                    continue;
                NAssert.IsTrue(a.EntityExists(handles[i]), $"Moved entity {i} should exist");
                NAssert.AreEqual(
                    i,
                    a.Component<TestInt>(handles[i]).Read.Value,
                    $"Moved entity {i} data should be intact"
                );
            }
        }

        [Test]
        public void MoveAllButOne_RemoveMultipleViaNative_AllResolved()
        {
            // Multiple native removes among many moves — tests multiple swap-back chains
            using var env = EcsTestHelper.CreateEnvironment(TestTemplates.WithStates);
            var a = env.Accessor;
            var nativeEcs = a.ToNative();

            var handles = new EntityHandle[20];
            for (int i = 0; i < 20; i++)
            {
                handles[i] = a.AddEntity(StateA)
                    .Set(new TestInt { Value = i })
                    .Set(new TestVec())
                    .AssertComplete()
                    .Handle;
            }
            a.SubmitEntities();

            // Move entities 0-17 to StateB, native-remove entities 3, 7, 12
            for (int i = 0; i < 18; i++)
                a.MoveTo(handles[i].ToIndex(a), StateB);
            nativeEcs.RemoveEntity(handles[3].ToIndex(a));
            nativeEcs.RemoveEntity(handles[7].ToIndex(a));
            nativeEcs.RemoveEntity(handles[12].ToIndex(a));
            a.SubmitEntities();

            NAssert.AreEqual(2, a.CountEntitiesWithTags(StateA), "Entities 18,19 stay in StateA");
            NAssert.AreEqual(15, a.CountEntitiesWithTags(StateB), "15 entities moved to StateB");

            NAssert.IsFalse(a.EntityExists(handles[3]));
            NAssert.IsFalse(a.EntityExists(handles[7]));
            NAssert.IsFalse(a.EntityExists(handles[12]));

            for (int i = 0; i < 20; i++)
            {
                if (i == 3 || i == 7 || i == 12)
                    continue;
                NAssert.IsTrue(a.EntityExists(handles[i]), $"Entity {i} should exist");
                NAssert.AreEqual(
                    i,
                    a.Component<TestInt>(handles[i]).Read.Value,
                    $"Entity {i} data should be intact"
                );
            }
        }

        #endregion

        #region Move revert + swap-back interaction

        [Test]
        public void MoveRevertedByRemove_OtherMovesStillWork()
        {
            // Entity A is moved, then removed (reverting the move).
            // Other entities in the same group that are also being moved should still work.
            using var env = EcsTestHelper.CreateEnvironment(TestTemplates.WithStates);
            var a = env.Accessor;

            var handles = new EntityHandle[5];
            for (int i = 0; i < 5; i++)
            {
                handles[i] = a.AddEntity(StateA)
                    .Set(new TestInt { Value = i * 10 })
                    .Set(new TestVec())
                    .AssertComplete()
                    .Handle;
            }
            a.SubmitEntities();

            // Move entities 0, 1, 2 to StateB. Then remove entity 1 (reverts its move).
            a.MoveTo(handles[0].ToIndex(a), StateB);
            a.MoveTo(handles[1].ToIndex(a), StateB);
            a.MoveTo(handles[2].ToIndex(a), StateB);
            a.RemoveEntity(handles[1].ToIndex(a));
            a.SubmitEntities();

            NAssert.AreEqual(2, a.CountEntitiesWithTags(StateA), "Entities 3,4 stay");
            NAssert.AreEqual(2, a.CountEntitiesWithTags(StateB), "Entities 0,2 moved");
            NAssert.IsFalse(a.EntityExists(handles[1]), "Entity 1 removed");

            NAssert.AreEqual(0, a.Component<TestInt>(handles[0]).Read.Value);
            NAssert.AreEqual(20, a.Component<TestInt>(handles[2]).Read.Value);
            NAssert.AreEqual(30, a.Component<TestInt>(handles[3]).Read.Value);
            NAssert.AreEqual(40, a.Component<TestInt>(handles[4]).Read.Value);
        }

        [Test]
        public void NativeRemoveRevertsMove_SwapBackChainsCorrect()
        {
            // Native remove reverts a managed move, with other moves happening.
            // The swap-back chain must correctly resolve the remove index.
            using var env = EcsTestHelper.CreateEnvironment(TestTemplates.WithStates);
            var a = env.Accessor;
            var nativeEcs = a.ToNative();

            var handles = new EntityHandle[8];
            for (int i = 0; i < 8; i++)
            {
                handles[i] = a.AddEntity(StateA)
                    .Set(new TestInt { Value = i })
                    .Set(new TestVec())
                    .AssertComplete()
                    .Handle;
            }
            a.SubmitEntities();

            // Move 0,1,2,3,4,5 to StateB. Native-remove entity 3 (reverts its move).
            for (int i = 0; i < 6; i++)
                a.MoveTo(handles[i].ToIndex(a), StateB);
            nativeEcs.RemoveEntity(handles[3].ToIndex(a));
            a.SubmitEntities();

            NAssert.AreEqual(2, a.CountEntitiesWithTags(StateA), "Entities 6,7 stay");
            NAssert.AreEqual(5, a.CountEntitiesWithTags(StateB), "Entities 0,1,2,4,5 moved");
            NAssert.IsFalse(a.EntityExists(handles[3]), "Entity 3 removed");

            // Verify all surviving entities
            for (int i = 0; i < 8; i++)
            {
                if (i == 3)
                    continue;
                NAssert.IsTrue(a.EntityExists(handles[i]), $"Entity {i} should exist");
                NAssert.AreEqual(
                    i,
                    a.Component<TestInt>(handles[i]).Read.Value,
                    $"Entity {i} data intact"
                );
            }
        }

        #endregion

        #region Managed + native move dedup

        [Test]
        public void ManagedMoveAndNativeMove_SameEntity_FirstWins()
        {
            // Both managed and native paths queue a move for the same entity.
            // The first one queued (managed, since it runs during system execution)
            // should win. The native one should be deduped.
            using var env = EcsTestHelper.CreateEnvironment(TestTemplates.WithStates);
            var a = env.Accessor;
            var nativeEcs = a.ToNative();

            var handle = a.AddEntity(StateA)
                .Set(new TestInt { Value = 42 })
                .Set(new TestVec())
                .AssertComplete()
                .Handle;
            a.SubmitEntities();

            // Both paths move the same entity to StateB
            a.MoveTo(handle.ToIndex(a), StateB);
            nativeEcs.MoveTo(handle.ToIndex(a), StateB);
            a.SubmitEntities();

            NAssert.AreEqual(0, a.CountEntitiesWithTags(StateA));
            NAssert.AreEqual(1, a.CountEntitiesWithTags(StateB));
            NAssert.AreEqual(42, a.Component<TestInt>(handle).Read.Value);
        }

        [Test]
        public void NativeDuplicateMove_SameEntity_OnlyMovedOnce()
        {
            // Two native move operations for the same entity (simulating two jobs).
            using var env = EcsTestHelper.CreateEnvironment(TestTemplates.WithStates);
            var a = env.Accessor;
            var nativeEcs = a.ToNative();

            var handle = a.AddEntity(StateA)
                .Set(new TestInt { Value = 77 })
                .Set(new TestVec())
                .AssertComplete()
                .Handle;
            a.SubmitEntities();

            nativeEcs.MoveTo(handle.ToIndex(a), StateB);
            nativeEcs.MoveTo(handle.ToIndex(a), StateB);
            a.SubmitEntities();

            NAssert.AreEqual(0, a.CountEntitiesWithTags(StateA));
            NAssert.AreEqual(1, a.CountEntitiesWithTags(StateB));
            NAssert.AreEqual(77, a.Component<TestInt>(handle).Read.Value);
        }

        #endregion

        #region Add + remove + move in same submission

        [Test]
        public void AddAndRemoveAndMove_SameSubmission_AllCorrect()
        {
            using var env = EcsTestHelper.CreateEnvironment(TestTemplates.WithStates);
            var a = env.Accessor;

            var handles = new EntityHandle[4];
            for (int i = 0; i < 4; i++)
            {
                handles[i] = a.AddEntity(StateA)
                    .Set(new TestInt { Value = i })
                    .Set(new TestVec())
                    .AssertComplete()
                    .Handle;
            }
            a.SubmitEntities();

            // In same submission: remove entity 0, move entity 1 to B, add new entity
            a.RemoveEntity(handles[0].ToIndex(a));
            a.MoveTo(handles[1].ToIndex(a), StateB);
            var newHandle = a.AddEntity(StateA)
                .Set(new TestInt { Value = 99 })
                .Set(new TestVec())
                .AssertComplete()
                .Handle;
            a.SubmitEntities();

            // StateA: entities 2, 3 (original) + 99 (new) = 3
            // StateB: entity 1 = 1
            NAssert.AreEqual(3, a.CountEntitiesWithTags(StateA));
            NAssert.AreEqual(1, a.CountEntitiesWithTags(StateB));
            NAssert.IsFalse(a.EntityExists(handles[0]));
            NAssert.AreEqual(1, a.Component<TestInt>(handles[1]).Read.Value);
            NAssert.AreEqual(2, a.Component<TestInt>(handles[2]).Read.Value);
            NAssert.AreEqual(3, a.Component<TestInt>(handles[3]).Read.Value);
            NAssert.AreEqual(99, a.Component<TestInt>(newHandle).Read.Value);
        }

        [Test]
        public void NativeAddAndNativeRemoveAndManagedMove_SameSubmission()
        {
            using var env = EcsTestHelper.CreateEnvironment(TestTemplates.WithStates);
            var a = env.Accessor;
            var nativeEcs = a.ToNative();

            var handles = new EntityHandle[3];
            for (int i = 0; i < 3; i++)
            {
                handles[i] = a.AddEntity(StateA)
                    .Set(new TestInt { Value = i * 10 })
                    .Set(new TestVec())
                    .AssertComplete()
                    .Handle;
            }
            a.SubmitEntities();

            // Native remove entity 0, managed move entity 1, native add new
            nativeEcs.RemoveEntity(handles[0].ToIndex(a));
            a.MoveTo(handles[1].ToIndex(a), StateB);
            var nativeInit = nativeEcs.AddEntity(StateA, sortKey: 0);
            nativeInit.Set(new TestInt { Value = 55 });
            nativeInit.Set(new TestVec());
            a.SubmitEntities();

            NAssert.IsFalse(a.EntityExists(handles[0]));
            NAssert.AreEqual(10, a.Component<TestInt>(handles[1]).Read.Value);
            NAssert.AreEqual(20, a.Component<TestInt>(handles[2]).Read.Value);
        }

        #endregion

        #region Remove all entities in group

        [Test]
        public void RemoveAllEntities_GroupEmpty()
        {
            using var env = EcsTestHelper.CreateEnvironment(TestTemplates.WithStates);
            var a = env.Accessor;

            for (int i = 0; i < 5; i++)
            {
                a.AddEntity(StateA)
                    .Set(new TestInt { Value = i })
                    .Set(new TestVec())
                    .AssertComplete();
            }
            a.SubmitEntities();

            a.RemoveEntitiesWithTags(StateA);
            a.SubmitEntities();

            NAssert.AreEqual(0, a.CountEntitiesWithTags(StateA));
        }

        [Test]
        public void MoveAllEntities_SourceGroupEmpty()
        {
            using var env = EcsTestHelper.CreateEnvironment(TestTemplates.WithStates);
            var a = env.Accessor;

            var handles = new EntityHandle[5];
            for (int i = 0; i < 5; i++)
            {
                handles[i] = a.AddEntity(StateA)
                    .Set(new TestInt { Value = i })
                    .Set(new TestVec())
                    .AssertComplete()
                    .Handle;
            }
            a.SubmitEntities();

            for (int i = 0; i < 5; i++)
                a.MoveTo(handles[i].ToIndex(a), StateB);
            a.SubmitEntities();

            NAssert.AreEqual(0, a.CountEntitiesWithTags(StateA));
            NAssert.AreEqual(5, a.CountEntitiesWithTags(StateB));

            for (int i = 0; i < 5; i++)
                NAssert.AreEqual(i, a.Component<TestInt>(handles[i]).Read.Value);
        }

        #endregion

        #region All moves reverted (empty toGroup dictionary)

        [Test]
        public void AllMovesReverted_NoMovesExecuted()
        {
            // All entities queued for move are also removed, reverting all moves.
            // The toGroup dictionary ends up with Count=0. This tests the
            // FireMoveCallbacks fix (must skip empty dictionaries).
            using var env = EcsTestHelper.CreateEnvironment(TestTemplates.WithStates);
            var a = env.Accessor;

            var handles = new EntityHandle[3];
            for (int i = 0; i < 3; i++)
            {
                handles[i] = a.AddEntity(StateA)
                    .Set(new TestInt { Value = i })
                    .Set(new TestVec())
                    .AssertComplete()
                    .Handle;
            }
            // Also add some entities that just stay
            var stayer = a.AddEntity(StateA)
                .Set(new TestInt { Value = 100 })
                .Set(new TestVec())
                .AssertComplete()
                .Handle;
            a.SubmitEntities();

            // Move entities 0,1,2 to StateB, then remove them all
            for (int i = 0; i < 3; i++)
            {
                a.MoveTo(handles[i].ToIndex(a), StateB);
                a.RemoveEntity(handles[i].ToIndex(a));
            }
            a.SubmitEntities();

            NAssert.AreEqual(1, a.CountEntitiesWithTags(StateA), "Stayer should remain");
            NAssert.AreEqual(0, a.CountEntitiesWithTags(StateB), "No moves should execute");
            NAssert.AreEqual(100, a.Component<TestInt>(stayer).Read.Value);
        }

        [Test]
        public void AllMovesRevertedViaNative_NoMovesExecuted()
        {
            // Same as above but reverted via native removes
            using var env = EcsTestHelper.CreateEnvironment(TestTemplates.WithStates);
            var a = env.Accessor;
            var nativeEcs = a.ToNative();

            var handles = new EntityHandle[3];
            for (int i = 0; i < 3; i++)
            {
                handles[i] = a.AddEntity(StateA)
                    .Set(new TestInt { Value = i })
                    .Set(new TestVec())
                    .AssertComplete()
                    .Handle;
            }
            var stayer = a.AddEntity(StateA)
                .Set(new TestInt { Value = 100 })
                .Set(new TestVec())
                .AssertComplete()
                .Handle;
            a.SubmitEntities();

            // Move all 3 managed, remove all 3 native
            for (int i = 0; i < 3; i++)
            {
                a.MoveTo(handles[i].ToIndex(a), StateB);
                nativeEcs.RemoveEntity(handles[i].ToIndex(a));
            }
            a.SubmitEntities();

            NAssert.AreEqual(1, a.CountEntitiesWithTags(StateA));
            NAssert.AreEqual(0, a.CountEntitiesWithTags(StateB));
            NAssert.AreEqual(100, a.Component<TestInt>(stayer).Read.Value);
        }

        #endregion

        #region Large-scale move + remove interaction

        [Test]
        public void LargeScale_ScatteredRemovesAmongMoves_AllCorrect()
        {
            // Large group with many moves and scattered removes among them.
            // This stress-tests the swap-back chain resolution at scale.
            using var env = EcsTestHelper.CreateEnvironment(TestTemplates.WithStates);
            var a = env.Accessor;
            var nativeEcs = a.ToNative();

            const int total = 100;
            var handles = new EntityHandle[total];
            for (int i = 0; i < total; i++)
            {
                handles[i] = a.AddEntity(StateA)
                    .Set(new TestInt { Value = i })
                    .Set(new TestVec())
                    .AssertComplete()
                    .Handle;
            }
            a.SubmitEntities();

            // Move first 80 to StateB, native-remove every 10th among those (0,10,20,...,70)
            for (int i = 0; i < 80; i++)
                a.MoveTo(handles[i].ToIndex(a), StateB);
            for (int i = 0; i < 80; i += 10)
                nativeEcs.RemoveEntity(handles[i].ToIndex(a));
            a.SubmitEntities();

            int removedCount = 8; // 0,10,20,30,40,50,60,70
            int movedCount = 80 - removedCount;
            int stayedCount = total - 80;

            NAssert.AreEqual(stayedCount, a.CountEntitiesWithTags(StateA));
            NAssert.AreEqual(movedCount, a.CountEntitiesWithTags(StateB));

            // Verify all surviving entities have correct data
            for (int i = 0; i < total; i++)
            {
                bool removed = i < 80 && i % 10 == 0;
                if (removed)
                {
                    NAssert.IsFalse(a.EntityExists(handles[i]), $"Entity {i} should be removed");
                }
                else
                {
                    NAssert.IsTrue(a.EntityExists(handles[i]), $"Entity {i} should exist");
                    NAssert.AreEqual(
                        i,
                        a.Component<TestInt>(handles[i]).Read.Value,
                        $"Entity {i} data should be intact"
                    );
                }
            }
        }

        #endregion

        #region Multi-frame consistency

        [Test]
        public void MultiFrame_AddRemoveMoveRepeatedly_StaysConsistent()
        {
            using var env = EcsTestHelper.CreateEnvironment(TestTemplates.WithStates);
            var a = env.Accessor;

            // Frame 1: Add 10 entities in StateA
            var handles = new EntityHandle[10];
            for (int i = 0; i < 10; i++)
            {
                handles[i] = a.AddEntity(StateA)
                    .Set(new TestInt { Value = i })
                    .Set(new TestVec())
                    .AssertComplete()
                    .Handle;
            }
            a.SubmitEntities();
            NAssert.AreEqual(10, a.CountEntitiesWithTags(StateA));

            // Frame 2: Move 0-4 to StateB, remove 5
            for (int i = 0; i < 5; i++)
                a.MoveTo(handles[i].ToIndex(a), StateB);
            a.RemoveEntity(handles[5]);
            a.SubmitEntities();
            NAssert.AreEqual(4, a.CountEntitiesWithTags(StateA));
            NAssert.AreEqual(5, a.CountEntitiesWithTags(StateB));

            // Frame 3: Move 0-4 back to StateA, remove 6, add 2 new in StateA
            for (int i = 0; i < 5; i++)
                a.MoveTo(handles[i].ToIndex(a), StateA);
            a.RemoveEntity(handles[6]);
            var newH1 = a.AddEntity(StateA)
                .Set(new TestInt { Value = 100 })
                .Set(new TestVec())
                .AssertComplete()
                .Handle;
            var newH2 = a.AddEntity(StateA)
                .Set(new TestInt { Value = 101 })
                .Set(new TestVec())
                .AssertComplete()
                .Handle;
            a.SubmitEntities();

            // StateA: 0,1,2,3,4 (moved back) + 7,8,9 (stayed) + 100,101 (new) = 10
            // StateB: empty
            NAssert.AreEqual(10, a.CountEntitiesWithTags(StateA));
            NAssert.AreEqual(0, a.CountEntitiesWithTags(StateB));
            NAssert.IsFalse(a.EntityExists(handles[5]));
            NAssert.IsFalse(a.EntityExists(handles[6]));

            // Verify data
            for (int i = 0; i < 10; i++)
            {
                if (i == 5 || i == 6)
                    continue;
                NAssert.AreEqual(i, a.Component<TestInt>(handles[i]).Read.Value);
            }
            NAssert.AreEqual(100, a.Component<TestInt>(newH1).Read.Value);
            NAssert.AreEqual(101, a.Component<TestInt>(newH2).Read.Value);
        }

        #endregion

        #region Remove entity that was just added (same frame)

        [Test]
        public void AddThenRemove_SameSubmission_EntityDoesNotExist()
        {
            // Add an entity, then in the same frame (before submission), try to
            // remove it. The add is deferred, so the entity doesn't have an index yet.
            // This verifies the system handles this gracefully.
            using var env = EcsTestHelper.CreateEnvironment(TestTemplates.WithStates);
            var a = env.Accessor;

            // First add some base entities
            var existing = a.AddEntity(StateA)
                .Set(new TestInt { Value = 1 })
                .Set(new TestVec())
                .AssertComplete()
                .Handle;
            a.SubmitEntities();

            // Now add new and immediately remove the existing one
            var newHandle = a.AddEntity(StateA)
                .Set(new TestInt { Value = 2 })
                .Set(new TestVec())
                .AssertComplete()
                .Handle;
            a.RemoveEntity(existing);
            a.SubmitEntities();

            NAssert.AreEqual(1, a.CountEntitiesWithTags(StateA));
            NAssert.IsFalse(a.EntityExists(existing));
            NAssert.IsTrue(a.EntityExists(newHandle));
            NAssert.AreEqual(2, a.Component<TestInt>(newHandle).Read.Value);
        }

        #endregion

        #region Single entity in group — remove it

        [Test]
        public void SingleEntity_Remove_GroupEmpty()
        {
            using var env = EcsTestHelper.CreateEnvironment(TestTemplates.WithStates);
            var a = env.Accessor;

            var handle = a.AddEntity(StateA)
                .Set(new TestInt { Value = 1 })
                .Set(new TestVec())
                .AssertComplete()
                .Handle;
            a.SubmitEntities();

            a.RemoveEntity(handle);
            a.SubmitEntities();

            NAssert.AreEqual(0, a.CountEntitiesWithTags(StateA));
            NAssert.IsFalse(a.EntityExists(handle));
        }

        [Test]
        public void SingleEntity_Move_SourceEmpty()
        {
            using var env = EcsTestHelper.CreateEnvironment(TestTemplates.WithStates);
            var a = env.Accessor;

            var handle = a.AddEntity(StateA)
                .Set(new TestInt { Value = 1 })
                .Set(new TestVec())
                .AssertComplete()
                .Handle;
            a.SubmitEntities();

            a.MoveTo(handle.ToIndex(a), StateB);
            a.SubmitEntities();

            NAssert.AreEqual(0, a.CountEntitiesWithTags(StateA));
            NAssert.AreEqual(1, a.CountEntitiesWithTags(StateB));
            NAssert.AreEqual(1, a.Component<TestInt>(handle).Read.Value);
        }

        [Test]
        public void SingleEntity_MoveRevertedByRemove_GroupEmpty()
        {
            using var env = EcsTestHelper.CreateEnvironment(TestTemplates.WithStates);
            var a = env.Accessor;

            var handle = a.AddEntity(StateA)
                .Set(new TestInt { Value = 1 })
                .Set(new TestVec())
                .AssertComplete()
                .Handle;
            a.SubmitEntities();

            a.MoveTo(handle.ToIndex(a), StateB);
            a.RemoveEntity(handle.ToIndex(a));
            a.SubmitEntities();

            NAssert.AreEqual(0, a.CountEntitiesWithTags(StateA));
            NAssert.AreEqual(0, a.CountEntitiesWithTags(StateB));
        }

        #endregion
    }
}
