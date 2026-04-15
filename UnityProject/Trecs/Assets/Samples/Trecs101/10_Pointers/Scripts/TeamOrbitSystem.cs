using Unity.Mathematics;

namespace Trecs.Samples.Pointers
{
    /// <summary>
    /// Fixed-update system that reads pointer data each frame:
    /// - SharedPtr&lt;TeamConfig&gt;: orbit parameters shared across the team
    /// - UniquePtr&lt;EntityState&gt;: per-entity mutable frame counter
    ///
    /// Note: pointer reads (.Get) resolve through the heap and are
    /// slightly more expensive than direct component reads. Use pointers
    /// for data that doesn't fit in unmanaged components (class objects,
    /// collections, large shared configs).
    /// </summary>
    public partial class TeamOrbitSystem : ISystem
    {
        [ForEachEntity(MatchByComponents = true)]
        void Execute(ref Position position, in TeamMember member)
        {
            // ─── Read SharedPtr: same config object for all team members ──
            var config = member.Config.Get(World);

            // ─── Read + mutate UniquePtr: exclusive per-entity state ──────
            var state = member.State.Get(World);
            state.FrameCount++;

            // Compute orbit position from shared config
            float angle = member.Phase + World.ElapsedTime * config.OrbitSpeed;
            position.Value = new float3(
                config.CenterX + math.cos(angle) * config.OrbitRadius,
                0.5f,
                math.sin(angle) * config.OrbitRadius
            );
        }
    }
}
