using System;
using UnityEngine;

namespace Trecs.Samples.FeedingFrenzyBenchmark
{
    [Serializable]
    public struct IdleBobSystemSettings
    {
        public float BobAmplitude;
        public float BobFrequency;
        public float BobBaseY;
    }

    [Serializable]
    public class CommonSettings
    {
        public float MealCountRatio = 0.9f;
        public float SpawnSpread = 900f;
        public float SpawnConcentration = 1.5f;
        public float MealSize = 2f;
        public float MealYOffset = -2.5f;
        public Color MealColor = new(r: 0.25848943f, g: 0.9245283f, b: 0, a: 0);
        public Color FishColor = new(0.7f, 0.9f, 1.0f, 0f);

        public int MinPresetFishCount = 5000;
        public int MaxPresetFishCount = 1000000;
        public int DefaultPresetIndex = 3;
    }
}
