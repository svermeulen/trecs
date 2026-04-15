using Unity.Mathematics;

namespace Trecs.Samples.Snake
{
    /// <summary>
    /// Tops the playfield up to <c>SnakeSettings.MaxFoodCount</c> food
    /// entities each fixed frame. Spawn cells are picked from
    /// <c>World.FixedRng</c> so they are deterministic and replay
    /// identically across recordings — try to keep the seed stable in
    /// WorldSettings or recordings will desync.
    /// </summary>
    [ExecutesAfter(typeof(SegmentTrimSystem))]
    public partial class FoodSpawnSystem : ISystem
    {
        readonly SnakeSettings _settings;
        readonly SnakeGameObjectManager _goManager;

        public FoodSpawnSystem(SnakeSettings settings, SnakeGameObjectManager goManager)
        {
            _settings = settings;
            _goManager = goManager;
        }

        public void Execute()
        {
            int currentFood = World.CountEntitiesWithTags<SnakeTags.SnakeFood>();
            int toSpawn = _settings.MaxFoodCount - currentFood;

            for (int i = 0; i < toSpawn; i++)
            {
                if (!TrySpawnFood())
                {
                    // Grid effectively full — give up for this frame.
                    break;
                }
            }
        }

        bool TrySpawnFood()
        {
            const int maxRetries = 30;
            int size = _settings.GridSize;

            for (int attempt = 0; attempt < maxRetries; attempt++)
            {
                int x = math.min((int)(World.FixedRng.Next() * size), size - 1);
                int y = math.min((int)(World.FixedRng.Next() * size), size - 1);

                if (IsCellOccupied(x, y))
                {
                    continue;
                }

                World
                    .AddEntity<SnakeTags.SnakeFood>()
                    .Set(new GridPos(new int2(x, y)))
                    .Set(_goManager.CreateFood())
                    .AssertComplete();
                return true;
            }

            return false;
        }

        bool IsCellOccupied(int x, int y)
        {
            var head = World.Query().WithTags<SnakeTags.SnakeHead>().Single();
            var headPos = head.Get<GridPos>().Read.Value;
            if (headPos.x == x && headPos.y == y)
            {
                return true;
            }

            foreach (var idx in World.Query().WithTags<SnakeTags.SnakeSegment>().EntityIndices())
            {
                var pos = World.Component<GridPos>(idx).Read.Value;
                if (pos.x == x && pos.y == y)
                {
                    return true;
                }
            }

            foreach (var idx in World.Query().WithTags<SnakeTags.SnakeFood>().EntityIndices())
            {
                var pos = World.Component<GridPos>(idx).Read.Value;
                if (pos.x == x && pos.y == y)
                {
                    return true;
                }
            }

            return false;
        }
    }
}
