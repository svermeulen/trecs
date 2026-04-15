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
        readonly SnakeGameObjectManager _goManager;

        public FoodConsumeSystem(SnakeGameObjectManager goManager)
        {
            _goManager = goManager;
        }

        public void Execute()
        {
            var headPos = World
                .Query()
                .WithTags<SnakeTags.SnakeHead>()
                .Single()
                .Get<GridPos>()
                .Read.Value;

            foreach (var foodIndex in World.Query().WithTags<SnakeTags.SnakeFood>().EntityIndices())
            {
                var foodPos = World.Component<GridPos>(foodIndex).Read.Value;
                if (foodPos.x != headPos.x || foodPos.y != headPos.y)
                {
                    continue;
                }

                var goId = World.Component<GameObjectId>(foodIndex).Read;
                _goManager.Destroy(goId);
                World.RemoveEntity(foodIndex);

                ref var length = ref World.GlobalComponent<SnakeLength>().Write;
                length.Value++;
                ref var score = ref World.GlobalComponent<Score>().Write;
                score.Value++;
                // At most one food per cell, so we're done.
                break;
            }
        }
    }
}
