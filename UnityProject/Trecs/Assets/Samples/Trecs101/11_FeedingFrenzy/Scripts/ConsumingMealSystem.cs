using Unity.Mathematics;

namespace Trecs.Samples.FeedingFrenzy101
{
    /// <summary>
    /// Checks whether eating fish have reached their meal and consumes it.
    ///
    /// Demonstrates: [WrapAsJob] with [FromWorld] for cross-entity access
    /// inside a Burst job. The NativeFactory parameter is annotated with
    /// [FromWorld(Tag = ...)] so the source generator handles group resolution,
    /// dependency tracking, lookup creation, and disposal automatically.
    /// </summary>
    public partial class ConsumingMealSystem : ISystem
    {
        [ForEachEntity(Tags = new[] { typeof(FrenzyTags.Fish), typeof(FrenzyTags.Eating) })]
        [WrapAsJob]
        static void Execute(
            in ConsumingFish fish,
            EntityIndex entityIndex,
            in NativeWorldAccessor world,
            [FromWorld(Tag = typeof(FrenzyTags.Meal))] in MealNutritionView.NativeFactory mealLookup
        )
        {
            var distSqr = math.lengthsq(fish.DestinationPosition - fish.SimPosition);
            if (distSqr >= EatDistanceSqr)
                return;

            var mealHandle = fish.TargetMeal;
            if (!world.TryGetEntityIndex(mealHandle, out var mealIndex))
                return;

            var meal = mealLookup.Create(mealIndex);
            fish.UniformScale = fish.UniformScale + 0.05f * meal.MealNutrition;

            world.RemoveEntity(mealIndex);
            fish.TargetMeal = EntityHandle.Null;
            world.MoveTo<FrenzyTags.Fish, FrenzyTags.NotEating>(entityIndex);
        }

        partial struct ConsumingFish
            : IAspect,
                IRead<SimPosition, DestinationPosition>,
                IWrite<TargetMeal, UniformScale> { }

        partial struct MealNutritionView : IAspect, IRead<MealNutrition> { }

        const float EatDistanceSqr = 1f;
    }
}
