using NUnit.Framework;
using Trecs.Internal;
using NAssert = NUnit.Framework.Assert;

namespace Trecs.Tests
{
    // Tags and template with partitions for set+move+remove interaction tests
    public struct SPTag : ITag { }

    public struct SPPartitionA : ITag { }

    public struct SPPartitionB : ITag { }

    public partial class SPTestEntity
        : ITemplate,
            ITagged<SPTag>,
            IPartitionedBy<SPPartitionA, SPPartitionB>
    {
        TestInt TestInt;
        TestVec TestVec;
    }

    public struct SPSet : IEntitySet<SPTag> { }

    public struct SPSet2 : IEntitySet<SPTag> { }

    /// <summary>
    /// Tests for set (filter) interactions with structural changes in the submission pipeline.
    /// Covers gaps identified in the audit: move+remove+set combined, native paths,
    /// multiple sets, large-scale operations, and cascading callbacks.
    /// </summary>
    [TestFixture]
    public class SubmissionPipelineSetTests
    {
        static readonly TagSet PartitionA = TagSet.FromTags(
            Tag<SPTag>.Value,
            Tag<SPPartitionA>.Value
        );
        static readonly TagSet PartitionB = TagSet.FromTags(
            Tag<SPTag>.Value,
            Tag<SPPartitionB>.Value
        );

        TestEnvironment CreateEnv() =>
            EcsTestHelper.CreateEnvironment(
                b =>
                {
                    b.AddSet<SPSet>();
                    b.AddSet<SPSet2>();
                },
                SPTestEntity.Template
            );

        #region Set + Move + Remove (all three in one frame)

        [Test]
        public void SetMoveRemove_EntityInSet_MovedThenRemoved_SetClean()
        {
            using var env = CreateEnv();
            var a = env.Accessor;

            var handles = new EntityHandle[4];
            for (int i = 0; i < 4; i++)
            {
                handles[i] = a.AddEntity(PartitionA)
                    .Set(new TestInt { Value = i })
                    .Set(new TestVec())
                    .AssertComplete()
                    .Handle;
            }
            a.SubmitEntities();

            var groupA = a.WorldInfo.GetSingleGroupWithTags(PartitionA);
            var set = a.Set<SPSet>();

            // Add entities 0, 1, 2 to set
            var write = set.Write;
            write.Add(new EntityIndex(0, groupA));
            write.Add(new EntityIndex(1, groupA));
            write.Add(new EntityIndex(2, groupA));
            a.SubmitEntities();
            NAssert.AreEqual(3, set.Read.Count);

            // Move entity 0 to PartitionB, remove entity 1, entity 2 stays
            a.SetTag<SPPartitionB>(handles[0].ToIndex(a));
            a.RemoveEntity(handles[1]);
            a.SubmitEntities();

            // Entity 0: moved (should still be in set)
            // Entity 1: removed (should be gone from set)
            // Entity 2: stayed (should still be in set)
            NAssert.AreEqual(2, set.Read.Count, "Set should have 2 entities");
            NAssert.AreEqual(2, a.Query().InSet<SPSet>().Count());
        }

        [Test]
        public void SetMoveRemove_MoveRevertedByRemove_SetCorrect()
        {
            // Entity is in set, moved, then removed (reverting the move).
            // Set should not contain the entity after submission.
            using var env = CreateEnv();
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

            var groupA = a.WorldInfo.GetSingleGroupWithTags(PartitionA);
            var set = a.Set<SPSet>();

            set.Write.Add(new EntityIndex(0, groupA));
            set.Write.Add(new EntityIndex(1, groupA));
            a.SubmitEntities();

            // Move entity 0, then remove it (reverts the move)
            a.SetTag<SPPartitionB>(handles[0].ToIndex(a));
            a.RemoveEntity(handles[0].ToIndex(a));
            a.SubmitEntities();

            // Entity 0 is gone; entity 1 should still be in set
            NAssert.AreEqual(1, set.Read.Count);
            NAssert.IsFalse(a.EntityExists(handles[0]));
        }

