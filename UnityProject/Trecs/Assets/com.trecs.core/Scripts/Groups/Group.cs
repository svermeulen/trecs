using System;
using System.Runtime.CompilerServices;
using Trecs.Internal;

namespace Trecs
{
    [TypeId(731604285)]
    public readonly struct Group : IEquatable<Group>, IComparable<Group>, IStableHashProvider
    {
        public readonly int Id;

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
