using NUnit.Framework;
using NAssert = NUnit.Framework.Assert;

namespace Trecs.Tests
{
    /// <summary>
    /// Tests for bulk group operations, multi-iteration submissions (cascading callbacks),
    /// and entity handle validity through structural changes.
    /// </summary>
    [TestFixture]
    public class SubmissionPipelineBulkAndCallbackTests
    {
        static readonly TagSet PartitionA = TagSet.FromTags(TestTags.Gamma, TestTags.PartitionA);
        static readonly TagSet PartitionB = TagSet.FromTags(TestTags.Gamma, TestTags.PartitionB);

        #region RemoveEntitiesWithTags + individual operations in same frame

        [Test]
        public void BulkRemove_PlusIndividualRemove_DifferentGroups()
        {
            using var env = EcsTestHelper.CreateEnvironment(
                TestTemplates.WithPartitions,
                TestTemplates.SimpleAlpha
            );
            var a = env.Accessor;

            // Add entities to two different templates
            for (int i = 0; i < 5; i++)
            {
                a.AddEntity(PartitionA)
                    .Set(new TestInt { Value = i })
                    .Set(new TestVec())
                    .AssertComplete();
            }
            var alphaHandle = a.AddEntity(TestTags.Alpha)
                .Set(new TestInt { Value = 99 })
                .AssertComplete()
                .Handle;
            a.SubmitEntities();

            // Bulk remove all PartitionA, individually remove the alpha entity
            a.RemoveEntitiesWithTags(PartitionA);
            a.RemoveEntity(alphaHandle);
            a.SubmitEntities();

            NAssert.AreEqual(0, a.CountEntitiesWithTags(PartitionA));
            NAssert.AreEqual(0, a.CountEntitiesWithTags(TestTags.Alpha));
        }

        [Test]
        public void BulkRemove_PlusMoveFromSameGroup_RemoveSupersedes()
        {
            // Move an entity out of a group, then bulk-remove all in the group.
            // The bulk remove iterates all entities still in the source group
            // (including the one queued for move), so remove supersedes move.
            // All entities should be removed.
            using var env = EcsTestHelper.CreateEnvironment(TestTemplates.WithPartitions);
            var a = env.Accessor;

            var handles = new EntityHandle[5];
            for (int i = 0; i < 5; i++)
            {
                handles[i] = a.AddEntity(PartitionA)
                    .Set(new TestInt { Value = i })
                    .Set(new TestVec())
                    .AssertComplete()
                    .Handle;
            }
            a.SubmitEntities();

            // Move entity 0 to PartitionB, then bulk remove PartitionA
            // The bulk remove sees entity 0 still in PartitionA and removes it,
            // superseding the move.
            a.MoveTo(handles[0].ToIndex(a), PartitionB);
            a.RemoveEntitiesWithTags(PartitionA);
            a.SubmitEntities();

            NAssert.AreEqual(0, a.CountEntitiesWithTags(PartitionA));
            NAssert.AreEqual(
                0,
                a.CountEntitiesWithTags(PartitionB),
                "Bulk remove supersedes the move"
            );
        }

        [Test]
        public void BulkRemove_PlusNativeAdd_SameGroup()
        {
            // Bulk remove a group, then native add to same group in same frame.
            // The bulk remove is queued first, processed during submission.
            // The native add is also deferred. Both should be applied.
            using var env = EcsTestHelper.CreateEnvironment(TestTemplates.WithPartitions);
            var a = env.Accessor;
            var nativeEcs = a.ToNative();

            for (int i = 0; i < 3; i++)
            {
                a.AddEntity(PartitionA)
                    .Set(new TestInt { Value = i })
                    .Set(new TestVec())
                    .AssertComplete();
            }
            a.SubmitEntities();

            // Bulk remove all, then native add new
            a.RemoveEntitiesWithTags(PartitionA);
            var init = nativeEcs.AddEntity(PartitionA, sortKey: 0);
            init.Set(new TestInt { Value = 777 });
            init.Set(new TestVec());
            a.SubmitEntities();

            // Removes happen first (in SingleSubmission), then adds.
            // The new entity should exist.
            NAssert.AreEqual(1, a.CountEntitiesWithTags(PartitionA));
        }

        [Test]
        public void BulkRemove_WithIndividualRemoveOfSameEntity()
        {
            // Both bulk remove and individual remove target the same entity.
            // The dedup logic should handle this.
            using var env = EcsTestHelper.CreateEnvironment(TestTemplates.WithPartitions);
            var a = env.Accessor;

            var handles = new EntityHandle[3];
            for (int i = 0; i < 3; i++)
            {
                handles[i] = a.AddEntity(PartitionA)
                    .Set(new TestInt { Value = i })
                    .Set(new TestVec())
                    .AssertComplete()
                    .Handle;
            }
            a.SubmitEntities();

            // Remove entity 0 individually, then bulk remove all PartitionA
            a.RemoveEntity(handles[0]);
            a.RemoveEntitiesWithTags(PartitionA);
            a.SubmitEntities();

            NAssert.AreEqual(0, a.CountEntitiesWithTags(PartitionA));
        }

