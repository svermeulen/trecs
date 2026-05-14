using NUnit.Framework;
using NAssert = NUnit.Framework.Assert;

namespace Trecs.Tests
{
    [TestFixture]
    public class ComplexLifecycleTests
    {
        static readonly TagSet PartitionA = TagSet.FromTags(TestTags.Gamma, TestTags.PartitionA);
        static readonly TagSet PartitionB = TagSet.FromTags(TestTags.Gamma, TestTags.PartitionB);

        #region Add-Remove-Add Cycles

        [Test]
        public void Lifecycle_AddRemoveAdd_CorrectCount()
        {
            using var env = EcsTestHelper.CreateEnvironment(TestTemplates.SimpleAlpha);
            var a = env.Accessor;

            // Add
            a.AddEntity(TestTags.Alpha).Set(new TestInt { Value = 1 }).AssertComplete();
            a.SubmitEntities();
            NAssert.AreEqual(1, a.CountEntitiesWithTags(TestTags.Alpha));

            // Remove
            var group = a.WorldInfo.GetSingleGroupWithTags(TestTags.Alpha);
            a.RemoveEntity(new EntityIndex(0, group));
            a.SubmitEntities();
            NAssert.AreEqual(0, a.CountEntitiesWithTags(TestTags.Alpha));

            // Add again
            a.AddEntity(TestTags.Alpha).Set(new TestInt { Value = 2 }).AssertComplete();
            a.SubmitEntities();
            NAssert.AreEqual(1, a.CountEntitiesWithTags(TestTags.Alpha));

            var comp = a.Query().WithTags(TestTags.Alpha).Single().Get<TestInt>();
            NAssert.AreEqual(2, comp.Read.Value);
        }

        [Test]
        public void Lifecycle_AddRemoveAdd_OldRefInvalid_NewRefValid()
        {
            using var env = EcsTestHelper.CreateEnvironment(TestTemplates.SimpleAlpha);
            var a = env.Accessor;

            var init1 = a.AddEntity(TestTags.Alpha).AssertComplete();
            var ref1 = init1.Handle;
            a.SubmitEntities();

            a.RemoveEntity(ref1);
            a.SubmitEntities();

            var init2 = a.AddEntity(TestTags.Alpha).AssertComplete();
            var ref2 = init2.Handle;
            a.SubmitEntities();

            NAssert.IsFalse(ref1.Exists(a));
            NAssert.IsTrue(ref2.Exists(a));
            NAssert.IsFalse(ref1.Equals(ref2));
        }

        #endregion

        #region Move Back And Forth

        [Test]
        public void Lifecycle_MoveBackAndForth_EndsInOriginalState()
        {
            using var env = EcsTestHelper.CreateEnvironment(TestTemplates.WithPartitions);
            var a = env.Accessor;

            var init = a.AddEntity(PartitionA)
                .Set(new TestInt { Value = 77 })
                .Set(new TestVec { X = 1.0f, Y = 2.0f })
                .AssertComplete();
            var entityHandle = init.Handle;
            a.SubmitEntities();

            // Move A -> B
            a.SetTag<TestPartitionB>(entityHandle.ToIndex(a));
            a.SubmitEntities();
            NAssert.AreEqual(0, a.CountEntitiesWithTags(PartitionA));
            NAssert.AreEqual(1, a.CountEntitiesWithTags(PartitionB));

            // Move B -> A
            a.SetTag<TestPartitionA>(entityHandle.ToIndex(a));
            a.SubmitEntities();
            NAssert.AreEqual(1, a.CountEntitiesWithTags(PartitionA));
            NAssert.AreEqual(0, a.CountEntitiesWithTags(PartitionB));

            // Component data preserved
            var comp = a.Component<TestInt>(entityHandle);
            NAssert.AreEqual(77, comp.Read.Value);
        }

