using System;
using System.Collections.Generic;
using System.Runtime.CompilerServices;
using Trecs.Internal;

namespace Trecs
{
    public readonly struct TagSet : IEquatable<TagSet>, IComparable<TagSet>, IStableHashProvider
    {
        public readonly int Id;

        public static readonly TagSet Null; //must stay here because of Burst

        public TagSet(int id)
            : this()
        {
            Id = id;
        }

        public readonly IReadOnlyList<Tag> Tags
        {
            get { return TagSetRegistry.TagSetToTags(this); }
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
        public static bool operator ==(TagSet c1, TagSet c2)
        {
            return c1.Equals(c2);
        }

        [MethodImpl(MethodImplOptions.AggressiveInlining)]
        public static bool operator !=(TagSet c1, TagSet c2)
        {
            return !c1.Equals(c2);
        }

        [MethodImpl(MethodImplOptions.AggressiveInlining)]
        public override readonly bool Equals(object obj)
        {
            return obj is TagSet other && Equals(other);
        }

        [MethodImpl(MethodImplOptions.AggressiveInlining)]
        public readonly bool Equals(TagSet other)
        {
            return other.Id == Id;
        }

        [MethodImpl(MethodImplOptions.AggressiveInlining)]
        public readonly int CompareTo(TagSet other)
        {
            return other.Id.CompareTo(Id);
        }

        public override readonly string ToString()
        {
            return TagSetRegistry.TagSetToString(this);
        }

        public static TagSet FromTags(IReadOnlyList<Tag> tags)
        {
            return TagSetRegistry.TagsToTagSet(tags);
        }

        public static TagSet FromTags(Tag t1)
        {
            return TagSetRegistry.TagsToTagSet(t1);
        }

        public static TagSet FromTags(Tag t1, Tag t2)
        {
            return TagSetRegistry.TagsToTagSet(t1, t2);
        }

        public static TagSet FromTags(Tag t1, Tag t2, Tag t3)
        {
            return TagSetRegistry.TagsToTagSet(t1, t2, t3);
        }

        public static TagSet FromTags(Tag t1, Tag t2, Tag t3, Tag t4)
        {
            return TagSetRegistry.TagsToTagSet(t1, t2, t3, t4);
        }

        public readonly TagSet CombineWith(TagSet other)
        {
            if (other.IsNull)
                return this;
            if (IsNull)
                return other;
            if (this == other)
                return this;
            return TagSetRegistry.CombineTagSets(this, other);
        }

        public static implicit operator TagSet(Tag tag)
        {
            return FromTags(tag);
        }
    }

    public class TagSet<T1>
        where T1 : struct, ITag
    {
        public static readonly TagSet Value = TagSet.FromTags(Tag<T1>.Value);

        public static void Warmup() { }
    }

    public class TagSet<T1, T2>
        where T1 : struct, ITag
        where T2 : struct, ITag
    {
        public static readonly TagSet Value = TagSet.FromTags(Tag<T1>.Value, Tag<T2>.Value);

        public static void Warmup() { }

        TagSet() { }
    }

    public class TagSet<T1, T2, T3>
        where T1 : struct, ITag
        where T2 : struct, ITag
        where T3 : struct, ITag
    {
        public static readonly TagSet Value = TagSet.FromTags(
            Tag<T1>.Value,
            Tag<T2>.Value,
            Tag<T3>.Value
        );

        public static void Warmup() { }

        TagSet() { }
    }

    public class TagSet<T1, T2, T3, T4>
        where T1 : struct, ITag
        where T2 : struct, ITag
        where T3 : struct, ITag
        where T4 : struct, ITag
    {
        public static readonly TagSet Value = TagSet.FromTags(
            Tag<T1>.Value,
            Tag<T2>.Value,
            Tag<T3>.Value,
            Tag<T4>.Value
        );

        public static void Warmup() { }

        TagSet() { }
    }
}
