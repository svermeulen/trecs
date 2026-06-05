using NUnit.Framework;
using Unity.Collections;
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
            a.World.Submit();

            // Bulk remove all PartitionA, individually remove the alpha entity
            a.RemoveEntitiesWithTags(PartitionA);
            a.RemoveEntity(alphaHandle);
            a.World.Submit();

            NAssert.AreEqual(0, a.CountEntitiesWithTags(PartitionA));
            NAssert.AreEqual(0, a.CountEntitiesWithTags(TestTags.Alpha));
        }

        [Test]
        public void BulkRemove_PlusMoveFromSameGroup_MoveEscapes()
        {
            // Move an entity out of a group, then bulk-remove all in the group.
            // Bulk removal is deferred and runs as a whole-group phase that reads
            // the group's live contents *after* moves have been applied. The move
            // runs first (move phase), so entity 0 has already left PartitionA by
            // the time the full-group removal reads the count — it escapes to
            // PartitionB. Everything still in PartitionA is removed.
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
            a.World.Submit();

            // Move entity 0 to PartitionB, then bulk remove PartitionA.
            a.SetTag<TestPartitionB>(handles[0].ToIndex(a));
            a.RemoveEntitiesWithTags(PartitionA);
            a.World.Submit();

            NAssert.AreEqual(0, a.CountEntitiesWithTags(PartitionA));
            NAssert.AreEqual(
                1,
                a.CountEntitiesWithTags(PartitionB),
                "A move out of the group runs before the whole-group removal, so the moved entity escapes"
            );
            NAssert.IsTrue(handles[0].Exists(a), "the moved-out entity survives in PartitionB");
        }

        [Test]
        public void BulkRemove_PlusMoveIntoSameGroup_MovedEntityIsRemoved()
        {
            // Inverse of the move-escapes case: move an entity INTO a group, then
            // bulk-remove that group in the same submit. Phase order is
            // moves → removes → adds → whole-group removals, so the move runs first
            // and the entity is sitting in PartitionB by the time the whole-group
            // removal reads PartitionB's live contents — so it gets removed too.
            using var env = EcsTestHelper.CreateEnvironment(TestTemplates.WithPartitions);
            var a = env.Accessor;

            // One entity already in PartitionB, plus one in PartitionA we'll move over.
            var stayer = a.AddEntity(PartitionB)
                .Set(new TestInt { Value = 0 })
                .Set(new TestVec())
                .AssertComplete()
                .Handle;
            var mover = a.AddEntity(PartitionA)
                .Set(new TestInt { Value = 1 })
                .Set(new TestVec())
                .AssertComplete()
                .Handle;
            a.World.Submit();

            // Move the PartitionA entity into PartitionB, then bulk-remove PartitionB.
            a.SetTag<TestPartitionB>(mover.ToIndex(a));
            a.RemoveEntitiesWithTags(PartitionB);
            a.World.Submit();

            NAssert.AreEqual(
                0,
                a.CountEntitiesWithTags(PartitionB),
                "the whole-group removal runs after the move-in, so the moved entity is removed too"
            );
            NAssert.AreEqual(0, a.CountEntitiesWithTags(PartitionA));
            NAssert.IsFalse(mover.Exists(a), "the entity moved into the removed group is gone");
            NAssert.IsFalse(stayer.Exists(a), "the entity already in the group is gone");
        }

        [Test]
        public void BulkRemove_PlusNativeAdd_SameGroup()
        {
            // Bulk remove a group, then native add to the same group in the same
            // frame. The whole-group removal phase runs *after* the add phase, so
            // the freshly added entity is materialized (fires OnAdded) and then
            // removed by the bulk removal (fires OnRemoved) — both lifecycle events
            // fire and nothing survives. This is the "no silent lifecycle drops"
            // guarantee: once an add is submitted it always pairs OnAdded with a
            // matching OnRemoved, even when a bulk removal lands in the same submit.
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
            a.World.Submit();

            // Bulk remove all, then native add new.
            a.RemoveEntitiesWithTags(PartitionA);
            using var refs = a.ReserveEntityHandles(1, Allocator.Temp);
            var init = nativeEcs.AddEntity(PartitionA, sortKey: 0, refs[0]);
            init.Set(new TestInt { Value = 777 });
            init.Set(new TestVec());
            a.World.Submit();

            // Adds run before the whole-group removal, so the new entity is added
            // and then removed in the same submit — the group ends up empty.
            NAssert.AreEqual(0, a.CountEntitiesWithTags(PartitionA));
            NAssert.IsFalse(
                refs[0].Exists(a),
                "the added-then-bulk-removed entity does not survive the submit"
            );
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
            a.World.Submit();

            // Remove entity 0 individually, then bulk remove all PartitionA
            a.RemoveEntity(handles[0]);
            a.RemoveEntitiesWithTags(PartitionA);
            a.World.Submit();

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
            a.World.Submit();

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
            a.World.Submit();

            // Original removed, callback-added entity exists
            NAssert.IsFalse(handle.Exists(a));
            NAssert.AreEqual(1, a.CountEntitiesWithTags(TestTags.Alpha));
            NAssert.IsTrue(addedByCallback.Exists(a));
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
            a.World.Submit();

            var subscription = a
                .Events.EntitiesWithTags(PartitionA)
                .OnAdded(
                    (group, indices) =>
                    {
                        // Move newly added entities to PartitionB
                        for (int idx = indices.Start; idx < indices.End; idx++)
                        {
                            var ei = new EntityIndex(idx, group);
                            a.SetTag<TestPartitionB>(ei);
                        }
                    }
                );

            // Add new entity to PartitionA -> callback moves it to PartitionB
            var newHandle = a.AddEntity(PartitionA)
                .Set(new TestInt { Value = 42 })
                .Set(new TestVec())
                .AssertComplete()
                .Handle;
            a.World.Submit();

            // The new entity should end up in PartitionB
            NAssert.IsTrue(newHandle.Exists(a));
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
            a.World.Submit();

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
            a.World.Submit();

            NAssert.IsFalse(handleA.Exists(a));
            NAssert.IsTrue(handleB.Exists(a));
            NAssert.IsTrue(handleC.Exists(a));
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
            a.World.Submit();

            // Remove entity 0 (causes swap-back of entity 4 to index 0)
            a.RemoveEntity(handles[0]);
            a.World.Submit();

            // All remaining handles should still be valid with correct data
            NAssert.IsFalse(handles[0].Exists(a));
            for (int i = 1; i < 5; i++)
            {
                NAssert.IsTrue(handles[i].Exists(a), $"Handle {i} should be valid");
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
            a.World.Submit();

            a.SetTag<TestPartitionB>(handle.ToIndex(a));
            a.World.Submit();

            // Handle should still work after move
            NAssert.IsTrue(handle.Exists(a));
            NAssert.AreEqual(42, a.Component<TestInt>(handle).Read.Value);

            // And the index should point to the new group
            var idx = handle.ToIndex(a);
            var groupB = a.WorldInfo.GetSingleGroupWithTags(PartitionB);
            NAssert.AreEqual(groupB, idx.GroupIndex);
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
            a.World.Submit();

            a.RemoveEntity(handle);
            a.World.Submit();

            NAssert.IsFalse(handle.Exists(a));
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
            a.World.Submit();

            a.RemoveEntity(oldHandle);
            a.World.Submit();

            // Add new entity (may reuse internal handle slot)
            var newHandle = a.AddEntity(PartitionA)
                .Set(new TestInt { Value = 2 })
                .Set(new TestVec())
                .AssertComplete()
                .Handle;
            a.World.Submit();

            NAssert.IsFalse(oldHandle.Exists(a), "Old handle should be invalid");
            NAssert.IsTrue(newHandle.Exists(a), "New handle should be valid");
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
            a.World.Submit();

            // Remove entities at scattered positions
            a.RemoveEntity(handles[1]);
            a.RemoveEntity(handles[3]);
            a.RemoveEntity(handles[5]);
            a.RemoveEntity(handles[7]);
            a.RemoveEntity(handles[9]);
            a.World.Submit();

            // Even-indexed entities should all survive with correct data
            for (int i = 0; i < 10; i += 2)
            {
                NAssert.IsTrue(handles[i].Exists(a), $"Handle {i} should be valid");
                NAssert.AreEqual(i, a.Component<TestInt>(handles[i]).Read.Value);
            }

            // Odd-indexed entities should be invalid
            for (int i = 1; i < 10; i += 2)
            {
                NAssert.IsFalse(handles[i].Exists(a), $"Handle {i} should be invalid");
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
            a.World.Submit();

            var tracked = handles[3]; // Track entity 3

            // Frame 2: Move 0,1 to PartitionB, remove 4
            a.SetTag<TestPartitionB>(handles[0].ToIndex(a));
            a.SetTag<TestPartitionB>(handles[1].ToIndex(a));
            a.RemoveEntity(handles[4]);
            a.World.Submit();

            NAssert.IsTrue(tracked.Exists(a));
            NAssert.AreEqual(3, a.Component<TestInt>(tracked).Read.Value);

            // Frame 3: Remove 2, move 5 to PartitionB
            a.RemoveEntity(handles[2]);
            a.SetTag<TestPartitionB>(handles[5].ToIndex(a));
            a.World.Submit();

            NAssert.IsTrue(tracked.Exists(a));
            NAssert.AreEqual(3, a.Component<TestInt>(tracked).Read.Value);

            // Frame 4: Move tracked to PartitionB
            a.SetTag<TestPartitionB>(tracked.ToIndex(a));
            a.World.Submit();

            NAssert.IsTrue(tracked.Exists(a));
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
            a.World.Submit();

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
            a.World.Submit();

            a.RemoveEntity(handle);
            a.World.Submit();

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