        [Test]
        public void Lifecycle_MoveMultipleEntities_CountsCorrect()
        {
            using var env = EcsTestHelper.CreateEnvironment(TestTemplates.WithPartitions);
            var a = env.Accessor;

            var refs = new EntityHandle[5];
            for (int i = 0; i < 5; i++)
            {
                var init = a.AddEntity(PartitionA).Set(new TestInt { Value = i }).AssertComplete();
                refs[i] = init.Handle;
            }
            a.SubmitEntities();

            // Move entities 0, 2, 4 to PartitionB
            a.SetTag<TestPartitionB>(refs[0].ToIndex(a));
            a.SetTag<TestPartitionB>(refs[2].ToIndex(a));
            a.SetTag<TestPartitionB>(refs[4].ToIndex(a));
            a.SubmitEntities();

            NAssert.AreEqual(2, a.CountEntitiesWithTags(PartitionA));
            NAssert.AreEqual(3, a.CountEntitiesWithTags(PartitionB));

            // All refs should still be valid
            for (int i = 0; i < 5; i++)
            {
                NAssert.IsTrue(refs[i].Exists(a), "Entity {0} should exist", i);
            }
        }

        #endregion

        #region Mixed Operations In Single Submission

        [Test]
        public void Lifecycle_AddAndRemoveDifferentEntities_SingleSubmission()
        {
            using var env = EcsTestHelper.CreateEnvironment(TestTemplates.SimpleAlpha);
            var a = env.Accessor;

            // Setup: add 3 entities
            var refs = new EntityHandle[3];
            for (int i = 0; i < 3; i++)
            {
                var init = a.AddEntity(TestTags.Alpha)
                    .Set(new TestInt { Value = (i + 1) * 100 })
                    .AssertComplete();
                refs[i] = init.Handle;
            }
            a.SubmitEntities();

            // In one submission: remove entity 1, add 2 new entities
            a.RemoveEntity(refs[1]);
            a.AddEntity(TestTags.Alpha).Set(new TestInt { Value = 400 }).AssertComplete();
            a.AddEntity(TestTags.Alpha).Set(new TestInt { Value = 500 }).AssertComplete();
            a.SubmitEntities();

            NAssert.AreEqual(4, a.CountEntitiesWithTags(TestTags.Alpha));
            NAssert.IsFalse(refs[1].Exists(a));
            NAssert.IsTrue(refs[0].Exists(a));
            NAssert.IsTrue(refs[2].Exists(a));
        }

        [Test]
        public void Lifecycle_MoveAndAdd_SingleSubmission()
        {
            using var env = EcsTestHelper.CreateEnvironment(TestTemplates.WithPartitions);
            var a = env.Accessor;

            var init = a.AddEntity(PartitionA).Set(new TestInt { Value = 10 }).AssertComplete();
            var entityHandle = init.Handle;
            a.SubmitEntities();

            // In one submission: move existing entity + add new one
            a.SetTag<TestPartitionB>(entityHandle.ToIndex(a));
            a.AddEntity(PartitionA).Set(new TestInt { Value = 20 }).AssertComplete();
            a.SubmitEntities();

            NAssert.AreEqual(1, a.CountEntitiesWithTags(PartitionA));
            NAssert.AreEqual(1, a.CountEntitiesWithTags(PartitionB));
        }

        [Test]
        public void Lifecycle_RemoveAndMoveInSameSubmission_DifferentEntities()
        {
            using var env = EcsTestHelper.CreateEnvironment(TestTemplates.WithPartitions);
            var a = env.Accessor;

            var refs = new EntityHandle[3];
            for (int i = 0; i < 3; i++)
            {
                var init = a.AddEntity(PartitionA)
                    .Set(new TestInt { Value = i * 10 })
                    .AssertComplete();
                refs[i] = init.Handle;
            }
            a.SubmitEntities();

            // Remove entity 0, move entity 2 to PartitionB
            a.RemoveEntity(refs[0]);
            a.SetTag<TestPartitionB>(refs[2].ToIndex(a));
            a.SubmitEntities();

            NAssert.AreEqual(1, a.CountEntitiesWithTags(PartitionA));
            NAssert.AreEqual(1, a.CountEntitiesWithTags(PartitionB));
            NAssert.IsFalse(refs[0].Exists(a));
            NAssert.IsTrue(refs[1].Exists(a));
            NAssert.IsTrue(refs[2].Exists(a));

            // Verify component values
            var comp1 = a.Component<TestInt>(refs[1]);
            var comp2 = a.Component<TestInt>(refs[2]);
            NAssert.AreEqual(10, comp1.Read.Value);
            NAssert.AreEqual(20, comp2.Read.Value);
        }

