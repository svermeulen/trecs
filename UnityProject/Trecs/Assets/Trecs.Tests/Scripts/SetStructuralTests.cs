using NUnit.Framework;
using NAssert = NUnit.Framework.Assert;
using Trecs.Internal;

namespace Trecs.Tests
{
    // Set for structural interaction tests
    public struct FiltStructSet : IEntitySet<QId1> { }

    [TestFixture]
    public class SetStructuralTests
    {
        TestEnvironment CreateEnv() =>
            EcsTestHelper.CreateEnvironment(b => b.AddSet<FiltStructSet>(), QTestEntityA.Template);

        #region Set + Entity Removal (swap-back scenarios)

        [Test]
        public void FilterRemoval_RemoveEntityInSet_SwapBackUpdatesSet()
        {
            // If entity at index 0 is in set and gets removed, the entity that
            // swap-backs to index 0 should NOT be in the set (unless it was already)
            using var env = CreateEnv();
            var a = env.Accessor;

            // Add 3 entities
            var refs = new EntityHandle[3];
            for (int i = 0; i < 3; i++)
            {
                var init = a.AddEntity(Tag<QId1>.Value)
                    .Set(new TestInt { Value = i * 10 })
                    .Set(new TestFloat())
                    .AssertComplete();
                refs[i] = init.Handle;
            }
            a.SubmitEntities();

            var group = a.WorldInfo.GetSingleGroupWithTags(Tag<QId1>.Value);
            var set = a.Set<FiltStructSet>();

            // Add entity 0 to set
            set.Write.Add(new EntityIndex(0, group));
            a.SubmitEntities();

            var read = set.Read;
            NAssert.IsTrue(read.Exists(new EntityIndex(0, group)));
            NAssert.AreEqual(1, read.Count);

            // Remove entity 0 - entity 2 will swap-back to index 0
            a.RemoveEntity(refs[0]);
            a.SubmitEntities();

            // After swap-back, the entity now at index 0 (formerly index 2) should NOT be in the set
            // The set should have been cleaned up
            NAssert.AreEqual(2, a.CountEntitiesWithTags(Tag<QId1>.Value));
            NAssert.AreEqual(
                0,
                set.Read.Count,
                "Set should be empty after the entity in it was removed"
            );
        }

        [Test]
        public void FilterRemoval_RemoveEntityNotInSet_SetUnchanged()
        {
            using var env = CreateEnv();
            var a = env.Accessor;

            var refs = new EntityHandle[3];
            for (int i = 0; i < 3; i++)
            {
                var init = a.AddEntity(Tag<QId1>.Value)
                    .Set(new TestInt { Value = i * 10 })
                    .Set(new TestFloat())
                    .AssertComplete();
                refs[i] = init.Handle;
            }
            a.SubmitEntities();

            var group = a.WorldInfo.GetSingleGroupWithTags(Tag<QId1>.Value);
            var set = a.Set<FiltStructSet>();

            // Add entity 0 to set
            set.Write.Add(new EntityIndex(0, group));
            a.SubmitEntities();

            // Remove entity 2 (not in set)
            a.RemoveEntity(refs[2]);
            a.SubmitEntities();

            // Set should still contain entity 0
            var read = set.Read;
            NAssert.AreEqual(1, read.Count);
            NAssert.IsTrue(read.Exists(new EntityIndex(0, group)));
        }

        [Test]
        public void FilterRemoval_SwapBackEntityAlsoInSet_BothHandled()
        {
            // Entity 0 and entity 2 are both in set.
            // Remove entity 0 -> entity 2 swaps to index 0.
            // Set should now have entity at index 0 (the swapped entity).
            using var env = CreateEnv();
            var a = env.Accessor;

            var refs = new EntityHandle[3];
            for (int i = 0; i < 3; i++)
            {
                var init = a.AddEntity(Tag<QId1>.Value)
                    .Set(new TestInt { Value = i * 10 })
                    .Set(new TestFloat())
                    .AssertComplete();
                refs[i] = init.Handle;
            }
            a.SubmitEntities();

            var group = a.WorldInfo.GetSingleGroupWithTags(Tag<QId1>.Value);
            var set = a.Set<FiltStructSet>();

            // Add entities 0 and 2 to set
            var write = set.Write;
            write.Add(new EntityIndex(0, group));
            write.Add(new EntityIndex(2, group));
            a.SubmitEntities();

            NAssert.AreEqual(2, set.Read.Count);

            // Remove entity 0 -> entity 2 swaps to index 0
            a.RemoveEntity(refs[0]);
            a.SubmitEntities();

            // After removal + swap-back:
            // - The removed entity should no longer be in the set
            // - The swapped entity (now at index 0) should still be in the set
            NAssert.AreEqual(2, a.CountEntitiesWithTags(Tag<QId1>.Value));
            NAssert.AreEqual(1, set.Read.Count, "Only the surviving entity should remain in set");
        }

