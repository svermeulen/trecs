using Unity.Mathematics;
using UnityEngine;

namespace Trecs.Serialization.Samples.Pointers
{
    /// <summary>
    /// Fixed-update system: walks each follower along a hard-coded figure-8
    /// path and appends the new position to its <see cref="TrailHistory"/>.
    ///
    /// Demonstrates reading and mutating a per-entity managed payload behind
    /// a <see cref="UniquePtr{T}"/>: <c>trail.Value.Get(World)</c> returns the
    /// live <see cref="TrailHistory"/> instance so we can append a Vector3
    /// to its list and trim the oldest entries.
    /// </summary>
    public partial class PatrolMovementSystem : ISystem
    {
        const float Speed = 1.5f;

        [ForEachEntity(MatchByComponents = true)]
        void Execute(ref Position position, ref PathPhase phase, in Trail trail)
        {
            phase.Value = (phase.Value + Speed * World.DeltaTime) % (2f * math.PI);
            position.Value = FigureEightAt(phase.Value);

            // Append to the per-entity managed trail. UniquePtr<TrailHistory>
            // Get() returns the same object every call — mutations stick.
            var trailHistory = trail.Value.Get(World);
            trailHistory.Positions.Add((Vector3)position.Value);

            while (trailHistory.Positions.Count > trailHistory.MaxLength)
                trailHistory.Positions.RemoveAt(0);
        }

        /// <summary>
        /// Lemniscate (figure-8 of Bernoulli) on the XZ plane, centred at the
        /// origin. Exposed so the scene initializer can place each follower
        /// at the correct initial position for its starting phase.
        /// </summary>
        public static float3 FigureEightAt(float phase)
        {
            const float size = 4f;
            return new float3(
                size * math.sin(phase),
                0.5f,
                size * math.sin(phase) * math.cos(phase)
            );
        }
    }
}