        [Test]
        public void Lifecycle_AddAndRemove_SameSubmission_NewEntityHandleResolvable()
        {
            using var env = EcsTestHelper.CreateEnvironment(TestTemplates.SimpleAlpha);
            var a = env.Accessor;

            // Setup: add 3 entities
            var refs = new EntityHandle[3];
            for (int i = 0; i < 3; i++)
            {
                var init = a.AddEntity(TestTags.Alpha)
                    .Set(new TestInt { Value = (i + 1) * 100 })
                    .AssertComplete();
                refs[i] = init.Handle;
            }
            a.SubmitEntities();

            // In one submission: remove entity 0, add a new entity
            a.RemoveEntity(refs[0]);
            var newInit = a.AddEntity(TestTags.Alpha)
                .Set(new TestInt { Value = 999 })
                .AssertComplete();
            var newRef = newInit.Handle;
            a.SubmitEntities();

            // The new entity's ref should resolve correctly
            NAssert.IsTrue(newRef.Exists(a));
            var newIndex = newRef.ToIndex(a);
            var comp = a.Component<TestInt>(newIndex);
            NAssert.AreEqual(
                999,
                comp.Read.Value,
                "New entity ref should resolve to correct data after same-submission add+remove"
            );
        }

        #endregion

        #region Data Integrity Under Stress

        [Test]
        public void Lifecycle_ScatteredRemoves_DataIntegrity()
        {
            using var env = EcsTestHelper.CreateEnvironment(TestTemplates.SimpleAlpha);
            var a = env.Accessor;

            const int count = 50;
            var refs = new EntityHandle[count];

            for (int i = 0; i < count; i++)
            {
                var init = a.AddEntity(TestTags.Alpha)
                    .Set(new TestInt { Value = i })
                    .AssertComplete();
                refs[i] = init.Handle;
            }
            a.SubmitEntities();

            // Remove every other entity
            for (int i = 0; i < count; i += 2)
            {
                a.RemoveEntity(refs[i]);
            }
            a.SubmitEntities();

            NAssert.AreEqual(count / 2, a.CountEntitiesWithTags(TestTags.Alpha));

            // Verify surviving entities have correct values
            for (int i = 1; i < count; i += 2)
            {
                NAssert.IsTrue(refs[i].Exists(a), "Entity {0} should exist", i);
                var comp = a.Component<TestInt>(refs[i]);
                NAssert.AreEqual(i, comp.Read.Value, "Entity {0} should have value {0}", i);
            }

            // Verify removed entities don't exist
            for (int i = 0; i < count; i += 2)
            {
                NAssert.IsFalse(refs[i].Exists(a), "Entity {0} should be removed", i);
            }
        }

        [Test]
        public void Lifecycle_MoveAll_ThenMoveBack_DataPreserved()
        {
            using var env = EcsTestHelper.CreateEnvironment(TestTemplates.WithPartitions);
            var a = env.Accessor;

            const int count = 20;
            var refs = new EntityHandle[count];

            for (int i = 0; i < count; i++)
            {
                var init = a.AddEntity(PartitionA)
                    .Set(new TestInt { Value = i * 10 })
                    .Set(new TestVec { X = i, Y = i + 0.5f })
                    .AssertComplete();
                refs[i] = init.Handle;
            }
            a.SubmitEntities();

            // Move all to PartitionB
            for (int i = 0; i < count; i++)
            {
                a.SetTag<TestPartitionB>(refs[i].ToIndex(a));
            }
            a.SubmitEntities();

            NAssert.AreEqual(0, a.CountEntitiesWithTags(PartitionA));
            NAssert.AreEqual(count, a.CountEntitiesWithTags(PartitionB));

            // Move all back to PartitionA
            for (int i = 0; i < count; i++)
            {
                a.SetTag<TestPartitionA>(refs[i].ToIndex(a));
            }
            a.SubmitEntities();

            NAssert.AreEqual(count, a.CountEntitiesWithTags(PartitionA));
            NAssert.AreEqual(0, a.CountEntitiesWithTags(PartitionB));

            // Verify all data is preserved
            for (int i = 0; i < count; i++)
            {
                var intComp = a.Component<TestInt>(refs[i]);
                var vecComp = a.Component<TestVec>(refs[i]);
                NAssert.AreEqual(i * 10, intComp.Read.Value, "Int value for entity {0}", i);
                NAssert.AreEqual((float)i, vecComp.Read.X, 0.001f, "Vec X for entity {0}", i);
                NAssert.AreEqual(i + 0.5f, vecComp.Read.Y, 0.001f, "Vec Y for entity {0}", i);
            }
        }