        [Test]
        public void FilterRemoval_RemoveMiddleEntity_FilterOnLastPreserved()
        {
            // Entity at last index is in set. Remove middle entity -> last entity swaps.
            // Set should track the entity at its new index.
            using var env = CreateEnv();
            var a = env.Accessor;

            var refs = new EntityHandle[4];
            for (int i = 0; i < 4; i++)
            {
                var init = a.AddEntity(Tag<QId1>.Value)
                    .Set(new TestInt { Value = i * 10 })
                    .Set(new TestFloat())
                    .AssertComplete();
                refs[i] = init.Handle;
            }
            a.SubmitEntities();

            var group = a.WorldInfo.GetSingleGroupWithTags(Tag<QId1>.Value);
            var set = a.Set<FiltStructSet>();

            // Add entity 3 (last) to set
            set.Write.Add(new EntityIndex(3, group));
            a.SubmitEntities();
            NAssert.AreEqual(1, set.Read.Count);

            // Remove entity 1 (middle) -> entity 3 swaps to index 1
            a.RemoveEntity(refs[1]);
            a.SubmitEntities();

            NAssert.AreEqual(3, a.CountEntitiesWithTags(Tag<QId1>.Value));

            // The entity formerly at index 3 is now at some other index after swap-back
            // Check that set still has exactly 1 entry
            NAssert.AreEqual(
                1,
                set.Read.Count,
                "Set count should remain 1 after non-set entity removed"
            );
        }

        #endregion

        #region Set + Multiple Removes

        [Test]
        public void FilterRemoval_RemoveAllFilteredEntities_FilterEmpty()
        {
            using var env = CreateEnv();
            var a = env.Accessor;

            var refs = new EntityHandle[5];
            for (int i = 0; i < 5; i++)
            {
                var init = a.AddEntity(Tag<QId1>.Value)
                    .Set(new TestInt { Value = i })
                    .Set(new TestFloat())
                    .AssertComplete();
                refs[i] = init.Handle;
            }
            a.SubmitEntities();

            var group = a.WorldInfo.GetSingleGroupWithTags(Tag<QId1>.Value);
            var set = a.Set<FiltStructSet>();

            // Add entities 1, 3 to set
            var write = set.Write;
            write.Add(new EntityIndex(1, group));
            write.Add(new EntityIndex(3, group));
            a.SubmitEntities();
            NAssert.AreEqual(2, set.Read.Count);

            // Remove both filtered entities
            a.RemoveEntity(refs[1]);
            a.RemoveEntity(refs[3]);
            a.SubmitEntities();

            NAssert.AreEqual(3, a.CountEntitiesWithTags(Tag<QId1>.Value));
            NAssert.AreEqual(0, set.Read.Count, "All filtered entities removed");
        }

        [Test]
        public void FilterRemoval_RemoveAllEntities_FilterEmpty()
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
            var set = a.Set<FiltStructSet>();

            var write = set.Write;
            write.Add(new EntityIndex(0, group));
            write.Add(new EntityIndex(1, group));
            write.Add(new EntityIndex(2, group));
            a.SubmitEntities();

            // Remove all entities using tag-based removal
            a.RemoveEntitiesWithTags(Tag<QId1>.Value);
            a.SubmitEntities();

            NAssert.AreEqual(0, a.CountEntitiesWithTags(Tag<QId1>.Value));
            NAssert.AreEqual(0, set.Read.Count);
        }

        #endregion

        #region Set + Add New Entities After Removal

