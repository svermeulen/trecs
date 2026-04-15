using System;
using Trecs.Internal;

namespace Trecs
{
    /// <summary>
    /// Runtime identity for a set, analogous to Tag for tag types.
    /// Holds a stable ID (for serialization), the associated tag scope, and debug name.
    /// </summary>
    public readonly struct SetDef : IEquatable<SetDef>, IStableHashProvider
    {
        public readonly SetId Id;
        public readonly TagSet Tags;
        public readonly string DebugName;

        public SetDef(SetId id, TagSet tags, string debugName)
        {
            Id = id;
            Tags = tags;
            DebugName = debugName;
        }

        public override bool Equals(object obj)
        {
            return obj is SetDef other && Equals(other);
        }

        public bool Equals(SetDef other)
        {
            return Id == other.Id;
        }

        public override int GetHashCode()
        {
            return GetStableHashCode();
        }

        public int GetStableHashCode()
        {
            return Id.Id;
        }

        public override string ToString()
        {
            return DebugName;
        }

        public static bool operator ==(SetDef a, SetDef b)
        {
            return a.Equals(b);
        }

        public static bool operator !=(SetDef a, SetDef b)
        {
            return !a.Equals(b);
        }
    }
}
