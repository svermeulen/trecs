using Unity.Mathematics;
using UnityEngine;

namespace Trecs.Samples.FeedingFrenzy101
{
    /// <summary>
    /// Adjusts the desired fish count via keyboard input.
    ///
    /// Up arrow doubles the count, Down arrow halves it (min 10).
    /// Displays current target and actual counts in the console.
    /// </summary>
    [VariableUpdate]
    public partial class CountInputSystem : ISystem
    {
        const int MinCount = 10;
        const int MaxCount = 200_000;

        public void Execute()
        {
            bool upPressed = Input.GetKeyDown(KeyCode.UpArrow);
            bool downPressed = Input.GetKeyDown(KeyCode.DownArrow);

            if (!upPressed && !downPressed)
            {
                return;
            }

            ref var desired = ref World.GlobalComponent<DesiredFishCount>().Write;

            if (upPressed)
            {
                desired.Value = math.min(desired.Value * 2, MaxCount);
            }
            else if (downPressed)
            {
                desired.Value = math.max(desired.Value / 2, MinCount);
            }

            int fishCount = World.CountEntitiesWithTags<FrenzyTags.Fish>();
            int mealCount = World.CountEntitiesWithTags<FrenzyTags.Meal>();
            Debug.Log(
                $"[FeedingFrenzy] Target: {desired.Value} fish | Current: {fishCount} fish, {mealCount} meals"
            );
        }
    }
}
