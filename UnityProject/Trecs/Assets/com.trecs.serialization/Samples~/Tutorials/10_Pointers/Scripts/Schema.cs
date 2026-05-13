using System.Collections.Generic;
using UnityEngine;

namespace Trecs.Serialization.Samples.Pointers
{
    public static class PatrolTags
    {
        public struct Follower : ITag { }
    }

    /// <summary>
    /// Per-entity trail of recent positions — each entity owns its own via
    /// UniquePtr.
    ///
    /// This data MUST live on the heap because it contains
    /// <see cref="List{T}"/>, a managed collection that cannot be stored in
    /// an unmanaged struct component. <see cref="UniquePtr{T}"/> is the right
    /// choice because each entity accumulates its own trail independently,
    /// and the list grows and shrinks dynamically.
    /// </summary>
    public class TrailHistory
    {
        public List<Vector3> Positions = new();
        public int MaxLength;
    }

    /// <summary>
    /// Per-entity trail history reference.
    /// The <see cref="UniquePtr{T}"/> handle is a 4-byte value type stored
    /// inline in the component.
    /// </summary>
    public partial struct Trail : IEntityComponent
    {
        public UniquePtr<TrailHistory> Value;
    }

    /// <summary>
    /// How far along the figure-8 this follower currently is, in radians.
    /// Wraps every 2π. Each follower starts at a different phase so they
    /// fan out along the curve instead of clumping at one point.
    /// </summary>
    [Unwrap]
    public partial struct PathPhase : IEntityComponent
    {
        public float Value;
    }

    public static partial class SampleTemplates
    {
        public partial class PatrolFollowerEntity
            : ITemplate,
                IExtends<CommonTemplates.RenderableGameObject>,
                ITagged<PatrolTags.Follower>
        {
            Position Position;
            PathPhase PathPhase;
            Trail Trail;
            PrefabId PrefabId = new(PointersPrefabs.Follower);
        }
    }
}
