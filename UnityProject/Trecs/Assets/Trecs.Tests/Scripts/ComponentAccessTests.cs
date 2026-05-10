using NUnit.Framework;
using Unity.Collections;
using NAssert = NUnit.Framework.Assert;
using Trecs.Internal;

namespace Trecs.Tests
{
    [TestFixture]
    public class ComponentAccessTests
    {
        #region QueryEntity

        [Test]
        public void Component_Get_ReturnsSetValue()
        {
            using var env = EcsTestHelper.CreateEnvironment(TestTemplates.SimpleAlpha);
            var a = env.Accessor;

            a.AddEntity(TestTags.Alpha).Set(new TestInt { Value = 123 }).AssertComplete();
            a.SubmitEntities();

            var group = a.WorldInfo.GetSingleGroupWithTags(TestTags.Alpha);
            var comp = a.Component<TestInt>(new EntityIndex(0, group));

            NAssert.AreEqual(123, comp.Read.Value);
        }

        [Test]
        public void Component_GetByEntityHandle_ReturnsSetValue()
        {
            using var env = EcsTestHelper.CreateEnvironment(TestTemplates.SimpleAlpha);
            var a = env.Accessor;

            var init = a.AddEntity(TestTags.Alpha)
                .Set(new TestInt { Value = 456 })
                .AssertComplete();
            var entityHandle = init.Handle;
            a.SubmitEntities();

            var comp = a.Component<TestInt>(entityHandle);

            NAssert.AreEqual(456, comp.Read.Value);
        }

        [Test]
        public void Component_ModifyRef_Persists()
        {
            using var env = EcsTestHelper.CreateEnvironment(TestTemplates.SimpleAlpha);
            var a = env.Accessor;

            a.AddEntity(TestTags.Alpha).Set(new TestInt { Value = 10 }).AssertComplete();
            a.SubmitEntities();

            var group = a.WorldInfo.GetSingleGroupWithTags(TestTags.Alpha);
            var comp = a.Component<TestInt>(new EntityIndex(0, group));
            comp.Write.Value = 20;

            var comp2 = a.Component<TestInt>(new EntityIndex(0, group));
            NAssert.AreEqual(20, comp2.Read.Value);
        }

        #endregion

        #region TryGet

        [Test]
        public void Component_TryGet_Existing_ReturnsTrue()
        {
            using var env = EcsTestHelper.CreateEnvironment(TestTemplates.SimpleAlpha);
            var a = env.Accessor;

            a.AddEntity(TestTags.Alpha).Set(new TestInt { Value = 55 }).AssertComplete();
            a.SubmitEntities();

            var group = a.WorldInfo.GetSingleGroupWithTags(TestTags.Alpha);
            var found = a.TryComponent<TestInt>(new EntityIndex(0, group), out var comp);

            NAssert.IsTrue(found);
            NAssert.AreEqual(55, comp.Read.Value);
        }

        [Test]
        public void Component_TryGet_AfterRemove_ReturnsFalse()
        {
            using var env = EcsTestHelper.CreateEnvironment(TestTemplates.SimpleAlpha);
            var a = env.Accessor;

            a.AddEntity(TestTags.Alpha).AssertComplete();
            a.SubmitEntities();

            var group = a.WorldInfo.GetSingleGroupWithTags(TestTags.Alpha);
            a.RemoveEntity(new EntityIndex(0, group));
            a.SubmitEntities();

            var found = a.TryComponent<TestInt>(new EntityIndex(0, group), out _);

            NAssert.IsFalse(found);
        }

        #endregion

        #region Arrays

        [Test]
        public void Component_Arrays_SingleType_CorrectCount()
        {
            using var env = EcsTestHelper.CreateEnvironment(TestTemplates.SimpleAlpha);
            var a = env.Accessor;

            for (int i = 0; i < 3; i++)
            {
                a.AddEntity(TestTags.Alpha).Set(new TestInt { Value = i }).AssertComplete();
            }
            a.SubmitEntities();

            var group = a.WorldInfo.GetSingleGroupWithTags(TestTags.Alpha);
            var count = a.CountEntitiesInGroup(group);

            NAssert.AreEqual(3, count);
        }

        [Test]
        public void Component_Arrays_TwoTypes_CorrectCount()
        {
            using var env = EcsTestHelper.CreateEnvironment(TestTemplates.TwoCompBeta);
            var a = env.Accessor;

            for (int i = 0; i < 4; i++)
            {
                a.AddEntity(TestTags.Beta)
                    .Set(new TestInt { Value = i })
                    .Set(new TestFloat { Value = i * 0.5f })
                    .AssertComplete();
            }
            a.SubmitEntities();

            var group = a.WorldInfo.GetSingleGroupWithTags(TestTags.Beta);
            var count = a.CountEntitiesInGroup(group);

            NAssert.AreEqual(4, count);
        }

