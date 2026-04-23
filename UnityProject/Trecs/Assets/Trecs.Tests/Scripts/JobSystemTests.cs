using NUnit.Framework;
using NAssert = NUnit.Framework.Assert;

namespace Trecs.Tests
{
    /// <summary>
    /// Main-thread iteration system used as the ground-truth reference for the
    /// <c>[WrapAsJob]</c> parity test. The logic is intentionally trivial (add 7
    /// to <see cref="TestInt"/>) — anything more elaborate would just make a
    /// parity mismatch harder to read without improving coverage.
    /// </summary>
    partial class JobParityMainThreadSystem : ISystem
    {
        [ForEachEntity(Tag = typeof(QId1))]
        void Move(ref TestInt value)
        {
            value.Value += 7;
        }

        public void Execute()
        {
            Move();
        }
    }

    /// <summary>
    /// Burst / job flavour of <see cref="JobParityMainThreadSystem"/>. Identical
    /// logic; the source generator produces a Burst-compiled <c>IJobFor</c> that
    /// the scheduling wrapper fires off each tick.
    /// </summary>
    partial class JobParityWrapAsJobSystem : ISystem
    {
        [ForEachEntity(Tag = typeof(QId1))]
        [WrapAsJob]
        static void Move(ref TestInt value)
        {
            value.Value += 7;
        }

        public void Execute()
        {
            Move();
        }
    }

    /// <summary>
    /// Schedules a writer <c>[WrapAsJob]</c> three times in a single fixed tick.
    /// If the job scheduler's dependency tracking is honest, each scheduled writer
    /// waits on the previous one's handle before running — so every increment
    /// lands and the final value equals <c>initial + 3 * increment</c>. If
    /// dependency tracking were broken, Unity's safety system would throw on a
    /// write/write conflict, failing the test.
    /// </summary>
    partial class SequentialWriteJobSystem : ISystem
    {
        public int IncrementAmount;

        [ForEachEntity(Tag = typeof(QId1))]
        [WrapAsJob]
        static void Increment(ref TestInt value, [PassThroughArgument] int amount)
        {
            value.Value += amount;
        }

        public void Execute()
        {
            Increment(IncrementAmount);
            Increment(IncrementAmount);
            Increment(IncrementAmount);
        }
    }

    /// <summary>
    /// Schedules two reader <c>[WrapAsJob]</c>s that both read
    /// <see cref="TestInt"/> in the same fixed tick. Correct behaviour is that
    /// the scheduler registers both as concurrent readers (read/read is not a
    /// conflict) and Unity's safety system doesn't throw on the second
    /// <c>ScheduleParallel</c> call. If dependency tracking mistakenly treated
    /// one of the reads as a writer, Unity would raise at schedule time.
    ///
    /// Verified post-condition: the component state is unchanged (readers did
    /// not mutate) — reading a handful of non-trivial values into a throwaway
    /// sum prevents Burst from compiling the methods into a literal no-op.
    /// </summary>
    partial class ConcurrentReadersJobSystem : ISystem
    {
        public int BallastA;
        public int BallastB;

        [ForEachEntity(Tag = typeof(QId1))]
        [WrapAsJob]
        static void ReadA(in TestInt value, [PassThroughArgument] int ballast)
        {
            // Burst-visible use of `value` to prevent the job being optimised away.
            _ = value.Value ^ ballast;
        }

        [ForEachEntity(Tag = typeof(QId1))]
        [WrapAsJob]
        static void ReadB(in TestInt value, [PassThroughArgument] int ballast)
        {
            _ = value.Value ^ ballast;
        }

        public void Execute()
        {
            ReadA(BallastA);
            ReadB(BallastB);
        }
    }

    /// <summary>
    /// Covers the invariants that make the Trecs job scheduling layer safe to
    /// use: functional parity between main-thread and <c>[WrapAsJob]</c>
    /// variants, sequential write dependency tracking, and the read/read
    /// concurrency allowance. These tests run against EditMode (Unity job
    /// threads may or may not be active) — the point is that Unity's safety
    /// system doesn't throw and the emitted results match the main-thread
    /// reference.
    /// </summary>
    [TestFixture]
    public class JobSystemTests
    {
        const int EntityCount = 10;

