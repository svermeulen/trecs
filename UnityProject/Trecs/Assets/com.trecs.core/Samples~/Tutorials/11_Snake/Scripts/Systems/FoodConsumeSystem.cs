namespace Trecs.Samples.Snake
{
    /// <summary>
    /// After the head has moved this fixed frame, check whether the head's
    /// new cell contains a food entity. If so: destroy the food, increment
    /// snake length and score.
    /// </summary>
    [ExecuteAfter(typeof(SnakeMovementSystem))]
    public partial class FoodConsumeSystem : ISystem
    {
        void Execute(
            [SingleEntity(typeof(TrecsTags.Globals))] in Globals globals,
            [SingleEntity(typeof(SnakeTags.SnakeHead))] in Head head
        )
        {
            foreach (var food in Food.Query(World).WithTags<SnakeTags.SnakeFood>())
            {
                if (food.GridPos.x != head.GridPos.x || food.GridPos.y != head.GridPos.y)
                {
                    continue;
                }

                food.Remove(World);

                globals.SnakeLength++;
                globals.Score++;

                // At most one food per cell, so we're done.
                break;
            }
        }

        partial struct Globals : IAspect, IWrite<SnakeLength, Score> { }

        partial struct Head : IAspect, IRead<GridPos> { }

        partial struct Food : IAspect, IRead<GridPos> { }
    }
}
