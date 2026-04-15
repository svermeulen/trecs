using UnityEngine;
using UnityEngine.SceneManagement;

namespace Trecs.Samples.FeedingFrenzyBenchmark
{
    public class StateApproachDynamicSwitcher
    {
        readonly WorldAccessor _ecs;

        public StateApproachDynamicSwitcher(World world)
        {
            _ecs = world.CreateAccessor();
        }

        public void Tick()
        {
            if (Input.GetKeyDown(KeyCode.F1))
            {
                SwitchStateApproach(FrenzyStateApproach.Branching);
            }
            else if (Input.GetKeyDown(KeyCode.F2))
            {
                SwitchStateApproach(FrenzyStateApproach.Sets);
            }
            else if (Input.GetKeyDown(KeyCode.F3))
            {
                SwitchStateApproach(FrenzyStateApproach.States);
            }
        }

        void SwitchStateApproach(FrenzyStateApproach approach)
        {
            // Read the current runtime config so we preserve any in-session
            // changes (e.g. iteration style toggled via Tab) when reloading.

            var current = _ecs.GlobalComponent<FrenzyConfig>().Read;

            if (current.StateApproach == approach)
            {
                return;
            }

            FrenzyCompositionRoot.ConfigOverride = new FrenzyConfigSettings
            {
                StateApproach = approach,
                IterationStyle = current.IterationStyle,
                Deterministic = current.Deterministic,
            };

            SceneManager.LoadScene(SceneManager.GetActiveScene().name);
        }
    }
}
