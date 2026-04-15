using NUnit.Framework;
using NAssert = NUnit.Framework.Assert;

namespace Trecs.Tests
{
    [TestFixture]
    public class EntityCrudTests
    {
        #region Add

        [Test]
        public void EntityCrud_AddSingle_CountIsOne()
        {
            using var env = EcsTestHelper.CreateEnvironment(TestTemplates.SimpleAlpha);
            var a = env.Accessor;

            a.AddEntity(TestTags.Alpha).AssertComplete();
            a.SubmitEntities();

            NAssert.AreEqual(1, a.CountEntitiesWithTags(TestTags.Alpha));
        }

        [Test]
        public void EntityCrud_AddSingle_EntityHandleIsValid()
        {
            using var env = EcsTestHelper.CreateEnvironment(TestTemplates.SimpleAlpha);
            var a = env.Accessor;

            var init = a.AddEntity(TestTags.Alpha).AssertComplete();
            var entityHandle = init.Handle;

            NAssert.IsFalse(entityHandle.IsNull);
        }

        [Test]
        public void EntityCrud_AddMultiple_CountReflectsAll()
        {
            using var env = EcsTestHelper.CreateEnvironment(TestTemplates.SimpleAlpha);
            var a = env.Accessor;

            for (int i = 0; i < 5; i++)
            {
                a.AddEntity(TestTags.Alpha).AssertComplete();
            }
            a.SubmitEntities();

            NAssert.AreEqual(5, a.CountEntitiesWithTags(TestTags.Alpha));
        }

        [Test]
        public void EntityCrud_AddWithComponentValue_Persists()
        {
            using var env = EcsTestHelper.CreateEnvironment(TestTemplates.SimpleAlpha);
            var a = env.Accessor;

            a.AddEntity(TestTags.Alpha).Set(new TestInt { Value = 99 }).AssertComplete();
            a.SubmitEntities();

            var comp = a.Query().WithTags(TestTags.Alpha).Single().Get<TestInt>();
            NAssert.AreEqual(99, comp.Read.Value);
        }

        [Test]
        public void EntityCrud_AddWithDefault_UsesTemplateDefault()
        {
            using var env = EcsTestHelper.CreateEnvironment(TestTemplates.WithDefaults);
            var a = env.Accessor;

            a.AddEntity(TestTags.Delta).AssertComplete();
            a.SubmitEntities();

            var intComp = a.Query().WithTags(TestTags.Delta).Single().Get<TestInt>();
            var floatComp = a.Query().WithTags(TestTags.Delta).Single().Get<TestFloat>();

            NAssert.AreEqual(42, intComp.Read.Value);
            NAssert.AreEqual(3.14f, floatComp.Read.Value, 0.001f);
        }

        [Test]
        public void EntityCrud_AddZeroComponents_Throws()
        {
            using var env = EcsTestHelper.CreateEnvironment(TestTemplates.ZeroComponents);
            var a = env.Accessor;

            a.AddEntity(TestTags.Epsilon).AssertComplete();
            a.SubmitEntities();

            NAssert.Catch(() =>
            {
                a.CountEntitiesWithTags(TestTags.Epsilon);
            });
        }

        [Test]
        public void EntityCrud_Add_NotVisibleBeforeSubmit()
        {
            using var env = EcsTestHelper.CreateEnvironment(TestTemplates.SimpleAlpha);
            var a = env.Accessor;

            a.AddEntity(TestTags.Alpha).AssertComplete();

            NAssert.AreEqual(0, a.CountEntitiesWithTags(TestTags.Alpha));

            a.SubmitEntities();

            NAssert.AreEqual(1, a.CountEntitiesWithTags(TestTags.Alpha));
        }

        #endregion

        #region Remove

        [Test]
        public void EntityCrud_RemoveByEntityIndex_CountDecreases()
        {
            using var env = EcsTestHelper.CreateEnvironment(TestTemplates.SimpleAlpha);
            var a = env.Accessor;

            for (int i = 0; i < 3; i++)
            {
                a.AddEntity(TestTags.Alpha).AssertComplete();
            }
            a.SubmitEntities();

            var group = a.WorldInfo.GetSingleGroupWithTags(TestTags.Alpha);
            a.RemoveEntity(new EntityIndex(0, group));
            a.SubmitEntities();

            NAssert.AreEqual(2, a.CountEntitiesWithTags(TestTags.Alpha));
        }

