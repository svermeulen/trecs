using System;
using NUnit.Framework;
using NAssert = NUnit.Framework.Assert;

namespace Trecs.Tests
{
    static class EnableTestLog
    {
        public static int CountA;
        public static int CountB;

        public static void Reset()
        {
            CountA = 0;
            CountB = 0;
        }
    }

    // Presentation phase so each Tick runs Execute exactly once — Fixed-phase
    // systems can run 0..N times per tick depending on accumulated time.
    [ExecuteIn(SystemPhase.Presentation)]
    partial class CountingEnableSystemA : ISystem
    {
        public void Execute() => EnableTestLog.CountA++;
    }

    [ExecuteIn(SystemPhase.Presentation)]
    partial class CountingEnableSystemB : ISystem
    {
        public void Execute() => EnableTestLog.CountB++;
    }

    [TestFixture]
    public class SystemEnableStateTests
    {
        TestEnvironment CreateEnv(params ISystem[] systems)
        {
            var builder = new WorldBuilder()
                .SetSettings(new WorldSettings())
                .AddEntityType(TrecsTemplates.Globals.Template)
                .AddBlobStore(EcsTestHelper.CreateBlobStore());

            var world = builder.Build();

            foreach (var system in systems)
            {
                world.AddSystem(system);
            }

            world.Initialize();
            return new TestEnvironment(world);
        }

        int FindSystemIndex(World world, Type systemType)
        {
            for (int i = 0; i < world.SystemCount; i++)
            {
                if (world.GetSystemMetadata(i).System.GetType() == systemType)
                {
                    return i;
                }
            }
            throw new InvalidOperationException($"System {systemType} not found");
        }

        [SetUp]
        public void SetUp() => EnableTestLog.Reset();

        #region Default state

        [Test]
        public void DefaultState_AllChannelsEnabled_NotPaused()
        {
            using var env = CreateEnv(new CountingEnableSystemA());

            for (int i = 0; i < env.World.SystemCount; i++)
            {
                NAssert.IsTrue(
                    env.Accessor.IsSystemEnabled(i, EnableChannel.Editor),
                    "Editor enabled by default"
                );
                NAssert.IsTrue(
                    env.Accessor.IsSystemEnabled(i, EnableChannel.Playback),
                    "Playback enabled by default"
                );
                NAssert.IsTrue(
                    env.Accessor.IsSystemEnabled(i, EnableChannel.User),
                    "User enabled by default"
                );
                NAssert.IsFalse(env.Accessor.IsSystemPaused(i), "Not paused by default");
            }
        }

        [Test]
        public void DefaultState_SystemRuns()
        {
            var sys = new CountingEnableSystemA();
            using var env = CreateEnv(sys);

            env.World.Tick();
            env.World.LateTick();

            NAssert.AreEqual(1, EnableTestLog.CountA);
        }

        #endregion

        #region Channels

        [Test]
        public void Channel_Disabled_SystemSkipped()
        {
            var sys = new CountingEnableSystemA();
            using var env = CreateEnv(sys);
            int idx = FindSystemIndex(env.World, typeof(CountingEnableSystemA));

            env.Accessor.SetSystemEnabled(idx, EnableChannel.User, false);

            env.World.Tick();
            env.World.LateTick();

            NAssert.AreEqual(0, EnableTestLog.CountA);
        }

        [Test]
        public void Channel_DisabledThenEnabled_SystemRuns()
        {
            var sys = new CountingEnableSystemA();
            using var env = CreateEnv(sys);
            int idx = FindSystemIndex(env.World, typeof(CountingEnableSystemA));

            env.Accessor.SetSystemEnabled(idx, EnableChannel.User, false);
            env.World.Tick();
            env.World.LateTick();
            NAssert.AreEqual(0, EnableTestLog.CountA, "disabled");

            env.Accessor.SetSystemEnabled(idx, EnableChannel.User, true);
            EnableTestLog.Reset();
            env.World.Tick();
            env.World.LateTick();
            NAssert.AreEqual(1, EnableTestLog.CountA, "re-enabled");
        }

