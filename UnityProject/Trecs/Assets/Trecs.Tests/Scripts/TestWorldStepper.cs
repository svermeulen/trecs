using Trecs.Internal;

namespace Trecs.Tests
{
    /// <summary>
    /// Shared, deterministic frame-stepping helper used by both the edit-mode
    /// (<c>Trecs.Tests</c>) and play-mode (<c>Trecs.Tests.PlayMode</c>) test
    /// assemblies. Lives in its own platform-agnostic assembly because the
    /// edit-mode test assembly is Editor-only and cannot be referenced from a
    /// player build.
    /// </summary>
    public static class TestWorldStepper
    {
        /// <summary>
        /// Advances <paramref name="frames"/> fixed-update frames in lockstep.
        /// Decouples from Unity's non-deterministic <c>Time.deltaTime</c> by
        /// pausing fixed update and explicitly stepping one frame per iteration.
        /// The leading Tick+LateTick gives variable-update systems one cycle to
        /// settle before the first stepped fixed frame.
        /// </summary>
        public static void StepFixedFrames(this World world, int frames)
        {
            var runner = world.GetSystemRunner();
            runner.FixedIsPaused = true;

            world.Tick();
            world.LateTick();

            for (int i = 0; i < frames; i++)
            {
                runner.StepFixedFrame();
                world.Tick();
                world.LateTick();
            }
        }
    }
}
