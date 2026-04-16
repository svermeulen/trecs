using NUnit.Framework;
using NAssert = NUnit.Framework.Assert;

namespace Trecs.Tests
{
    /// <summary>
    /// Tests that global components remain stable and accessible through
    /// structural changes (adds, removes, moves) on regular entities.
    /// </summary>
    [TestFixture]
    public class GlobalComponentStructuralTests
    {
        static readonly TagSet PartitionA = TagSet.FromTags(TestTags.Gamma, TestTags.PartitionA);
        static readonly TagSet PartitionB = TagSet.FromTags(TestTags.Gamma, TestTags.PartitionB);

        TestEnvironment CreateEnv() =>
            EcsTestHelper.CreateEnvironment(
                new WorldSettings(),
                null,
                globalsTemplate: TestGlobalsTemplate.Template,
                TestTemplates.WithPartitions
            );

        #region Global unaffected by regular entity adds

        [Test]
        public void GlobalComponent_UnchangedAfterMassAdds()
        {
            using var env = CreateEnv();
            var a = env.Accessor;

            a.GlobalComponent<TestGlobalInt>().Write.Value = 999;
            a.GlobalComponent<TestGlobalFloat>().Write.Value = 3.14f;

            for (int i = 0; i < 50; i++)
            {
                a.AddEntity(PartitionA)
                    .Set(new TestInt { Value = i })
                    .Set(new TestVec())
                    .AssertComplete();
            }
            a.SubmitEntities();

            NAssert.AreEqual(999, a.GlobalComponent<TestGlobalInt>().Read.Value);
            NAssert.AreEqual(3.14f, a.GlobalComponent<TestGlobalFloat>().Read.Value, 0.001f);
        }

        #endregion

        #region Global unaffected by removes with swap-backs

        [Test]
        public void GlobalComponent_UnchangedAfterScatteredRemoves()
        {
            using var env = CreateEnv();
            var a = env.Accessor;

            var handles = new EntityHandle[20];
            for (int i = 0; i < 20; i++)
            {
                handles[i] = a.AddEntity(PartitionA)
                    .Set(new TestInt { Value = i })
                    .Set(new TestVec())
                    .AssertComplete()
                    .Handle;
            }
            a.SubmitEntities();

            a.GlobalComponent<TestGlobalInt>().Write.Value = 42;

            // Remove scattered entities causing multiple swap-backs
            for (int i = 0; i < 20; i += 3)
                a.RemoveEntity(handles[i]);
            a.SubmitEntities();

            NAssert.AreEqual(42, a.GlobalComponent<TestGlobalInt>().Read.Value);
        }

        [Test]
        public void GlobalComponent_UnchangedAfterBulkRemove()
        {
            using var env = CreateEnv();
            var a = env.Accessor;

            for (int i = 0; i < 10; i++)
            {
                a.AddEntity(PartitionA)
                    .Set(new TestInt { Value = i })
                    .Set(new TestVec())
                    .AssertComplete();
            }
            a.SubmitEntities();

            a.GlobalComponent<TestGlobalInt>().Write.Value = 77;

            a.RemoveEntitiesWithTags(PartitionA);
            a.SubmitEntities();

            NAssert.AreEqual(0, a.CountEntitiesWithTags(PartitionA));
            NAssert.AreEqual(77, a.GlobalComponent<TestGlobalInt>().Read.Value);
        }

        #endregion

        #region Global unaffected by moves

        [Test]
        public void GlobalComponent_UnchangedAfterMoves()
        {
            using var env = CreateEnv();
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

            a.GlobalComponent<TestGlobalInt>().Write.Value = 123;

            for (int i = 0; i < 5; i++)
                a.MoveTo(handles[i].ToIndex(a), PartitionB);
            a.SubmitEntities();

            NAssert.AreEqual(123, a.GlobalComponent<TestGlobalInt>().Read.Value);
        }

        #endregion

        #region Global unaffected by mixed operations

        [Test]
        public void GlobalComponent_UnchangedAfterMixedAddRemoveMove()
        {
            using var env = CreateEnv();
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

            a.GlobalComponent<TestGlobalInt>().Write.Value = 555;
            a.GlobalComponent<TestGlobalFloat>().Write.Value = 2.718f;

            // Mix: move 0-2, remove 3-5, add 3 new
            for (int i = 0; i < 3; i++)
                a.MoveTo(handles[i].ToIndex(a), PartitionB);
            for (int i = 3; i < 6; i++)
                a.RemoveEntity(handles[i]);
            for (int i = 0; i < 3; i++)
            {
                a.AddEntity(PartitionA)
                    .Set(new TestInt { Value = 100 + i })
                    .Set(new TestVec())
                    .AssertComplete();
            }
            a.SubmitEntities();

            NAssert.AreEqual(555, a.GlobalComponent<TestGlobalInt>().Read.Value);
            NAssert.AreEqual(2.718f, a.GlobalComponent<TestGlobalFloat>().Read.Value, 0.001f);
        }