        #endregion

        #region Observer + Structural Changes

        [Test]
        public void Observer_MoveDoesNotFireOnAdded()
        {
            // Moves only fire OnMoved, not OnAdded/OnRemoved
            using var env = EcsTestHelper.CreateEnvironment(TestTemplates.WithPartitions);
            var a = env.Accessor;

            var init = a.AddEntity(PartitionA).AssertComplete();
            var entityHandle = init.Handle;
            a.SubmitEntities();

            int addedCallCount = 0;
            var sub = a
                .Events.EntitiesWithTags(PartitionB)
                .OnAdded(
                    (GroupIndex group, EntityRange indices) =>
                    {
                        addedCallCount++;
                    }
                );

            a.SetTag<TestPartitionB>(entityHandle.ToIndex(a));
            a.SubmitEntities();

            NAssert.AreEqual(0, addedCallCount, "OnAdded should NOT fire for moved entities");
            sub.Dispose();
        }

        [Test]
        public void Observer_MoveDoesNotFireOnRemoved()
        {
            // Moves only fire OnMoved, not OnAdded/OnRemoved
            using var env = EcsTestHelper.CreateEnvironment(TestTemplates.WithPartitions);
            var a = env.Accessor;

            var init = a.AddEntity(PartitionA).AssertComplete();
            var entityHandle = init.Handle;
            a.SubmitEntities();

            int removedCallCount = 0;
            var sub = a
                .Events.EntitiesWithTags(PartitionA)
                .OnRemoved(
                    (GroupIndex group, EntityRange indices) =>
                    {
                        removedCallCount++;
                    }
                );

            a.SetTag<TestPartitionB>(entityHandle.ToIndex(a));
            a.SubmitEntities();

            NAssert.AreEqual(0, removedCallCount, "OnRemoved should NOT fire for moved entities");
            sub.Dispose();
        }

        [Test]
        public void Observer_MoveFiresOnMoved()
        {
            using var env = EcsTestHelper.CreateEnvironment(TestTemplates.WithPartitions);
            var a = env.Accessor;

            var init = a.AddEntity(PartitionA).AssertComplete();
            var entityHandle = init.Handle;
            a.SubmitEntities();

            int movedCallCount = 0;
            GroupIndex observedFrom = default;
            GroupIndex observedTo = default;
            var sub = a
                .Events.EntitiesWithTags(PartitionB)
                .OnMoved(
                    (GroupIndex fromGroup, GroupIndex toGroup, EntityRange indices) =>
                    {
                        movedCallCount++;
                        observedFrom = fromGroup;
                        observedTo = toGroup;
                    }
                );

            var expectedGroupA = a.WorldInfo.GetSingleGroupWithTags(PartitionA);
            var expectedGroupB = a.WorldInfo.GetSingleGroupWithTags(PartitionB);

            a.SetTag<TestPartitionB>(entityHandle.ToIndex(a));
            a.SubmitEntities();

            NAssert.AreEqual(1, movedCallCount, "OnMoved should fire exactly once");
            NAssert.AreEqual(expectedGroupA, observedFrom);
            NAssert.AreEqual(expectedGroupB, observedTo);
            sub.Dispose();
        }

        [Test]
        public void Observer_MultipleSubmissions_CallbacksFire()
        {
            using var env = EcsTestHelper.CreateEnvironment(TestTemplates.SimpleAlpha);
            var a = env.Accessor;

            int callCount = 0;
            var sub = a
                .Events.EntitiesWithTags(TestTags.Alpha)
                .OnAdded(
                    (GroupIndex group, EntityRange indices) =>
                    {
                        callCount++;
                    }
                );

            // Multiple submissions, each should trigger the callback
            a.AddEntity(TestTags.Alpha).AssertComplete();
            a.SubmitEntities();
            NAssert.AreEqual(1, callCount);

            a.AddEntity(TestTags.Alpha).AssertComplete();
            a.SubmitEntities();
            NAssert.AreEqual(2, callCount);

            a.AddEntity(TestTags.Alpha).AssertComplete();
            a.SubmitEntities();
            NAssert.AreEqual(3, callCount);

            sub.Dispose();
        }

