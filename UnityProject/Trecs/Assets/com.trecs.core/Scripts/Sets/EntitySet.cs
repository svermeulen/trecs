using System;

namespace Trecs
{
    /// <summary>
    /// Runtime identity for an entity set, analogous to <see cref="Tag"/> for tag types.
    /// Holds a stable ID (for serialization), the associated tag scope, and debug name.
    /// Obtained per-type via <see cref="EntitySet{T}.Value"/>, or enumerated via
    /// <see cref="WorldInfo.AllSets"/>.
    /// </summary>
    public readonly struct EntitySet : IEquatable<EntitySet>
    {
        public readonly SetId Id;

        /// <summary>
        /// The tag scope this set is restricted to — i.e. only entities whose group
        /// tag set is a superset of this <see cref="TagSet"/> can be members. Set from
        /// the type arguments to <c>IEntitySet&lt;...&gt;</c>; for a "global" set
        /// declared as <c>IEntitySet</c> with no type arguments (any entity is eligible),
        /// this is <see cref="TagSet.Null"/>.
        /// </summary>
        public readonly TagSet Tags;
        public readonly string DebugName;

        // The IEntitySet struct type the set was registered with via
        // WorldBuilder.AddSet&lt;T&gt;(). Reflection-only — runtime never
        // touches it — but useful for editor tooling that wants to show
        // FullName / Namespace alongside the debug name.
        public readonly Type SetType;

        public EntitySet(SetId id, TagSet tags, string debugName, Type setType)
        {
            Id = id;
            Tags = tags;
            DebugName = debugName;
            SetType = setType;
        }

        public override bool Equals(object obj)
        {
            return obj is EntitySet other && Equals(other);
        }

        public bool Equals(EntitySet other)
        {
            return Id == other.Id;
        }

        // Stable hash across sessions.
        public override int GetHashCode()
        {
            return Id.Id;
        }

        public override string ToString()
        {
            return DebugName;
        }

        public static bool operator ==(EntitySet a, EntitySet b)
        {
            return a.Equals(b);
        }

        public static bool operator !=(EntitySet a, EntitySet b)
        {
            return !a.Equals(b);
        }
    }

    /// <summary>
    /// Generic per-type cache for <see cref="EntitySet"/> values derived from
    /// <see cref="IEntitySet"/> struct types. Provides zero-allocation access to
    /// pre-computed identity instances.
    /// </summary>
    public static class EntitySet<T>
        where T : struct, IEntitySet
    {
        public static readonly EntitySet Value = SetFactory.CreateSet(typeof(T));
    }
}