        [Test]
        public void Component_Arrays_ValuesMatch()
        {
            using var env = EcsTestHelper.CreateEnvironment(TestTemplates.SimpleAlpha);
            var a = env.Accessor;

            a.AddEntity(TestTags.Alpha).Set(new TestInt { Value = 100 }).AssertComplete();
            a.AddEntity(TestTags.Alpha).Set(new TestInt { Value = 200 }).AssertComplete();
            a.SubmitEntities();

            var group = a.WorldInfo.GetSingleGroupWithTags(TestTags.Alpha);
            var comp0 = a.Component<TestInt>(new EntityIndex(0, group));
            var comp1 = a.Component<TestInt>(new EntityIndex(1, group));

            NAssert.AreEqual(100, comp0.Read.Value);
            NAssert.AreEqual(200, comp1.Read.Value);
        }

        [Test]
        public void Component_Arrays_EmptyGroup_ZeroCount()
        {
            using var env = EcsTestHelper.CreateEnvironment(TestTemplates.SimpleAlpha);
            var a = env.Accessor;

            var group = a.WorldInfo.GetSingleGroupWithTags(TestTags.Alpha);
            var count = a.CountEntitiesInGroup(group);

            NAssert.AreEqual(0, count);
        }

        #endregion

        #region Single

        [Test]
        public void Component_GetSingle_OneEntity_Returns()
        {
            using var env = EcsTestHelper.CreateEnvironment(TestTemplates.SimpleAlpha);
            var a = env.Accessor;

            a.AddEntity(TestTags.Alpha).Set(new TestInt { Value = 999 }).AssertComplete();
            a.SubmitEntities();

            var comp = a.Query().WithTags(TestTags.Alpha).Single().Get<TestInt>();

            NAssert.AreEqual(999, comp.Read.Value);
        }

        [Test]
        public void Component_TryGetSingle_NoEntity_False()
        {
            using var env = EcsTestHelper.CreateEnvironment(TestTemplates.SimpleAlpha);
            var a = env.Accessor;

            var found = a.Query().WithTags(TestTags.Alpha).TrySingle(out _);

            NAssert.IsFalse(found);
        }

        #endregion

        #region Global

        [Test]
        public void Component_GetGlobal_ReturnsValue()
        {
            using var env = EcsTestHelper.CreateEnvironment(TestTemplates.SimpleAlpha);
            var a = env.Accessor;

            // Global entity exists after init - verify via WorldInfo
            NAssert.IsNotNull(a.WorldInfo.GlobalTemplate);
            var globalEntityIndex = a.WorldInfo.GlobalEntityIndex;
            NAssert.AreEqual(a.WorldInfo.GlobalGroup, globalEntityIndex.GroupIndex);
        }

        #endregion

        #region Buffer Alignment

        [Test]
        public void Component_TwoTypeBuffers_AreAligned()
        {
            using var env = EcsTestHelper.CreateEnvironment(TestTemplates.TwoCompBeta);
            var a = env.Accessor;

            for (int i = 0; i < 5; i++)
            {
                a.AddEntity(TestTags.Beta)
                    .Set(new TestInt { Value = i })
                    .Set(new TestFloat { Value = i * 0.5f })
                    .AssertComplete();
            }
            a.SubmitEntities();

            var group = a.WorldInfo.GetSingleGroupWithTags(TestTags.Beta);
            var buf1 = a.ComponentBuffer<TestInt>(group).Read;
            var buf2 = a.ComponentBuffer<TestFloat>(group).Read;
            var count = a.CountEntitiesInGroup(group);

            NAssert.AreEqual(5, count);

            for (int i = 0; i < count; i++)
            {
                NAssert.AreEqual(
                    buf1[i].Value * 0.5f,
                    buf2[i].Value,
                    0.001f,
                    $"Buffers misaligned at index {i}: int={buf1[i].Value}, float={buf2[i].Value}"
                );
            }
        }

        [Test]
        public void Component_BufferWrite_PersistsToEntityQuery()
        {
            using var env = EcsTestHelper.CreateEnvironment(TestTemplates.SimpleAlpha);
            var a = env.Accessor;

            for (int i = 0; i < 3; i++)
            {
                a.AddEntity(TestTags.Alpha).Set(new TestInt { Value = i }).AssertComplete();
            }
            a.SubmitEntities();

            var group = a.WorldInfo.GetSingleGroupWithTags(TestTags.Alpha);
            var buffer = a.ComponentBuffer<TestInt>(group).Write;

            // Modify through buffer
            buffer[1].Value = 999;

            // Verify through QueryEntity
            var comp = a.Component<TestInt>(new EntityIndex(1, group));
            NAssert.AreEqual(
                999,
                comp.Read.Value,
                "Buffer write should be visible through QueryEntity (same backing memory)"
            );
        }

