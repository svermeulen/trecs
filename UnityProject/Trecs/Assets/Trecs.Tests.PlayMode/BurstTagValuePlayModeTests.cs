using NUnit.Framework;
using Trecs.Internal;
using NAssert = NUnit.Framework.Assert;

namespace Trecs.Tests.PlayMode
{
    // Regression guard for issue #10 (`Tag<T>.Value cannot be burst compiled`).
    //
    // The EditMode JobSystemTests cover the managed value, but in batch-mode
    // EditMode Burst falls back to managed execution when a job fails to compile,
    // so a BC1040 regression there is silent. Burst only AOT-compiles (and hard-
    // fails on BC1040) when building a Player — which is exactly what running this
    // assembly under `--platform standalone-osx` does. The [WrapAsJob] method below
    // loads Tag<BurstTagMarker>.Value from inside the Burst body, so if Tag<T> ever
    // regresses to caching its value in a mutable static field the AOT player build
    // fails outright.

    public struct BurstTagMarker : ITag { }

    public partial struct BurstTagReadback : IEntityComponent
    {
        public int Value;
    }

    public partial class BurstTagEntity : ITemplate, ITagged<BurstTagMarker>
    {
        BurstTagReadback BurstTagReadback;
    }

    partial class BurstTagReaderSystem : ISystem
    {
        [ForEachEntity(Tag = typeof(BurstTagMarker))]
        [WrapAsJob]
        static void Read(ref BurstTagReadback value)
        {
            value.Value = Tag<BurstTagMarker>.Value.Value;
        }

        public void Execute()
        {
            Read();
        }
    }

    [TestFixture]
    public class BurstTagValuePlayModeTests
    {
        const int EntityCount = 8;

        [Test]
        public void WrapAsJob_ReadingTagValue_AotBurstCompilesAndMatches()
        {
            var world = new WorldBuilder().AddTemplate(BurstTagEntity.Template).Build();
            world.AddSystems(new ISystem[] { new BurstTagReaderSystem() });
            world.Initialize();

            try
            {
                var a = world.CreateAccessor(AccessorRole.Unrestricted);
                for (int i = 0; i < EntityCount; i++)
                {
                    a.AddEntity<BurstTagMarker>()
                        .Set(new BurstTagReadback { Value = -1 })
                        .AssertComplete();
                }
                a.Submit();

                // One fixed tick → Execute() schedules the Burst job, which writes
                // Tag<BurstTagMarker>.Value.Value into every entity's component.
                StepFixedFrames(world, 1);

                int expected = Tag<BurstTagMarker>.Value.Value;
                var group = a.WorldInfo.GetSingleGroupWithTags(Tag<BurstTagMarker>.Value);
                int count = a.CountEntitiesWithTags(Tag<BurstTagMarker>.Value);
                NAssert.AreEqual(EntityCount, count);

                var buffer = a.ComponentBuffer<BurstTagReadback>(group).Read;
                for (int i = 0; i < count; i++)
                {
                    NAssert.AreEqual(
                        expected,
                        buffer[i].Value,
                        $"Entity {i}: AOT Burst job read Tag<BurstTagMarker>.Value as "
                            + $"{buffer[i].Value} but managed Tag<BurstTagMarker>.Value is {expected}."
                    );
                }
            }
            finally
            {
                world.Dispose();
            }
        }

        // Mirrors EcsTestHelper.TestEnvironment.StepFixedFrames (that helper lives in
        // the Editor-only Trecs.Tests assembly, so it can't be reused from here).
        static void StepFixedFrames(World world, int frames)
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
