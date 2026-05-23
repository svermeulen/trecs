using Unity.Mathematics;
using UnityEngine;

namespace Trecs.Samples.FeedingFrenzy101
{
    /// <summary>
    /// Captures Up/Down arrow key-down events at variable cadence and forwards
    /// them to the simulation via <see cref="FishCountAdjustInput"/> on the
    /// global entity. The actual mutation of <see cref="DesiredFishCount"/>
    /// happens in <see cref="ApplyControlInputSystem"/> during fixed update.
    /// </summary>
    [ExecuteIn(SystemPhase.Input)]
    public partial class InputSystem : ISystem
    {
        int _pendingCountDirection;

        public void Tick()
        {
            if (Input.GetKeyDown(KeyCode.UpArrow))
            {
                _pendingCountDirection = 1;
            }
            else if (Input.GetKeyDown(KeyCode.DownArrow))
            {
                _pendingCountDirection = -1;
            }
        }

        public void Execute()
        {
            if (_pendingCountDirection != 0)
            {
                World.GlobalEntityHandle.AddInput(
                    World,
                    new FishCountAdjustInput { Direction = _pendingCountDirection }
                );
                _pendingCountDirection = 0;
            }
        }
    }

    /// <summary>
    /// Fixed-update consumer: reads the input component queued by
    /// <see cref="InputSystem"/> and applies it to <see cref="DesiredFishCount"/>.
    /// Runs before <see cref="FishAdderAndRemover"/> so adjustments take effect
    /// in the same fixed frame they were queued for.
    /// </summary>
    [ExecuteBefore(typeof(FishAdderAndRemover))]
    public partial class ApplyControlInputSystem : ISystem
    {
        readonly int _minCount;
        readonly int _maxCount;

        public ApplyControlInputSystem(int minCount, int maxCount)
        {
            _minCount = minCount;
            _maxCount = maxCount;
        }

        public void Execute()
        {
            int direction = World.GlobalComponent<FishCountAdjustInput>().Read.Direction;
            if (direction == 0)
            {
                return;
            }

            ref var desired = ref World.GlobalComponent<DesiredFishCount>().Write;
            if (direction > 0)
            {
                desired.Value = math.min(desired.Value * 2, _maxCount);
            }
            else
            {
                desired.Value = math.max(desired.Value / 2, _minCount);
            }
        }
    }
}
