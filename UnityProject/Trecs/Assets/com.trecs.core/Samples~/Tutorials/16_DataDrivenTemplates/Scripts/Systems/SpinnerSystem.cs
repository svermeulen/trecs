using Unity.Mathematics;

namespace Trecs.Samples.DataDrivenTemplates
{
    /// <summary>
    /// Rotates any entity that declares a <see cref="Rotation"/> component.
    /// Iteration is driven by component presence, not by template identity, so
    /// this system works on data-driven templates the same way it would on
    /// source-generated ones.
    /// </summary>
    public partial class SpinnerSystem : ISystem
    {
        [ForEachEntity(MatchByComponents = true)]
        void Execute(ref Rotation rotation)
        {
            float angle = World.DeltaTime * 1.5f;
            rotation.Value = math.mul(rotation.Value, quaternion.RotateY(angle));
        }
    }
}
