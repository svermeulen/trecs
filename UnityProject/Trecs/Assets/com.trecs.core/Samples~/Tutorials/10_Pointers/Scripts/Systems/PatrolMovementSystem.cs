using Unity.Mathematics;
using UnityEngine;

namespace Trecs.Samples.Pointers
{
    /// <summary>
    /// Fixed-update system that moves entities along shared patrol routes
    /// and records positions into each entity's unique trail history.
    ///
    /// Demonstrates reading both pointer types:
    /// - SharedPtr&lt;PatrolRoute&gt;: waypoints and speed, shared across the route
    /// - UniquePtr&lt;TrailHistory&gt;: per-entity mutable position history
    /// </summary>
    public partial class PatrolMovementSystem : ISystem
    {
        [ForEachEntity(MatchByComponents = true)]
        void Execute(ref Position position, in Route route, in Trail trail)
        {
            // Read shared route — same waypoint list for all followers of this route
            var patrolRoute = route.Value.Get(World);
            var waypoints = patrolRoute.Waypoints;

            // Compute position from elapsed time + initial offset
            float totalProgress = route.Progress + World.ElapsedTime * patrolRoute.Speed;
            float wrappedProgress = totalProgress % waypoints.Count;

            int indexA = (int)wrappedProgress;
            int indexB = (indexA + 1) % waypoints.Count;
            float t = wrappedProgress - indexA;

            position.Value = math.lerp((float3)waypoints[indexA], (float3)waypoints[indexB], t);

            // Record position in unique trail — each entity has its own list
            var trailHistory = trail.Value.Get(World);
            trailHistory.Positions.Add((Vector3)position.Value);

            while (trailHistory.Positions.Count > trailHistory.MaxLength)
                trailHistory.Positions.RemoveAt(0);
        }
    }
}
