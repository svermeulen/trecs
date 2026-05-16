using System;
using UnityEngine;

namespace Trecs.Samples.FeedingFrenzyBenchmark
{
    [ExecuteIn(SystemPhase.Input)]
    public partial class ConfigInputSystem : ISystem
    {
        int _pendingIterationStyleDelta;

        public void Tick()
        {
            if (Input.GetKeyDown(KeyCode.Tab))
            {
                _pendingIterationStyleDelta = Input.GetKey(KeyCode.LeftShift) ? -1 : 1;
            }
        }

        public void Execute()
        {
            if (_pendingIterationStyleDelta != 0)
            {
                var current = World.GlobalComponent<DesiredIterationStyle>().Read.Value;
                int count = Enum.GetValues(typeof(IterationStyle)).Length;
                var next = (IterationStyle)(
                    ((int)current + _pendingIterationStyleDelta + count) % count
                );
                World.GlobalEntityHandle.AddInput(
                    World,
                    new DesiredIterationStyle { Value = next }
                );
                _pendingIterationStyleDelta = 0;
            }
        }
    }
}
