using NUnit.Framework;
using NAssert = NUnit.Framework.Assert;

namespace Trecs.Tests
{
    [TestFixture]
    public class BatchOperationTests
    {
        #region BulkAdd

        [Test]
        public void Batch_Add100_CountCorrect()
        {
            using var env = EcsTestHelper.CreateEnvironment(TestTemplates.SimpleAlpha);
            var a = env.Accessor;

            for (int i = 0; i < 100; i++)
            {
                a.AddEntity(TestTags.Alpha).Set(new TestInt { Value = i }).AssertComplete();
            }
            a.SubmitEntities();

            NAssert.AreEqual(100, a.CountEntitiesWithTags(TestTags.Alpha));
        }

        [Test]
        public void Batch_Add1000_CountCorrect()
        {
            using var env = EcsTestHelper.CreateEnvironment(TestTemplates.SimpleAlpha);
            var a = env.Accessor;

            for (int i = 0; i < 1000; i++)
            {
                a.AddEntity(TestTags.Alpha).Set(new TestInt { Value = i }).AssertComplete();
            }
            a.SubmitEntities();

            NAssert.AreEqual(1000, a.CountEntitiesWithTags(TestTags.Alpha));
        }

        #endregion

        #region SwapBack

        [Test]
        public void Batch_RemoveMiddle_OtherValuesIntact()
        {
            using var env = EcsTestHelper.CreateEnvironment(TestTemplates.SimpleAlpha);
            var a = env.Accessor;

            var entityHandles = new EntityHandle[5];
            for (int i = 0; i < 5; i++)
            {
                var init = a.AddEntity(TestTags.Alpha)
                    .Set(new TestInt { Value = (i + 1) * 10 })
                    .AssertComplete();
                entityHandles[i] = init.Handle;
            }
            a.SubmitEntities();

            // Remove entity at index 2 (value 30)
            a.RemoveEntity(entityHandles[2]);
            a.SubmitEntities();

            NAssert.AreEqual(4, a.CountEntitiesWithTags(TestTags.Alpha));

            // Verify remaining entities still have correct values via entityHandle
            NAssert.AreEqual(10, a.Component<TestInt>(entityHandles[0]).Read.Value);
            NAssert.AreEqual(20, a.Component<TestInt>(entityHandles[1]).Read.Value);
            NAssert.AreEqual(40, a.Component<TestInt>(entityHandles[3]).Read.Value);
            NAssert.AreEqual(50, a.Component<TestInt>(entityHandles[4]).Read.Value);
        }

        [Test]
        public void Batch_RemoveMultiple_ValuesIntact()
        {
            using var env = EcsTestHelper.CreateEnvironment(TestTemplates.SimpleAlpha);
            var a = env.Accessor;

            var entityHandles = new EntityHandle[10];
            for (int i = 0; i < 10; i++)
            {
                var init = a.AddEntity(TestTags.Alpha)
                    .Set(new TestInt { Value = i })
                    .AssertComplete();
                entityHandles[i] = init.Handle;
            }
            a.SubmitEntities();

            // Remove entities at indices 1, 4, 7
            a.RemoveEntity(entityHandles[1]);
            a.RemoveEntity(entityHandles[4]);
            a.RemoveEntity(entityHandles[7]);
            a.SubmitEntities();

            NAssert.AreEqual(7, a.CountEntitiesWithTags(TestTags.Alpha));

            // Verify surviving entities via entityHandle
            NAssert.AreEqual(0, a.Component<TestInt>(entityHandles[0]).Read.Value);
            NAssert.AreEqual(2, a.Component<TestInt>(entityHandles[2]).Read.Value);
            NAssert.AreEqual(3, a.Component<TestInt>(entityHandles[3]).Read.Value);
            NAssert.AreEqual(5, a.Component<TestInt>(entityHandles[5]).Read.Value);
            NAssert.AreEqual(6, a.Component<TestInt>(entityHandles[6]).Read.Value);
            NAssert.AreEqual(8, a.Component<TestInt>(entityHandles[8]).Read.Value);
            NAssert.AreEqual(9, a.Component<TestInt>(entityHandles[9]).Read.Value);
        }