        #endregion

        #region Native set write + native entity remove

        [Test]
        public void NativeSetAdd_PlusNativeRemove_DifferentEntities()
        {
            using var env = CreateEnv();
            var a = env.Accessor;
            var nativeEcs = a.ToNative();

            var handles = new EntityHandle[4];
            for (int i = 0; i < 4; i++)
            {
                handles[i] = a.AddEntity(PartitionA)
                    .Set(new TestInt { Value = i * 10 })
                    .Set(new TestVec())
                    .AssertComplete()
                    .Handle;
            }
            a.SubmitEntities();

            var groupA = a.WorldInfo.GetSingleGroupWithTags(PartitionA);
            var set = a.Set<SPSet>();

            // Deferred add entity 1 to set, native-remove entity 0
            a.Set<SPSet>().Defer.Add(new EntityIndex(1, groupA));
            nativeEcs.RemoveEntity(handles[0].ToIndex(a));
            a.SubmitEntities();

            // After swap-back from removing entity 0, entity at index 1 may have moved.
            // The set should still correctly track the entity that was originally at index 1.
            NAssert.AreEqual(3, a.CountEntitiesWithTags(PartitionA));
            NAssert.AreEqual(1, set.Read.Count, "Set should have 1 entity from the deferred add");
        }

        [Test]
        public void NativeSetAdd_PlusNativeRemove_SameEntity()
        {
            // Add entity to set via deferred ops, then native-remove the same entity.
            // The entity should not be in the set (it was removed).
            using var env = CreateEnv();
            var a = env.Accessor;
            var nativeEcs = a.ToNative();

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

            var groupA = a.WorldInfo.GetSingleGroupWithTags(PartitionA);
            var set = a.Set<SPSet>();

            // Add entity 0 to set, then native-remove entity 0
            a.Set<SPSet>().Defer.Add(new EntityIndex(0, groupA));
            nativeEcs.RemoveEntity(handles[0].ToIndex(a));
            a.SubmitEntities();

            NAssert.AreEqual(2, a.CountEntitiesWithTags(PartitionA));
            NAssert.AreEqual(0, set.Read.Count, "Entity was removed, set should be empty");
        }

        #endregion

        #region Multiple sets tracking same entity through structural changes

        [Test]
        public void TwoSets_SameEntity_RemoveEntity_BothSetsUpdated()
        {
            using var env = CreateEnv();
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

            var groupA = a.WorldInfo.GetSingleGroupWithTags(PartitionA);
            var set1 = a.Set<SPSet>();
            var set2 = a.Set<SPSet2>();

            // Entity 0 in both sets, entity 1 in set1 only
            set1.Write.Add(new EntityIndex(0, groupA));
            set1.Write.Add(new EntityIndex(1, groupA));
            set2.Write.Add(new EntityIndex(0, groupA));
            a.SubmitEntities();

            NAssert.AreEqual(2, set1.Read.Count);
            NAssert.AreEqual(1, set2.Read.Count);

            // Remove entity 0 (in both sets)
            a.RemoveEntity(handles[0]);
            a.SubmitEntities();

            NAssert.AreEqual(1, set1.Read.Count, "Set1 should lose entity 0, keep entity 1");
            NAssert.AreEqual(0, set2.Read.Count, "Set2 should lose entity 0, now empty");
        }