        [Test]
        public void EntityCrud_RemoveByEntityHandle_CountDecreases()
        {
            using var env = EcsTestHelper.CreateEnvironment(TestTemplates.SimpleAlpha);
            var a = env.Accessor;

            var init = a.AddEntity(TestTags.Alpha).AssertComplete();
            var entityHandle = init.Handle;
            a.AddEntity(TestTags.Alpha).AssertComplete();
            a.AddEntity(TestTags.Alpha).AssertComplete();
            a.SubmitEntities();

            a.RemoveEntity(entityHandle);
            a.SubmitEntities();

            NAssert.AreEqual(2, a.CountEntitiesWithTags(TestTags.Alpha));
        }

        [Test]
        public void EntityCrud_RemoveWithTags_RemovesMatching()
        {
            using var env = EcsTestHelper.CreateEnvironment(
                TestTemplates.SimpleAlpha,
                TestTemplates.TwoCompBeta
            );
            var a = env.Accessor;

            a.AddEntity(TestTags.Alpha).AssertComplete();
            a.AddEntity(TestTags.Alpha).AssertComplete();
            a.AddEntity(TestTags.Beta).Set(new TestFloat { Value = 1.0f }).AssertComplete();
            a.SubmitEntities();

            a.RemoveEntitiesWithTags(TestTags.Alpha);
            a.SubmitEntities();

            NAssert.AreEqual(0, a.CountEntitiesWithTags(TestTags.Alpha));
            NAssert.AreEqual(1, a.CountEntitiesWithTags(TestTags.Beta));
        }

        [Test]
        public void EntityCrud_RemoveAll_CountIsZero()
        {
            using var env = EcsTestHelper.CreateEnvironment(TestTemplates.SimpleAlpha);
            var a = env.Accessor;

            for (int i = 0; i < 5; i++)
            {
                a.AddEntity(TestTags.Alpha).AssertComplete();
            }
            a.SubmitEntities();

            a.RemoveEntitiesWithTags(TestTags.Alpha);
            a.SubmitEntities();

            NAssert.AreEqual(0, a.CountEntitiesWithTags(TestTags.Alpha));
        }

        #endregion

        #region Move

        [Test]
        public void EntityCrud_MoveEntity_ChangesGroup()
        {
            using var env = EcsTestHelper.CreateEnvironment(TestTemplates.WithStates);
            var a = env.Accessor;

            var stateA = TagSet.FromTags(TestTags.Gamma, TestTags.StateA);
            var stateB = TagSet.FromTags(TestTags.Gamma, TestTags.StateB);

            a.AddEntity(stateA).AssertComplete();
            a.SubmitEntities();

            NAssert.AreEqual(1, a.CountEntitiesWithTags(stateA));
            NAssert.AreEqual(0, a.CountEntitiesWithTags(stateB));

            var groupA = a.WorldInfo.GetSingleGroupWithTags(stateA);
            a.MoveTo(new EntityIndex(0, groupA), stateB);
            a.SubmitEntities();

            NAssert.AreEqual(0, a.CountEntitiesWithTags(stateA));
            NAssert.AreEqual(1, a.CountEntitiesWithTags(stateB));
        }

        [Test]
        public void EntityCrud_MoveEntity_PreservesComponents()
        {
            using var env = EcsTestHelper.CreateEnvironment(TestTemplates.WithStates);
            var a = env.Accessor;

            var stateA = TagSet.FromTags(TestTags.Gamma, TestTags.StateA);
            var stateB = TagSet.FromTags(TestTags.Gamma, TestTags.StateB);

            a.AddEntity(stateA)
                .Set(new TestInt { Value = 77 })
                .Set(new TestVec { X = 1.5f, Y = 2.5f })
                .AssertComplete();
            a.SubmitEntities();

            var groupA = a.WorldInfo.GetSingleGroupWithTags(stateA);
            a.MoveTo(new EntityIndex(0, groupA), stateB);
            a.SubmitEntities();

            var intComp = a.Query().WithTags(stateB).Single().Get<TestInt>();
            var vecComp = a.Query().WithTags(stateB).Single().Get<TestVec>();

            NAssert.AreEqual(77, intComp.Read.Value);
            NAssert.AreEqual(1.5f, vecComp.Read.X, 0.001f);
            NAssert.AreEqual(2.5f, vecComp.Read.Y, 0.001f);
        }

