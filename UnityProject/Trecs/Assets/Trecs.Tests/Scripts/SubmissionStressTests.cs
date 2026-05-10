using System.Diagnostics;
using NUnit.Framework;
using Unity.Collections;
using Debug = UnityEngine.Debug;
using NAssert = NUnit.Framework.Assert;
using Trecs.Internal;

namespace Trecs.Tests
{
    /// <summary>
    /// Stress tests for the entity submission pipeline.
    /// Exercises removes, moves, and adds at various scales to validate
    /// correctness and measure performance scaling.
    /// </summary>
    [TestFixture]
    public class SubmissionStressTests
    {
        #region Large Remove

        [TestCase(100)]
        [TestCase(500)]
        [TestCase(1000)]
        [TestCase(5000)]
        public void Stress_RemoveN_FromGroup(int count)
        {
            using var env = EcsTestHelper.CreateEnvironment(TestTemplates.WithPartitions);
            var a = env.Accessor;
            var tags = TagSet.FromTags(TestTags.Gamma, TestTags.PartitionA);

            // Add entities
            var entityIds = new EntityHandle[count];
            for (int i = 0; i < count; i++)
            {
                var init = a.AddEntity(tags)
                    .Set(new TestInt { Value = i })
                    .Set(new TestVec { X = i * 0.1f, Y = i * 0.2f })
                    .AssertComplete();
                entityIds[i] = init.Handle;
            }
            a.SubmitEntities();
            NAssert.AreEqual(count, a.CountEntitiesWithTags(tags));

            // Remove half (scattered indices to stress swap-back)
            int removeCount = count / 2;
            for (int i = 0; i < removeCount; i++)
            {
                // Remove every other entity to create scattered removals
                a.RemoveEntity(entityIds[i * 2]);
            }

            var sw = Stopwatch.StartNew();
            a.SubmitEntities();
            sw.Stop();

            NAssert.AreEqual(count - removeCount, a.CountEntitiesWithTags(tags));

            // Verify remaining entities are intact
            for (int i = 0; i < removeCount; i++)
            {
                var oddRef = entityIds[i * 2 + 1];
                NAssert.IsTrue(a.EntityExists(oddRef));
                NAssert.AreEqual(i * 2 + 1, a.Component<TestInt>(oddRef).Read.Value);
            }

            Debug.Log(
                $"[StressTest] Remove {removeCount}/{count}: {sw.Elapsed.TotalMilliseconds:F3} ms"
            );
        }

        #endregion

        #region Large Move

        [TestCase(100)]
        [TestCase(500)]
        [TestCase(1000)]
        [TestCase(5000)]
        public void Stress_MoveN_BetweenGroups(int count)
        {
            using var env = EcsTestHelper.CreateEnvironment(TestTemplates.WithPartitions);
            var a = env.Accessor;
            var partitionA = TagSet.FromTags(TestTags.Gamma, TestTags.PartitionA);
            var partitionB = TagSet.FromTags(TestTags.Gamma, TestTags.PartitionB);

            // Add entities to partition A
            var entityIds = new EntityHandle[count];
            for (int i = 0; i < count; i++)
            {
                var init = a.AddEntity(partitionA)
                    .Set(new TestInt { Value = i })
                    .Set(new TestVec { X = i * 0.1f, Y = i * 0.2f })
                    .AssertComplete();
                entityIds[i] = init.Handle;
            }
            a.SubmitEntities();
            NAssert.AreEqual(count, a.CountEntitiesWithTags(partitionA));

            // Move half to partition B
            int moveCount = count / 2;
            for (int i = 0; i < moveCount; i++)
            {
                a.MoveTo(entityIds[i * 2].ToIndex(a), partitionB);
            }

            var sw = Stopwatch.StartNew();
            a.SubmitEntities();
            sw.Stop();

            NAssert.AreEqual(count - moveCount, a.CountEntitiesWithTags(partitionA));
            NAssert.AreEqual(moveCount, a.CountEntitiesWithTags(partitionB));

            // Verify moved entities are intact
            for (int i = 0; i < moveCount; i++)
            {
                var movedRef = entityIds[i * 2];
                NAssert.IsTrue(a.EntityExists(movedRef));
                NAssert.AreEqual(i * 2, a.Component<TestInt>(movedRef).Read.Value);
            }

            // Verify remaining entities are intact
            for (int i = 0; i < moveCount; i++)
            {
                var stayedRef = entityIds[i * 2 + 1];
                NAssert.IsTrue(a.EntityExists(stayedRef));
                NAssert.AreEqual(i * 2 + 1, a.Component<TestInt>(stayedRef).Read.Value);
            }

            Debug.Log(
                $"[StressTest] Move {moveCount}/{count}: {sw.Elapsed.TotalMilliseconds:F3} ms"
            );
        }