        [Test]
        public void TwoSets_SameEntity_MoveEntity_BothSetsTrack()
        {
            using var env = CreateEnv();
            var a = env.Accessor;

            var handle = a.AddEntity(PartitionA)
                .Set(new TestInt { Value = 42 })
                .Set(new TestVec())
                .AssertComplete()
                .Handle;
            a.SubmitEntities();

            var groupA = a.WorldInfo.GetSingleGroupWithTags(PartitionA);
            var set1 = a.Set<SPSet>();
            var set2 = a.Set<SPSet2>();

            set1.Write.Add(new EntityIndex(0, groupA));
            set2.Write.Add(new EntityIndex(0, groupA));
            a.SubmitEntities();

            // Move entity to PartitionB
            a.SetTag<SPPartitionB>(handle.ToIndex(a));
            a.SubmitEntities();

            NAssert.AreEqual(1, set1.Read.Count, "Set1 should track entity through move");
            NAssert.AreEqual(1, set2.Read.Count, "Set2 should track entity through move");

            // Verify query through each set finds the entity
            NAssert.AreEqual(1, a.Query().InSet<SPSet>().Count());
            NAssert.AreEqual(1, a.Query().InSet<SPSet2>().Count());
        }

        [Test]
        public void TwoSets_MoveAndRemoveDifferentEntities_BothSetsCorrect()
        {
            using var env = CreateEnv();
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

            var groupA = a.WorldInfo.GetSingleGroupWithTags(PartitionA);
            var set1 = a.Set<SPSet>();
            var set2 = a.Set<SPSet2>();

            // Set1: entities 0, 2, 4
            // Set2: entities 1, 3
            set1.Write.Add(new EntityIndex(0, groupA));
            set1.Write.Add(new EntityIndex(2, groupA));
            set1.Write.Add(new EntityIndex(4, groupA));
            set2.Write.Add(new EntityIndex(1, groupA));
            set2.Write.Add(new EntityIndex(3, groupA));
            a.SubmitEntities();

            // Move entity 0 (set1), remove entity 1 (set2)
            a.SetTag<SPPartitionB>(handles[0].ToIndex(a));
            a.RemoveEntity(handles[1]);
            a.SubmitEntities();

            NAssert.AreEqual(3, set1.Read.Count, "Set1: entity 0 moved but tracked, 2 and 4 stay");
            NAssert.AreEqual(1, set2.Read.Count, "Set2: entity 1 removed, entity 3 stays");
        }

        #endregion

        #region Large-scale set operations with structural changes

        [Test]
        public void LargeScale_SetWithScatteredRemoves_SetConsistent()
        {
            using var env = CreateEnv();
            var a = env.Accessor;

            const int total = 50;
            var handles = new EntityHandle[total];
            for (int i = 0; i < total; i++)
            {
                handles[i] = a.AddEntity(PartitionA)
                    .Set(new TestInt { Value = i })
                    .Set(new TestVec())
                    .AssertComplete()
                    .Handle;
            }
            a.SubmitEntities();

            var groupA = a.WorldInfo.GetSingleGroupWithTags(PartitionA);
            var set = a.Set<SPSet>();

            // Add every other entity to set (0, 2, 4, ..., 48)
            var write = set.Write;
            for (int i = 0; i < total; i += 2)
                write.Add(new EntityIndex(i, groupA));
            a.SubmitEntities();

            NAssert.AreEqual(25, set.Read.Count);

            // Remove every 5th entity (some in set, some not)
            for (int i = 0; i < total; i += 5)
                a.RemoveEntity(handles[i]);
            a.SubmitEntities();

            // Verify set count matches query count
            int setCount = set.Read.Count;
            int queryCount = a.Query().InSet<SPSet>().Count();
            NAssert.AreEqual(
                setCount,
                queryCount,
                $"Set count ({setCount}) should match query count ({queryCount})"
            );

            // The set should have lost entities 0, 10, 20, 30, 40 (in set, and removed)
            // Entities 5, 15, 25, 35, 45 were removed but NOT in set
            // So set lost 5 entities: 25 - 5 = 20
            NAssert.AreEqual(
                20,
                setCount,
                "Set should have 20 entities after removing 5 that were in set"
            );
        }

