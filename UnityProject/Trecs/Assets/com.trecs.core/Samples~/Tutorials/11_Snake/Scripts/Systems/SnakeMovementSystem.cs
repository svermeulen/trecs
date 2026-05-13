namespace Trecs.Samples.Snake
{
    /// <summary>
    /// Counts down the global move tick and, when it reaches zero, applies
    /// any pending turn input, spawns a new body segment at the head's
    /// current position, and advances the head one cell in its current
    /// direction (wrapping at the grid edges).
    /// </summary>
    [ExecuteAfter(typeof(SnakeInputSystem))]
    public partial class SnakeMovementSystem : ISystem
    {
        readonly SnakeSettings _settings;

        public SnakeMovementSystem(SnakeSettings settings)
        {
            _settings = settings;
        }

        void Execute([SingleEntity(typeof(SnakeTags.SnakeHead))] in SnakeHead head)
        {
            ref var counter = ref World.GlobalComponent<MoveTickCounter>().Write;

            if (counter.FramesUntilNextMove > 0)
            {
                counter.FramesUntilNextMove--;
                return;
            }

            counter.FramesUntilNextMove = _settings.FramesPerMove - 1;

            // Apply turn input if it's non-zero and not a 180° reversal.
            var requested = World.GlobalComponent<MoveInput>().Read.RequestedDirection;

            bool hasRequest = requested.x != 0 || requested.y != 0;
            bool isReversal = requested.x == -head.Direction.x && requested.y == -head.Direction.y;

            if (hasRequest && !isReversal)
            {
                head.Direction = requested;
            }

            // Snapshot the head's pre-move position so we can drop a segment
            // there. The new segment is created with FrameSpawned = current
            // fixed frame so SegmentTrimSystem can remove the oldest first.
            var prevPos = head.GridPos;

            World
                .AddEntity<SnakeTags.SnakeSegment>()
                .Set(new GridPos(prevPos))
                .Set(new SegmentAge(World.Frame));

            // Advance the head and wrap with floor-mod so negative
            // intermediate values still land in [0, GridSize).
            int size = _settings.GridSize;
            var newPos = head.GridPos + head.Direction;
            newPos.x = ((newPos.x % size) + size) % size;
            newPos.y = ((newPos.y % size) + size) % size;
            head.GridPos = newPos;
        }

        partial struct SnakeHead : IAspect, IWrite<Direction, GridPos> { }
    }
}
