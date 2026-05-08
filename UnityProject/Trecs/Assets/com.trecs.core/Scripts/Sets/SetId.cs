using System;
using Trecs.Internal;

namespace Trecs
{
    /// <summary>
    /// Stable integer identifier for an entity set definition. Corresponds to a
    /// <see cref="EntitySet"/> registered via <see cref="WorldBuilder.AddSet{T}"/>.
    /// </summary>
    [TypeId(364820517)]
    public readonly struct SetId
        : IEquatable<SetId>,
            IComparable<SetId>,
            IComparable,
            IStableHashProvider
    {
        public readonly int Id;

        public SetId(int id)
        {
            Id = id;
        }

        public bool Equals(SetId other) => Id == other.Id;

        public override bool Equals(object obj) => obj is SetId other && Equals(other);

        public override int GetHashCode() => Id;

        public int GetStableHashCode() => Id;

        public int CompareTo(SetId other) => Id.CompareTo(other.Id);

        public int CompareTo(object obj)
        {
            if (obj is SetId other)
                return CompareTo(other);
            throw new ArgumentException("Comparing SetId with the wrong type");
        }

        public override string ToString() => Id.ToString();

        public static bool operator ==(SetId a, SetId b) => a.Id == b.Id;

        public static bool operator !=(SetId a, SetId b) => a.Id != b.Id;
    }
}
