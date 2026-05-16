using NUnit.Framework;
using NAssert = NUnit.Framework.Assert;

namespace Trecs.Tests
{
    [TestFixture]
    public class TemplateInheritanceTests
    {
        #region Child Entity Has Parent Components

        [Test]
        public void TemplateInheritance_ChildEntity_HasParentComponent()
        {
            // ChildOfAlpha inherits SimpleAlpha (TestInt + Alpha), adds TestFloat + Beta
            using var env = EcsTestHelper.CreateEnvironment(TestTemplates.ChildOfAlpha);
            var a = env.Accessor;

            // Create entity using child's tags (Alpha + Beta)
            var tags = TagSet.FromTags(TestTags.Alpha, TestTags.Beta);
            a.AddEntity(tags)
                .Set(new TestInt { Value = 10 })
                .Set(new TestFloat { Value = 2.5f })
                .AssertComplete();
            a.SubmitEntities();

            // Verify both parent (TestInt) and child (TestFloat) components are accessible
            var intComp = a.Query().WithTags(tags).SingleHandle().Component<TestInt>(a);
            var floatComp = a.Query().WithTags(tags).SingleHandle().Component<TestFloat>(a);

            NAssert.AreEqual(10, intComp.Read.Value);
            NAssert.AreEqual(2.5f, floatComp.Read.Value, 0.001f);
        }

        [Test]
        public void TemplateInheritance_ChildEntity_QueriedByParentTag()
        {
            using var env = EcsTestHelper.CreateEnvironment(TestTemplates.ChildOfAlpha);
            var a = env.Accessor;

            var tags = TagSet.FromTags(TestTags.Alpha, TestTags.Beta);
            a.AddEntity(tags).AssertComplete();
            a.SubmitEntities();

            // Entity should be queryable via parent tag (Alpha)
            NAssert.AreEqual(
                1,
                a.CountEntitiesWithTags(TestTags.Alpha),
                "Entity from child template should be queryable by parent tag"
            );

            // Entity should also be queryable via child tag (Beta)
            NAssert.AreEqual(
                1,
                a.CountEntitiesWithTags(TestTags.Beta),
                "Entity from child template should be queryable by child tag"
            );

            // And via both tags combined
            NAssert.AreEqual(
                1,
                a.CountEntitiesWithTags(tags),
                "Entity should be queryable by both tags combined"
            );
        }

        #endregion

        #region Default Propagation

        [Test]
        public void TemplateInheritance_DefaultsFromParent_Propagate()
        {
            // ChildWithDefaults inherits WithDefaults (TestInt=42, TestFloat=3.14 + Delta),
            // adds TestVec + Epsilon
            using var env = EcsTestHelper.CreateEnvironment(TestTemplates.ChildWithDefaults);
            var a = env.Accessor;

            var tags = TagSet.FromTags(TestTags.Delta, TestTags.Epsilon);
            a.AddEntity(tags).AssertComplete();
            a.SubmitEntities();

            // Parent defaults should propagate to child entities
            var intComp = a.Query().WithTags(tags).SingleHandle().Component<TestInt>(a);
            var floatComp = a.Query().WithTags(tags).SingleHandle().Component<TestFloat>(a);
            var vecComp = a.Query().WithTags(tags).SingleHandle().Component<TestVec>(a);

            NAssert.AreEqual(
                42,
                intComp.Read.Value,
                "TestInt default from parent (42) should propagate to child entity"
            );
            NAssert.AreEqual(
                3.14f,
                floatComp.Read.Value,
                0.001f,
                "TestFloat default from parent (3.14) should propagate to child entity"
            );
            NAssert.AreEqual(
                0f,
                vecComp.Read.X,
                0.001f,
                "TestVec from child should use default (zero)"
            );
        }

        #endregion

        #region Resolved Template

        [Test]
        public void TemplateInheritance_ResolvedTemplate_HasAllComponents()
        {
            using var env = EcsTestHelper.CreateEnvironment(TestTemplates.ChildOfAlpha);
            var a = env.Accessor;

            var tags = TagSet.FromTags(TestTags.Alpha, TestTags.Beta);
            var group = a.WorldInfo.GetSingleGroupWithTags(tags);
            var resolved = a.WorldInfo.GetResolvedTemplateForGroup(group);

            NAssert.IsTrue(
                resolved.HasComponent<TestInt>(),
                "Resolved template should include parent component TestInt"
            );
            NAssert.IsTrue(
                resolved.HasComponent<TestFloat>(),
                "Resolved template should include child component TestFloat"
            );
        }

        #endregion
    }
}