        #endregion

        #region Native Accessor Operations

        [Test]
        public void Native_AddEntity_CountCorrectAfterSubmit()
        {
            using var env = EcsTestHelper.CreateEnvironment(TestTemplates.SimpleAlpha);
            var a = env.Accessor;
            var nativeEcs = a.ToNative();

            var init = nativeEcs.AddEntity(TestTags.Alpha, sortKey: 0);
            init.Set(new TestInt { Value = 99 });
            a.SubmitEntities();

            NAssert.AreEqual(1, a.CountEntitiesWithTags(TestTags.Alpha));
        }

        [Test]
        public void Native_AddMultiple_AllSubmitted()
        {
            using var env = EcsTestHelper.CreateEnvironment(TestTemplates.SimpleAlpha);
            var a = env.Accessor;
            var nativeEcs = a.ToNative();

            for (int i = 0; i < 3; i++)
            {
                var init = nativeEcs.AddEntity(TestTags.Alpha, sortKey: (uint)i);
                init.Set(new TestInt { Value = i * 10 });
            }
            a.SubmitEntities();

            NAssert.AreEqual(3, a.CountEntitiesWithTags(TestTags.Alpha));
        }

        [Test]
        public void Native_MixedManagedAndNativeAdds()
        {
            using var env = EcsTestHelper.CreateEnvironment(TestTemplates.SimpleAlpha);
            var a = env.Accessor;
            var nativeEcs = a.ToNative();

            // Mix managed and native adds
            a.AddEntity(TestTags.Alpha).Set(new TestInt { Value = 1 }).AssertComplete();

            var nativeInit = nativeEcs.AddEntity(TestTags.Alpha, sortKey: 0);
            nativeInit.Set(new TestInt { Value = 2 });

            a.AddEntity(TestTags.Alpha).Set(new TestInt { Value = 3 }).AssertComplete();

            a.SubmitEntities();

            NAssert.AreEqual(3, a.CountEntitiesWithTags(TestTags.Alpha));
        }

        [Test]
        public void Native_RemoveEntity_CountDecreases()
        {
            using var env = EcsTestHelper.CreateEnvironment(TestTemplates.SimpleAlpha);
            var a = env.Accessor;
            var nativeEcs = a.ToNative();

            for (int i = 0; i < 3; i++)
            {
                a.AddEntity(TestTags.Alpha).Set(new TestInt { Value = i }).AssertComplete();
            }
            a.SubmitEntities();

            var group = a.WorldInfo.GetSingleGroupWithTags(TestTags.Alpha);
            nativeEcs.RemoveEntity(new EntityIndex(1, group));
            a.SubmitEntities();

            NAssert.AreEqual(2, a.CountEntitiesWithTags(TestTags.Alpha));
        }

        [Test]
        public void Native_MoveEntity_ChangesGroup()
        {
            using var env = EcsTestHelper.CreateEnvironment(TestTemplates.WithPartitions);
            var a = env.Accessor;
            var nativeEcs = a.ToNative();

            a.AddEntity(PartitionA).Set(new TestInt { Value = 10 }).AssertComplete();
            a.SubmitEntities();

            var groupA = a.WorldInfo.GetSingleGroupWithTags(PartitionA);
            nativeEcs.SetTag<TestPartitionB>(new EntityIndex(0, groupA));
            a.SubmitEntities();

            NAssert.AreEqual(0, a.CountEntitiesWithTags(PartitionA));
            NAssert.AreEqual(1, a.CountEntitiesWithTags(PartitionB));
        }

        [Test]
        public void Native_EntityHandleResolution_Works()
        {
            using var env = EcsTestHelper.CreateEnvironment(TestTemplates.SimpleAlpha);
            var a = env.Accessor;
            var nativeEcs = a.ToNative();

            var init = a.AddEntity(TestTags.Alpha).Set(new TestInt { Value = 42 }).AssertComplete();
            var entityHandle = init.Handle;
            a.SubmitEntities();

            // Native accessor should be able to resolve the entity ref
            bool found = entityHandle.TryToIndex(nativeEcs, out var entityIndex);
            NAssert.IsTrue(found);
            NAssert.AreEqual(42, a.Component<TestInt>(entityIndex).Read.Value);
        }