        [Test]
        public void EntityCrud_MoveEntity_EntityHandleStaysValid()
        {
            using var env = EcsTestHelper.CreateEnvironment(TestTemplates.WithStates);
            var a = env.Accessor;

            var stateA = TagSet.FromTags(TestTags.Gamma, TestTags.StateA);
            var stateB = TagSet.FromTags(TestTags.Gamma, TestTags.StateB);

            var init = a.AddEntity(stateA).AssertComplete();
            var entityHandle = init.Handle;
            a.SubmitEntities();

            var groupA = a.WorldInfo.GetSingleGroupWithTags(stateA);
            a.MoveTo(new EntityIndex(0, groupA), stateB);
            a.SubmitEntities();

            NAssert.IsTrue(a.EntityExists(entityHandle));
        }

        [Test]
        public void EntityCrud_MoveEntity_CountsUpdate()
        {
            using var env = EcsTestHelper.CreateEnvironment(TestTemplates.WithStates);
            var a = env.Accessor;

            var stateA = TagSet.FromTags(TestTags.Gamma, TestTags.StateA);
            var stateB = TagSet.FromTags(TestTags.Gamma, TestTags.StateB);

            a.AddEntity(stateA).AssertComplete();
            a.AddEntity(stateA).AssertComplete();
            a.SubmitEntities();

            var groupA = a.WorldInfo.GetSingleGroupWithTags(stateA);
            a.MoveTo(new EntityIndex(0, groupA), stateB);
            a.SubmitEntities();

            NAssert.AreEqual(1, a.CountEntitiesWithTags(stateA));
            NAssert.AreEqual(1, a.CountEntitiesWithTags(stateB));
        }

        #endregion

        #region Count

        [Test]
        public void EntityCrud_CountWithTags_Correct()
        {
            using var env = EcsTestHelper.CreateEnvironment(TestTemplates.SimpleAlpha);
            var a = env.Accessor;

            for (int i = 0; i < 7; i++)
            {
                a.AddEntity(TestTags.Alpha).AssertComplete();
            }
            a.SubmitEntities();

            NAssert.AreEqual(7, a.CountEntitiesWithTags(TestTags.Alpha));
        }

        [Test]
        public void EntityCrud_CountInGroup_Correct()
        {
            using var env = EcsTestHelper.CreateEnvironment(TestTemplates.SimpleAlpha);
            var a = env.Accessor;

            for (int i = 0; i < 4; i++)
            {
                a.AddEntity(TestTags.Alpha).AssertComplete();
            }
            a.SubmitEntities();

            var group = a.WorldInfo.GetSingleGroupWithTags(TestTags.Alpha);
            NAssert.AreEqual(4, a.CountEntitiesInGroup(group));
        }

        [Test]
        public void EntityCrud_CountEmpty_ReturnsZero()
        {
            using var env = EcsTestHelper.CreateEnvironment(TestTemplates.SimpleAlpha);
            var a = env.Accessor;

            NAssert.AreEqual(0, a.CountEntitiesWithTags(TestTags.Alpha));
        }

        #endregion

        #region Global

        [Test]
        public void EntityCrud_GlobalEntity_Exists()
        {
            using var env = EcsTestHelper.CreateEnvironment(TestTemplates.SimpleAlpha);
            var a = env.Accessor;

            // The global entity should be present after initialization
            // Verify via WorldInfo properties (DB-level queries on global group are internal)
            NAssert.AreEqual(1, a.WorldInfo.GlobalGroups.Count);
            NAssert.IsNotNull(a.WorldInfo.GlobalTemplate);
        }

        [Test]
        public void EntityCrud_GlobalEntityIndex_MatchesWorldInfo()
        {
            using var env = EcsTestHelper.CreateEnvironment(TestTemplates.SimpleAlpha);
            var a = env.Accessor;

            var globalEntityIndex = a.GlobalEntityIndex;
            var worldInfoGlobalEntityIndex = a.WorldInfo.GlobalEntityIndex;

            NAssert.AreEqual(worldInfoGlobalEntityIndex, globalEntityIndex);
        }

        #endregion
    }
}
