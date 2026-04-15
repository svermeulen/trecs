namespace Trecs.Samples.Snake
{
    /// <summary>
    /// Counts down the global move tick and, when it reaches zero, applies
    /// any pending turn input, spawns a new body segment at the head's
    /// current position, and advances the head one cell in its current
    /// direction (wrapping at the grid edges).
    /// </summary>
    [ExecutesAfter(typeof(SnakeInputSystem))]
    public partial class SnakeMovementSystem : ISystem
    {
        readonly SnakeSettings _settings;
        readonly SnakeGameObjectManager _goManager;

        public SnakeMovementSystem(SnakeSettings settings, SnakeGameObjectManager goManager)
        {
            _settings = settings;
            _goManager = goManager;
        }

        public void Execute()
        {
            ref var counter = ref World.GlobalComponent<MoveTickCounter>().Write;
            if (counter.FramesUntilNextMove > 0)
            {
                counter.FramesUntilNextMove--;
                return;
            }

            counter.FramesUntilNextMove = _settings.FramesPerMove - 1;

            var head = World.Query().WithTags<SnakeTags.SnakeHead>().Single();

            // Apply turn input if it's non-zero and not a 180° reversal.
            var requested = World.GlobalComponent<MoveInput>().Read.RequestedDirection;
            ref var direction = ref head.Get<Direction>().Write;

            bool hasRequest = requested.x != 0 || requested.y != 0;
            bool isReversal =
                requested.x == -direction.Value.x && requested.y == -direction.Value.y;

            if (hasRequest && !isReversal)
            {
                direction.Value = requested;
            }

            // Snapshot the head's pre-move position so we can drop a segment
            // there. The new segment is created with FrameSpawned = current
            // fixed frame so SegmentTrimSystem can remove the oldest first.
            ref var headPos = ref head.Get<GridPos>().Write;
            var prevPos = headPos.Value;

            World
                .AddEntity<SnakeTags.SnakeSegment>()
                .Set(new GridPos(prevPos))
                .Set(new SegmentAge(World.FixedFrame))
                .Set(_goManager.CreateSegment())
                .AssertComplete();

            // Advance the head and wrap with floor-mod so negative
            // intermediate values still land in [0, GridSize).
            int size = _settings.GridSize;
            var newPos = headPos.Value + direction.Value;
            newPos.x = ((newPos.x % size) + size) % size;
            newPos.y = ((newPos.y % size) + size) % size;
            headPos.Value = newPos;
        }
    }
}