        #endregion

        #region Query Edge Cases

        [Test]
        public void Query_SingleThrowsOnMultipleEntities()
        {
            using var env = EcsTestHelper.CreateEnvironment(TestTemplates.SimpleAlpha);
            var a = env.Accessor;

            a.AddEntity(TestTags.Alpha).AssertComplete();
            a.AddEntity(TestTags.Alpha).AssertComplete();
            a.SubmitEntities();

            NAssert.Catch(() => a.Query().WithTags(TestTags.Alpha).Single());
        }

        [Test]
        public void Query_SingleThrowsOnEmpty()
        {
            using var env = EcsTestHelper.CreateEnvironment(TestTemplates.SimpleAlpha);
            var a = env.Accessor;

            NAssert.Catch(() => a.Query().WithTags(TestTags.Alpha).Single());
        }

        [Test]
        public void Query_TrySingle_FalseOnMultiple()
        {
            using var env = EcsTestHelper.CreateEnvironment(TestTemplates.SimpleAlpha);
            var a = env.Accessor;

            a.AddEntity(TestTags.Alpha).AssertComplete();
            a.AddEntity(TestTags.Alpha).AssertComplete();
            a.SubmitEntities();

            NAssert.IsFalse(a.Query().WithTags(TestTags.Alpha).TrySingle(out _));
        }

        [Test]
        public void Query_Count_AcrossMultipleGroups()
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

            // Count with just the Gamma tag should match both groups
            var gammaCount = a.Query().WithTags(TestTags.Gamma).Count();
            NAssert.AreEqual(5, gammaCount);
        }

        [Test]
        public void Query_Count_AfterMixedOps()
        {
            using var env = EcsTestHelper.CreateEnvironment(TestTemplates.WithPartitions);
            var a = env.Accessor;

            var refs = new EntityHandle[5];
            for (int i = 0; i < 5; i++)
            {
                var init = a.AddEntity(PartitionA).AssertComplete();
                refs[i] = init.Handle;
            }
            a.SubmitEntities();

            // Remove 2, move 1 to PartitionB
            a.RemoveEntity(refs[0]);
            a.RemoveEntity(refs[1]);
            a.SetTag<TestPartitionB>(refs[2].ToIndex(a));
            a.SubmitEntities();

            NAssert.AreEqual(2, a.Query().WithTags(PartitionA).Count());
            NAssert.AreEqual(1, a.Query().WithTags(PartitionB).Count());
            NAssert.AreEqual(3, a.Query().WithTags(TestTags.Gamma).Count());
        }

        [Test]
        public void Query_GroupSlices_CoversAllEntities()
        {
            using var env = EcsTestHelper.CreateEnvironment(TestTemplates.WithPartitions);
            var a = env.Accessor;

            for (int i = 0; i < 3; i++)
            {
                a.AddEntity(PartitionA).Set(new TestInt { Value = i }).AssertComplete();
            }
            for (int i = 0; i < 2; i++)
            {
                a.AddEntity(PartitionB).Set(new TestInt { Value = 10 + i }).AssertComplete();
            }
            a.SubmitEntities();

            int totalEntities = 0;
            int groupCount = 0;
            foreach (var slice in a.Query().WithTags(TestTags.Gamma).GroupSlices())
            {
                groupCount++;
                totalEntities += (int)slice.Count;
            }

            NAssert.AreEqual(2, groupCount, "Should iterate over both groups");
            NAssert.AreEqual(5, totalEntities, "Should cover all entities");
        }

        #endregion

        #region EntityHandle Under Complex Structural Changes

