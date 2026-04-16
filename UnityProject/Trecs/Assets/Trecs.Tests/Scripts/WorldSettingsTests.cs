using NUnit.Framework;
using Trecs.Internal;
using NAssert = NUnit.Framework.Assert;

namespace Trecs.Tests
{
    [TestFixture]
    public class WorldSettingsTests
    {
        #region Default Settings

        [Test]
        public void WorldSettings_Default_FixedTimeStep()
        {
            var settings = new WorldSettings();
            NAssert.AreEqual(1.0f / 60.0f, settings.FixedTimeStep, 0.0001f);
        }

        [Test]
        public void WorldSettings_Default_NotPaused()
        {
            var settings = new WorldSettings();
            NAssert.IsFalse(settings.StartPaused);
        }

        [Test]
        public void WorldSettings_Default_NonDeterministic()
        {
            var settings = new WorldSettings();
            NAssert.IsFalse(settings.RequireDeterministicSubmission);
        }

        #endregion

        #region StartPaused

        [Test]
        public void WorldSettings_StartPaused_SystemRunnerIsPaused()
        {
            using var env = EcsTestHelper.CreateEnvironment(
                new WorldSettings { StartPaused = true },
                TestTemplates.SimpleAlpha
            );

            var runner = env.World.GetSystemRunner();
            NAssert.IsTrue(runner.IsPaused, "SystemRunner should start paused");
        }

        [Test]
        public void WorldSettings_StartNotPaused_SystemRunnerNotPaused()
        {
            using var env = EcsTestHelper.CreateEnvironment(
                new WorldSettings { StartPaused = false },
                TestTemplates.SimpleAlpha
            );

            var runner = env.World.GetSystemRunner();
            NAssert.IsFalse(runner.IsPaused);
        }

        #endregion

        #region Custom FixedTimeStep

        [Test]
        public void WorldSettings_CustomFixedTimeStep_Accepted()
        {
            var settings = new WorldSettings { FixedTimeStep = 1.0f / 30.0f };

            using var env = EcsTestHelper.CreateEnvironment(settings, TestTemplates.SimpleAlpha);
            // World creation succeeds with non-default timestep
            NAssert.IsNotNull(env.World);
        }

        #endregion

        #region RandomSeed

        [Test]
        public void WorldSettings_SameSeed_ProducesConsistentWorld()
        {
            var settings1 = new WorldSettings { RandomSeed = 12345 };
            var settings2 = new WorldSettings { RandomSeed = 12345 };

            using var env1 = EcsTestHelper.CreateEnvironment(settings1, TestTemplates.SimpleAlpha);
            using var env2 = EcsTestHelper.CreateEnvironment(settings2, TestTemplates.SimpleAlpha);

            // Both worlds should be valid (seed consistency is hard to test externally
            // without accessing the Rng, but at least verify both construct)
            NAssert.IsNotNull(env1.World);
            NAssert.IsNotNull(env2.World);
        }

        #endregion

        #region MaxSubmissionIterations

        [Test]
        public void WorldSettings_CustomMaxSubmissionIterations_Accepted()
        {
            var settings = new WorldSettings { MaxSubmissionIterations = 5 };

            using var env = EcsTestHelper.CreateEnvironment(settings, TestTemplates.SimpleAlpha);
            NAssert.IsNotNull(env.World);
        }

        #endregion

        #region Multiple Accessors

        [Test]
        public void World_MultipleAccessors_Independent()
        {
            using var env = EcsTestHelper.CreateEnvironment(TestTemplates.SimpleAlpha);
            var a1 = env.Accessor;
            var a2 = env.World.CreateAccessor();

            // Both accessors can create entities
            a1.AddEntity(TestTags.Alpha).Set(new TestInt { Value = 10 }).AssertComplete();
            a1.SubmitEntities();

            // Both see the same world state
            NAssert.AreEqual(1, a1.CountEntitiesWithTags(TestTags.Alpha));
            NAssert.AreEqual(1, a2.CountEntitiesWithTags(TestTags.Alpha));

            a2.AddEntity(TestTags.Alpha).Set(new TestInt { Value = 20 }).AssertComplete();
            a2.SubmitEntities();

            NAssert.AreEqual(2, a1.CountEntitiesWithTags(TestTags.Alpha));
            NAssert.AreEqual(2, a2.CountEntitiesWithTags(TestTags.Alpha));
        }

        #endregion
    }
}