        [Test]
        public void Channels_AreIndependent_ToggleOneDoesNotAffectOther()
        {
            var sys = new CountingEnableSystemA();
            using var env = CreateEnv(sys);
            int idx = FindSystemIndex(env.World, typeof(CountingEnableSystemA));

            // Two channels disable concurrently
            env.Accessor.SetSystemEnabled(idx, EnableChannel.User, false);
            env.Accessor.SetSystemEnabled(idx, EnableChannel.Playback, false);
            env.World.Tick();
            env.World.LateTick();
            NAssert.AreEqual(0, EnableTestLog.CountA, "disabled by both");

            // Re-enable one — system stays skipped because the other still disables
            env.Accessor.SetSystemEnabled(idx, EnableChannel.Playback, true);
            EnableTestLog.Reset();
            env.World.Tick();
            env.World.LateTick();
            NAssert.AreEqual(0, EnableTestLog.CountA, "still disabled by User");

            // Re-enable the other — now it runs
            env.Accessor.SetSystemEnabled(idx, EnableChannel.User, true);
            EnableTestLog.Reset();
            env.World.Tick();
            env.World.LateTick();
            NAssert.AreEqual(1, EnableTestLog.CountA, "all channels enabled");
        }

        [Test]
        public void Channel_TogglingOne_DoesNotChangeOtherQuery()
        {
            var sys = new CountingEnableSystemA();
            using var env = CreateEnv(sys);
            int idx = FindSystemIndex(env.World, typeof(CountingEnableSystemA));

            env.Accessor.SetSystemEnabled(idx, EnableChannel.User, false);

            NAssert.IsFalse(env.Accessor.IsSystemEnabled(idx, EnableChannel.User));
            NAssert.IsTrue(
                env.Accessor.IsSystemEnabled(idx, EnableChannel.Editor),
                "Editor untouched"
            );
            NAssert.IsTrue(
                env.Accessor.IsSystemEnabled(idx, EnableChannel.Playback),
                "Playback untouched"
            );
        }

        [Test]
        public void Channel_OneSystemDisabled_OtherStillRuns()
        {
            using var env = CreateEnv(new CountingEnableSystemA(), new CountingEnableSystemB());
            int idxA = FindSystemIndex(env.World, typeof(CountingEnableSystemA));

            env.Accessor.SetSystemEnabled(idxA, EnableChannel.User, false);
            env.World.Tick();
            env.World.LateTick();

            NAssert.AreEqual(0, EnableTestLog.CountA, "A disabled");
            NAssert.AreEqual(1, EnableTestLog.CountB, "B unaffected");
        }

        #endregion

        #region Paused

        [Test]
        public void Paused_SkipsSystem()
        {
            var sys = new CountingEnableSystemA();
            using var env = CreateEnv(sys);
            int idx = FindSystemIndex(env.World, typeof(CountingEnableSystemA));

            env.Accessor.SetSystemPaused(idx, true);
            env.World.Tick();
            env.World.LateTick();

            NAssert.AreEqual(0, EnableTestLog.CountA);
        }

        [Test]
        public void Paused_Unpaused_SystemRunsAgain()
        {
            var sys = new CountingEnableSystemA();
            using var env = CreateEnv(sys);
            int idx = FindSystemIndex(env.World, typeof(CountingEnableSystemA));

            env.Accessor.SetSystemPaused(idx, true);
            env.World.Tick();
            env.World.LateTick();
            NAssert.AreEqual(0, EnableTestLog.CountA);

            env.Accessor.SetSystemPaused(idx, false);
            EnableTestLog.Reset();
            env.World.Tick();
            env.World.LateTick();
            NAssert.AreEqual(1, EnableTestLog.CountA);
        }

        [Test]
        public void IsSystemPaused_ReflectsLastSet()
        {
            using var env = CreateEnv(new CountingEnableSystemA());
            int idx = FindSystemIndex(env.World, typeof(CountingEnableSystemA));

            NAssert.IsFalse(env.Accessor.IsSystemPaused(idx));
            env.Accessor.SetSystemPaused(idx, true);
            NAssert.IsTrue(env.Accessor.IsSystemPaused(idx));
            env.Accessor.SetSystemPaused(idx, false);
            NAssert.IsFalse(env.Accessor.IsSystemPaused(idx));
        }

        [Test]
        public void Paused_AndChannelDisabled_BothMustClear_ForSystemToRun()
        {
            var sys = new CountingEnableSystemA();
            using var env = CreateEnv(sys);
            int idx = FindSystemIndex(env.World, typeof(CountingEnableSystemA));

            env.Accessor.SetSystemPaused(idx, true);
            env.Accessor.SetSystemEnabled(idx, EnableChannel.User, false);

            env.World.Tick();
            env.World.LateTick();
            NAssert.AreEqual(0, EnableTestLog.CountA, "both blocking");

            // Clear only paused — channel still blocks
            env.Accessor.SetSystemPaused(idx, false);
            EnableTestLog.Reset();
            env.World.Tick();
            env.World.LateTick();
            NAssert.AreEqual(0, EnableTestLog.CountA, "channel still disabled");

            // Clear channel too — now runs
            env.Accessor.SetSystemEnabled(idx, EnableChannel.User, true);
            EnableTestLog.Reset();
            env.World.Tick();
            env.World.LateTick();
            NAssert.AreEqual(1, EnableTestLog.CountA, "both clear");
        }