        [Test]
        public void EntityHandle_MultipleRemoves_AllRefsCorrectlyInvalidated()
        {
            using var env = EcsTestHelper.CreateEnvironment(TestTemplates.SimpleAlpha);
            var a = env.Accessor;

            var refs = new EntityHandle[10];
            for (int i = 0; i < 10; i++)
            {
                var init = a.AddEntity(TestTags.Alpha)
                    .Set(new TestInt { Value = i })
                    .AssertComplete();
                refs[i] = init.Handle;
            }
            a.SubmitEntities();

            // Remove entities 0, 3, 5, 7, 9
            a.RemoveEntity(refs[0]);
            a.RemoveEntity(refs[3]);
            a.RemoveEntity(refs[5]);
            a.RemoveEntity(refs[7]);
            a.RemoveEntity(refs[9]);
            a.SubmitEntities();

            // Removed refs should be invalid
            NAssert.IsFalse(refs[0].Exists(a));
            NAssert.IsFalse(refs[3].Exists(a));
            NAssert.IsFalse(refs[5].Exists(a));
            NAssert.IsFalse(refs[7].Exists(a));
            NAssert.IsFalse(refs[9].Exists(a));

            // Surviving refs should be valid with correct data
            int[] surviving = { 1, 2, 4, 6, 8 };
            foreach (var i in surviving)
            {
                NAssert.IsTrue(refs[i].Exists(a), "Entity {0} should exist", i);
                var comp = a.Component<TestInt>(refs[i]);
                NAssert.AreEqual(i, comp.Read.Value, "Entity {0} data mismatch", i);
            }
        }

        [Test]
        public void EntityHandle_MoveAndRemoveInterleaved_AllRefsCorrect()
        {
            using var env = EcsTestHelper.CreateEnvironment(TestTemplates.WithPartitions);
            var a = env.Accessor;

            var refs = new EntityHandle[6];
            for (int i = 0; i < 6; i++)
            {
                var init = a.AddEntity(PartitionA)
                    .Set(new TestInt { Value = i * 10 })
                    .AssertComplete();
                refs[i] = init.Handle;
            }
            a.SubmitEntities();

            // Entity 0: remove
            // Entity 1: move to PartitionB
            // Entity 2: stays
            // Entity 3: remove
            // Entity 4: move to PartitionB
            // Entity 5: stays
            a.RemoveEntity(refs[0]);
            a.SetTag<TestPartitionB>(refs[1].ToIndex(a));
            a.RemoveEntity(refs[3]);
            a.SetTag<TestPartitionB>(refs[4].ToIndex(a));
            a.SubmitEntities();

            NAssert.IsFalse(refs[0].Exists(a));
            NAssert.IsTrue(refs[1].Exists(a));
            NAssert.IsTrue(refs[2].Exists(a));
            NAssert.IsFalse(refs[3].Exists(a));
            NAssert.IsTrue(refs[4].Exists(a));
            NAssert.IsTrue(refs[5].Exists(a));

            NAssert.AreEqual(2, a.CountEntitiesWithTags(PartitionA));
            NAssert.AreEqual(2, a.CountEntitiesWithTags(PartitionB));

            // Verify data integrity for surviving entities
            NAssert.AreEqual(10, a.Component<TestInt>(refs[1]).Read.Value);
            NAssert.AreEqual(20, a.Component<TestInt>(refs[2]).Read.Value);
            NAssert.AreEqual(40, a.Component<TestInt>(refs[4]).Read.Value);
            NAssert.AreEqual(50, a.Component<TestInt>(refs[5]).Read.Value);
        }

        #endregion

        #region Multiple Component Types Under Structural Changes

        [Test]
        public void MultiComp_ScatteredRemoves_AllComponentsConsistent()
        {
            using var env = EcsTestHelper.CreateEnvironment(TestTemplates.WithPartitions);
            var a = env.Accessor;

            const int count = 30;
            var refs = new EntityHandle[count];

            for (int i = 0; i < count; i++)
            {
                var init = a.AddEntity(PartitionA)
                    .Set(new TestInt { Value = i })
                    .Set(new TestVec { X = i * 1.0f, Y = i * 2.0f })
                    .AssertComplete();
                refs[i] = init.Handle;
            }
            a.SubmitEntities();

            // Remove every 3rd entity
            for (int i = 0; i < count; i += 3)
            {
                a.RemoveEntity(refs[i]);
            }
            a.SubmitEntities();

            // Verify surviving entities have consistent data across both components
            for (int i = 0; i < count; i++)
            {
                if (i % 3 == 0)
                {
                    NAssert.IsFalse(refs[i].Exists(a));
                    continue;
                }

                NAssert.IsTrue(refs[i].Exists(a), "Entity {0} should exist", i);
                var intComp = a.Component<TestInt>(refs[i]);
                var vecComp = a.Component<TestVec>(refs[i]);

                NAssert.AreEqual(i, intComp.Read.Value, "TestInt for entity {0}", i);
                NAssert.AreEqual(i * 1.0f, vecComp.Read.X, 0.001f, "TestVec.X for entity {0}", i);
                NAssert.AreEqual(i * 2.0f, vecComp.Read.Y, 0.001f, "TestVec.Y for entity {0}", i);
            }
        }

