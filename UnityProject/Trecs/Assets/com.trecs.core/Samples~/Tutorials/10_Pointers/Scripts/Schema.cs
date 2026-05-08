using System.Collections.Generic;
using UnityEngine;

namespace Trecs.Samples.Pointers
{
    public static class PatrolTags
    {
        public struct Follower : ITag { }
    }

    /// <summary>
    /// Shared patrol route — allocated once per route shape, cloned to each follower.
    /// All followers of the same route share the same object via SharedPtr.
    ///
    /// This data MUST live on the heap because it contains List&lt;Vector3&gt;,
    /// a managed collection that cannot be stored in an unmanaged struct component.
    /// SharedPtr is the right choice because the route is immutable and shared:
    /// duplicating 20+ waypoints per entity would waste memory.
    /// </summary>
    public class PatrolRoute
    {
        public List<Vector3> Waypoints;
        public Color Color;
        public float Speed;
    }

    /// <summary>
    /// Per-entity trail of recent positions — each entity owns its own via UniquePtr.
    ///
    /// This data MUST live on the heap because it contains List&lt;Vector3&gt;,
    /// a managed collection that cannot be stored in an unmanaged struct component.
    /// UniquePtr is the right choice because each entity accumulates its own
    /// trail independently, and the list grows/shrinks dynamically.
    /// </summary>
    public class TrailHistory
    {
        public List<Vector3> Positions = new();
        public int MaxLength;
    }

    /// <summary>
    /// Shared route reference and per-entity progress along it.
    /// The SharedPtr handle is a 12-byte value type stored inline in the component.
    /// </summary>
    public partial struct Route : IEntityComponent
    {
        public SharedPtr<PatrolRoute> Value;
        public float Progress;
    }

    /// <summary>
    /// Per-entity trail history reference.
    /// The UniquePtr handle is a 4-byte value type stored inline in the component.
    /// </summary>
    public partial struct Trail : IEntityComponent
    {
        public UniquePtr<TrailHistory> Value;
    }

    public static partial class SampleTemplates
    {
        public partial class PatrolFollowerEntity : ITemplate, ITagged<PatrolTags.Follower>
        {
            Position Position;
            Route Route;
            Trail Trail;
            GameObjectId GameObjectId;
        }
    }
}