        #endregion

        #region Three Component Query

        [Test]
        public void Component_ThreeTypeQuery_ReturnsAlignedBuffers()
        {
            using var env = EcsTestHelper.CreateEnvironment(TestTemplates.WithPartitions);
            var a = env.Accessor;

            var tags = TagSet.FromTags(TestTags.Gamma, TestTags.PartitionA);
            for (int i = 0; i < 4; i++)
            {
                a.AddEntity(tags)
                    .Set(new TestInt { Value = i })
                    .Set(new TestVec { X = i * 1.0f, Y = i * 2.0f })
                    .AssertComplete();
            }
            a.SubmitEntities();

            int totalEntities = 0;
            foreach (var slice in a.Query().WithTags(tags).GroupSlices())
            {
                var intBuf = a.ComponentBuffer<TestInt>(slice.GroupIndex).Read;
                var vecBuf = a.ComponentBuffer<TestVec>(slice.GroupIndex).Read;
                for (int i = 0; i < slice.Count; i++)
                {
                    NAssert.AreEqual(
                        intBuf[i].Value * 1.0f,
                        vecBuf[i].X,
                        0.001f,
                        $"Int/Vec.X misaligned at index {i}"
                    );
                    NAssert.AreEqual(
                        intBuf[i].Value * 2.0f,
                        vecBuf[i].Y,
                        0.001f,
                        $"Int/Vec.Y misaligned at index {i}"
                    );
                    totalEntities++;
                }
            }
            NAssert.AreEqual(4, totalEntities);
        }

        #endregion

        #region NativeComponentLookup

        [Test]
        public void NativeComponentLookup_Get_ReturnsCorrectComponent()
        {
            using var env = EcsTestHelper.CreateEnvironment(TestTemplates.SimpleAlpha);
            var a = env.Accessor;

            a.AddEntity(TestTags.Alpha).Set(new TestInt { Value = 77 }).AssertComplete();
            a.AddEntity(TestTags.Alpha).Set(new TestInt { Value = 88 }).AssertComplete();
            a.SubmitEntities();

            var group = a.WorldInfo.GetSingleGroupWithTags(TestTags.Alpha);
            using var lookup = a.CreateNativeComponentLookupRead<TestInt>(
                TestTags.Alpha,
                Allocator.TempJob
            );

            NAssert.IsTrue(lookup.IsCreated);
            NAssert.AreEqual(77, lookup[new EntityIndex(0, group)].Value);
            NAssert.AreEqual(88, lookup[new EntityIndex(1, group)].Value);
        }

        [Test]
        public void NativeComponentLookup_TryGet_ReturnsFalseForWrongGroup()
        {
            using var env = EcsTestHelper.CreateEnvironment(
                TestTemplates.SimpleAlpha,
                TestTemplates.TwoCompBeta
            );
            var a = env.Accessor;

            a.AddEntity(TestTags.Alpha).Set(new TestInt { Value = 10 }).AssertComplete();
            a.AddEntity(TestTags.Beta)
                .Set(new TestInt { Value = 20 })
                .Set(new TestFloat { Value = 1.0f })
                .AssertComplete();
            a.SubmitEntities();

            // Lookup scoped to Alpha only
            using var lookup = a.CreateNativeComponentLookupRead<TestInt>(
                TestTags.Alpha,
                Allocator.TempJob
            );

            var alphaGroup = a.WorldInfo.GetSingleGroupWithTags(TestTags.Alpha);
            var betaGroup = a.WorldInfo.GetSingleGroupWithTags(TestTags.Beta);

            NAssert.IsTrue(lookup.TryGet(new EntityIndex(0, alphaGroup), out var val));
            NAssert.AreEqual(10, val.Value);

            NAssert.IsFalse(
                lookup.TryGet(new EntityIndex(0, betaGroup), out _),
                "TryGet should return false for a group not in the lookup"
            );
        }

