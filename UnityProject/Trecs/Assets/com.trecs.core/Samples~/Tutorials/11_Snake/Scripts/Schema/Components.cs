using Unity.Mathematics;

namespace Trecs.Samples.Snake
{
    [Unwrap]
    public partial struct GridPos : IEntityComponent
    {
        public int2 Value;
    }

    [Unwrap]
    public partial struct Direction : IEntityComponent
    {
        // Unit vector on the grid: (1,0), (-1,0), (0,1), or (0,-1).
        public int2 Value;
    }

    [Unwrap]
    public partial struct MoveInput : IEntityComponent
    {
        // (0,0) means "no turn requested this frame". Otherwise the
        // requested direction. Stored as int2 so it serializes via the
        // existing core int2 blit serializer with no custom registration.
        public int2 RequestedDirection;
    }

    [Unwrap]
    public partial struct SegmentAge : IEntityComponent
    {
        // Fixed frame at which this segment was spawned. Used by
        // SegmentTrimSystem to find and remove the oldest segments first.
        public int Value;
    }

    [Unwrap]
    public partial struct SnakeLength : IEntityComponent
    {
        // Target body length, including the head. The trim system removes
        // segments until the segment count reaches Value-1 (head + segments).
        public int Value;
    }

    [Unwrap]
    public partial struct Score : IEntityComponent
    {
        public int Value;
    }

    [Unwrap]
    public partial struct MoveTickCounter : IEntityComponent
    {
        // Counts down each fixed frame. When it reaches 0 the snake takes
        // one grid step and the counter resets to FramesPerMove.
        public int FramesUntilNextMove;
    }
}
