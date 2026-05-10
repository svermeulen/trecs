using System;
using NUnit.Framework;
using Trecs.Internal;
using NAssert = NUnit.Framework.Assert;

namespace Trecs.Tests
{
    /// <summary>
    /// Interpolation subsystem tests — the framework copies current component values into
    /// <see cref="InterpolatedPrevious{T}"/> each fixed tick and blends them into
    /// <see cref="Interpolated{T}"/> each variable tick. These tests exercise the
    /// building blocks (extension, saver, utility) without depending on the
    /// source-generated <see cref="InterpolatedUpdater{T}"/> variants.
    /// </summary>
    [TestFixture]
    public class InterpolationTests
    {
        static Template InterpolatedIntTemplate =>
            new Template(
                debugName: "InterpolatedIntTemplate",
                localBaseTemplates: Array.Empty<Template>(),
                partitions: Array.Empty<TagSet>(),
                localComponentDeclarations: new IComponentDeclaration[]
                {
                    new ComponentDeclaration<TestInt>(
                        null,
                        null,
                        null,
                        null,
                        null,
                        default(TestInt)
                    ),
                    new ComponentDeclaration<Interpolated<TestInt>>(
                        null,
                        null,
                        null,
                        null,
                        null,
                        default(Interpolated<TestInt>)
                    ),
                    new ComponentDeclaration<InterpolatedPrevious<TestInt>>(
                        null,
                        null,
                        null,
                        null,
                        null,
                        default(InterpolatedPrevious<TestInt>)
                    ),
                },
                localTags: new Tag[] { TestTags.Alpha }
            );

        // ─── SetInterpolated extension ─────────────────────────────────────────

        [Test]
        public void SetInterpolated_InitializesAllThreeComponents()
        {
            using var env = EcsTestHelper.CreateEnvironment(InterpolatedIntTemplate);
            var a = env.Accessor;

            a.AddEntity(TestTags.Alpha)
                .SetInterpolated(new TestInt { Value = 42 })
                .AssertComplete();
            a.SubmitEntities();

            var entity = a.Query().WithTags(TestTags.Alpha).Single();

            NAssert.AreEqual(42, entity.Get<TestInt>().Read.Value);
            NAssert.AreEqual(42, entity.Get<Interpolated<TestInt>>().Read.Value.Value);
            NAssert.AreEqual(42, entity.Get<InterpolatedPrevious<TestInt>>().Read.Value.Value);
        }

        [Test]
        public void SetInterpolated_PreviousMatchesCurrentOnInitialization()
        {
            using var env = EcsTestHelper.CreateEnvironment(InterpolatedIntTemplate);
            var a = env.Accessor;

            a.AddEntity(TestTags.Alpha).SetInterpolated(new TestInt { Value = 7 }).AssertComplete();
            a.SubmitEntities();

            var entity = a.Query().WithTags(TestTags.Alpha).Single();
            var current = entity.Get<TestInt>().Read.Value;
            var previous = entity.Get<InterpolatedPrevious<TestInt>>().Read.Value.Value;

            NAssert.AreEqual(
                current,
                previous,
                "Previous should equal current at initialization so the first variable-update frame has no visible jump"
            );
        }

        // ─── InterpolatedPreviousSaver ────────────────────────────────────────

        [Test]
        public void InterpolatedPreviousSaver_CopiesCurrentIntoPreviousOnSave()
        {
            using var env = EcsTestHelper.CreateEnvironment(
                b => b.AddInterpolatedPreviousSaver(new InterpolatedPreviousSaver<TestInt>()),
                InterpolatedIntTemplate
            );
            var a = env.Accessor;

            a.AddEntity(TestTags.Alpha).SetInterpolated(new TestInt { Value = 1 }).AssertComplete();
            a.SubmitEntities();

            // Mutate current to simulate a fixed tick advancing state.
            var entity = a.Query().WithTags(TestTags.Alpha).Single();
            entity.Get<TestInt>().Write.Value = 100;

            // One tick drives the saver via the fixed-update loop.
            env.World.Tick();
            env.World.LateTick();

            entity = a.Query().WithTags(TestTags.Alpha).Single();
            NAssert.AreEqual(
                100,
                entity.Get<InterpolatedPrevious<TestInt>>().Read.Value.Value,
                "After a tick, Previous should have been updated to the current value"
            );
        }

        [Test]
        public void InterpolatedPreviousSaver_ComponentTypeMatchesT()
        {
            var saver = new InterpolatedPreviousSaver<TestInt>();
            NAssert.AreEqual(typeof(TestInt), saver.ComponentType);
        }

        [Test]
        public void InterpolatedPreviousSaverManager_RejectsDuplicateRegistrations()
        {
            NAssert.Catch<TrecsException>(() =>
            {
                using var _ = EcsTestHelper.CreateEnvironment(
                    b =>
                        b.AddInterpolatedPreviousSaver(new InterpolatedPreviousSaver<TestInt>())
                            .AddInterpolatedPreviousSaver(new InterpolatedPreviousSaver<TestInt>()),
                    InterpolatedIntTemplate
                );
            });
        }

        // ─── InterpolationUtil.CalculatePercentThroughFixedFrame ───────────────
        //
        // The percent is computed from the world's variable/fixed elapsed/delta
        // time state. We can't directly set those in tests, but we can verify the
        // mathematical behaviour by constructing a world and letting it tick —
        // at t=0 before the first fixed tick completes the result should be 0.

        [Test]
        public void CalculatePercentThroughFixedFrame_BeforeFirstFixedTick_ReturnsZero()
        {
            using var env = EcsTestHelper.CreateEnvironment();
            var percent = InterpolationUtil.CalculatePercentThroughFixedFrame(env.Accessor);
            NAssert.AreEqual(0f, percent);
        }
    }
}
