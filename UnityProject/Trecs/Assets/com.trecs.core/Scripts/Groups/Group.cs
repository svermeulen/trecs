using System;
using System.Runtime.CompilerServices;
using Trecs.Internal;

namespace Trecs
{
    /// <summary>
    /// Identifies a contiguous storage bucket for entities that share the same <see cref="TagSet"/>.
    /// All entities in a group have identical component layout, enabling cache-friendly iteration.
    /// Groups are created implicitly when entity templates are registered via <see cref="WorldBuilder"/>.
    /// Use <see cref="WorldInfo"/> to query which groups exist and what tags/components they contain.
    /// </summary>
    [TypeId(731604285)]
    public readonly struct Group : IEquatable<Group>, IComparable<Group>, IStableHashProvider
    {
        public readonly int Id;

        /// <summary>
        /// Sentinel value representing no group. <see cref="IsNull"/> returns <c>true</c> for this value.
        /// </summary>
        public static readonly Group Null;

        public Group(int id)
            : this()
        {
            Id = id;
        }

        public readonly bool IsNull => this == Null;

        [MethodImpl(MethodImplOptions.AggressiveInlining)]
        public override readonly int GetHashCode()
        {
            return GetStableHashCode();
        }

        public readonly int GetStableHashCode()
        {
            return Id;
        }

        [MethodImpl(MethodImplOptions.AggressiveInlining)]
        public static bool operator ==(Group c1, Group c2)
        {
            return c1.Equals(c2);
        }

        [MethodImpl(MethodImplOptions.AggressiveInlining)]
        public static bool operator !=(Group c1, Group c2)
        {
            return !c1.Equals(c2);
        }

        [MethodImpl(MethodImplOptions.AggressiveInlining)]
        public override readonly bool Equals(object obj)
        {
            return obj is Group other && Equals(other);
        }

        [MethodImpl(MethodImplOptions.AggressiveInlining)]
        public readonly bool Equals(Group other)
        {
            return other.Id == Id;
        }

        [MethodImpl(MethodImplOptions.AggressiveInlining)]
        public readonly int CompareTo(Group other)
        {
            return other.Id.CompareTo(Id);
        }

        /// <summary>
        /// Returns the <see cref="TagSet"/> that defines this group's tag combination.
        /// </summary>
        public readonly TagSet AsTagSet()
        {
            return new TagSet(Id);
        }

        public override readonly string ToString()
        {
            return TagSetRegistry.TagSetToString(AsTagSet());
        }
    }
}