        #endregion

        #region Multi-iteration submissions (cascading callbacks)

        [Test]
        public void Callback_AddsEntity_ProcessedInNextIteration()
        {
            // OnRemoved callback adds a new entity.
            // This triggers a second submission iteration.
            using var env = EcsTestHelper.CreateEnvironment(TestTemplates.SimpleAlpha);
            var a = env.Accessor;

            var handle = a.AddEntity(TestTags.Alpha)
                .Set(new TestInt { Value = 1 })
                .AssertComplete()
                .Handle;
            a.SubmitEntities();

            EntityHandle addedByCallback = default;
            var subscription = a
                .Events.EntitiesWithTags(TestTags.Alpha)
                .OnRemoved(
                    (group, indices) =>
                    {
                        var init = a.AddEntity(TestTags.Alpha)
                            .Set(new TestInt { Value = 999 })
                            .AssertComplete();
                        addedByCallback = init.Handle;
                    }
                );

            a.RemoveEntity(handle);
            a.SubmitEntities();

            // Original removed, callback-added entity exists
            NAssert.IsFalse(a.EntityExists(handle));
            NAssert.AreEqual(1, a.CountEntitiesWithTags(TestTags.Alpha));
            NAssert.IsTrue(a.EntityExists(addedByCallback));
            NAssert.AreEqual(999, a.Component<TestInt>(addedByCallback).Read.Value);

            subscription.Dispose();
        }

        [Test]
        public void Callback_MovesEntity_ProcessedInNextIteration()
        {
            // OnAdded callback moves the entity to a different state.
            using var env = EcsTestHelper.CreateEnvironment(TestTemplates.WithPartitions);
            var a = env.Accessor;

            // Pre-populate PartitionA with a base entity
            a.AddEntity(PartitionA)
                .Set(new TestInt { Value = 0 })
                .Set(new TestVec())
                .AssertComplete();
            a.SubmitEntities();

            var subscription = a
                .Events.EntitiesWithTags(PartitionA)
                .OnAdded(
                    (group, indices) =>
                    {
                        // Move newly added entities to PartitionB
                        for (int idx = indices.Start; idx < indices.End; idx++)
                        {
                            var ei = new EntityIndex(idx, group);
                            a.MoveTo(ei, PartitionB);
                        }
                    }
                );

            // Add new entity to PartitionA -> callback moves it to PartitionB
            var newHandle = a.AddEntity(PartitionA)
                .Set(new TestInt { Value = 42 })
                .Set(new TestVec())
                .AssertComplete()
                .Handle;
            a.SubmitEntities();

            // The new entity should end up in PartitionB
            NAssert.IsTrue(a.EntityExists(newHandle));
            NAssert.AreEqual(42, a.Component<TestInt>(newHandle).Read.Value);
            NAssert.AreEqual(1, a.CountEntitiesWithTags(PartitionB));

            subscription.Dispose();
        }

        [Test]
        public void Callback_TwoLevelCascade_BothProcessed()
        {
            // Remove entity A -> OnRemoved callback adds entity B
            // -> OnAdded callback for B adds entity C
            // Requires 3 submission iterations (remove, add B, add C).
            using var env = EcsTestHelper.CreateEnvironment(TestTemplates.SimpleAlpha);
            var a = env.Accessor;

            var handleA = a.AddEntity(TestTags.Alpha)
                .Set(new TestInt { Value = 1 })
                .AssertComplete()
                .Handle;
            a.SubmitEntities();

            EntityHandle handleB = default;
            EntityHandle handleC = default;
            bool addedB = false;
            bool addedC = false;

            var sub1 = a
                .Events.EntitiesWithTags(TestTags.Alpha)
                .OnRemoved(
                    (group, indices) =>
                    {
                        if (!addedB)
                        {
                            addedB = true;
                            handleB = a.AddEntity(TestTags.Alpha)
                                .Set(new TestInt { Value = 2 })
                                .AssertComplete()
                                .Handle;
                        }
                    }
                );

            var sub2 = a
                .Events.EntitiesWithTags(TestTags.Alpha)
                .OnAdded(
                    (group, indices) =>
                    {
                        if (!addedC)
                        {
                            addedC = true;
                            handleC = a.AddEntity(TestTags.Alpha)
                                .Set(new TestInt { Value = 3 })
                                .AssertComplete()
                                .Handle;
                        }
                    }
                );

            a.RemoveEntity(handleA);
            a.SubmitEntities();

            NAssert.IsFalse(a.EntityExists(handleA));
            NAssert.IsTrue(a.EntityExists(handleB));
            NAssert.IsTrue(a.EntityExists(handleC));
            NAssert.AreEqual(2, a.CountEntitiesWithTags(TestTags.Alpha));
            NAssert.AreEqual(2, a.Component<TestInt>(handleB).Read.Value);
            NAssert.AreEqual(3, a.Component<TestInt>(handleC).Read.Value);

            sub1.Dispose();
            sub2.Dispose();
        }

