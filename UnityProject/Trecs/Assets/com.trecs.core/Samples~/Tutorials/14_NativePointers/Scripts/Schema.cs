using Unity.Collections;
using Unity.Mathematics;

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
    /// Unlike Sample 10's managed PatrolRoute, every field here is unmanaged so that
    /// the data can live in a native (Burst-accessible) heap allocation and be read
    /// from inside a Burst job. Waypoints use FixedList512Bytes to stay inline.
    /// </summary>
    public struct TRoute
    {
        public FixedList512Bytes<float3> Waypoints;
        public float4 Color;
        public float Speed;
    }

    /// <summary>
    /// Per-entity trail of recent positions — each entity owns its own via NativeUniquePtr.
    ///
    /// Unmanaged so a Burst job can mutate the list via <c>GetMut</c>. FixedList512Bytes
    /// caps the trail length at ~42 float3 entries, which is plenty for visualization.
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
