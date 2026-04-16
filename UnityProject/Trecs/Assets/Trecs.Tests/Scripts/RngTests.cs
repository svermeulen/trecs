using System.Collections.Generic;
using System.Linq;
using NUnit.Framework;
using Trecs.Collections;
using UnityEngine;
using NAssert = NUnit.Framework.Assert;

namespace Trecs.Tests
{
    [TestFixture]
    public class RngTests
    {
        #region Basic Operations

        [Test]
        public void TestNextInt()
        {
            var rng = new Rng();

            var found = new HashSet<int>();
            var countdown = 10;

            // NextInt should be exclusive the given max and inclusive the given min
            while (found.Count < 3 || countdown > 0)
            {
                found.Add(rng.NextInt(0, 3));
                countdown -= 1;
            }

            NAssert.AreEqual(3, found.Count);
            NAssert.IsTrue(found.Contains(0));
            NAssert.IsTrue(found.Contains(1));
            NAssert.IsTrue(found.Contains(2));
        }

        [Test]
        public void TestNext()
        {
            var rng = new Rng();

            for (int i = 0; i < 10; i++)
            {
                var value = rng.Next();
                NAssert.IsTrue(value >= 0f);
                NAssert.IsTrue(value < 1f);
            }
        }

        #endregion

        #region Determinism

        [Test]
        public void TestDeterministicBehavior()
        {
            const ulong seed = 12345;
            var rng1 = new Rng(seed);
            var rng2 = new Rng(seed);

            for (int i = 0; i < 100; i++)
            {
                NAssert.IsTrue(
                    rng1.Next() == rng2.Next(),
                    "Next() should be deterministic with same seed"
                );
                NAssert.IsTrue(
                    rng1.NextUint() == rng2.NextUint(),
                    "NextUint() should be deterministic with same seed"
                );
                NAssert.IsTrue(
                    rng1.NextBool() == rng2.NextBool(),
                    "NextBool() should be deterministic with same seed"
                );
                NAssert.IsTrue(
                    rng1.NextInt() == rng2.NextInt(),
                    "NextInt() should be deterministic with same seed"
                );
            }
        }

        [Test]
        public void TestDifferentSeedsProduceDifferentSequences()
        {
            var rng1 = new Rng(1);
            var rng2 = new Rng(2);

            bool foundDifference = false;
            for (int i = 0; i < 100; i++)
            {
                if (rng1.NextUint() != rng2.NextUint())
                {
                    foundDifference = true;
                    break;
                }
            }

            NAssert.IsTrue(foundDifference, "Different seeds should produce different sequences");
        }

        #endregion

        #region Ranges

        [Test]
        public void TestNextFloatRanges()
        {
            var rng = new Rng(42);

            for (int i = 0; i < 1000; i++)
            {
                var value = rng.Next();
                NAssert.IsTrue(
                    value >= 0f && value < 1f,
                    $"Next() value {value} should be in range [0, 1)"
                );
            }

            for (int i = 0; i < 100; i++)
            {
                float min = -10f + i * 0.1f;
                float max = min + 5f + i * 0.05f;
                var value = rng.NextFloat(min, max);
                NAssert.IsTrue(
                    value >= min && value <= max,
                    $"NextFloat({min}, {max}) value {value} should be in range [{min}, {max}]"
                );
            }
        }

        [Test]
        public void TestNextIntRanges()
        {
            var rng = new Rng(456);

            for (int i = 0; i < 100; i++)
            {
                int min = -100 + i;
                int max = min + 50;
                var value = rng.NextInt(min, max);
                NAssert.IsTrue(
                    value >= min && value < max,
                    $"NextInt({min}, {max}) value {value} should be in range [{min}, {max})"
                );
            }
        }

        [Test]
        public void TestNextUintRanges()
        {
            var rng = new Rng(101112);

            for (int i = 0; i < 100; i++)
            {
                uint max = (uint)(100 + i * 10);
                var value = rng.NextUint(max);
                NAssert.IsTrue(
                    value < max,
                    $"NextUint({max}) value {value} should be less than {max}"
                );
            }

            for (int i = 0; i < 100; i++)
            {
                var value = rng.NextUint();
                NAssert.IsTrue(value <= uint.MaxValue, "NextUint() should be within uint range");
            }
        }

        [Test]
        public void TestNextLongRange()
        {
            var rng = new Rng(131415);

            for (int i = 0; i < 100; i++)
            {
                var value = rng.NextLong();
                NAssert.IsTrue(
                    value >= long.MinValue && value <= long.MaxValue,
                    "NextLong should be within long range"
                );
            }
        }

        #endregion

        #region Distribution

        [Test]
        public void TestNextBoolDistribution()
        {
            var rng = new Rng(161718);
            int trueCount = 0;
            int falseCount = 0;
            const int iterations = 10000;

            for (int i = 0; i < iterations; i++)
            {
                if (rng.NextBool())
                    trueCount++;
                else
                    falseCount++;
            }

            float trueRatio = (float)trueCount / iterations;
            NAssert.IsTrue(
                trueRatio > 0.45f && trueRatio < 0.55f,
                $"NextBool distribution should be roughly 50/50, got {trueRatio:F3} true ratio"
            );
        }

