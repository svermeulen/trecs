using Unity.Mathematics;

namespace Trecs.Samples.DataDrivenTemplates
{
    /// <summary>
    /// Moves entities in a horizontal circle. Matches any template that declares
    /// both <see cref="Position"/> and <see cref="OrbitParams"/> — the system
    /// needs no knowledge of which template those components came from.
    /// </summary>
    public partial class OrbiterSystem : ISystem
    {
        [ForEachEntity(MatchByComponents = true)]
        void Execute(ref Position position, in OrbitParams orbit)
        {
            float t = World.ElapsedTime * orbit.Speed + orbit.Phase;
            position.Value = new float3(math.cos(t) * orbit.Radius, 0f, math.sin(t) * orbit.Radius);
        }
    }
}
