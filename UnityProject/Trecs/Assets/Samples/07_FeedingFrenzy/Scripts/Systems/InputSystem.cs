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
    public partial class InputSystem : ISystem
    {
        readonly int _minCount;
        readonly int _maxCount;

        public InputSystem(int minCount, int maxCount)
        {
            _minCount = minCount;
            _maxCount = maxCount;
        }

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
                desired.Value = math.min(desired.Value * 2, _maxCount);
            }
            else if (downPressed)
            {
                desired.Value = math.max(desired.Value / 2, _minCount);
            }
        }
    }
}
