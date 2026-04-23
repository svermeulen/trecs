using Unity.Collections;
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
    /// Unlike Sample 10's managed PatrolRoute, every field here is unmanaged so the
    /// data can live in a native (Burst-accessible) heap allocation and be read from
    /// inside a Burst job. Marked <c>readonly struct</c> both to document intent
    /// (shared routes are immutable) and to avoid the defensive copies the compiler
    /// would otherwise emit when the struct is accessed through an <c>in</c> reference.
    ///
    /// Waypoints use <see cref="FixedList512Bytes{T}"/>, which stores elements inline
    /// and caps the list at roughly 500 bytes of payload (≈ 42 float3 entries). Routes
    /// bigger than that need a different container — <c>NativeArray</c> via
    /// <c>NativeBlobPtr</c>, for example.
    /// </summary>
    public readonly struct TRoute
    {
        public readonly FixedList512Bytes<float3> Waypoints;
        public readonly Color Color;
        public readonly float Speed;

        public TRoute(in FixedList512Bytes<float3> waypoints, Color color, float speed)
        {
            Waypoints = waypoints;
            Color = color;
            Speed = speed;
        }
    }

    /// <summary>
    /// Per-entity trail of recent positions — each entity owns its own via NativeUniquePtr.
    ///
    /// Unmanaged so a Burst job can mutate it via <c>GetMut</c>. Unlike <see cref="TRoute"/>,
    /// this is a mutable struct: the movement job appends to and trims <see cref="Positions"/>
    /// each tick. The same <see cref="FixedList512Bytes{T}"/> capacity cap applies.
    /// </summary>
    public struct TTrail
    {
        public FixedList512Bytes<float3> Positions;
        public int MaxLength;
    }

    /// <summary>
    /// Shared route reference and per-entity progress along it.
    /// The NativeSharedPtr handle is a 12-byte value type stored inline in the component.
    /// </summary>
    public partial struct CNativeRoute : IEntityComponent
    {
        public NativeSharedPtr<TRoute> Value;
        public float Progress;
    }

    /// <summary>
    /// Per-entity trail history reference.
    /// The NativeUniquePtr handle is a 4-byte value type stored inline in the component.
    /// </summary>
    public partial struct CNativeTrail : IEntityComponent
    {
        public NativeUniquePtr<TTrail> Value;
    }

    public static partial class SampleTemplates
    {
        public partial class NativePatrolFollowerEntity
            : ITemplate,
                IHasTags<NativePatrolTags.Follower>
        {
            public Position Position;
            public CNativeRoute Route;
            public CNativeTrail Trail;
            public GameObjectId GameObjectId;
        }
    }
}
