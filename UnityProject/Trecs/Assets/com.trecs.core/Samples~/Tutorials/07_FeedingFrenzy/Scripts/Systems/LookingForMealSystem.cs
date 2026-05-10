using Unity.Mathematics;

namespace Trecs.Samples.FeedingFrenzy101
{
    /// <summary>
    /// Pairs idle fish with available meals each frame.
    ///
    /// Demonstrates: Aspect queries with partition-based tag filtering
    /// and SetTag partition transitions.
    ///
    /// This system is serial (not a job) because pairing fish 1:1 with
    /// meals is inherently sequential — each pairing removes both from
    /// the available pool.
    /// </summary>
    public partial class LookingForMealSystem : ISystem
    {
        public void Execute()
        {
            var mealIter = Meal.Query(World)
                .WithTags<FrenzyTags.Meal, FrenzyTags.NotEating>()
                .GetEnumerator();

            foreach (
                var fish in Fish.Query(World).WithTags<FrenzyTags.Fish, FrenzyTags.NotEating>()
            )
            {
                if (!mealIter.MoveNext())
                {
                    break;
                }

                PairFishWithMeal(fish, mealIter.Current);
            }
        }

        void PairFishWithMeal(in Fish fish, in Meal meal)
        {
            // Store a stable handle to the meal (survives group moves)
            fish.TargetMeal = meal.Handle(World);
            meal.ApproachingFish = fish.Handle(World);

            // Set destination at the meal's position, keeping the fish's Y
            var destPos = meal.Position;
            destPos.y = fish.SimPosition.y;
            fish.DestinationPosition = destPos;

            // Point the fish toward the meal
            var delta = fish.DestinationPosition - fish.SimPosition;
            var dir = math.lengthsq(delta) > 0.001f ? math.normalize(delta) : new float3(1, 0, 0);
            fish.Velocity = dir * fish.Speed;
            fish.SimRotation = quaternion.LookRotationSafe(dir, math.up());

            // Transition both to Eating partition — this moves them into
            // separate groups so eating systems only iterate eating entities
            fish.SetTag<FrenzyTags.Eating>(World);
            meal.SetTag<FrenzyTags.Eating>(World);
        }

        partial struct Fish
            : IAspect,
                IRead<Speed>,
                IWrite<SimPosition, SimRotation, TargetMeal, Velocity, DestinationPosition> { }

        partial struct Meal : IAspect, IRead<Position>, IWrite<ApproachingFish> { }
    }
}
