using System;
using System.Collections;
using System.Collections.Generic;
using NUnit.Framework;
using Trecs.Samples.FeedingFrenzyBenchmark;
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
        const int FramesToRun = 60 * 5;

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

        [UnityTest]
        public IEnumerator Pointers() => RunScene("Pointers");

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

                // One extra frame so Bootstrap.Awake has run and CurrentWorld is set.
                yield return null;
                Assert.IsNotNull(
                    FrenzyCompositionRoot.CurrentWorld,
                    "FrenzyCompositionRoot.CurrentWorld was not populated"
                );

                const int dwellFrames = 180;
                foreach (IterationStyle style in Enum.GetValues(typeof(IterationStyle)))
                {
                    FrenzyCompositionRoot
                        .CurrentWorld.GlobalComponent<FrenzyConfig>()
                        .Write.IterationStyle = style;

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
        public IEnumerator SaveGame() => RunScene("SaveGame");

        [UnityTest]
        public IEnumerator NativePointers() => RunScene("NativePointers");

        [UnityTest]
        public IEnumerator AspectInterfaces() => RunScene("AspectInterfaces");

        [UnityTest]
        public IEnumerator BlobStorage() => RunScene("BlobStorage");

        [UnityTest]
        public IEnumerator ReactiveEvents() => RunScene("ReactiveEvents");

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
