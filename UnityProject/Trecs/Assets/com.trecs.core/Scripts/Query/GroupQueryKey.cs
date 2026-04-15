using System;

namespace Trecs
{
    internal readonly struct GroupQueryKey : IEquatable<GroupQueryKey>
    {
        public readonly TagSet PositiveTags;
        public readonly TagSet NegativeTags;
        public readonly ComponentTypeIdSet PositiveComponents;
        public readonly ComponentTypeIdSet NegativeComponents;

        public GroupQueryKey(
            TagSet positiveTags,
            TagSet negativeTags = default,
            ComponentTypeIdSet positiveComponents = default,
            ComponentTypeIdSet negativeComponents = default
        )
        {
            PositiveTags = positiveTags;
            NegativeTags = negativeTags;
            PositiveComponents = positiveComponents;
            NegativeComponents = negativeComponents;
        }

        public bool HasNegativeTags => !NegativeTags.IsNull;
        public bool HasPositiveComponents => !PositiveComponents.IsNull;
        public bool HasNegativeComponents => !NegativeComponents.IsNull;

        public bool Equals(GroupQueryKey other)
        {
            return PositiveTags == other.PositiveTags
                && NegativeTags == other.NegativeTags
                && PositiveComponents == other.PositiveComponents
                && NegativeComponents == other.NegativeComponents;
        }

        public override bool Equals(object obj)
        {
            return obj is GroupQueryKey other && Equals(other);
        }

        public override int GetHashCode()
        {
            unchecked
            {
                int hash = PositiveTags.GetHashCode();
                hash = hash * 397 ^ NegativeTags.GetHashCode();
                hash = hash * 397 ^ PositiveComponents.GetHashCode();
                hash = hash * 397 ^ NegativeComponents.GetHashCode();
                return hash;
            }
        }

        public static bool operator ==(GroupQueryKey left, GroupQueryKey right) =>
            left.Equals(right);

        public static bool operator !=(GroupQueryKey left, GroupQueryKey right) =>
            !left.Equals(right);
    }
}