        [Test]
        public void NativeComponentLookup_AcrossMultipleGroups()
        {
            using var env = EcsTestHelper.CreateEnvironment(TestTemplates.WithPartitions);
            var a = env.Accessor;

            var partitionA = TagSet.FromTags(TestTags.Gamma, TestTags.PartitionA);
            var partitionB = TagSet.FromTags(TestTags.Gamma, TestTags.PartitionB);

            a.AddEntity(partitionA).Set(new TestInt { Value = 100 }).AssertComplete();
            a.AddEntity(partitionB).Set(new TestInt { Value = 200 }).AssertComplete();
            a.SubmitEntities();

            // Lookup scoped to all Gamma-tagged groups
            using var lookup = a.CreateNativeComponentLookupRead<TestInt>(
                TagSet.FromTags(TestTags.Gamma),
                Allocator.TempJob
            );

            var groupA = a.WorldInfo.GetSingleGroupWithTags(partitionA);
            var groupB = a.WorldInfo.GetSingleGroupWithTags(partitionB);

            NAssert.AreEqual(100, lookup[new EntityIndex(0, groupA)].Value);
            NAssert.AreEqual(200, lookup[new EntityIndex(0, groupB)].Value);
        }

        #endregion

        #region QueryEntities

        [Test]
        public void QueryEntities_SingleGroup_ReturnsEntities()
        {
            using var env = EcsTestHelper.CreateEnvironment(TestTemplates.SimpleAlpha);
            var a = env.Accessor;

            a.AddEntity(TestTags.Alpha).Set(new TestInt { Value = 10 }).AssertComplete();
            a.AddEntity(TestTags.Alpha).Set(new TestInt { Value = 20 }).AssertComplete();
            a.SubmitEntities();

            int groupCount = 0;
            int totalEntities = 0;

            foreach (var slice in a.Query().WithTags(TestTags.Alpha).GroupSlices())
            {
                groupCount++;
                totalEntities += (int)slice.Count;
            }

            NAssert.AreEqual(1, groupCount);
            NAssert.AreEqual(2, totalEntities);
        }

        [Test]
        public void QueryEntities_MultipleGroups_IteratesAll()
        {
            using var env = EcsTestHelper.CreateEnvironment(TestTemplates.WithPartitions);
            var a = env.Accessor;

            var partitionAGroups = a.WorldInfo.GetGroupsWithTags(
                TagSet.FromTags(TestTags.Gamma, TestTags.PartitionA)
            );
            var partitionBGroups = a.WorldInfo.GetGroupsWithTags(
                TagSet.FromTags(TestTags.Gamma, TestTags.PartitionB)
            );

            a.AddEntity(TagSet.FromTags(TestTags.Gamma, TestTags.PartitionA))
                .Set(new TestInt { Value = 1 })
                .AssertComplete();
            a.AddEntity(TagSet.FromTags(TestTags.Gamma, TestTags.PartitionB))
                .Set(new TestInt { Value = 2 })
                .AssertComplete();
            a.SubmitEntities();

            int groupCount = 0;
            int totalEntities = 0;

            foreach (var slice in a.Query().WithTags(TagSet.FromTags(TestTags.Gamma)).GroupSlices())
            {
                groupCount++;
                totalEntities += (int)slice.Count;
            }

            NAssert.AreEqual(2, groupCount);
            NAssert.AreEqual(2, totalEntities);
        }

        [Test]
        public void QueryEntities_SkipsEmptyGroups()
        {
            using var env = EcsTestHelper.CreateEnvironment(TestTemplates.WithPartitions);
            var a = env.Accessor;

            a.AddEntity(TagSet.FromTags(TestTags.Gamma, TestTags.PartitionA))
                .Set(new TestInt { Value = 1 })
                .AssertComplete();
            a.SubmitEntities();

            int groupCount = 0;

            foreach (var slice in a.Query().WithTags(TagSet.FromTags(TestTags.Gamma)).GroupSlices())
            {
                groupCount++;
            }

            NAssert.AreEqual(1, groupCount);
        }

        [Test]
        public void QueryEntities_TwoComponent_ReturnsEntities()
        {
            using var env = EcsTestHelper.CreateEnvironment(TestTemplates.TwoCompBeta);
            var a = env.Accessor;

            a.AddEntity(TestTags.Beta)
                .Set(new TestInt { Value = 5 })
                .Set(new TestFloat { Value = 1.5f })
                .AssertComplete();
            a.SubmitEntities();

            int groupCount = 0;

            foreach (var slice in a.Query().WithTags(TestTags.Beta).GroupSlices())
            {
                var buf1 = a.ComponentBuffer<TestInt>(slice.GroupIndex).Read;
                var buf2 = a.ComponentBuffer<TestFloat>(slice.GroupIndex).Read;
                groupCount++;
                NAssert.AreEqual(1, (int)slice.Count);
                NAssert.AreEqual(5, buf1[0].Value);
                NAssert.AreEqual(1.5f, buf2[0].Value, 0.001f);
            }

            NAssert.AreEqual(1, groupCount);
        }

        #endregion
    }
}