        #endregion

        #region Metadata API

        [Test]
        public void SystemCount_EqualsRegistered()
        {
            using var env = CreateEnv(new CountingEnableSystemA(), new CountingEnableSystemB());
            NAssert.AreEqual(2, env.World.SystemCount);
            NAssert.AreEqual(2, env.Accessor.SystemCount);
        }

        [Test]
        public void GetSystemMetadata_ReturnsRegisteredSystems()
        {
            using var env = CreateEnv(new CountingEnableSystemA(), new CountingEnableSystemB());

            bool foundA = false;
            bool foundB = false;
            for (int i = 0; i < env.World.SystemCount; i++)
            {
                var meta = env.World.GetSystemMetadata(i);
                NAssert.IsNotNull(meta);
                NAssert.IsNotNull(meta.System);
                if (meta.System is CountingEnableSystemA)
                    foundA = true;
                if (meta.System is CountingEnableSystemB)
                    foundB = true;
            }
            NAssert.IsTrue(foundA && foundB, "both systems discoverable via GetSystemMetadata");
        }

        [Test]
        public void GetSystemMetadata_OutOfRange_Asserts()
        {
            using var env = CreateEnv(new CountingEnableSystemA());

            NAssert.Throws<TrecsException>(() => env.World.GetSystemMetadata(-1));
            NAssert.Throws<TrecsException>(() =>
                env.World.GetSystemMetadata(env.World.SystemCount)
            );
        }

        #endregion

        #region IsSystemEffectivelyEnabled

        [Test]
        public void IsSystemEffectivelyEnabled_TrueByDefault()
        {
            using var env = CreateEnv(new CountingEnableSystemA());
            int idx = FindSystemIndex(env.World, typeof(CountingEnableSystemA));

            NAssert.IsTrue(env.World.IsSystemEffectivelyEnabled(idx));
            NAssert.IsTrue(env.Accessor.IsSystemEffectivelyEnabled(idx));
        }

        [Test]
        public void IsSystemEffectivelyEnabled_FalseWhenChannelDisables()
        {
            using var env = CreateEnv(new CountingEnableSystemA());
            int idx = FindSystemIndex(env.World, typeof(CountingEnableSystemA));

            env.Accessor.SetSystemEnabled(idx, EnableChannel.User, false);
            NAssert.IsFalse(env.World.IsSystemEffectivelyEnabled(idx));
            NAssert.IsFalse(env.Accessor.IsSystemEffectivelyEnabled(idx));
        }

        [Test]
        public void IsSystemEffectivelyEnabled_FalseWhenPaused()
        {
            using var env = CreateEnv(new CountingEnableSystemA());
            int idx = FindSystemIndex(env.World, typeof(CountingEnableSystemA));

            env.Accessor.SetSystemPaused(idx, true);
            NAssert.IsFalse(env.World.IsSystemEffectivelyEnabled(idx));
            NAssert.IsFalse(env.Accessor.IsSystemEffectivelyEnabled(idx));
        }

        [Test]
        public void IsSystemEffectivelyEnabled_RequiresAllChannelsAndUnpaused()
        {
            using var env = CreateEnv(new CountingEnableSystemA());
            int idx = FindSystemIndex(env.World, typeof(CountingEnableSystemA));

            env.Accessor.SetSystemEnabled(idx, EnableChannel.User, false);
            env.Accessor.SetSystemPaused(idx, true);
            NAssert.IsFalse(env.World.IsSystemEffectivelyEnabled(idx));

            // Clear paused — channel still blocks
            env.Accessor.SetSystemPaused(idx, false);
            NAssert.IsFalse(env.World.IsSystemEffectivelyEnabled(idx));

            // Clear channel — now effectively enabled
            env.Accessor.SetSystemEnabled(idx, EnableChannel.User, true);
            NAssert.IsTrue(env.World.IsSystemEffectivelyEnabled(idx));
        }

        #endregion
    }
}