        [Test]
        public void Batch_RemoveAll_CountZero()
        {
            using var env = EcsTestHelper.CreateEnvironment(TestTemplates.SimpleAlpha);
            var a = env.Accessor;

            for (int i = 0; i < 50; i++)
            {
                a.AddEntity(TestTags.Alpha).Set(new TestInt { Value = i }).AssertComplete();
            }
            a.SubmitEntities();

            a.RemoveEntitiesWithTags(TestTags.Alpha);
            a.SubmitEntities();

            NAssert.AreEqual(0, a.CountEntitiesWithTags(TestTags.Alpha));
        }

        #endregion

        #region Mixed

        [Test]
        public void Batch_AddRemoveInterleaved_FinalCountCorrect()
        {
            using var env = EcsTestHelper.CreateEnvironment(TestTemplates.SimpleAlpha);
            var a = env.Accessor;

            // Add 10 entities
            var entityHandles = new EntityHandle[10];
            for (int i = 0; i < 10; i++)
            {
                var init = a.AddEntity(TestTags.Alpha)
                    .Set(new TestInt { Value = i })
                    .AssertComplete();
                entityHandles[i] = init.Handle;
            }
            a.SubmitEntities();
            NAssert.AreEqual(10, a.CountEntitiesWithTags(TestTags.Alpha));

            // Remove 3
            a.RemoveEntity(entityHandles[0]);
            a.RemoveEntity(entityHandles[5]);
            a.RemoveEntity(entityHandles[9]);
            a.SubmitEntities();
            NAssert.AreEqual(7, a.CountEntitiesWithTags(TestTags.Alpha));

            // Add 5 more
            for (int i = 0; i < 5; i++)
            {
                a.AddEntity(TestTags.Alpha).Set(new TestInt { Value = 100 + i }).AssertComplete();
            }
            a.SubmitEntities();

            NAssert.AreEqual(12, a.CountEntitiesWithTags(TestTags.Alpha));
        }

        [Test]
        public void Batch_AddRemoveMove_ComponentsConsistent()
        {
            using var env = EcsTestHelper.CreateEnvironment(TestTemplates.WithPartitions);
            var a = env.Accessor;

            var partitionA = TagSet.FromTags(TestTags.Gamma, TestTags.PartitionA);
            var partitionB = TagSet.FromTags(TestTags.Gamma, TestTags.PartitionB);

            // Add 5 to partition A
            var entityHandles = new EntityHandle[5];
            for (int i = 0; i < 5; i++)
            {
                var init = a.AddEntity(partitionA)
                    .Set(new TestInt { Value = i * 10 })
                    .AssertComplete();
                entityHandles[i] = init.Handle;
            }
            a.SubmitEntities();

            // Move entity 1 to partition B
            a.MoveTo(entityHandles[1].ToIndex(a), partitionB);
            // Remove entity 3
            a.RemoveEntity(entityHandles[3]);
            a.SubmitEntities();

            NAssert.AreEqual(3, a.CountEntitiesWithTags(partitionA));
            NAssert.AreEqual(1, a.CountEntitiesWithTags(partitionB));

            // Verify moved entity component value
            var movedComp = a.Component<TestInt>(entityHandles[1]);
            NAssert.AreEqual(10, movedComp.Read.Value);

            // Verify remaining in partition A
            NAssert.IsTrue(a.EntityExists(entityHandles[0]));
            NAssert.AreEqual(0, a.Component<TestInt>(entityHandles[0]).Read.Value);
            NAssert.IsTrue(a.EntityExists(entityHandles[2]));
            NAssert.AreEqual(20, a.Component<TestInt>(entityHandles[2]).Read.Value);
            NAssert.IsTrue(a.EntityExists(entityHandles[4]));
            NAssert.AreEqual(40, a.Component<TestInt>(entityHandles[4]).Read.Value);
        }

