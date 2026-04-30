using Unity.Mathematics;
using UnityEngine;

namespace Trecs.Samples.JobSystem
{
    [Phase(SystemPhase.Presentation)]
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

            if (upPressed || downPressed)
            {
                ref var desired = ref World.GlobalComponent<DesiredNumParticles>().Write;

                if (upPressed)
                {
                    desired.Value = math.min(desired.Value * 2, _maxCount);
                }
                else
                {
                    desired.Value = math.max(desired.Value / 2, _minCount);
                }
            }

            if (Input.GetKeyDown(KeyCode.J))
            {
                ref var jobsEnabled = ref World.GlobalComponent<IsJobsEnabled>().Write;
                jobsEnabled.Value = !jobsEnabled.Value;
            }
        }
    }
}