        [Test]
        public void WrapAsJob_AndMainThread_ProduceSameResults()
        {
            using var envMain = EcsTestHelper.CreateEnvironment(
                b => b.AddSystem(new JobParityMainThreadSystem()),
                QTestEntityA.Template
            );
            using var envJob = EcsTestHelper.CreateEnvironment(
                b => b.AddSystem(new JobParityWrapAsJobSystem()),
                QTestEntityA.Template
            );

            SpawnEntities(envMain);
            SpawnEntities(envJob);

            envMain.StepFixedFrames(3);
            envJob.StepFixedFrames(3);

            var mainValues = CollectTestIntValues(envMain);
            var jobValues = CollectTestIntValues(envJob);

            NAssert.AreEqual(
                mainValues.Length,
                jobValues.Length,
                "Entity counts should match between the two worlds."
            );
            for (int i = 0; i < mainValues.Length; i++)
            {
                NAssert.AreEqual(
                    mainValues[i],
                    jobValues[i],
                    $"Entity {i}: [WrapAsJob] produced {jobValues[i]} but main-thread produced {mainValues[i]}."
                );
            }
        }

        [Test]
        public void SequentialWriteJobs_AllApply()
        {
            var system = new SequentialWriteJobSystem { IncrementAmount = 5 };
            using var env = EcsTestHelper.CreateEnvironment(
                b => b.AddSystem(system),
                QTestEntityA.Template
            );

            SpawnEntities(env);

            // One fixed tick → Execute() schedules three sequential Increment jobs.
            env.StepFixedFrames(1);

            var values = CollectTestIntValues(env);
            for (int i = 0; i < values.Length; i++)
            {
                int expected = (i * 10) + 3 * 5;
                NAssert.AreEqual(
                    expected,
                    values[i],
                    $"Entity {i}: expected initial {i * 10} + 3×5 = {expected}, got {values[i]}. "
                        + "If fewer than 3 increments landed, dependency tracking is losing writes."
                );
            }
        }

        [Test]
        public void ConcurrentReaderJobs_ScheduleWithoutConflict()
        {
            var system = new ConcurrentReadersJobSystem { BallastA = 0x5A, BallastB = 0xA5 };
            using var env = EcsTestHelper.CreateEnvironment(
                b => b.AddSystem(system),
                QTestEntityA.Template
            );

            SpawnEntities(env);

            var beforeValues = CollectTestIntValues(env);

            // The assertion here is "does not throw" — if the scheduler mis-tracks
            // either read as a write, Unity's safety system throws on the second
            // ScheduleParallel call and the test fails.
            env.StepFixedFrames(1);

            // Post-condition: readers did not mutate state.
            var afterValues = CollectTestIntValues(env);
            NAssert.AreEqual(beforeValues.Length, afterValues.Length);
            for (int i = 0; i < beforeValues.Length; i++)
            {
                NAssert.AreEqual(
                    beforeValues[i],
                    afterValues[i],
                    $"Entity {i}: readers mutated state from {beforeValues[i]} to {afterValues[i]}."
                );
            }
        }

        // ─── Helpers ────────────────────────────────────────────────────────────

        static void SpawnEntities(TestEnvironment env)
        {
            var a = env.Accessor;
            for (int i = 0; i < EntityCount; i++)
            {
                a.AddEntity(Tag<QId1>.Value)
                    .Set(new TestInt { Value = i * 10 })
                    .Set(new TestFloat())
                    .AssertComplete();
            }
            a.SubmitEntities();
        }

        /// <summary>
        /// Reads every QId1 entity's TestInt value in declaration order.
        /// Uses a simple query + ComponentBuffer rather than iterating via aspects
        /// to keep the comparison uncoupled from the aspect source generator.
        /// </summary>
        static int[] CollectTestIntValues(TestEnvironment env)
        {
            var a = env.Accessor;
            var group = a.WorldInfo.GetSingleGroupWithTags(Tag<QId1>.Value);
            var count = a.CountEntitiesWithTags(Tag<QId1>.Value);
            var buffer = a.ComponentBuffer<TestInt>(group).Read;
            var values = new int[count];
            for (int i = 0; i < count; i++)
            {
                values[i] = buffer[i].Value;
            }
            return values;
        }
    }
}