        #endregion

        #region Global writable during structural changes

        [Test]
        public void GlobalComponent_WriteBetweenSubmissions_Persists()
        {
            using var env = CreateEnv();
            var a = env.Accessor;

            // Frame 1: add entities, set global
            for (int i = 0; i < 5; i++)
            {
                a.AddEntity(PartitionA)
                    .Set(new TestInt { Value = i })
                    .Set(new TestVec())
                    .AssertComplete();
            }
            a.GlobalComponent<TestGlobalInt>().Write.Value = 10;
            a.SubmitEntities();
            NAssert.AreEqual(10, a.GlobalComponent<TestGlobalInt>().Read.Value);

            // Frame 2: remove some, update global
            a.RemoveEntitiesWithTags(PartitionA);
            a.GlobalComponent<TestGlobalInt>().Write.Value = 20;
            a.SubmitEntities();
            NAssert.AreEqual(20, a.GlobalComponent<TestGlobalInt>().Read.Value);

            // Frame 3: add more, update global again
            for (int i = 0; i < 3; i++)
            {
                a.AddEntity(PartitionB)
                    .Set(new TestInt { Value = i })
                    .Set(new TestVec())
                    .AssertComplete();
            }
            a.GlobalComponent<TestGlobalInt>().Write.Value = 30;
            a.SubmitEntities();
            NAssert.AreEqual(30, a.GlobalComponent<TestGlobalInt>().Read.Value);
        }

        #endregion

        #region Global entity handle stability

        [Test]
        public void GlobalEntityHandle_StableThroughStructuralChanges()
        {
            using var env = CreateEnv();
            var a = env.Accessor;

            var globalHandle = a.GlobalEntityHandle;
            NAssert.IsTrue(a.EntityExists(globalHandle));

            // Add, submit, remove, submit, move, submit — global handle should be stable
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
            NAssert.IsTrue(a.EntityExists(globalHandle), "After adds");

            a.MoveTo(handles[0].ToIndex(a), PartitionB);
            a.RemoveEntity(handles[1]);
            a.SubmitEntities();
            NAssert.IsTrue(a.EntityExists(globalHandle), "After move+remove");

            a.RemoveEntitiesWithTags(PartitionA);
            a.RemoveEntitiesWithTags(PartitionB);
            a.SubmitEntities();
            NAssert.IsTrue(
                a.EntityExists(globalHandle),
                "After bulk remove of all regular entities"
            );
        }

        #endregion

        #region Global accessible in callbacks

        [Test]
        public void GlobalComponent_ReadableInOnRemovedCallback()
        {
            using var env = CreateEnv();
            var a = env.Accessor;

            a.GlobalComponent<TestGlobalInt>().Write.Value = 88;

            var handle = a.AddEntity(PartitionA)
                .Set(new TestInt { Value = 1 })
                .Set(new TestVec())
                .AssertComplete()
                .Handle;
            a.SubmitEntities();

            int globalValueInCallback = -1;
            var sub = a
                .Events.InGroupsWithTags(PartitionA)
                .OnRemoved(
                    (group, indices) =>
                    {
                        globalValueInCallback = a.GlobalComponent<TestGlobalInt>().Read.Value;
                    }
                );

            a.RemoveEntity(handle);
            a.SubmitEntities();

            NAssert.AreEqual(
                88,
                globalValueInCallback,
                "Global component should be readable during OnRemoved callback"
            );
            sub.Dispose();
        }

        [Test]
        public void GlobalComponent_WritableInOnAddedCallback()
        {
            using var env = CreateEnv();
            var a = env.Accessor;

            a.GlobalComponent<TestGlobalInt>().Write.Value = 0;

            var sub = a
                .Events.InGroupsWithTags(PartitionA)
                .OnAdded(
                    (group, indices) =>
                    {
                        a.GlobalComponent<TestGlobalInt>().Write.Value += indices.Count;
                    }
                );

            for (int i = 0; i < 5; i++)
            {
                a.AddEntity(PartitionA)
                    .Set(new TestInt { Value = i })
                    .Set(new TestVec())
                    .AssertComplete();
            }
            a.SubmitEntities();

            NAssert.AreEqual(
                5,
                a.GlobalComponent<TestGlobalInt>().Read.Value,
                "Global component should be writable during OnAdded callback"
            );
            sub.Dispose();
        }

        #endregion
    }
}
