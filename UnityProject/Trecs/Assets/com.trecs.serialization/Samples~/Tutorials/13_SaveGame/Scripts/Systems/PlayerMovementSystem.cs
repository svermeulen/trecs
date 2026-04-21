using Unity.Mathematics;

namespace Trecs.Serialization.Samples.SaveGame
{
    /// <summary>
    /// Sokoban movement, driven by tap input. Every fixed frame that the
    /// global MoveInput has a non-zero step:
    ///
    /// 1. If the target cell is a wall, reject the move.
    /// 2. If the target cell has a box, try to push it one cell further in
    ///    the same direction — only if the cell beyond is empty (no wall,
    ///    no other box).
    /// 3. Otherwise the player walks into the target cell.
    ///
    /// No rate-limiting: PlayerInputSystem uses GetKeyDown so at most one
    /// move is queued per keypress.
    /// </summary>
    [ExecutesAfter(typeof(PlayerInputSystem))]
    public partial class PlayerMovementSystem : ISystem
    {
        public void Execute()
        {
            var step = World.GlobalComponent<MoveInput>().Read.Step;
            if (step.x == 0 && step.y == 0)
            {
                return;
            }

            var player = PlayerView.Query(World).WithTags<SaveGameTags.Player>().Single();
            var currentPos = player.GridPos;
            var targetPos = currentPos + step;

            if (IsWall(targetPos))
            {
                return;
            }

            if (TryFindBoxAt(targetPos, out var boxToPush))
            {
                var beyondPos = targetPos + step;
                if (IsWall(beyondPos) || TryFindBoxAt(beyondPos, out _))
                {
                    return;
                }
                boxToPush.GridPos = beyondPos;
            }

            player.GridPos = targetPos;
        }

        // Linear scans over walls / boxes are fine at this scale (one player
        // move per fixed tick, ~30 entities). For larger levels or higher
        // tick rates, cache cell occupancy in a global component keyed by
        // int2 and invalidate it when entities move.
        bool IsWall(int2 pos)
        {
            foreach (var wall in WallView.Query(World).WithTags<SaveGameTags.Wall>())
            {
                if (wall.GridPos.x == pos.x && wall.GridPos.y == pos.y)
                {
                    return true;
                }
            }
            return false;
        }

        bool TryFindBoxAt(int2 pos, out BoxView box)
        {
            foreach (var b in BoxView.Query(World).WithTags<SaveGameTags.Box>())
            {
                if (b.GridPos.x == pos.x && b.GridPos.y == pos.y)
                {
                    box = b;
                    return true;
                }
            }
            box = default;
            return false;
        }

        partial struct PlayerView : IAspect, IWrite<GridPos> { }

        partial struct BoxView : IAspect, IWrite<GridPos> { }

        partial struct WallView : IAspect, IRead<GridPos> { }
    }
}
