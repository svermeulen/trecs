using Unity.Mathematics;

namespace Trecs.Samples.Interpolation
{
    /// <summary>
    /// Fixed-update system: moves all entities (smooth AND raw) in circles.
    ///
    /// Both entity types receive identical physics updates. The visual
    /// difference comes entirely from how the renderer reads position data:
    /// interpolated (smooth) vs direct (raw).
    /// </summary>
    public partial class OrbitSystem : ISystem
    {
        [ForEachEntity(MatchByComponents = true)]
        void Execute(ref Position position, in OrbitParams orbit)
        {
            float angle = orbit.Phase + World.ElapsedTime * orbit.Speed;
            position.Value = new float3(
                orbit.CenterX + math.cos(angle) * orbit.Radius,
                0.5f,
                math.sin(angle) * orbit.Radius
            );
        }
    }
}
