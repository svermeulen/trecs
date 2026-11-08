using System.IO;
using System.Threading;
using NUnit.Framework;
using Trecs.Internal;

namespace Trecs.Tests
{
    [TestFixture]
    public class FixedLoopTests
    {
        [Test]
        public void PauseRequestedMidLoop_StopsAtThatStep()
        {
            using var env = EcsTestHelper.CreateEnvironment();
            var runner = env.World.GetSystemRunner();
            env.World.Tick();
            env.World.LateTick();

            // Several steps due in one tick; EditMode's delta time is zero.
            runner.InterpolationOffset += 10 * runner.FixedDeltaTime;
            var pauseFrame = env.Accessor.FixedFrame + 2;
            using var sub = env.Accessor.Events.OnFixedUpdateCompleted(() =>
            {
                if (env.Accessor.FixedFrame == pauseFrame)
                {
                    runner.FixedIsPaused = true;
                }
            });

            env.World.Tick();
            env.World.LateTick();

            Assert.AreEqual(pauseFrame, env.Accessor.FixedFrame);
        }

        [Test]
        public void SkipForward_DropsTimeInsteadOfAddingItToFixedTime()
        {
            using var env = EcsTestHelper.CreateEnvironment(
                new WorldSettings
                {
                    MaxSecondsForFixedUpdatePerFrame = 0.001f,
                    WarnOnFixedUpdateFallingBehind = false,
                }
            );
            var runner = env.World.GetSystemRunner();
            env.World.Tick();
            env.World.LateTick();
            var variableTime = runner.VariableElapsedTime;

            // Each step outlasts the budget, so the tick runs one of the due steps and drops the rest.
            runner.InterpolationOffset += 10 * runner.FixedDeltaTime;
            var frame = env.Accessor.FixedFrame;
            using var sub = env.Accessor.Events.OnFixedUpdateCompleted(() => Thread.Sleep(5));

            env.World.Tick();
            env.World.LateTick();

            Assert.AreEqual(frame + 1, env.Accessor.FixedFrame, "the tick did not skip");
            Assert.AreEqual(
                env.Accessor.FixedFrame * runner.FixedDeltaTime,
                runner.FixedElapsedTime,
                1e-5f
            );
            Assert.AreEqual(variableTime + runner.VariableDeltaTime, runner.VariableElapsedTime);
        }

        [Test]
        public void VariableClock_RunsOnAcrossPauseAndRestore()
        {
            using var env = EcsTestHelper.CreateEnvironment();
            var runner = env.World.GetSystemRunner();
            env.World.Tick();
            env.World.LateTick();

            // Far ahead of fixed time, so a reset of variable time to fixed time shows.
            runner.VariableElapsedTime = 100f;
            var expected = 100f;
            var frame = runner.VariableFrame;
            void TickOnce()
            {
                env.World.Tick();
                env.World.LateTick();
                expected += runner.VariableDeltaTime;
            }
            using var snapshots = new SnapshotSerializer(
                new WorldStateSerializer(env.World),
                env.World.SerializerRegistry,
                env.World
            );
            using var snapshot = new MemoryStream();
            snapshots.SaveSnapshot(1, snapshot);

            runner.FixedIsPaused = true;
            TickOnce();
            runner.FixedIsPaused = false;
            TickOnce();
            snapshot.Position = 0;
            snapshots.LoadSnapshot(snapshot);
            TickOnce();

            Assert.AreEqual(expected, runner.VariableElapsedTime, 1e-4f);
            Assert.AreEqual(frame + 3, runner.VariableFrame);
        }
    }
}
