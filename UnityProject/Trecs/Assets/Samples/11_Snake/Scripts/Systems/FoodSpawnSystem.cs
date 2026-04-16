using Unity.Mathematics;

namespace Trecs.Samples.Snake
{
    /// <summary>
    /// Tops the playfield up to <c>SnakeSettings.MaxFoodCount</c> food
    /// entities each fixed frame. Spawn cells are picked from
    /// <c>World.Rng</c> so they are deterministic and replay
    /// identically across recordings — try to keep the seed stable in
    /// WorldSettings or recordings will desync.
    /// </summary>
    [ExecutesAfter(typeof(SegmentTrimSystem))]
    public partial class FoodSpawnSystem : ISystem
    {
        readonly SnakeSettings _settings;

        public FoodSpawnSystem(SnakeSettings settings)
        {
            _settings = settings;
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
            const int maxRetries = 5;

            int size = _settings.GridSize;

            for (int attempt = 0; attempt < maxRetries; attempt++)
            {
                int x = World.Rng.NextInt(0, size);
                int y = World.Rng.NextInt(0, size);

                if (IsCellOccupied(x, y))
                {
                    continue;
                }

                World.AddEntity<SnakeTags.SnakeFood>().Set(new GridPos(new int2(x, y)));
                return true;
            }

            return false;
        }

        bool IsCellOccupied(int x, int y)
        {
            foreach (var blocker in CellBlocker.Query(World).MatchByComponents())
            {
                if (blocker.GridPos.x == x && blocker.GridPos.y == y)
                {
                    return true;
                }
            }

            return false;
        }

        partial struct CellBlocker : IAspect, IRead<GridPos> { }
    }
}
