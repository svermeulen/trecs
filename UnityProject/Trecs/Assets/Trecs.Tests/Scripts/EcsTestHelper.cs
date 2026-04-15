using System;

namespace Trecs.Tests
{
    public class TestEnvironment : IDisposable
    {
        public World EcsWorld;

        WorldAccessor _accessor;
        public WorldAccessor Accessor => _accessor ??= EcsWorld.CreateAccessor();

        public TestEnvironment(World ecs)
        {
            EcsWorld = ecs;
        }

        public void Dispose()
        {
            EcsWorld.Dispose();
        }
    }

    public static class EcsTestHelper
    {
        public static TestEnvironment CreateEnvironment(params Template[] templates)
        {
            return CreateEnvironment(new WorldSettings(), templates);
        }

        public static TestEnvironment CreateEnvironment(
            WorldSettings settings,
            params Template[] templates
        )
        {
            var testGlobals = new Template(
                debugName: "TestGlobals",
                localBaseTemplates: new Template[] { TrecsTemplates.Globals.Template },
                states: Array.Empty<TagSet>(),
                localComponentDeclarations: Array.Empty<IComponentDeclaration>(),
                localTags: Array.Empty<Tag>()
            );

            var blobStoreCommon = new BlobStoreCommon(null);
            var blobStore = new BlobStoreInMemory(
                new BlobStoreInMemorySettings { MaxMemoryCacheMb = 100 },
                blobStoreCommon
            );

            var builder = new WorldBuilder()
                .SetSettings(settings)
                .AddTemplate(testGlobals)
                .AddBlobStore(blobStore);

            foreach (var template in templates)
            {
                builder.AddTemplate(template);
            }

            return new TestEnvironment(builder.BuildAndInitialize());
        }
    }
}
