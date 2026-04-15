using System;
using System.ComponentModel;
using System.Runtime.CompilerServices;

namespace Trecs.Internal
{
    [EditorBrowsable(EditorBrowsableState.Never)]
    [TypeId(468195302)]
    public readonly struct ComponentId : IEquatable<ComponentId>, IComparable<ComponentId>
    {
        public readonly int Value;

        public ComponentId(int id)
        {
            Value = id;
        }

        public bool Equals(ComponentId other)
        {
            return Value == other.Value;
        }

        public override bool Equals(object obj)
        {
            return obj is ComponentId other && Equals(other);
        }

        public override int GetHashCode()
        {
            return Value;
        }

        [MethodImpl(MethodImplOptions.AggressiveInlining)]
        public static bool operator ==(ComponentId c1, ComponentId c2)
        {
            return c1.Equals(c2);
        }

        [MethodImpl(MethodImplOptions.AggressiveInlining)]
        public static bool operator !=(ComponentId c1, ComponentId c2)
        {
            return !c1.Equals(c2);
        }

        public int CompareTo(ComponentId other)
        {
            return Value.CompareTo(other.Value);
        }
    }
}
