using System;
using Trecs.Internal;

namespace Trecs.Tests
{
    public class TestEnvironment : IDisposable
    {
        public World World;

        WorldAccessor _accessor;
        public WorldAccessor Accessor => _accessor ??= World.CreateAccessor();

        public TestEnvironment(World world)
        {
            World = world;
        }

        public void Dispose()
        {
            if (!World.IsDisposed)
            {
                World.Dispose();
            }
        }

        /// <summary>
        /// Advances <paramref name="frames"/> fixed-update frames in lockstep.
        /// Decouples from Unity's non-deterministic <c>Time.deltaTime</c> in EditMode
        /// by pausing fixed update and explicitly stepping one frame per iteration.
        ///
        /// The first Tick+LateTick "primes" the pause flag: the public
        /// <c>FixedIsPaused</c> setter only updates <c>_desiredFixedIsPaused</c>;
        /// the actual <c>_fixedIsPaused</c> field doesn't sync until the start of
        /// the next <c>Tick()</c>. Without the prime, the first <c>StepFrame()</c>
        /// silently warns-and-returns because the runner isn't paused yet, and the
        /// first fixed step is lost. The prime itself runs no fixed update — the
        /// pause takes effect at its start.
        ///
        /// Safe to call repeatedly: subsequent primes are no-op ticks.
        /// </summary>
        public void StepFixedFrames(int frames)
        {
            var runner = World.GetSystemRunner();
            runner.FixedIsPaused = true;

            World.Tick();
            World.LateTick();

            for (int i = 0; i < frames; i++)
            {
                runner.StepFrame();
                World.Tick();
                World.LateTick();
            }
        }
    }

    public static class EcsTestHelper
    {
        public static BlobStoreInMemory CreateBlobStore()
        {
            return new BlobStoreInMemory(
                new BlobStoreInMemorySettings { MaxMemoryCacheMb = 100 },
                null
            );
        }

        public static TestEnvironment CreateEnvironment(params Template[] templates)
        {
            return CreateEnvironment(new WorldSettings(), null, templates);
        }

        public static TestEnvironment CreateEnvironment(
            WorldSettings settings,
            params Template[] templates
        )
        {
            return CreateEnvironment(settings, null, templates);
        }

        public static TestEnvironment CreateEnvironment(
            Action<WorldBuilder> configure,
            params Template[] templates
        )
        {
            return CreateEnvironment(new WorldSettings(), configure, templates);
        }

        public static TestEnvironment CreateEnvironment(
            WorldSettings settings,
            Action<WorldBuilder> configure,
            params Template[] templates
        )
        {
            return CreateEnvironment(settings, configure, globalsTemplate: null, templates);
        }

        public static TestEnvironment CreateEnvironment(
            WorldSettings settings,
            Action<WorldBuilder> configure,
            Template globalsTemplate,
            params Template[] templates
        )
        {
            if (globalsTemplate == null)
            {
                globalsTemplate = new Template(
                    debugName: "TestGlobals",
                    localBaseTemplates: new Template[] { TrecsTemplates.Globals.Template },
                    partitions: Array.Empty<TagSet>(),
                    localComponentDeclarations: Array.Empty<IComponentDeclaration>(),
                    localTags: Array.Empty<Tag>()
                );
            }

            var builder = new WorldBuilder()
                .SetSettings(settings)
                .AddEntityType(globalsTemplate)
                .AddBlobStore(CreateBlobStore());

            foreach (var template in templates)
            {
                builder.AddEntityType(template);
            }

            configure?.Invoke(builder);

            return new TestEnvironment(builder.BuildAndInitialize());
        }
    }
}
