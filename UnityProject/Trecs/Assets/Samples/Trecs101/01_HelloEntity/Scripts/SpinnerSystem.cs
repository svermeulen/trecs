using Unity.Mathematics;

namespace Trecs.Samples.HelloEntity
{
    public partial class SpinnerSystem : ISystem
    {
        readonly float _rotationSpeed;

        public SpinnerSystem(float rotationSpeed)
        {
            _rotationSpeed = rotationSpeed;
        }

        [ForEachEntity(MatchByComponents = true)]
        void Execute(ref Rotation rotation)
        {
            float angle = World.FixedDeltaTime * _rotationSpeed;
            rotation.Value = math.mul(rotation.Value, quaternion.RotateY(angle));
        }
    }
}