        [Test]
        public void TestUniformDistributionFloat()
        {
            var rng = new Rng(192021);
            const int bucketCount = 10;
            const int iterations = 10000;
            var buckets = new int[bucketCount];

            for (int i = 0; i < iterations; i++)
            {
                var value = rng.Next();
                int bucket = Mathf.FloorToInt(value * bucketCount);
                if (bucket >= bucketCount)
                    bucket = bucketCount - 1;
                buckets[bucket]++;
            }

            float expectedPerBucket = (float)iterations / bucketCount;
            for (int i = 0; i < bucketCount; i++)
            {
                float ratio = buckets[i] / expectedPerBucket;
                NAssert.IsTrue(
                    ratio > 0.8f && ratio < 1.2f,
                    $"Bucket {i} has {buckets[i]} items, expected ~{expectedPerBucket:F0} (ratio: {ratio:F2})"
                );
            }
        }

        [Test]
        public void TestUniformDistributionInt()
        {
            var rng = new Rng(222324);
            const int min = 0;
            const int max = 20;
            const int iterations = 10000;
            var counts = new int[max - min];

            for (int i = 0; i < iterations; i++)
            {
                var value = rng.NextInt(min, max);
                counts[value - min]++;
            }

            float expectedPerValue = (float)iterations / (max - min);
            for (int i = 0; i < counts.Length; i++)
            {
                float ratio = counts[i] / expectedPerValue;
                NAssert.IsTrue(
                    ratio > 0.7f && ratio < 1.3f,
                    $"Value {i + min} appeared {counts[i]} times, expected ~{expectedPerValue:F0} (ratio: {ratio:F2})"
                );
            }
        }

        [Test]
        public void TestStatisticalIndependence()
        {
            var rng = new Rng(313233);
            const int iterations = 1000;
            var consecutive = new List<uint>();

            for (int i = 0; i < iterations; i++)
            {
                consecutive.Add(rng.NextUint());
            }

            int transitionsUp = 0;
            int transitionsDown = 0;

            for (int i = 1; i < consecutive.Count; i++)
            {
                if (consecutive[i] > consecutive[i - 1])
                    transitionsUp++;
                else if (consecutive[i] < consecutive[i - 1])
                    transitionsDown++;
            }

            int totalTransitions = transitionsUp + transitionsDown;
            float upRatio = (float)transitionsUp / totalTransitions;

            NAssert.IsTrue(
                upRatio > 0.4f && upRatio < 0.6f,
                $"Statistical independence test: up transition ratio {upRatio:F3} should be roughly 0.5"
            );

            var uniqueValues = consecutive.Distinct().Count();
            float uniqueRatio = (float)uniqueValues / iterations;
            NAssert.IsTrue(
                uniqueRatio > 0.95f,
                $"Statistical independence test: unique ratio {uniqueRatio:F3} should be very high"
            );
        }

        #endregion

        #region Edge Cases

        [Test]
        public void TestNextFloatEdgeCases()
        {
            var rng = new Rng(123);

            var value = rng.NextFloat(5f, 5f);
            NAssert.IsTrue(
                Mathf.Approximately(value, 5f),
                "NextFloat with equal min/max should return that value"
            );

            for (int i = 0; i < 10; i++)
            {
                value = rng.NextFloat(0f, float.Epsilon);
                NAssert.IsTrue(
                    value >= 0f && value <= float.Epsilon,
                    "NextFloat should handle very small ranges"
                );
            }
        }

        [Test]
        public void TestNextIntEdgeCases()
        {
            var rng = new Rng(789);

            var value = rng.NextInt(10, 11);
            NAssert.IsTrue(value == 10, "NextInt with range of 1 should return min value");

            value = rng.NextInt(int.MinValue + 1, int.MinValue + 2);
            NAssert.IsTrue(
                value == int.MinValue + 1,
                "NextInt should handle extreme negative values"
            );

            value = rng.NextInt(int.MaxValue - 1, int.MaxValue);
            NAssert.IsTrue(
                value == int.MaxValue - 1,
                "NextInt should handle extreme positive values"
            );
        }

        [Test]
        public void TestSequenceUniqueness()
        {
            var rng = new Rng(252627);
            var seen = new HashSet<uint>();
            const int iterations = 1000;

            for (int i = 0; i < iterations; i++)
            {
                var value = rng.NextUint();
                NAssert.IsTrue(
                    !seen.Contains(value),
                    $"Duplicate value {value} found at iteration {i}"
                );
                seen.Add(value);
            }

            NAssert.AreEqual(iterations, seen.Count, "All generated values should be unique");
        }

        [Test]
        public void TestLargeSeedValues()
        {
            var seeds = new ulong[]
            {
                ulong.MaxValue,
                ulong.MaxValue - 1,
                0x8000000000000000UL,
                0x7FFFFFFFFFFFFFFFUL,
            };

            foreach (var seed in seeds)
            {
                var rng = new Rng(seed);

                for (int i = 0; i < 10; i++)
                {
                    var value = rng.Next();
                    NAssert.IsTrue(
                        value >= 0f && value < 1f,
                        $"Large seed {seed} should still produce valid random values"
                    );
                }
            }
        }

        [Test]
        public void TestPeriodIsLarge()
        {
            var rng = new Rng(282930);
            var initial = rng.NextUint();

            bool foundCycle = false;
            for (int i = 0; i < 100000; i++)
            {
                if (rng.NextUint() == initial)
                {
                    foundCycle = true;
                    break;
                }
            }

            NAssert.IsTrue(
                !foundCycle,
                "RNG should have a very large period (no cycle found in 100k iterations)"
            );
        }

        #endregion
    }
}