        [Test]
        public void LargeScale_SetWithMixedMovesAndRemoves()
        {
            using var env = CreateEnv();
            var a = env.Accessor;
            var nativeEcs = a.ToNative();

            const int total = 40;
            var handles = new EntityHandle[total];
            for (int i = 0; i < total; i++)
            {
                handles[i] = a.AddEntity(PartitionA)
                    .Set(new TestInt { Value = i })
                    .Set(new TestVec())
                    .AssertComplete()
                    .Handle;
            }
            a.SubmitEntities();

            var groupA = a.WorldInfo.GetSingleGroupWithTags(PartitionA);
            var set = a.Set<SPSet>();

            // Add first 30 to set
            var write = set.Write;
            for (int i = 0; i < 30; i++)
                write.Add(new EntityIndex(i, groupA));
            a.SubmitEntities();

            NAssert.AreEqual(30, set.Read.Count);

            // Move first 20 to PartitionB, native-remove every 4th among them (0, 4, 8, 12, 16)
            for (int i = 0; i < 20; i++)
                a.SetTag<SPPartitionB>(handles[i].ToIndex(a));
            for (int i = 0; i < 20; i += 4)
                nativeEcs.RemoveEntity(handles[i].ToIndex(a));
            a.SubmitEntities();

            // Verify set count matches query count
            int setCount = set.Read.Count;
            int queryCount = a.Query().InSet<SPSet>().Count();
            NAssert.AreEqual(
                setCount,
                queryCount,
                $"Set count ({setCount}) should match query count ({queryCount})"
            );

            // 5 removed (were in set): 30 - 5 = 25
            NAssert.AreEqual(25, setCount);
        }

        #endregion

        #region Set + Add + Remove + Move (all four in one frame)

        [Test]
        public void AllFourOps_SetAddMoveRemoveEntityAdd_Consistent()
        {
            using var env = CreateEnv();
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

            var groupA = a.WorldInfo.GetSingleGroupWithTags(PartitionA);
            var set = a.Set<SPSet>();

            // Add entities 0, 2 to set
            set.Write.Add(new EntityIndex(0, groupA));
            set.Write.Add(new EntityIndex(2, groupA));
            a.SubmitEntities();

            // In one submission:
            // - Deferred set add entity 3
            // - Move entity 0 to PartitionB (in set)
            // - Remove entity 1 (not in set, but causes swap-back)
            // - Add new entity
            a.Set<SPSet>().Defer.Add(new EntityIndex(3, groupA));
            a.SetTag<SPPartitionB>(handles[0].ToIndex(a));
            a.RemoveEntity(handles[1]);
            var newHandle = a.AddEntity(PartitionA)
                .Set(new TestInt { Value = 99 })
                .Set(new TestVec())
                .AssertComplete()
                .Handle;
            a.SubmitEntities();

            // Entity 0: moved (should stay in set)
            // Entity 1: removed (was not in set)
            // Entity 2: stayed (in set, possibly at new index after swap-back)
            // Entity 3: stayed (was added to set via deferred)
            // New entity: added (not in set)
            int setCount = set.Read.Count;
            int queryCount = a.Query().InSet<SPSet>().Count();
            NAssert.AreEqual(setCount, queryCount);
            NAssert.AreEqual(
                3,
                setCount,
                "Set should have entities 0 (moved), 2 (stayed), 3 (added to set)"
            );
        }

        #endregion

        #region Callbacks that trigger set modifications

        [Test]
        public void OnRemovedCallback_AddsToSet_SetUpdatedAcrossSubmissions()
        {
            using var env = CreateEnv();
            var a = env.Accessor;

            var handles = new EntityHandle[4];
            for (int i = 0; i < 4; i++)
            {
                handles[i] = a.AddEntity(PartitionA)
                    .Set(new TestInt { Value = i * 10 })
                    .Set(new TestVec())
                    .AssertComplete()
                    .Handle;
            }
            a.SubmitEntities();

            // Callback: when entity removed from PartitionA, add entity 3 to set
            var subscription = a
                .Events.EntitiesWithTags(PartitionA)
                .OnRemoved(
                    (group, indices) =>
                    {
                        if (a.EntityExists(handles[3]))
                        {
                            var idx = handles[3].ToIndex(a);
                            a.Set<SPSet>().Write.Add(idx);
                        }
                    }
                );

            // Remove entity 0 -> callback fires -> adds entity 3 to set
            a.RemoveEntity(handles[0]);
            a.SubmitEntities();

            NAssert.IsFalse(a.EntityExists(handles[0]));
            NAssert.AreEqual(
                1,
                a.Set<SPSet>().Read.Count,
                "Entity 3 should be in set via callback"
            );

            subscription.Dispose();
        }

