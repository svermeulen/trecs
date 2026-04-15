using Unity.Mathematics;

namespace Trecs.Samples.FeedingFrenzy101
{
    /// <summary>
    /// Spawns or removes fish to match the desired count.
    ///
    /// Lerps toward the target for smooth visual transitions.
    /// Also computes and sets the desired meal count based on a ratio.
    /// </summary>
    public partial class FishAdderAndRemover : ISystem
    {
        readonly float _mealCountRatio;
        readonly float _spawnSpread;

        int _currentTarget;

        public FishAdderAndRemover(float mealCountRatio, float spawnSpread)
        {
            _mealCountRatio = mealCountRatio;
            _spawnSpread = spawnSpread;
        }

        public void Execute()
        {
            int desiredFish = World.GlobalComponent<DesiredFishCount>().Read.Value;

            // Smooth lerp toward desired count
            _currentTarget = (int)
                math.lerp(_currentTarget, desiredFish, math.saturate(World.DeltaTime));

            // Update desired meal count
            ref var desiredMeals = ref World.GlobalComponent<DesiredMealCount>().Write;
            desiredMeals.Value = (int)(_currentTarget * _mealCountRatio);

            int currentCount = World.CountEntitiesWithTags<FrenzyTags.Fish>();
            int delta = _currentTarget - currentCount;

            if (delta > 0)
                SpawnFish(delta);
            else if (delta < 0)
                RemoveFish(-delta);
        }

        void SpawnFish(int count)
        {
            for (int i = 0; i < count; i++)
            {
                var scalePx = World.Rng.Next();
                var scale = math.lerp(0.5f, 1.5f, scalePx);
                var speed = math.lerp(8f, 3f, scalePx);

                // Radial spawn distribution (concentrated near center)
                float angle = World.Rng.Next() * 2f * math.PI;
                float radius = _spawnSpread * math.pow(-math.log(1f - World.Rng.Next()), 0.5f);
                var pos = new float3(
                    math.cos(angle) * radius,
                    -1f * scale,
                    math.sin(angle) * radius
                );

                World
                    .AddEntity<FrenzyTags.Fish, FrenzyTags.NotEating>()
                    .Set(new Position(pos))
                    .Set(new SimPosition(pos))
                    .Set(new Speed(speed))
                    .Set(new UniformScale(scale))
                    .AssertComplete();
            }
        }

        void RemoveFish(int count)
        {
            int removed = 0;

            // Prefer removing idle fish first
            foreach (
                var entityIndex in World
                    .Query()
                    .WithTags<FrenzyTags.Fish, FrenzyTags.NotEating>()
                    .EntityIndices()
            )
            {
                World.RemoveEntity(entityIndex);
                removed++;
                if (removed >= count)
                    return;
            }

            // Then remove eating fish
            foreach (
                var entityIndex in World
                    .Query()
                    .WithTags<FrenzyTags.Fish, FrenzyTags.Eating>()
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
