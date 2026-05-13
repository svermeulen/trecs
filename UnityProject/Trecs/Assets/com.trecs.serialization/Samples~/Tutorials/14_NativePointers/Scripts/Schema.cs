using Trecs.Collections;
using Unity.Mathematics;

namespace Trecs.Serialization.Samples.NativePointers
{
    public static class NativePatrolTags
    {
        public struct Follower : ITag { }
    }

    /// <summary>
    /// Per-entity trail of recent positions — each entity owns its own via
    /// <see cref="NativeUniquePtr{T}"/>.
    ///
    /// Every field is unmanaged so the blob can live in Trecs' native heap
    /// and be read or mutated from inside a Burst-compiled job.
    /// <see cref="Positions"/> uses <see cref="FixedList64{T}"/> from
    /// Trecs.Collections — an inline, bounded list sized to 64 elements; for
    /// longer trails pick a larger <c>FixedListN</c> or switch to a
    /// heap-allocated container via <c>NativeBlobPtr</c>.
    /// </summary>
    public struct TrailHistory
    {
        public FixedList64<float3> Positions;
        public int MaxLength;
    }

    /// <summary>
    /// Per-entity trail history reference.
    /// The <see cref="NativeUniquePtr{T}"/> handle is a 4-byte value type
    /// stored inline in the component.
    /// </summary>
    public partial struct Trail : IEntityComponent
    {
        public NativeUniquePtr<TrailHistory> Value;
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
        public partial class NativePatrolFollowerEntity
            : ITemplate,
                IExtends<CommonTemplates.RenderableGameObject>,
                ITagged<NativePatrolTags.Follower>
        {
            Position Position;
            PathPhase PathPhase;
            Trail Trail;
            PrefabId PrefabId = new(NativePointersPrefabs.Follower);
        }
    }
}