        [Test]
        public void OnRemovedCallback_RemovesAnotherEntity_SetCleanedUp()
        {
            using var env = CreateEnv();
            var a = env.Accessor;

            var handles = new EntityHandle[4];
            for (int i = 0; i < 4; i++)
            {
                handles[i] = a.AddEntity(PartitionA)
                    .Set(new TestInt { Value = i * 10 })
                    .Set(new TestVec())
                    .AssertComplete()
                    .Handle;
            }
            a.SubmitEntities();

            var groupA = a.WorldInfo.GetSingleGroupWithTags(PartitionA);
            var set = a.Set<SPSet>();

            // Entities 1 and 2 in set
            set.Write.Add(new EntityIndex(1, groupA));
            set.Write.Add(new EntityIndex(2, groupA));
            a.SubmitEntities();

            // Callback: on remove, also remove entity 2
            var subscription = a
                .Events.EntitiesWithTags(PartitionA)
                .OnRemoved(
                    (group, indices) =>
                    {
                        if (a.EntityExists(handles[2]))
                            a.RemoveEntity(handles[2]);
                    }
                );

            // Remove entity 0 -> callback removes entity 2 (which is in set)
            a.RemoveEntity(handles[0]);
            a.SubmitEntities();

            NAssert.IsFalse(a.EntityExists(handles[0]));
            NAssert.IsFalse(a.EntityExists(handles[2]));
            // Set should have lost entity 2 (removed by callback)
            // Entity 1 should still be in set
            NAssert.AreEqual(1, set.Read.Count, "Only entity 1 should remain in set");

            subscription.Dispose();
        }

        #endregion

        #region Multi-frame set + structural changes

        [Test]
        public void MultiFrame_SetAddRemoveMove_StaysConsistent()
        {
            using var env = CreateEnv();
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

            var groupA = a.WorldInfo.GetSingleGroupWithTags(PartitionA);
            var set = a.Set<SPSet>();

            // Frame 1: Add entities 0, 2, 4 to set
            set.Write.Add(new EntityIndex(0, groupA));
            set.Write.Add(new EntityIndex(2, groupA));
            set.Write.Add(new EntityIndex(4, groupA));
            a.SubmitEntities();
            NAssert.AreEqual(3, set.Read.Count);

            // Frame 2: Move entity 0 to PartitionB, remove entity 1
            a.SetTag<SPPartitionB>(handles[0].ToIndex(a));
            a.RemoveEntity(handles[1]);
            a.SubmitEntities();
            NAssert.AreEqual(3, set.Read.Count, "Entity 0 moved but still tracked, 1 not in set");

            // Frame 3: Move entity 0 back, remove entity 4 (in set)
            a.SetTag<SPPartitionA>(handles[0].ToIndex(a));
            a.RemoveEntity(handles[4]);
            a.SubmitEntities();
            NAssert.AreEqual(2, set.Read.Count, "Lost entity 4, entity 0 moved back");

            // Frame 4: Remove entity 0 (in set), add new entity
            a.RemoveEntity(handles[0]);
            var newHandle = a.AddEntity(PartitionA)
                .Set(new TestInt { Value = 99 })
                .Set(new TestVec())
                .AssertComplete()
                .Handle;
            a.SubmitEntities();
            NAssert.AreEqual(1, set.Read.Count, "Only entity 2 remains in set");

            // Verify query through set matches
            NAssert.AreEqual(1, a.Query().InSet<SPSet>().Count());
        }

        #endregion
    }
}