        #endregion

        #region Large Add

        [TestCase(100)]
        [TestCase(500)]
        [TestCase(1000)]
        [TestCase(5000)]
        public void Stress_AddN_ToGroup(int count)
        {
            using var env = EcsTestHelper.CreateEnvironment(TestTemplates.WithPartitions);
            var a = env.Accessor;
            var tags = TagSet.FromTags(TestTags.Gamma, TestTags.PartitionA);

            // Schedule adds
            for (int i = 0; i < count; i++)
            {
                a.AddEntity(tags)
                    .Set(new TestInt { Value = i })
                    .Set(new TestVec { X = i * 0.1f, Y = i * 0.2f })
                    .AssertComplete();
            }

            var sw = Stopwatch.StartNew();
            a.SubmitEntities();
            sw.Stop();

            NAssert.AreEqual(count, a.CountEntitiesWithTags(tags));

            Debug.Log($"[StressTest] Add {count}: {sw.Elapsed.TotalMilliseconds:F3} ms");
        }

        #endregion

        #region Mixed Operations

        [TestCase(100)]
        [TestCase(500)]
        [TestCase(1000)]
        [TestCase(5000)]
        public void Stress_MixedOps_N(int count)
        {
            using var env = EcsTestHelper.CreateEnvironment(TestTemplates.WithPartitions);
            var a = env.Accessor;
            var partitionA = TagSet.FromTags(TestTags.Gamma, TestTags.PartitionA);
            var partitionB = TagSet.FromTags(TestTags.Gamma, TestTags.PartitionB);

            // Add entities to partition A
            var entityIds = new EntityHandle[count];
            for (int i = 0; i < count; i++)
            {
                var init = a.AddEntity(partitionA)
                    .Set(new TestInt { Value = i })
                    .Set(new TestVec { X = i * 0.1f, Y = i * 0.2f })
                    .AssertComplete();
                entityIds[i] = init.Handle;
            }
            a.SubmitEntities();

            // Schedule mixed: remove 25%, move 25%, leave 50%
            int removeCount = count / 4;
            int moveCount = count / 4;
            for (int i = 0; i < removeCount; i++)
            {
                a.RemoveEntity(entityIds[i]);
            }
            for (int i = 0; i < moveCount; i++)
            {
                a.MoveTo(entityIds[removeCount + i].ToIndex(a), partitionB);
            }
            // Also add new entities
            int addCount = count / 4;
            for (int i = 0; i < addCount; i++)
            {
                a.AddEntity(partitionA)
                    .Set(new TestInt { Value = count + i })
                    .Set(new TestVec { X = 0, Y = 0 })
                    .AssertComplete();
            }

            var sw = Stopwatch.StartNew();
            a.SubmitEntities();
            sw.Stop();

            int expectedA = count - removeCount - moveCount + addCount;
            NAssert.AreEqual(expectedA, a.CountEntitiesWithTags(partitionA));
            NAssert.AreEqual(moveCount, a.CountEntitiesWithTags(partitionB));

            Debug.Log(
                $"[StressTest] Mixed (remove={removeCount}, move={moveCount}, add={addCount}) from {count}: {sw.Elapsed.TotalMilliseconds:F3} ms"
            );
        }

        #endregion

        #region Remove Chain Stress

        [TestCase(1000)]
        [TestCase(5000)]
        public void Stress_RemoveAll_N(int count)
        {
            using var env = EcsTestHelper.CreateEnvironment(TestTemplates.WithPartitions);
            var a = env.Accessor;
            var tags = TagSet.FromTags(TestTags.Gamma, TestTags.PartitionA);

            var entityIds = new EntityHandle[count];
            for (int i = 0; i < count; i++)
            {
                var init = a.AddEntity(tags)
                    .Set(new TestInt { Value = i })
                    .Set(new TestVec { X = i * 0.1f, Y = i * 0.2f })
                    .AssertComplete();
                entityIds[i] = init.Handle;
            }
            a.SubmitEntities();

            // Remove ALL entities (worst case for swap-back chains without descending sort)
            for (int i = 0; i < count; i++)
            {
                a.RemoveEntity(entityIds[i]);
            }

            var sw = Stopwatch.StartNew();
            a.SubmitEntities();
            sw.Stop();

            NAssert.AreEqual(0, a.CountEntitiesWithTags(tags));

            Debug.Log($"[StressTest] RemoveAll {count}: {sw.Elapsed.TotalMilliseconds:F3} ms");
        }

