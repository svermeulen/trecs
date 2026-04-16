namespace Trecs.Samples.Snake
{
    /// <summary>
    /// After the head has moved this fixed frame, check whether the head's
    /// new cell contains a food entity. If so: destroy the food, increment
    /// snake length and score.
    /// </summary>
    [ExecutesAfter(typeof(SnakeMovementSystem))]
    public partial class FoodConsumeSystem : ISystem
    {
        public void Execute()
        {
            var headPos = World
                .Query()
                .WithTags<SnakeTags.SnakeHead>()
                .Single()
                .Get<GridPos>()
                .Read.Value;

            var globals = Globals.Query(World).WithTags<TrecsTags.Globals>().Single();

            foreach (var food in Food.Query(World).WithTags<SnakeTags.SnakeFood>())
            {
                if (food.GridPos.x != headPos.x || food.GridPos.y != headPos.y)
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

        partial struct Food : IAspect, IRead<GridPos> { }
    }
}
