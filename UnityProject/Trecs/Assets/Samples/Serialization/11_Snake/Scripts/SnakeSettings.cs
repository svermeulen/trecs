using System;

namespace Trecs.Samples.Snake
{
    [Serializable]
    public class SnakeSettings
    {
        // Width and height of the wraparound playfield, in cells.
        public int GridSize = 20;

        // Snake takes a single grid step every N fixed frames. At the
        // default 60 Hz fixed step, FramesPerMove = 6 yields ~10 moves/sec.
        public int FramesPerMove = 6;

        // Maximum number of food entities allowed on the field at once.
        public int MaxFoodCount = 3;

        // Length the snake starts with (head + body segments).
        public int InitialSnakeLength = 4;

        // Deterministic RNG seed used for food spawn placement. Recordings
        // are tied to this seed — change it and old recordings will desync.
        public ulong RandomSeed = 12345;
    }
}