        [Test]
        public void Batch_RemoveChain_NoOutOfRange()
        {
            using var env = EcsTestHelper.CreateEnvironment(TestTemplates.SimpleAlpha);
            var a = env.Accessor;

            // Add 10 entities
            var entityHandles = new EntityHandle[10];
            for (int i = 0; i < 10; i++)
            {
                var init = a.AddEntity(TestTags.Alpha)
                    .Set(new TestInt { Value = i })
                    .AssertComplete();
                entityHandles[i] = init.Handle;
            }
            a.SubmitEntities();

            // Remove from the end first, then middle - tests multi-hop swap-back
            a.RemoveEntity(entityHandles[9]);
            a.RemoveEntity(entityHandles[8]);
            a.RemoveEntity(entityHandles[5]);
            a.RemoveEntity(entityHandles[2]);
            a.SubmitEntities();

            NAssert.AreEqual(6, a.CountEntitiesWithTags(TestTags.Alpha));

            // Verify surviving entities
            NAssert.IsTrue(a.EntityExists(entityHandles[0]));
            NAssert.IsTrue(a.EntityExists(entityHandles[1]));
            NAssert.IsTrue(a.EntityExists(entityHandles[3]));
            NAssert.IsTrue(a.EntityExists(entityHandles[4]));
            NAssert.IsTrue(a.EntityExists(entityHandles[6]));
            NAssert.IsTrue(a.EntityExists(entityHandles[7]));

            // Verify values
            NAssert.AreEqual(0, a.Component<TestInt>(entityHandles[0]).Read.Value);
            NAssert.AreEqual(1, a.Component<TestInt>(entityHandles[1]).Read.Value);
            NAssert.AreEqual(3, a.Component<TestInt>(entityHandles[3]).Read.Value);
            NAssert.AreEqual(4, a.Component<TestInt>(entityHandles[4]).Read.Value);
            NAssert.AreEqual(6, a.Component<TestInt>(entityHandles[6]).Read.Value);
            NAssert.AreEqual(7, a.Component<TestInt>(entityHandles[7]).Read.Value);
        }

        [Test]
        public void Batch_BulkTagRemove_WithMovesInSameSubmission()
        {
            using var env = EcsTestHelper.CreateEnvironment(
                TestTemplates.SimpleAlpha,
                TestTemplates.WithPartitions
            );
            var a = env.Accessor;

            var partitionA = TagSet.FromTags(TestTags.Gamma, TestTags.PartitionA);
            var partitionB = TagSet.FromTags(TestTags.Gamma, TestTags.PartitionB);

            // Add 3 Alpha entities and 3 PartitionA entities
            for (int i = 0; i < 3; i++)
            {
                a.AddEntity(TestTags.Alpha).Set(new TestInt { Value = i }).AssertComplete();
            }

            var gammaRefs = new EntityHandle[3];
            for (int i = 0; i < 3; i++)
            {
                var init = a.AddEntity(partitionA)
                    .Set(new TestInt { Value = 100 + i })
                    .AssertComplete();
                gammaRefs[i] = init.Handle;
            }
            a.SubmitEntities();

            // Move one Gamma entity to PartitionB, then bulk-remove all Alpha
            a.MoveTo(gammaRefs[0].ToIndex(a), partitionB);
            a.RemoveEntitiesWithTags(TestTags.Alpha);
            a.SubmitEntities();

            NAssert.AreEqual(
                0,
                a.CountEntitiesWithTags(TestTags.Alpha),
                "All Alpha entities should be removed"
            );
            NAssert.AreEqual(
                2,
                a.CountEntitiesWithTags(partitionA),
                "2 Gamma entities should remain in PartitionA"
            );
            NAssert.AreEqual(
                1,
                a.CountEntitiesWithTags(partitionB),
                "1 Gamma entity should be in PartitionB after move"
            );
        }

        #endregion
    }
}
