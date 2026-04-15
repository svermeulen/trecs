using UnityEngine;

namespace Trecs.Samples.Pointers
{
    public static class TeamTags
    {
        public struct Member : ITag { }
    }

    /// <summary>
    /// Shared team configuration — allocated once, cloned to each team member.
    /// All members of the same team share the same object via SharedPtr.
    /// Must be a class (reference type) for SharedPtr.
    /// </summary>
    public class TeamConfig
    {
        public Color Color;
        public float OrbitSpeed;
        public float OrbitRadius;
        public float CenterX;
    }

    /// <summary>
    /// Per-entity mutable state — each entity owns its own instance via UniquePtr.
    /// Must be a class (reference type) for UniquePtr.
    /// </summary>
    public class EntityState
    {
        public int FrameCount;
    }

    /// <summary>
    /// Component that holds pointers to heap data.
    /// SharedPtr and UniquePtr are value-type structs (just handles/IDs),
    /// so this component is fully unmanaged and ECS-compatible.
    /// </summary>
    public partial struct TeamMember : IEntityComponent
    {
        /// <summary>
        /// Shared config: multiple entities hold clones of the same SharedPtr,
        /// all pointing to the same TeamConfig object. Reference-counted.
        /// </summary>
        public SharedPtr<TeamConfig> Config;

        /// <summary>
        /// Unique state: each entity has its own EntityState object.
        /// Exclusive ownership, directly mutable.
        /// </summary>
        public UniquePtr<EntityState> State;

        /// <summary>Orbit phase offset (radians).</summary>
        public float Phase;
    }

    public static partial class SampleTemplates
    {
        public partial class TeamMemberEntity : ITemplate, IHasTags<TeamTags.Member>
        {
            public Position Position;
            public TeamMember TeamMember;
            public GameObjectId GameObjectId;
        }
    }
}
