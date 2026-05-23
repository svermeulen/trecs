using Unity.Mathematics;
using UnityEngine;

namespace Trecs.Samples.JobSystem
{
    /// <summary>
    /// Captures one-shot key-down events at variable cadence and forwards them
    /// to the simulation via input components on the global entity. The actual
    /// state mutation happens in <see cref="ApplyControlInputSystem"/> during
    /// fixed update.
    /// </summary>
    [ExecuteIn(SystemPhase.Input)]
    public partial class InputSystem : ISystem
    {
        int _pendingCountDirection;
        bool _pendingToggleJobs;

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

            if (Input.GetKeyDown(KeyCode.J))
            {
                _pendingToggleJobs = true;
            }
        }

        public void Execute()
        {
            if (_pendingCountDirection != 0)
            {
                World.GlobalEntityHandle.AddInput(
                    World,
                    new ParticleCountAdjustInput { Direction = _pendingCountDirection }
                );
                _pendingCountDirection = 0;
            }

            if (_pendingToggleJobs)
            {
                World.GlobalEntityHandle.AddInput(World, new ToggleJobsInput { Toggle = true });
                _pendingToggleJobs = false;
            }
        }
    }

    /// <summary>
    /// Fixed-update consumer: reads the input components queued by
    /// <see cref="InputSystem"/> and applies them to the deterministic fixed
    /// components (<see cref="DesiredNumParticles"/>, <see cref="IsJobsEnabled"/>).
    /// Runs before <see cref="ParticleSpawnerSystem"/> so adjustments take
    /// effect in the same fixed frame they were queued for.
    /// </summary>
    [ExecuteBefore(typeof(ParticleSpawnerSystem))]
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
            int direction = World.GlobalComponent<ParticleCountAdjustInput>().Read.Direction;
            if (direction != 0)
            {
                ref var desired = ref World.GlobalComponent<DesiredNumParticles>().Write;
                if (direction > 0)
                {
                    desired.Value = math.min(desired.Value * 2, _maxCount);
                }
                else
                {
                    desired.Value = math.max(desired.Value / 2, _minCount);
                }
            }

            if (World.GlobalComponent<ToggleJobsInput>().Read.Toggle)
            {
                ref var jobsEnabled = ref World.GlobalComponent<IsJobsEnabled>().Write;
                jobsEnabled.Value = !jobsEnabled.Value;
            }
        }
    }
}
