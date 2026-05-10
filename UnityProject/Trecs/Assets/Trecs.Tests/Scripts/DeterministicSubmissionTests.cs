using NUnit.Framework;
using Trecs.Internal;
using NAssert = NUnit.Framework.Assert;

namespace Trecs.Tests
{
    /// <summary>
    /// Tests for deterministic submission mode: verifies that native removes and moves
    /// produce identical results regardless of queue order.
    /// </summary>
    [TestFixture]
    public class DeterministicSubmissionTests
    {
        static readonly TagSet PartitionA = TagSet.FromTags(TestTags.Gamma, TestTags.PartitionA);
        static readonly TagSet PartitionB = TagSet.FromTags(TestTags.Gamma, TestTags.PartitionB);

        TestEnvironment CreateEnv()
        {
            var settings = new WorldSettings { RequireDeterministicSubmission = true };
            return EcsTestHelper.CreateEnvironment(settings, TestTemplates.WithPartitions);
        }

        #region Deterministic native removes

        [Test]
        public void DeterministicRemove_DifferentQueueOrder_SameResult()
        {
            // Two runs with native removes queued in different orders
            // should produce identical surviving entity data.
            int[] survivorValuesForward;
            int[] survivorValuesReverse;

            // Run 1: remove in forward order (0, 2, 4)
            {
                using var env = CreateEnv();
                var a = env.Accessor;
                var nativeEcs = a.ToNative();

                var handles = new EntityHandle[6];
                for (int i = 0; i < 6; i++)
                {
                    handles[i] = a.AddEntity(PartitionA)
                        .Set(new TestInt { Value = i })
                        .Set(new TestVec())
                        .AssertComplete()
                        .Handle;
                }
                a.SubmitEntities();

                nativeEcs.RemoveEntity(handles[0].ToIndex(a));
                nativeEcs.RemoveEntity(handles[2].ToIndex(a));
                nativeEcs.RemoveEntity(handles[4].ToIndex(a));
                a.SubmitEntities();

                survivorValuesForward = CollectValues(a, PartitionA);
            }

            // Run 2: remove in reverse order (4, 2, 0)
            {
                using var env = CreateEnv();
                var a = env.Accessor;
                var nativeEcs = a.ToNative();

                var handles = new EntityHandle[6];
                for (int i = 0; i < 6; i++)
                {
                    handles[i] = a.AddEntity(PartitionA)
                        .Set(new TestInt { Value = i })
                        .Set(new TestVec())
                        .AssertComplete()
                        .Handle;
                }
                a.SubmitEntities();

                nativeEcs.RemoveEntity(handles[4].ToIndex(a));
                nativeEcs.RemoveEntity(handles[2].ToIndex(a));
                nativeEcs.RemoveEntity(handles[0].ToIndex(a));
                a.SubmitEntities();

                survivorValuesReverse = CollectValues(a, PartitionA);
            }

            NAssert.AreEqual(survivorValuesForward.Length, survivorValuesReverse.Length);
            for (int i = 0; i < survivorValuesForward.Length; i++)
            {
                NAssert.AreEqual(
                    survivorValuesForward[i],
                    survivorValuesReverse[i],
                    $"Survivor at index {i} should match regardless of removal order"
                );
            }
        }

        [Test]
        public void DeterministicRemove_ScatteredOrder_SameAsSorted()
        {
            int[] valuesScattered;
            int[] valuesSorted;

            // Run 1: scattered removal order
            {
                using var env = CreateEnv();
                var a = env.Accessor;
                var nativeEcs = a.ToNative();

                var handles = new EntityHandle[10];
                for (int i = 0; i < 10; i++)
                {
                    handles[i] = a.AddEntity(PartitionA)
                        .Set(new TestInt { Value = i })
                        .Set(new TestVec())
                        .AssertComplete()
                        .Handle;
                }
                a.SubmitEntities();

                // Remove 7, 1, 5, 3 (scattered)
                nativeEcs.RemoveEntity(handles[7].ToIndex(a));
                nativeEcs.RemoveEntity(handles[1].ToIndex(a));
                nativeEcs.RemoveEntity(handles[5].ToIndex(a));
                nativeEcs.RemoveEntity(handles[3].ToIndex(a));
                a.SubmitEntities();

                valuesScattered = CollectValues(a, PartitionA);
            }

            // Run 2: sorted removal order
            {
                using var env = CreateEnv();
                var a = env.Accessor;
                var nativeEcs = a.ToNative();

                var handles = new EntityHandle[10];
                for (int i = 0; i < 10; i++)
                {
                    handles[i] = a.AddEntity(PartitionA)
                        .Set(new TestInt { Value = i })
                        .Set(new TestVec())
                        .AssertComplete()
                        .Handle;
                }
                a.SubmitEntities();

                // Remove 1, 3, 5, 7 (sorted)
                nativeEcs.RemoveEntity(handles[1].ToIndex(a));
                nativeEcs.RemoveEntity(handles[3].ToIndex(a));
                nativeEcs.RemoveEntity(handles[5].ToIndex(a));
                nativeEcs.RemoveEntity(handles[7].ToIndex(a));
                a.SubmitEntities();

                valuesSorted = CollectValues(a, PartitionA);
            }

            NAssert.AreEqual(valuesScattered.Length, valuesSorted.Length);
            for (int i = 0; i < valuesScattered.Length; i++)
            {
                NAssert.AreEqual(
                    valuesScattered[i],
                    valuesSorted[i],
                    $"Index {i} should match regardless of remove queue order"
                );
            }
        }

