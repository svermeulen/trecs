using UnityEngine;

namespace Trecs.Samples.FeedingFrenzyBenchmark
{
    [InputSystem]
    public partial class FishCountPresetInputSystem : ISystem
    {
        readonly int[] _presets;
        int _pendingDelta;

        public FishCountPresetInputSystem(int[] presets)
        {
            _presets = presets;
        }

        public void Tick()
        {
            if (Input.GetKeyDown(KeyCode.LeftArrow))
            {
                _pendingDelta = -1;
            }
            else if (Input.GetKeyDown(KeyCode.RightArrow))
            {
                _pendingDelta = 1;
            }
        }

        public void Execute()
        {
            if (_pendingDelta != 0)
            {
                int current = World.GlobalComponent<DesiredPreset>().Read.Value;
                int next = Mathf.Clamp(current + _pendingDelta, 0, _presets.Length - 1);
                World.AddInput(World.GlobalEntityHandle, new DesiredPreset { Value = next });
                _pendingDelta = 0;
            }
        }
    }
}