        #endregion

        #region Entity handle validity through structural changes

        [Test]
        public void HandleValid_AfterOtherEntityRemoved_SwapBack()
        {
            // When another entity is removed causing swap-back,
            // our handle should still resolve correctly.
            using var env = EcsTestHelper.CreateEnvironment(TestTemplates.WithPartitions);
            var a = env.Accessor;

            var handles = new EntityHandle[5];
            for (int i = 0; i < 5; i++)
            {
                handles[i] = a.AddEntity(PartitionA)
                    .Set(new TestInt { Value = i * 10 })
                    .Set(new TestVec())
                    .AssertComplete()
                    .Handle;
            }
            a.SubmitEntities();

            // Remove entity 0 (causes swap-back of entity 4 to index 0)
            a.RemoveEntity(handles[0]);
            a.SubmitEntities();

            // All remaining handles should still be valid with correct data
            NAssert.IsFalse(a.EntityExists(handles[0]));
            for (int i = 1; i < 5; i++)
            {
                NAssert.IsTrue(a.EntityExists(handles[i]), $"Handle {i} should be valid");
                NAssert.AreEqual(
                    i * 10,
                    a.Component<TestInt>(handles[i]).Read.Value,
                    $"Handle {i} should have correct data"
                );
            }
        }

        [Test]
        public void HandleValid_AfterMove_ResolvesToNewGroup()
        {
            using var env = EcsTestHelper.CreateEnvironment(TestTemplates.WithPartitions);
            var a = env.Accessor;

            var handle = a.AddEntity(PartitionA)
                .Set(new TestInt { Value = 42 })
                .Set(new TestVec())
                .AssertComplete()
                .Handle;
            a.SubmitEntities();

            a.MoveTo(handle.ToIndex(a), PartitionB);
            a.SubmitEntities();

            // Handle should still work after move
            NAssert.IsTrue(a.EntityExists(handle));
            NAssert.AreEqual(42, a.Component<TestInt>(handle).Read.Value);

            // And the index should point to the new group
            var idx = handle.ToIndex(a);
            var groupB = a.WorldInfo.GetSingleGroupWithTags(PartitionB);
            NAssert.AreEqual(groupB, idx.Group);
        }

        [Test]
        public void HandleInvalid_AfterRemove_ExistsFalse()
        {
            using var env = EcsTestHelper.CreateEnvironment(TestTemplates.WithPartitions);
            var a = env.Accessor;

            var handle = a.AddEntity(PartitionA)
                .Set(new TestInt { Value = 1 })
                .Set(new TestVec())
                .AssertComplete()
                .Handle;
            a.SubmitEntities();

            a.RemoveEntity(handle);
            a.SubmitEntities();

            NAssert.IsFalse(a.EntityExists(handle));
            NAssert.IsFalse(handle.TryToIndex(a, out _));
        }

        [Test]
        public void HandleInvalid_AfterRemove_NewEntityAtSameSlot_OldHandleStillInvalid()
        {
            // After removing an entity, a new entity might reuse the same internal slot.
            // The old handle should still be invalid (version mismatch).
            using var env = EcsTestHelper.CreateEnvironment(TestTemplates.WithPartitions);
            var a = env.Accessor;

            var oldHandle = a.AddEntity(PartitionA)
                .Set(new TestInt { Value = 1 })
                .Set(new TestVec())
                .AssertComplete()
                .Handle;
            a.SubmitEntities();

            a.RemoveEntity(oldHandle);
            a.SubmitEntities();

            // Add new entity (may reuse internal handle slot)
            var newHandle = a.AddEntity(PartitionA)
                .Set(new TestInt { Value = 2 })
                .Set(new TestVec())
                .AssertComplete()
                .Handle;
            a.SubmitEntities();

            NAssert.IsFalse(a.EntityExists(oldHandle), "Old handle should be invalid");
            NAssert.IsTrue(a.EntityExists(newHandle), "New handle should be valid");
            NAssert.AreEqual(2, a.Component<TestInt>(newHandle).Read.Value);
        }