        #endregion

        #region Deterministic native moves

        [Test]
        public void DeterministicMove_DifferentQueueOrder_SameResult()
        {
            int[] stateAForward,
                stateBForward;
            int[] stateAReverse,
                stateBReverse;

            // Run 1: move in forward order
            {
                using var env = CreateEnv();
                var a = env.Accessor;
                var nativeEcs = a.ToNative();

                var handles = new EntityHandle[6];
                for (int i = 0; i < 6; i++)
                {
                    handles[i] = a.AddEntity(PartitionA)
                        .Set(new TestInt { Value = i })
                        .Set(new TestVec())
                        .AssertComplete()
                        .Handle;
                }
                a.SubmitEntities();

                nativeEcs.MoveTo(handles[0].ToIndex(a), PartitionB);
                nativeEcs.MoveTo(handles[2].ToIndex(a), PartitionB);
                nativeEcs.MoveTo(handles[4].ToIndex(a), PartitionB);
                a.SubmitEntities();

                stateAForward = CollectValues(a, PartitionA);
                stateBForward = CollectValues(a, PartitionB);
            }

            // Run 2: move in reverse order
            {
                using var env = CreateEnv();
                var a = env.Accessor;
                var nativeEcs = a.ToNative();

                var handles = new EntityHandle[6];
                for (int i = 0; i < 6; i++)
                {
                    handles[i] = a.AddEntity(PartitionA)
                        .Set(new TestInt { Value = i })
                        .Set(new TestVec())
                        .AssertComplete()
                        .Handle;
                }
                a.SubmitEntities();

                nativeEcs.MoveTo(handles[4].ToIndex(a), PartitionB);
                nativeEcs.MoveTo(handles[2].ToIndex(a), PartitionB);
                nativeEcs.MoveTo(handles[0].ToIndex(a), PartitionB);
                a.SubmitEntities();

                stateAReverse = CollectValues(a, PartitionA);
                stateBReverse = CollectValues(a, PartitionB);
            }

            NAssert.AreEqual(stateAForward.Length, stateAReverse.Length);
            for (int i = 0; i < stateAForward.Length; i++)
                NAssert.AreEqual(
                    stateAForward[i],
                    stateAReverse[i],
                    $"PartitionA index {i} should match"
                );

            NAssert.AreEqual(stateBForward.Length, stateBReverse.Length);
            for (int i = 0; i < stateBForward.Length; i++)
                NAssert.AreEqual(
                    stateBForward[i],
                    stateBReverse[i],
                    $"PartitionB index {i} should match"
                );
        }

        #endregion

        #region Deterministic mixed operations

        [Test]
        public void DeterministicMixed_RemoveAndMove_DifferentOrder_SameResult()
        {
            int[] stateA1,
                stateB1;
            int[] stateA2,
                stateB2;

            // Run 1: removes first then moves
            {
                using var env = CreateEnv();
                var a = env.Accessor;
                var nativeEcs = a.ToNative();

                var handles = new EntityHandle[8];
                for (int i = 0; i < 8; i++)
                {
                    handles[i] = a.AddEntity(PartitionA)
                        .Set(new TestInt { Value = i })
                        .Set(new TestVec())
                        .AssertComplete()
                        .Handle;
                }
                a.SubmitEntities();

                nativeEcs.RemoveEntity(handles[1].ToIndex(a));
                nativeEcs.RemoveEntity(handles[5].ToIndex(a));
                nativeEcs.MoveTo(handles[0].ToIndex(a), PartitionB);
                nativeEcs.MoveTo(handles[3].ToIndex(a), PartitionB);
                a.SubmitEntities();

                stateA1 = CollectValues(a, PartitionA);
                stateB1 = CollectValues(a, PartitionB);
            }

            // Run 2: interleaved order
            {
                using var env = CreateEnv();
                var a = env.Accessor;
                var nativeEcs = a.ToNative();

                var handles = new EntityHandle[8];
                for (int i = 0; i < 8; i++)
                {
                    handles[i] = a.AddEntity(PartitionA)
                        .Set(new TestInt { Value = i })
                        .Set(new TestVec())
                        .AssertComplete()
                        .Handle;
                }
                a.SubmitEntities();

                nativeEcs.MoveTo(handles[3].ToIndex(a), PartitionB);
                nativeEcs.RemoveEntity(handles[5].ToIndex(a));
                nativeEcs.MoveTo(handles[0].ToIndex(a), PartitionB);
                nativeEcs.RemoveEntity(handles[1].ToIndex(a));
                a.SubmitEntities();

                stateA2 = CollectValues(a, PartitionA);
                stateB2 = CollectValues(a, PartitionB);
            }

            NAssert.AreEqual(stateA1.Length, stateA2.Length);
            for (int i = 0; i < stateA1.Length; i++)
                NAssert.AreEqual(stateA1[i], stateA2[i], $"PartitionA index {i}");

            NAssert.AreEqual(stateB1.Length, stateB2.Length);
            for (int i = 0; i < stateB1.Length; i++)
                NAssert.AreEqual(stateB1[i], stateB2[i], $"PartitionB index {i}");
        }

        #endregion

        static int[] CollectValues(WorldAccessor a, TagSet tags)
        {
            var group = a.WorldInfo.GetSingleGroupWithTags(tags);
            int count = a.CountEntitiesWithTags(tags);
            var values = new int[count];
            for (int i = 0; i < count; i++)
                values[i] = a.Component<TestInt>(new EntityIndex(i, group)).Read.Value;
            return values;
        }
    }
}