        [Test]
        public void FilterRemoval_AddAfterRemove_SetOnlyContainsExplicitlyAdded()
        {
            using var env = CreateEnv();
            var a = env.Accessor;

            var init = a.AddEntity(Tag<QId1>.Value)
                .Set(new TestInt { Value = 10 })
                .Set(new TestFloat())
                .AssertComplete();
            a.SubmitEntities();

            var group = a.WorldInfo.GetSingleGroupWithTags(Tag<QId1>.Value);
            var set = a.Set<FiltStructSet>();

            set.Write.Add(new EntityIndex(0, group));
            a.SubmitEntities();

            // Remove filtered entity
            a.RemoveEntity(init.Handle);
            a.SubmitEntities();

            // Add a new entity (gets index 0 since group is empty)
            a.AddEntity(Tag<QId1>.Value)
                .Set(new TestInt { Value = 20 })
                .Set(new TestFloat())
                .AssertComplete();
            a.SubmitEntities();

            // New entity should NOT be in the set
            NAssert.AreEqual(1, a.CountEntitiesWithTags(Tag<QId1>.Value));
            NAssert.AreEqual(
                0,
                set.Read.Count,
                "New entity at recycled index should not be in set"
            );
        }

        #endregion

        #region Set Query Integration

        [Test]
        public void SetQuery_IterationMatchesSetCount()
        {
            using var env = CreateEnv();
            var a = env.Accessor;

            for (int i = 0; i < 10; i++)
            {
                a.AddEntity(Tag<QId1>.Value)
                    .Set(new TestInt { Value = i })
                    .Set(new TestFloat())
                    .AssertComplete();
            }
            a.SubmitEntities();

            var group = a.WorldInfo.GetSingleGroupWithTags(Tag<QId1>.Value);
            var set = a.Set<FiltStructSet>();

            // Add every other entity to set
            for (int i = 0; i < 10; i += 2)
            {
                set.Write.Add(new EntityIndex(i, group));
            }
            a.SubmitEntities();

            NAssert.AreEqual(5, set.Read.Count);

            // Query through the set should yield exactly 5 entities
            int queryCount = a.Query().InSet<FiltStructSet>().Count();
            NAssert.AreEqual(5, queryCount, "Query through set should match set count");
        }

        [Test]
        public void SetQuery_AfterRemoves_IterationStillCorrect()
        {
            using var env = CreateEnv();
            var a = env.Accessor;

            var refs = new EntityHandle[10];
            for (int i = 0; i < 10; i++)
            {
                var init = a.AddEntity(Tag<QId1>.Value)
                    .Set(new TestInt { Value = i })
                    .Set(new TestFloat())
                    .AssertComplete();
                refs[i] = init.Handle;
            }
            a.SubmitEntities();

            var group = a.WorldInfo.GetSingleGroupWithTags(Tag<QId1>.Value);
            var set = a.Set<FiltStructSet>();

            // Add entities 0-4 to set
            for (int i = 0; i < 5; i++)
            {
                set.Write.Add(new EntityIndex(i, group));
            }
            a.SubmitEntities();

            // Remove entities 0 and 2 (both in set)
            a.RemoveEntity(refs[0]);
            a.RemoveEntity(refs[2]);
            a.SubmitEntities();

            // After removal + swap-backs, query through set should yield correct count
            int setCount = set.Read.Count;
            int queryCount = a.Query().InSet<FiltStructSet>().Count();
            NAssert.AreEqual(
                setCount,
                queryCount,
                "Query count should match set count after removals"
            );
        }

        [Test]
        public void SetQuery_LargeScale_ConsistentWithCount()
        {
            using var env = CreateEnv();
            var a = env.Accessor;

            const int total = 100;
            var refs = new EntityHandle[total];
            for (int i = 0; i < total; i++)
            {
                var init = a.AddEntity(Tag<QId1>.Value)
                    .Set(new TestInt { Value = i })
                    .Set(new TestFloat())
                    .AssertComplete();
                refs[i] = init.Handle;
            }
            a.SubmitEntities();

            var group = a.WorldInfo.GetSingleGroupWithTags(Tag<QId1>.Value);
            var set = a.Set<FiltStructSet>();

            // Add every 3rd entity to set
            for (int i = 0; i < total; i += 3)
            {
                set.Write.Add(new EntityIndex(i, group));
            }
            a.SubmitEntities();

            int expectedCount = (total + 2) / 3; // ceiling division
            NAssert.AreEqual(expectedCount, set.Read.Count);

            // Remove every 5th entity (some in set, some not)
            for (int i = 0; i < total; i += 5)
            {
                a.RemoveEntity(refs[i]);
            }
            a.SubmitEntities();

            // After removals, query count should match set count
            int setCount = set.Read.Count;
            int queryCount = a.Query().InSet<FiltStructSet>().Count();
            NAssert.AreEqual(
                setCount,
                queryCount,
                "Query count ({0}) should match set count ({1}) after large-scale removals",
                queryCount,
                setCount
            );
        }

