using UnityEngine;
using UnityEngine.SceneManagement;

namespace Trecs.Samples.FeedingFrenzyBenchmark
{
    public class SubsetApproachDynamicSwitcher
    {
        readonly WorldAccessor _world;

        public SubsetApproachDynamicSwitcher(World world)
        {
            _world = world.CreateAccessor(AccessorRole.Variable);
        }

        public void Tick()
        {
            if (Input.GetKeyDown(KeyCode.F1))
            {
                SwitchSubsetApproach(FrenzySubsetApproach.Branching);
            }
            else if (Input.GetKeyDown(KeyCode.F2))
            {
                SwitchSubsetApproach(FrenzySubsetApproach.Sets);
            }
            else if (Input.GetKeyDown(KeyCode.F3))
            {
                SwitchSubsetApproach(FrenzySubsetApproach.Partitions);
            }
        }

        void SwitchSubsetApproach(FrenzySubsetApproach approach)
        {
            // Read the current runtime config so we preserve any in-session
            // changes (e.g. iteration style toggled via Tab) when reloading.

            var current = _world.GlobalComponent<FrenzyConfig>().Read;

            if (current.SubsetApproach == approach)
            {
                return;
            }

            FrenzyCompositionRoot.ConfigOverride = new FrenzyConfigSettings
            {
                SubsetApproach = approach,
                IterationStyle = current.IterationStyle,
                Deterministic = current.Deterministic,
            };

            SceneManager.LoadScene(SceneManager.GetActiveScene().name);
        }
    }
}
