using System;
using UnityEngine;

namespace Trecs.Samples.FeedingFrenzyBenchmark
{
    [Phase(SystemPhase.Input)]
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
                ref var config = ref World.GlobalComponent<FrenzyConfig>().Write;
                int count = Enum.GetValues(typeof(IterationStyle)).Length;
                config.IterationStyle = (IterationStyle)(
                    ((int)config.IterationStyle + _pendingIterationStyleDelta + count) % count
                );
                _pendingIterationStyleDelta = 0;
            }
        }
    }
}
