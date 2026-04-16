using System;

namespace Trecs.Tests
{
    public class TestEnvironment : IDisposable
    {
        public World EcsWorld;

        WorldAccessor _accessor;
        public WorldAccessor Accessor => _accessor ??= EcsWorld.CreateAccessor();

        public TestEnvironment(World world)
        {
            EcsWorld = world;
        }

        public void Dispose()
        {
            EcsWorld.Dispose();
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
