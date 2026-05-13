using UnityEngine;

namespace Trecs.Samples.HelloEntity
{
    // Presentation phase: it's a best practice to never touch GameObjects from
    // a Fixed update system. Fixed update is supposed to be deterministic
    // (free of outside state) so the world can be serialized, snapshotted,
    // recorded, and replayed.
    [ExecuteIn(SystemPhase.Presentation)]
    public partial class SpinnerGameObjectUpdater : ISystem
    {
        readonly Transform _spinnerCube;

        public SpinnerGameObjectUpdater(Transform spinnerCube)
        {
            _spinnerCube = spinnerCube;
        }

        [ForEachEntity(MatchByComponents = true)]
        void Execute(in Rotation rotation)
        {
            _spinnerCube.rotation = rotation.Value;
        }
    }
}
