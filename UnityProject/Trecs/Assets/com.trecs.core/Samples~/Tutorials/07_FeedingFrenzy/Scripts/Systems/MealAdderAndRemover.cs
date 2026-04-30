using Unity.Mathematics;

namespace Trecs.Samples.FeedingFrenzy101
{
    /// <summary>
    /// Spawns or removes meals to match the desired count.
    ///
    /// Only spawns meals in the NotEating partition. When removing,
    /// prefers removing uneaten (NotEating) meals first.
    /// </summary>
    [ExecuteAfter(typeof(FishAdderAndRemover))]
    public partial class MealAdderAndRemover : ISystem
    {
        readonly float _spawnSpread;
        readonly int _maxPerFrame;

        public MealAdderAndRemover(float spawnSpread, int maxPerFrame)
        {
            _spawnSpread = spawnSpread;
            _maxPerFrame = maxPerFrame;
        }

        public void Execute()
        {
            int desiredMeals = World.GlobalComponent<DesiredMealCount>().Read.Value;
            int currentCount = World.CountEntitiesWithTags<FrenzyTags.Meal>();
            int delta = desiredMeals - currentCount;

            if (delta > 0)
            {
                SpawnMeals(math.min(delta, _maxPerFrame));
            }
            else if (delta < 0)
            {
                RemoveMeals(math.min(-delta, _maxPerFrame));
            }
        }

        void SpawnMeals(int count)
        {
            for (int i = 0; i < count; i++)
            {
                var nutrition = 0.5f + World.Rng.Next();

                // Radial spawn distribution (concentrated near center)
                float angle = World.Rng.Next() * 2f * math.PI;
                float radius = _spawnSpread * math.pow(-math.log(1f - World.Rng.Next()), 0.5f);
                var pos = new float3(math.cos(angle) * radius, 0.25f, math.sin(angle) * radius);

                World
                    .AddEntity<FrenzyTags.Meal, FrenzyTags.NotEating>()
                    .Set(new Position(pos))
                    .Set(new MealNutrition(nutrition))
                    .Set(new UniformScale(0.5f));
            }
        }

        void RemoveMeals(int count)
        {
            int removed = 0;

            // Prefer removing uneaten meals
            foreach (
                var entityIndex in World
                    .Query()
                    .WithTags<FrenzyTags.Meal, FrenzyTags.NotEating>()
                    .EntityIndices()
            )
            {
                World.RemoveEntity(entityIndex);
                removed++;
                if (removed >= count)
                    return;
            }

            // Then remove eaten meals if needed
            foreach (
                var entityIndex in World
                    .Query()
                    .WithTags<FrenzyTags.Meal, FrenzyTags.Eating>()
                    .EntityIndices()
            )
            {
                World.RemoveEntity(entityIndex);
                removed++;
                if (removed >= count)
                    return;
            }
        }
    }
}