        #endregion

        #region Native Set Operations

        [Test]
        public void NativeSetAdd_EntityAppearsInSet()
        {
            using var env = CreateEnv();
            var a = env.Accessor;

            a.AddEntity(Tag<QId1>.Value)
                .Set(new TestInt { Value = 1 })
                .Set(new TestFloat())
                .AssertComplete();
            a.SubmitEntities();

            var group = a.WorldInfo.GetSingleGroupWithTags(Tag<QId1>.Value);
            var set = a.Set<FiltStructSet>();

            a.Set<FiltStructSet>().Defer.Add(new EntityIndex(0, group));
            a.SubmitEntities();

            // Expected behavior: entity should be in set after one submit
            var read = set.Read;
            NAssert.IsTrue(
                read.Exists(new EntityIndex(0, group)),
                "Native set add should take effect in the same submission cycle"
            );
            NAssert.AreEqual(1, read.Count);
        }

        // NativeSetAdd_RequiresExtraSubmit_Bug — removed: bug was fixed by using
        // Add/Remove in FlushNativeSetQueue

        [Test]
        public void NativeSetRemove_EntityRemovedFromSet()
        {
            using var env = CreateEnv();
            var a = env.Accessor;

            a.AddEntity(Tag<QId1>.Value)
                .Set(new TestInt { Value = 1 })
                .Set(new TestFloat())
                .AssertComplete();
            a.SubmitEntities();

            var group = a.WorldInfo.GetSingleGroupWithTags(Tag<QId1>.Value);
            var set = a.Set<FiltStructSet>();

            set.Write.Add(new EntityIndex(0, group));
            a.SubmitEntities();
            NAssert.AreEqual(1, set.Read.Count);

            a.Set<FiltStructSet>().Defer.Remove(new EntityIndex(0, group));
            a.SubmitEntities();

            // Expected behavior: entity should be removed after one submit
            var read = set.Read;
            NAssert.IsFalse(
                read.Exists(new EntityIndex(0, group)),
                "Native set remove should take effect in the same submission cycle"
            );
            NAssert.AreEqual(0, read.Count);
        }

        // NativeSetRemove_RequiresExtraSubmit_Bug — removed: bug was fixed by using
        // Add/Remove in FlushNativeSetQueue

        #endregion

        #region Deferred Set Ops + Structural Changes Interaction

        [Test]
        public void DeferredSetRemove_PlusEntityRemoval_SwapBackCorrect()
        {
            // Edge case: deferred set remove targets entity at index 0,
            // AND entity at index 0 is also being removed (structural).
            // FlushAllDeferredOps runs first (removes index 0 from set),
            // then structural removal swap-backs last entity into index 0.
            // The swap-backed entity should NOT be in the set.
            using var env = CreateEnv();
            var a = env.Accessor;

            // Add 3 entities
            var refs = new EntityHandle[3];
            for (int i = 0; i < 3; i++)
            {
                var init = a.AddEntity(Tag<QId1>.Value)
                    .Set(new TestInt { Value = (i + 1) * 10 })
                    .Set(new TestFloat());
                init.AssertComplete();
                refs[i] = init.Handle;
            }
            a.SubmitEntities();

            var group = a.WorldInfo.GetSingleGroupWithTags(Tag<QId1>.Value);
            var set = a.Set<FiltStructSet>();

            // Put all 3 entities in set
            var write = set.Write;
            write.Add(new EntityIndex(0, group));
            write.Add(new EntityIndex(1, group));
            write.Add(new EntityIndex(2, group));
            a.SubmitEntities();
            NAssert.AreEqual(3, set.Read.Count);

            // Now: deferred remove entity 0 from set AND structural remove entity 0
            set.Write.Remove(new EntityIndex(0, group));
            a.RemoveEntity(refs[0]);
            a.SubmitEntities();

            // After submission:
            // - FlushAllDeferredOps removed index 0 from set
            // - Structural removal removed entity 0, swap-backed entity 2 into index 0
            // - RemoveEntitiesFromSets handled the swap-back (remapped index 2 -> 0 in set)
            // Result: set should contain 2 entities (the former entities 1 and 2)
            NAssert.AreEqual(2, set.Read.Count, "Set should have 2 entities after removing one");
        }