        [Test]
        public void HandleValid_ThroughMultipleSwapBacks()
        {
            // Remove multiple entities causing cascading swap-backs.
            // Remaining entity handles should all still be valid.
            using var env = EcsTestHelper.CreateEnvironment(TestTemplates.WithPartitions);
            var a = env.Accessor;

            var handles = new EntityHandle[10];
            for (int i = 0; i < 10; i++)
            {
                handles[i] = a.AddEntity(PartitionA)
                    .Set(new TestInt { Value = i })
                    .Set(new TestVec())
                    .AssertComplete()
                    .Handle;
            }
            a.SubmitEntities();

            // Remove entities at scattered positions
            a.RemoveEntity(handles[1]);
            a.RemoveEntity(handles[3]);
            a.RemoveEntity(handles[5]);
            a.RemoveEntity(handles[7]);
            a.RemoveEntity(handles[9]);
            a.SubmitEntities();

            // Even-indexed entities should all survive with correct data
            for (int i = 0; i < 10; i += 2)
            {
                NAssert.IsTrue(a.EntityExists(handles[i]), $"Handle {i} should be valid");
                NAssert.AreEqual(i, a.Component<TestInt>(handles[i]).Read.Value);
            }

            // Odd-indexed entities should be invalid
            for (int i = 1; i < 10; i += 2)
            {
                NAssert.IsFalse(a.EntityExists(handles[i]), $"Handle {i} should be invalid");
            }
        }

        [Test]
        public void HandleValid_ThroughMoveAndRemoveOfOthers()
        {
            // One entity is being tracked. Other entities are moved and removed.
            // The tracked entity handle should remain valid throughout.
            using var env = EcsTestHelper.CreateEnvironment(TestTemplates.WithPartitions);
            var a = env.Accessor;

            var handles = new EntityHandle[6];
            for (int i = 0; i < 6; i++)
            {
                handles[i] = a.AddEntity(PartitionA)
                    .Set(new TestInt { Value = i })
                    .Set(new TestVec())
                    .AssertComplete()
                    .Handle;
            }
            a.SubmitEntities();

            var tracked = handles[3]; // Track entity 3

            // Frame 2: Move 0,1 to PartitionB, remove 4
            a.MoveTo(handles[0].ToIndex(a), PartitionB);
            a.MoveTo(handles[1].ToIndex(a), PartitionB);
            a.RemoveEntity(handles[4]);
            a.SubmitEntities();

            NAssert.IsTrue(a.EntityExists(tracked));
            NAssert.AreEqual(3, a.Component<TestInt>(tracked).Read.Value);

            // Frame 3: Remove 2, move 5 to PartitionB
            a.RemoveEntity(handles[2]);
            a.MoveTo(handles[5].ToIndex(a), PartitionB);
            a.SubmitEntities();

            NAssert.IsTrue(a.EntityExists(tracked));
            NAssert.AreEqual(3, a.Component<TestInt>(tracked).Read.Value);

            // Frame 4: Move tracked to PartitionB
            a.MoveTo(tracked.ToIndex(a), PartitionB);
            a.SubmitEntities();

            NAssert.IsTrue(a.EntityExists(tracked));
            NAssert.AreEqual(3, a.Component<TestInt>(tracked).Read.Value);
        }

        #endregion

        #region TryToIndex safety

        [Test]
        public void TryToIndex_ValidHandle_ReturnsTrue()
        {
            using var env = EcsTestHelper.CreateEnvironment(TestTemplates.WithPartitions);
            var a = env.Accessor;

            var handle = a.AddEntity(PartitionA)
                .Set(new TestInt { Value = 1 })
                .Set(new TestVec())
                .AssertComplete()
                .Handle;
            a.SubmitEntities();

            NAssert.IsTrue(handle.TryToIndex(a, out var idx));
            NAssert.AreEqual(0, idx.Index);
        }

        [Test]
        public void TryToIndex_RemovedHandle_ReturnsFalse()
        {
            using var env = EcsTestHelper.CreateEnvironment(TestTemplates.WithPartitions);
            var a = env.Accessor;

            var handle = a.AddEntity(PartitionA)
                .Set(new TestInt { Value = 1 })
                .Set(new TestVec())
                .AssertComplete()
                .Handle;
            a.SubmitEntities();

            a.RemoveEntity(handle);
            a.SubmitEntities();

            NAssert.IsFalse(handle.TryToIndex(a, out _));
        }

        [Test]
        public void TryToIndex_NullHandle_ReturnsFalse()
        {
            using var env = EcsTestHelper.CreateEnvironment(TestTemplates.WithPartitions);
            var a = env.Accessor;

            NAssert.IsFalse(EntityHandle.Null.TryToIndex(a, out _));
        }

        #endregion
    }
}
