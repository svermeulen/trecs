using Trecs.Collections;
using Unity.Mathematics;

namespace Trecs.Samples.NativePointers
{
    /// <summary>
    /// Fixed-update system: walks each follower along a hard-coded figure-8
    /// path and appends the new position to its per-entity
    /// <see cref="TrailHistory"/>. Runs as a Burst job via
    /// <c>[WrapAsJob]</c> — the pointer resolution and mutation happen
    /// inside Burst-compiled code, which is the whole reason to use
    /// <see cref="NativeUniquePtr{T}"/> over the managed
    /// <c>UniquePtr&lt;T&gt;</c> from Sample 10.
    /// </summary>
    public partial class PatrolMovementSystem : ISystem
    {
        const float Speed = 1.5f;

        [ForEachEntity(typeof(NativePatrolTags.Follower))]
        [WrapAsJob]
        static void Execute(
            ref Position position,
            ref PathPhase phase,
            ref Trail trail,
            in NativeWorldAccessor world
        )
        {
            phase.Value = (phase.Value + Speed * world.DeltaTime) % (2f * math.PI);
            position.Value = FigureEightAt(phase.Value);

            // Mutate the per-entity trail. The Write wrapper carries a per-allocation
            // AtomicSafetyHandle so Unity's job-safety walker can detect cross-job
            // read/write conflicts on the same blob at schedule time.
            ref var trailData = ref trail.Value.Write(world.UniquePtrResolver).Value;
            if (trailData.Positions.Count >= trailData.MaxLength)
                trailData.Positions.RemoveAt(0);
            trailData.Positions.Add(position.Value);
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
