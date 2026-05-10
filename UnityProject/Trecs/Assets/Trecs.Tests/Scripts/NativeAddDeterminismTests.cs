using NUnit.Framework;
using Unity.Collections;
using NAssert = NUnit.Framework.Assert;
using Trecs.Internal;

namespace Trecs.Tests
{
    [TestFixture]
    public class NativeAddDeterminismTests
    {
        static readonly WorldSettings DeterministicSettings = new()
        {
            RequireDeterministicSubmission = true,
        };

        [Test]
        public void NativeAdd_SortKeyOrder_EntitiesAreSortedBySortKey()
        {
            using var env = EcsTestHelper.CreateEnvironment(
                DeterministicSettings,
                TestTemplates.SimpleAlpha
            );
            var a = env.Accessor;
            var nativeEcs = a.ToNative();
            var group = a.WorldInfo.GetSingleGroupWithTags(TestTags.Alpha);

            // Add 5 entities with scrambled sort keys (reverse order)
            int[] sortKeys = { 4, 2, 0, 3, 1 };
            var refs = a.ReserveEntityHandles(sortKeys.Length, Allocator.Temp);
            for (int i = 0; i < sortKeys.Length; i++)
            {
                var init = nativeEcs.AddEntity(TestTags.Alpha, sortKey: (uint)sortKeys[i], refs[i]);
                init.Set(new TestInt { Value = sortKeys[i] * 10 });
            }
            refs.Dispose();

            a.SubmitEntities();

            NAssert.AreEqual(5, a.CountEntitiesWithTags(TestTags.Alpha));

            // After sorting by sort key, expected order: 0, 10, 20, 30, 40
            for (int i = 0; i < 5; i++)
            {
                var comp = a.Component<TestInt>(new EntityIndex(i, group));
                NAssert.AreEqual(
                    i * 10,
                    comp.Read.Value,
                    $"Entity at index {i} should have value {i * 10} but had {comp.Read.Value}"
                );
            }
        }

        [Test]
        public void NativeAdd_MultipleThreadBags_SortKeyOrderAcrossBags()
        {
            using var env = EcsTestHelper.CreateEnvironment(
                DeterministicSettings,
                TestTemplates.SimpleAlpha
            );
            var a = env.Accessor;
            var nativeEcs = a.ToNative();
            var group = a.WorldInfo.GetSingleGroupWithTags(TestTags.Alpha);

            var refs = a.ReserveEntityHandles(4, Allocator.Temp);

            var init0 = nativeEcs.AddEntity(TestTags.Alpha, sortKey: 3, refs[0]);
            init0.Set(new TestInt { Value = 30 });

            var init1 = nativeEcs.AddEntity(TestTags.Alpha, sortKey: 1, refs[1]);
            init1.Set(new TestInt { Value = 10 });

            var init2 = nativeEcs.AddEntity(TestTags.Alpha, sortKey: 2, refs[2]);
            init2.Set(new TestInt { Value = 20 });

            var init3 = nativeEcs.AddEntity(TestTags.Alpha, sortKey: 0, refs[3]);
            init3.Set(new TestInt { Value = 0 });

            refs.Dispose();
            a.SubmitEntities();

            NAssert.AreEqual(4, a.CountEntitiesWithTags(TestTags.Alpha));

            for (int i = 0; i < 4; i++)
            {
                var comp = a.Component<TestInt>(new EntityIndex(i, group));
                NAssert.AreEqual(
                    i * 10,
                    comp.Read.Value,
                    $"Entity at index {i} should have value {i * 10} but had {comp.Read.Value}"
                );
            }
        }

        [Test]
        public void NativeAdd_AlreadySorted_NoReorder()
        {
            using var env = EcsTestHelper.CreateEnvironment(
                DeterministicSettings,
                TestTemplates.SimpleAlpha
            );
            var a = env.Accessor;
            var nativeEcs = a.ToNative();
            var group = a.WorldInfo.GetSingleGroupWithTags(TestTags.Alpha);

            var refs = a.ReserveEntityHandles(5, Allocator.Temp);
            for (int i = 0; i < 5; i++)
            {
                var init = nativeEcs.AddEntity(TestTags.Alpha, sortKey: (uint)i, refs[i]);
                init.Set(new TestInt { Value = i * 10 });
            }
            refs.Dispose();

            a.SubmitEntities();

            NAssert.AreEqual(5, a.CountEntitiesWithTags(TestTags.Alpha));

            for (int i = 0; i < 5; i++)
            {
                var comp = a.Component<TestInt>(new EntityIndex(i, group));
                NAssert.AreEqual(i * 10, comp.Read.Value);
            }
        }

