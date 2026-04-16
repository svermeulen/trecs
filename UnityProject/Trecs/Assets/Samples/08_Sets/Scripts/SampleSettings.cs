using System;

namespace Trecs.Samples.Sets
{
    [Serializable]
    public class SampleSettings
    {
        // Grid dimensions (GridSize x GridSize particles).
        public int GridSize = 15;

        // Distance between particles in world units.
        public float Spacing = 1.0f;

        // Half-width of each wave band in world units.
        public float WaveBandWidth = 3f;

        // Oscillation speed of the horizontal (X) wave.
        public float WaveXSpeed = 1.5f;

        // Oscillation speed of the vertical (Z) wave.
        public float WaveZSpeed = 1.0f;

        // How far WaveX lifts particles upward.
        public float LiftAmount = 1.5f;

        // Base sphere scale (matches SceneInitializer spawn scale).
        public float BaseScale = 0.6f;

        // Extra scale added at full WaveZ intensity.
        public float ScaleBoost = 0.8f;
    }
}
