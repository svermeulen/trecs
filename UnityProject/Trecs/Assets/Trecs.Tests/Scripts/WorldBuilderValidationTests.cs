using NUnit.Framework;
using NAssert = NUnit.Framework.Assert;

namespace Trecs.Tests
{
    partial class WBVDummySystem : ISystem
    {
        public void Execute() { }
    }

    [TestFixture]
    public class WorldBuilderValidationTests
    {
        #region Duplicate Template

        [Test]
        public void WorldBuilder_DuplicateTemplate_Throws()
        {
            var template = TestTemplates.SimpleAlpha;

            NAssert.Catch(() =>
            {
                new WorldBuilder()
                    .SetSettings(new WorldSettings())
                    .AddEntityType(TrecsTemplates.Globals.Template)
                    .AddEntityType(template)
                    .AddEntityType(template)
                    .AddBlobStore(EcsTestHelper.CreateBlobStore())
                    .BuildAndInitialize()
                    .Dispose();
            });
        }

        #endregion

        #region Double Build

        [Test]
        public void WorldBuilder_DoubleBuild_Throws()
        {
            var builder = new WorldBuilder()
                .SetSettings(new WorldSettings())
                .AddEntityType(TrecsTemplates.Globals.Template)
                .AddBlobStore(EcsTestHelper.CreateBlobStore());

            using var world = builder.BuildAndInitialize();

            NAssert.Catch(() =>
            {
                builder.Build().Dispose();
            });
        }

        #endregion

        #region System Added After Initialize

        [Test]
        public void World_AddSystemAfterInitialize_Throws()
        {
            using var env = EcsTestHelper.CreateEnvironment(TestTemplates.SimpleAlpha);

            NAssert.Catch(() =>
            {
                env.World.AddSystem(new WBVDummySystem());
            });
        }

        #endregion

        #region Build Produces Valid World

        [Test]
        public void WorldBuilder_MinimalBuild_Succeeds()
        {
            using var env = EcsTestHelper.CreateEnvironment(TestTemplates.SimpleAlpha);

            NAssert.IsNotNull(env.World);
            NAssert.IsNotNull(env.Accessor);
        }

        [Test]
        public void WorldBuilder_MultipleTemplates_Succeeds()
        {
            using var env = EcsTestHelper.CreateEnvironment(
                TestTemplates.SimpleAlpha,
                TestTemplates.TwoCompBeta,
                TestTemplates.WithPartitions
            );

            NAssert.IsNotNull(env.Accessor);
            NAssert.AreEqual(0, env.Accessor.CountEntitiesWithTags(TestTags.Alpha));
        }

        #endregion

        #region Build Then AddSystem Then Initialize

        [Test]
        public void World_BuildThenAddSystem_Succeeds()
        {
            var builder = new WorldBuilder()
                .SetSettings(new WorldSettings())
                .AddEntityType(TrecsTemplates.Globals.Template)
                .AddEntityType(TestTemplates.SimpleAlpha)
                .AddBlobStore(EcsTestHelper.CreateBlobStore());

            var world = builder.Build();
            world.AddSystem(new WBVDummySystem());
            world.Initialize();

            NAssert.IsNotNull(world);
            world.Dispose();
        }

        #endregion
    }
}
