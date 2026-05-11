using NUnit.Framework;
using NAssert = NUnit.Framework.Assert;

namespace Trecs.Tests
{
    public partial struct TestGlobalInt : IEntityComponent
    {
        public int Value;
    }

    public partial struct TestGlobalFloat : IEntityComponent
    {
        public float Value;
    }

    public partial class TestGlobalsTemplate : ITemplate, IExtends<TrecsTemplates.Globals>
    {
        TestGlobalInt GlobalInt = new TestGlobalInt { Value = 0 };
        TestGlobalFloat GlobalFloat = new TestGlobalFloat { Value = 0f };
    }

    [TestFixture]
    public class GlobalComponentTests
    {
        TestEnvironment CreateEnv() =>
            EcsTestHelper.CreateEnvironment(
                new WorldSettings(),
                null,
                globalsTemplate: TestGlobalsTemplate.Template,
                TestTemplates.SimpleAlpha
            );

        [Test]
        public void Global_ReadInitialValue_ReturnsDefault()
        {
            using var env = CreateEnv();
            var a = env.Accessor;

            ref readonly var value = ref a.GlobalComponent<TestGlobalInt>().Read;
            NAssert.AreEqual(0, value.Value);
        }

        [Test]
        public void Global_Write_PersistsOnRead()
        {
            using var env = CreateEnv();
            var a = env.Accessor;

            ref var comp = ref a.GlobalComponent<TestGlobalInt>().Write;
            comp.Value = 42;

            ref readonly var readBack = ref a.GlobalComponent<TestGlobalInt>().Read;
            NAssert.AreEqual(42, readBack.Value);
        }

        [Test]
        public void Global_WriteMultipleTimes_LastValuePersists()
        {
            using var env = CreateEnv();
            var a = env.Accessor;

            ref var comp1 = ref a.GlobalComponent<TestGlobalInt>().Write;
            comp1.Value = 10;

            ref var comp2 = ref a.GlobalComponent<TestGlobalInt>().Write;
            comp2.Value = 20;

            ref readonly var readBack = ref a.GlobalComponent<TestGlobalInt>().Read;
            NAssert.AreEqual(20, readBack.Value);
        }

        [Test]
        public void Global_TwoComponents_Independent()
        {
            using var env = CreateEnv();
            var a = env.Accessor;

            ref var intComp = ref a.GlobalComponent<TestGlobalInt>().Write;
            intComp.Value = 99;

            ref var floatComp = ref a.GlobalComponent<TestGlobalFloat>().Write;
            floatComp.Value = 3.14f;

            NAssert.AreEqual(99, a.GlobalComponent<TestGlobalInt>().Read.Value);
            NAssert.AreEqual(3.14f, a.GlobalComponent<TestGlobalFloat>().Read.Value, 0.001f);
        }

        [Test]
        public void Global_QueryGlobalEntity_ReturnsRef()
        {
            using var env = CreateEnv();
            var a = env.Accessor;

            NAssert.AreEqual(0, a.GlobalComponent<TestGlobalInt>().Read.Value);

            a.GlobalComponent<TestGlobalInt>().Write.Value = 55;
            NAssert.AreEqual(55, a.GlobalComponent<TestGlobalInt>().Read.Value);
        }

        [Test]
        public void Global_EntityHandle_IsNotNull()
        {
            using var env = CreateEnv();
            var a = env.Accessor;

            NAssert.IsFalse(a.GlobalEntityHandle.IsNull);
        }

        [Test]
        public void Global_EntityIndex_MatchesRef()
        {
            using var env = CreateEnv();
            var a = env.Accessor;

            var globalIndex = a.GlobalEntityIndex;
            var refToIndex = a.GlobalEntityHandle.ToIndex(a);

            NAssert.AreEqual(globalIndex, refToIndex);
        }

        [Test]
        public void Global_EntityExists_True()
        {
            using var env = CreateEnv();
            var a = env.Accessor;

            NAssert.IsTrue(a.EntityExists(a.GlobalEntityHandle));
        }

        [Test]
        public void Global_RegularEntities_DoNotAffectGlobals()
        {
            using var env = CreateEnv();
            var a = env.Accessor;

            ref var globalComp = ref a.GlobalComponent<TestGlobalInt>().Write;
            globalComp.Value = 100;

            // Add and remove regular entities
            for (int i = 0; i < 5; i++)
            {
                a.AddEntity(TestTags.Alpha).AssertComplete();
            }
            a.SubmitEntities();

            a.RemoveEntitiesWithTags(TestTags.Alpha);
            a.SubmitEntities();

            // Global component should be unchanged
            NAssert.AreEqual(100, a.GlobalComponent<TestGlobalInt>().Read.Value);
        }
    }
}