        [Test]
        public void NativeAdd_MixedWithManagedAdds_NativeAddsSortedSeparately()
        {
            using var env = EcsTestHelper.CreateEnvironment(
                DeterministicSettings,
                TestTemplates.SimpleAlpha
            );
            var a = env.Accessor;
            var nativeEcs = a.ToNative();
            var group = a.WorldInfo.GetSingleGroupWithTags(TestTags.Alpha);

            // Add 2 entities via managed path first
            a.AddEntity(TestTags.Alpha).Set(new TestInt { Value = 100 }).AssertComplete();
            a.AddEntity(TestTags.Alpha).Set(new TestInt { Value = 200 }).AssertComplete();

            // Add 3 entities via native path with scrambled sort keys and reserved refs
            var refs = a.ReserveEntityHandles(3, Allocator.Temp);

            var init0 = nativeEcs.AddEntity(TestTags.Alpha, sortKey: 2, refs[0]);
            init0.Set(new TestInt { Value = 20 });

            var init1 = nativeEcs.AddEntity(TestTags.Alpha, sortKey: 0, refs[1]);
            init1.Set(new TestInt { Value = 0 });

            var init2 = nativeEcs.AddEntity(TestTags.Alpha, sortKey: 1, refs[2]);
            init2.Set(new TestInt { Value = 10 });

            refs.Dispose();
            a.SubmitEntities();

            NAssert.AreEqual(5, a.CountEntitiesWithTags(TestTags.Alpha));

            // Managed adds come first (indices 0,1), native adds follow sorted (indices 2,3,4)
            NAssert.AreEqual(100, a.Component<TestInt>(new EntityIndex(0, group)).Read.Value);
            NAssert.AreEqual(200, a.Component<TestInt>(new EntityIndex(1, group)).Read.Value);
            NAssert.AreEqual(0, a.Component<TestInt>(new EntityIndex(2, group)).Read.Value);
            NAssert.AreEqual(10, a.Component<TestInt>(new EntityIndex(3, group)).Read.Value);
            NAssert.AreEqual(20, a.Component<TestInt>(new EntityIndex(4, group)).Read.Value);
        }

        [Test]
        public void NativeAdd_ReservedRefs_EntityHandlesResolveCorrectly()
        {
            using var env = EcsTestHelper.CreateEnvironment(
                DeterministicSettings,
                TestTemplates.SimpleAlpha
            );
            var a = env.Accessor;
            var nativeEcs = a.ToNative();

            var refs = a.ReserveEntityHandles(3, Allocator.Temp);

            // sortKey 2 -> will end up at index 2
            var init0 = nativeEcs.AddEntity(TestTags.Alpha, sortKey: 2, refs[0]);
            init0.Set(new TestInt { Value = 20 });

            // sortKey 0 -> will end up at index 0
            var init1 = nativeEcs.AddEntity(TestTags.Alpha, sortKey: 0, refs[1]);
            init1.Set(new TestInt { Value = 0 });

            // sortKey 1 -> will end up at index 1
            var init2 = nativeEcs.AddEntity(TestTags.Alpha, sortKey: 1, refs[2]);
            init2.Set(new TestInt { Value = 10 });

            a.SubmitEntities();

            // Each reserved ref should resolve to the entity created with it
            NAssert.AreEqual(20, a.Component<TestInt>(refs[0]).Read.Value);
            NAssert.AreEqual(0, a.Component<TestInt>(refs[1]).Read.Value);
            NAssert.AreEqual(10, a.Component<TestInt>(refs[2]).Read.Value);

            refs.Dispose();
        }

        [Test]
        public void NativeAdd_ReservedRefs_DeterministicAcrossRuns()
        {
            EntityHandle[] RunOnce()
            {
                using var env = EcsTestHelper.CreateEnvironment(
                    DeterministicSettings,
                    TestTemplates.SimpleAlpha
                );
                var a = env.Accessor;
                var nativeEcs = a.ToNative();
                var group = a.WorldInfo.GetSingleGroupWithTags(TestTags.Alpha);

                var refs = a.ReserveEntityHandles(3, Allocator.Temp);

                var init0 = nativeEcs.AddEntity(TestTags.Alpha, sortKey: 2, refs[0]);
                init0.Set(new TestInt { Value = 20 });

                var init1 = nativeEcs.AddEntity(TestTags.Alpha, sortKey: 0, refs[1]);
                init1.Set(new TestInt { Value = 0 });

                var init2 = nativeEcs.AddEntity(TestTags.Alpha, sortKey: 1, refs[2]);
                init2.Set(new TestInt { Value = 10 });

                var result = new EntityHandle[] { refs[0], refs[1], refs[2] };
                refs.Dispose();
                a.SubmitEntities();
                return result;
            }

            var run1 = RunOnce();
            var run2 = RunOnce();

            for (int i = 0; i < 3; i++)
            {
                NAssert.AreEqual(
                    run1[i],
                    run2[i],
                    $"EntityHandle at index {i} differs between runs: {run1[i]} vs {run2[i]}"
                );
            }
        }
    }
}
