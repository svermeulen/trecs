using Unity.Mathematics;

namespace Trecs.Samples.NativePointers
{
    /// <summary>
    /// Fixed-update system that moves entities along shared patrol routes
    /// and appends positions to each entity's trail. Runs as a Burst job
    /// via <c>[WrapAsJob]</c> — the pointer resolution happens inside
    /// Burst-compiled code, which is the reason to use the native pointer
    /// variants over the managed ones from Sample 10.
    ///
    /// Demonstrates reading both native pointer types inside a job:
    /// - NativeSharedPtr&lt;TRoute&gt;: waypoints + speed, shared across followers
    /// - NativeUniquePtr&lt;TTrail&gt;: per-entity mutable position history
    /// </summary>
    public partial class PatrolMovementSystem : ISystem
    {
        [ForEachEntity(Tag = typeof(NativePatrolTags.Follower))]
        [WrapAsJob]
        static void Execute(
            ref Position position,
            in CNativeRoute route,
            ref CNativeTrail trail,
            in NativeWorldAccessor world
        )
        {
            // Resolve the shared route inside the job via NativeWorldAccessor —
            // this is the whole point of NativeSharedPtr: it is Burst-safe.
            ref readonly var patrolRoute = ref route.Value.Get(world);
            ref readonly var waypoints = ref patrolRoute.Waypoints;

            float totalProgress = route.Progress + world.ElapsedTime * patrolRoute.Speed;
            float wrappedProgress = totalProgress % waypoints.Length;

            int indexA = (int)wrappedProgress;
            int indexB = (indexA + 1) % waypoints.Length;
            float t = wrappedProgress - indexA;

            position.Value = math.lerp(waypoints[indexA], waypoints[indexB], t);

            // Mutate the per-entity trail. GetMut is an extension method on
            // NativeUniquePtr that requires `ref this` — so the component
            // holding the pointer must itself be accessed by ref.
            ref var trailData = ref trail.Value.GetMut(world);
            if (trailData.Positions.Length >= trailData.MaxLength)
            {
                trailData.Positions.RemoveAt(0);
            }
            trailData.Positions.Add(position.Value);
        }
    }
}