        [Test]
        public void DeferredSetAdd_PlusEntityRemoval_SwapBackCorrect()
        {
            // Edge case: deferred set add targets entity at index 1,
            // AND entity at index 0 is being removed (structural).
            // After swap-back, entity formerly at index 2 moves to index 0.
            // The deferred add at index 1 should still be in the set.
            using var env = CreateEnv();
            var a = env.Accessor;

            // Add 3 entities
            var refs = new EntityHandle[3];
            for (int i = 0; i < 3; i++)
            {
                var init = a.AddEntity(Tag<QId1>.Value)
                    .Set(new TestInt { Value = (i + 1) * 10 })
                    .Set(new TestFloat());
                init.AssertComplete();
                refs[i] = init.Handle;
            }
            a.SubmitEntities();

            var group = a.WorldInfo.GetSingleGroupWithTags(Tag<QId1>.Value);
            var set = a.Set<FiltStructSet>();

            // Deferred add entity 1 to set, AND remove entity 0 structurally
            set.Write.Add(new EntityIndex(1, group));
            a.RemoveEntity(refs[0]);
            a.SubmitEntities();

            // After submission:
            // - FlushAllDeferredOps added index 1 to set
            // - Structural removal removed entity 0, swap-backed entity 2 into index 0
            // - RemoveEntitiesFromSets handled swap-back (but entity 0 wasn't in set)
            // Result: entity at index 1 should be in set (unchanged by swap-back at index 0)
            var read = set.Read;
            NAssert.AreEqual(1, read.Count, "Set should have 1 entity (the one added at index 1)");
            NAssert.IsTrue(
                read.Exists(new EntityIndex(1, group)),
                "Entity at index 1 should still be in set after swap-back at index 0"
            );
        }

        [Test]
        public void DeferredSetAdd_PlusRemovalOfSameEntity_SetCleanedUp()
        {
            // Edge case: deferred set add targets entity at index 0,
            // but that entity is also being removed structurally.
            // After submission, the set should NOT contain the removed entity.
            using var env = CreateEnv();
            var a = env.Accessor;

            // Add 2 entities
            var refs = new EntityHandle[2];
            for (int i = 0; i < 2; i++)
            {
                var init = a.AddEntity(Tag<QId1>.Value)
                    .Set(new TestInt { Value = (i + 1) * 10 })
                    .Set(new TestFloat());
                init.AssertComplete();
                refs[i] = init.Handle;
            }
            a.SubmitEntities();

            var group = a.WorldInfo.GetSingleGroupWithTags(Tag<QId1>.Value);
            var set = a.Set<FiltStructSet>();

            // Deferred add entity 0 to set, AND remove entity 0 structurally
            set.Write.Add(new EntityIndex(0, group));
            a.RemoveEntity(refs[0]);
            a.SubmitEntities();

            // After submission:
            // - FlushAllDeferredOps added index 0 to set
            // - Structural removal removed entity 0, swap-backed entity 1 into index 0
            // - RemoveEntitiesFromSets removed index 0 from set (the old entity)
            //   and remapped index 1 -> 0 for the swap-backed entity... but entity 1
            //   wasn't in the set, so the remap is a no-op
            // Result: set should be empty -- the entity we added was removed
            NAssert.AreEqual(
                0,
                set.Read.Count,
                "Set should be empty -- the added entity was removed structurally"
            );
        }

        [Test]
        public void DeferredSetRemoveThenAdd_SameEntity_EntityEndsUpInSet()
        {
            // Verify that remove-then-add for the same entity in the same frame
            // results in the entity being in the set (removes processed before adds)
            using var env = CreateEnv();
            var a = env.Accessor;

            a.AddEntity(Tag<QId1>.Value)
                .Set(new TestInt { Value = 1 })
                .Set(new TestFloat())
                .AssertComplete();
            a.SubmitEntities();

            var group = a.WorldInfo.GetSingleGroupWithTags(Tag<QId1>.Value);
            var set = a.Set<FiltStructSet>();

            // Add entity to set first
            set.Write.Add(new EntityIndex(0, group));
            a.SubmitEntities();
            NAssert.IsTrue(set.Read.Exists(new EntityIndex(0, group)));

            // Remove then re-add in same frame
            var write = set.Write;
            write.Remove(new EntityIndex(0, group));
            write.Add(new EntityIndex(0, group));
            a.SubmitEntities();

            NAssert.IsTrue(
                set.Read.Exists(new EntityIndex(0, group)),
                "Entity should be in set after remove-then-add (removes processed before adds)"
            );
        }

        #endregion
    }
}
