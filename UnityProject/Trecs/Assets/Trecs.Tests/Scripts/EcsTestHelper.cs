using System;

namespace Trecs.Tests
{
    public class TestEnvironment : IDisposable
    {
        public World World;

        WorldAccessor _accessor;
        public WorldAccessor Accessor =>
            _accessor ??= World.CreateAccessor(AccessorRole.Unrestricted);

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
        /// Delegates to the shared <see cref="TestWorldStepper.StepFixedFrames"/> so
        /// edit-mode and play-mode tests step identically.
        /// </summary>
        public void StepFixedFrames(int frames)
        {
            World.StepFixedFrames(frames);
        }
    }

    public static class EcsTestHelper
    {
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
                globalsTemplate = TestTemplate
                    .Named("TestGlobals")
                    .Extending(TrecsTemplates.Globals.Template);
            }

            var builder = new WorldBuilder().SetSettings(settings).AddTemplate(globalsTemplate);

            foreach (var template in templates)
            {
                builder.AddTemplate(template);
            }

            configure?.Invoke(builder);

            return new TestEnvironment(builder.BuildAndInitialize());
        }
    }
}
