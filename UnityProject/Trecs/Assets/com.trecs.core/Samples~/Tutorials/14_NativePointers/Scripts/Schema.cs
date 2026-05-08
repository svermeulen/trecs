using Trecs.Collections;
using Unity.Mathematics;
using UnityEngine;

namespace Trecs.Samples.NativePointers
{
    public static class NativePatrolTags
    {
        public struct Follower : ITag { }
    }

    /// <summary>
    /// Shared patrol route — allocated once per route shape, cloned to each follower.
    /// All followers of the same route share the same data via NativeSharedPtr.
    ///
    /// Every field is unmanaged so the data can live in a native (Burst-accessible)
    /// heap allocation and be read from inside a Burst job. Marked <c>readonly struct</c>
    /// both to document intent (shared routes are immutable) and to avoid the defensive
    /// copies the compiler would otherwise emit when accessing through an <c>in</c> reference.
    ///
    /// Waypoints use <see cref="FixedList64{T}"/> from Trecs.Collections — an inline,
    /// bounded list sized to 64 elements. For bigger routes, pick a larger
    /// <c>FixedListN</c> or switch to a heap-allocated container via <c>NativeBlobPtr</c>.
    /// </summary>
    public readonly struct PatrolRoute
    {
        public readonly FixedList64<float3> Waypoints;
        public readonly Color Color;
        public readonly float Speed;

        public PatrolRoute(in FixedList64<float3> waypoints, Color color, float speed)
        {
            Waypoints = waypoints;
            Color = color;
            Speed = speed;
        }
    }

    /// <summary>
    /// Per-entity trail of recent positions — each entity owns its own via NativeUniquePtr.
    ///
    /// Unlike <see cref="PatrolRoute"/>, this is a mutable struct: the movement job
    /// appends to and trims <see cref="Positions"/> each tick, so
    /// <see cref="TrailHistory"/> must expose writable fields and cannot be readonly.
    /// </summary>
    public struct TrailHistory
    {
        public FixedList64<float3> Positions;
        public int MaxLength;
    }

    /// <summary>
    /// Shared route reference and per-entity progress along it.
    /// The NativeSharedPtr handle is a 12-byte value type stored inline in the component.
    /// </summary>
    public partial struct Route : IEntityComponent
    {
        public NativeSharedPtr<PatrolRoute> Value;
        public float Progress;
    }

    /// <summary>
    /// Per-entity trail history reference.
    /// The NativeUniquePtr handle is a 4-byte value type stored inline in the component.
    /// </summary>
    public partial struct Trail : IEntityComponent
    {
        public NativeUniquePtr<TrailHistory> Value;
    }

    public static partial class SampleTemplates
    {
        public partial class NativePatrolFollowerEntity
            : ITemplate,
                ITagged<NativePatrolTags.Follower>
        {
            Position Position;
            Route Route;
            Trail Trail;
            GameObjectId GameObjectId;
        }
    }
}
