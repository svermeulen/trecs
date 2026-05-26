using System;
using System.Collections;
using System.Collections.Generic;
using NUnit.Framework;
using Trecs.Samples.DynamicCollections;
using Trecs.Samples.FeedingFrenzyBenchmark;
using Trecs.Samples.HeightmapBlobs;
using UnityEngine.SceneManagement;
using UnityEngine.TestTools;
using Assert = NUnit.Framework.Assert;

namespace Trecs.Tests.PlayMode
{
    // Smoke-tests every sample scene by loading it in play mode and ticking
    // for a handful of frames. Unity Test Framework auto-fails any [UnityTest]
    // that logs an unhandled error or exception, so a passing run means the
    // scene initialized, ran its update loop, and tore down without issue.
    [TestFixture]
    public class SampleScenesPlayModeTests
    {
        const int FramesToRun = 60 * 3;

        [UnityTest]
        public IEnumerator HelloEntity() => RunScene("HelloEntity");

        [UnityTest]
        public IEnumerator SpawnAndDestroy() => RunScene("SpawnAndDestroy");

        [UnityTest]
        public IEnumerator Aspects() => RunScene("Aspects");

        [UnityTest]
        public IEnumerator PredatorPrey() => RunScene("PredatorPrey");

        [UnityTest]
        public IEnumerator JobSystem() => RunScene("JobSystem");

        [UnityTest]
        public IEnumerator Partitions() => RunScene("Partitions");

        [UnityTest]
        public IEnumerator FeedingFrenzy() => RunScene("FeedingFrenzy");

        [UnityTest]
        public IEnumerator Sets() => RunScene("Sets");

        [UnityTest]
        public IEnumerator Interpolation() => RunScene("Interpolation");

        public static IEnumerable<TestCaseData> DynamicCollectionsCases()
        {
            foreach (TrailCollectionType type in Enum.GetValues(typeof(TrailCollectionType)))
            {
                yield return new TestCaseData(type)
                    .Returns(null)
                    .SetName($"DynamicCollections({type})");
            }
        }

        [UnityTest, TestCaseSource(nameof(DynamicCollectionsCases))]
        public IEnumerator DynamicCollections(TrailCollectionType collectionType)
        {
            DynamicCollectionsCompositionRoot.CollectionTypeOverride = collectionType;
            try
            {
                yield return RunScene("DynamicCollections");
            }
            finally
            {
                DynamicCollectionsCompositionRoot.CollectionTypeOverride = null;
            }
        }

        [UnityTest]
        public IEnumerator Snake() => RunScene("Snake");

        public static IEnumerable<TestCaseData> FeedingFrenzyBenchmarkCases()
        {
            foreach (FrenzySubsetApproach approach in Enum.GetValues(typeof(FrenzySubsetApproach)))
            {
                foreach (var deterministic in new[] { false, true })
                {
                    yield return new TestCaseData(approach, deterministic)
                        .Returns(null)
                        .SetName(
                            $"FeedingFrenzyBenchmark({approach},Deterministic={deterministic})"
                        );
                }
            }
        }

        // Default UnityTest timeout (3 min) is generous, but be explicit here
        // since cycling all IterationStyle values can run long.
        [UnityTest, TestCaseSource(nameof(FeedingFrenzyBenchmarkCases)), Timeout(600_000)]
        public IEnumerator FeedingFrenzyBenchmark(
            FrenzySubsetApproach subsetApproach,
            bool deterministic
        )
        {
            FrenzyCompositionRoot.ConfigOverride = new FrenzyConfigSettings
            {
                SubsetApproach = subsetApproach,
                IterationStyle = (IterationStyle)0,
                Deterministic = deterministic,
            };

            try
            {
                var load = SceneManager.LoadSceneAsync(
                    "FeedingFrenzyBenchmark",
                    LoadSceneMode.Single
                );
                Assert.IsNotNull(
                    load,
                    "Scene 'FeedingFrenzyBenchmark' is not in EditorBuildSettings"
                );
                yield return load;

                const int dwellFrames = 60;
                foreach (IterationStyle style in Enum.GetValues(typeof(IterationStyle)))
                {
                    ConfigInputSystem.TestPendingIterationStyle = style;

                    for (int i = 0; i < dwellFrames; i++)
                    {
                        yield return null;
                    }
                }

                LogAssert.NoUnexpectedReceived();
            }
            finally
            {
                FrenzyCompositionRoot.ConfigOverride = null;
            }
        }

        [UnityTest]
        public IEnumerator AspectInterfaces() => RunScene("AspectInterfaces");

        [UnityTest]
        public IEnumerator BlobSeedPattern() => RunScene("BlobSeedPattern");

        [UnityTest]
        public IEnumerator ReactiveEvents() => RunScene("ReactiveEvents");

        [UnityTest]
        public IEnumerator MultipleWorlds() => RunScene("MultipleWorlds");

        public static IEnumerable<TestCaseData> HeightmapBlobsCases()
        {
            foreach (HeightmapFlavor flavor in Enum.GetValues(typeof(HeightmapFlavor)))
            {
                yield return new TestCaseData(flavor)
                    .Returns(null)
                    .SetName($"HeightmapBlobs({flavor})");
            }
        }

        [UnityTest, TestCaseSource(nameof(HeightmapBlobsCases))]
        public IEnumerator HeightmapBlobs(HeightmapFlavor flavor)
        {
            HeightmapBlobsCompositionRoot.FlavorOverride = flavor;
            try
            {
                yield return RunScene("HeightmapBlobs");
            }
            finally
            {
                HeightmapBlobsCompositionRoot.FlavorOverride = null;
            }
        }

        static IEnumerator RunScene(string sceneName)
        {
            var load = SceneManager.LoadSceneAsync(sceneName, LoadSceneMode.Single);
            Assert.IsNotNull(load, $"Scene '{sceneName}' is not in EditorBuildSettings");
            yield return load;

            for (int i = 0; i < FramesToRun; i++)
            {
                yield return null;
            }

            LogAssert.NoUnexpectedReceived();
        }
    }
}