        [Test]
        public void MultiComp_NativeMove_DataPreserved()
        {
            using var env = EcsTestHelper.CreateEnvironment(TestTemplates.WithPartitions);
            var a = env.Accessor;
            var nativeEcs = a.ToNative();

            var init = a.AddEntity(PartitionA)
                .Set(new TestInt { Value = 55 })
                .Set(new TestVec { X = 3.0f, Y = 4.0f })
                .AssertComplete();
            var entityHandle = init.Handle;
            a.SubmitEntities();

            // Move via native path
            var groupA = a.WorldInfo.GetSingleGroupWithTags(PartitionA);
            nativeEcs.SetTag<TestPartitionB>(new EntityIndex(0, groupA));
            a.SubmitEntities();

            NAssert.AreEqual(0, a.CountEntitiesWithTags(PartitionA));
            NAssert.AreEqual(1, a.CountEntitiesWithTags(PartitionB));

            // Data should be preserved
            NAssert.IsTrue(entityHandle.Exists(a));
            NAssert.AreEqual(55, a.Component<TestInt>(entityHandle).Read.Value);
            NAssert.AreEqual(3.0f, a.Component<TestVec>(entityHandle).Read.X, 0.001f);
            NAssert.AreEqual(4.0f, a.Component<TestVec>(entityHandle).Read.Y, 0.001f);
        }

        #endregion

        #region Observer + Add/Remove Same Submission

        [Test]
        public void Observer_AddAndRemoveDifferent_SameSubmission_BothFire()
        {
            using var env = EcsTestHelper.CreateEnvironment(TestTemplates.SimpleAlpha);
            var a = env.Accessor;

            // Setup: add 1 entity
            var init = a.AddEntity(TestTags.Alpha).Set(new TestInt { Value = 1 }).AssertComplete();
            var existingRef = init.Handle;
            a.SubmitEntities();

            int addedCount = 0;
            int removedCount = 0;
            var addSub = a
                .Events.EntitiesWithTags(TestTags.Alpha)
                .OnAdded(
                    (GroupIndex g, EntityRange i) =>
                    {
                        addedCount += i.End - i.Start;
                    }
                );
            var removeSub = a
                .Events.EntitiesWithTags(TestTags.Alpha)
                .OnRemoved(
                    (GroupIndex g, EntityRange i) =>
                    {
                        removedCount += i.End - i.Start;
                    }
                );

            // In one submission: remove existing, add new
            a.RemoveEntity(existingRef);
            a.AddEntity(TestTags.Alpha).Set(new TestInt { Value = 2 }).AssertComplete();
            a.SubmitEntities();

            NAssert.AreEqual(1u, addedCount, "OnAdded should fire for the new entity");
            NAssert.AreEqual(1u, removedCount, "OnRemoved should fire for the removed entity");
            NAssert.AreEqual(1, a.CountEntitiesWithTags(TestTags.Alpha));

            addSub.Dispose();
            removeSub.Dispose();
        }

        #endregion

        #region WorldInfo Edge Cases

        [Test]
        public void WorldInfo_GetGroupsWithTags_ReturnsExpectedGroups()
        {
            using var env = EcsTestHelper.CreateEnvironment(TestTemplates.WithPartitions);
            var a = env.Accessor;

            // WithPartitions template has Gamma tag + PartitionA and PartitionB partitions
            var groups = a.WorldInfo.GetGroupsWithTags(TestTags.Gamma);
            NAssert.AreEqual(
                2,
                groups.Count,
                "Should have 2 groups for Gamma tag (PartitionA and PartitionB)"
            );
        }

        [Test]
        public void WorldInfo_GetSingleGroupWithTags_ThrowsOnMultiple()
        {
            using var env = EcsTestHelper.CreateEnvironment(TestTemplates.WithPartitions);
            var a = env.Accessor;

            // Gamma alone matches 2 groups, so GetSingleGroupWithTags should throw
            NAssert.Catch(() => a.WorldInfo.GetSingleGroupWithTags(TestTags.Gamma));
        }

        #endregion
    }
}
