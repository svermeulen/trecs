using NUnit.Framework;
using NAssert = NUnit.Framework.Assert;

namespace Trecs.Tests
{
    [TestFixture]
    public class WorldDisposeTests
    {
        [Test]
        public void World_Dispose_AfterMixedOperations_DoesNotThrow()
        {
            var env = EcsTestHelper.CreateEnvironment(TestTemplates.WithPartitions);
            var a = env.Accessor;

            var partitionA = TagSet.FromTags(TestTags.Gamma, TestTags.PartitionA);
            var partitionB = TagSet.FromTags(TestTags.Gamma, TestTags.PartitionB);

            // Add entities
            var refs = new EntityHandle[5];
            for (int i = 0; i < 5; i++)
            {
                var init = a.AddEntity(partitionA)
                    .Set(new TestInt { Value = i })
                    .Set(new TestVec { X = i, Y = i })
                    .AssertComplete();
                refs[i] = init.Handle;
            }
            a.SubmitEntities();

            // Remove some
            a.RemoveEntity(refs[0]);
            a.RemoveEntity(refs[2]);
            a.SubmitEntities();

            // Move some
            a.MoveTo(refs[1].ToIndex(a), partitionB);
            a.SubmitEntities();

            // Add more
            a.AddEntity(partitionA).Set(new TestInt { Value = 99 }).AssertComplete();
            a.SubmitEntities();

            // Dispose should not throw
            NAssert.DoesNotThrow(() => env.Dispose());
        }

        [Test]
        public void World_IsDisposed_TrueAfterDispose()
        {
            var env = EcsTestHelper.CreateEnvironment(TestTemplates.SimpleAlpha);
            var world = env.EcsWorld;

            NAssert.IsFalse(world.IsDisposed);

            env.Dispose();

            NAssert.IsTrue(world.IsDisposed);
        }
    }
}