        #endregion

        #region Native Add Sort Overhead

        [TestCase(100)]
        [TestCase(500)]
        [TestCase(1000)]
        [TestCase(5000)]
        public void Stress_NativeAddN_WithSort(int count)
        {
            var settings = new WorldSettings { RequireDeterministicSubmission = true };
            using var env = EcsTestHelper.CreateEnvironment(settings, TestTemplates.SimpleAlpha);
            var a = env.Accessor;
            var nativeEcs = a.ToNative();
            var group = a.WorldInfo.GetSingleGroupWithTags(TestTags.Alpha);
            var refs = a.ReserveEntityHandles(count, Allocator.Temp);

            // Schedule native adds with reverse sort keys (worst case for sorting)
            for (int i = 0; i < count; i++)
            {
                var init = nativeEcs.AddEntity(
                    TestTags.Alpha,
                    sortKey: (uint)(count - 1 - i),
                    refs[i]
                );
                init.Set(new TestInt { Value = count - 1 - i });
            }
            refs.Dispose();

            var sw = Stopwatch.StartNew();
            a.SubmitEntities();
            sw.Stop();

            NAssert.AreEqual(count, a.CountEntitiesWithTags(TestTags.Alpha));

            // Verify sorted order
            for (int i = 0; i < count; i++)
            {
                var comp = a.Component<TestInt>(new EntityIndex(i, group));
                NAssert.AreEqual(
                    i,
                    comp.Read.Value,
                    $"Entity at index {i} should have value {i} but had {comp.Read.Value}"
                );
            }

            Debug.Log(
                $"[StressTest] NativeAdd (sorted) {count}: {sw.Elapsed.TotalMilliseconds:F3} ms"
            );
        }

        [TestCase(100)]
        [TestCase(500)]
        [TestCase(1000)]
        [TestCase(5000)]
        public void Stress_NativeAddN_NoSort(int count)
        {
            // Same as WithSort but with RequireDeterministicSubmission=false to measure sort overhead
            var settings = new WorldSettings { RequireDeterministicSubmission = false };
            using var env = EcsTestHelper.CreateEnvironment(settings, TestTemplates.SimpleAlpha);
            var a = env.Accessor;
            var nativeEcs = a.ToNative();
            var group = a.WorldInfo.GetSingleGroupWithTags(TestTags.Alpha);

            for (int i = 0; i < count; i++)
            {
                var init = nativeEcs.AddEntity(TestTags.Alpha, sortKey: (uint)(count - 1 - i));
                init.Set(new TestInt { Value = count - 1 - i });
            }

            var sw = Stopwatch.StartNew();
            a.SubmitEntities();
            sw.Stop();

            NAssert.AreEqual(count, a.CountEntitiesWithTags(TestTags.Alpha));

            Debug.Log(
                $"[StressTest] NativeAdd (no sort) {count}: {sw.Elapsed.TotalMilliseconds:F3} ms"
            );
        }

        [TestCase(100)]
        [TestCase(500)]
        [TestCase(1000)]
        [TestCase(5000)]
        public void Stress_NativeAddN_MultipleBags_WithSort(int count)
        {
            var settings = new WorldSettings { RequireDeterministicSubmission = true };
            using var env = EcsTestHelper.CreateEnvironment(settings, TestTemplates.SimpleAlpha);
            var a = env.Accessor;
            var nativeEcs = a.ToNative();
            var group = a.WorldInfo.GetSingleGroupWithTags(TestTags.Alpha);
            var refs = a.ReserveEntityHandles(count, Allocator.Temp);

            // Add with interleaved sort keys
            for (int i = 0; i < count; i++)
            {
                var init = nativeEcs.AddEntity(TestTags.Alpha, sortKey: (uint)i, refs[i]);
                init.Set(new TestInt { Value = i });
            }
            refs.Dispose();

            var sw = Stopwatch.StartNew();
            a.SubmitEntities();
            sw.Stop();

            NAssert.AreEqual(count, a.CountEntitiesWithTags(TestTags.Alpha));

            // Verify sorted order
            for (int i = 0; i < count; i++)
            {
                var comp = a.Component<TestInt>(new EntityIndex(i, group));
                NAssert.AreEqual(
                    i,
                    comp.Read.Value,
                    $"Entity at index {i} should have value {i} but had {comp.Read.Value}"
                );
            }

            Debug.Log(
                $"[StressTest] NativeAdd (multi-bag sorted) {count}: {sw.Elapsed.TotalMilliseconds:F3} ms"
            );
        }

        #endregion
    }
}
