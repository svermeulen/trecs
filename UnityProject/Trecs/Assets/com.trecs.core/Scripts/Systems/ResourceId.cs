using System;
using System.Runtime.CompilerServices;

namespace Trecs.Internal
{
    /// <summary>
    /// Identifies a trackable resource in the job scheduler — either a component type or a set.
    /// Used as part of a (ResourceId, Group) composite key for dependency tracking.
    /// </summary>
    public readonly struct ResourceId : IEquatable<ResourceId>
    {
        readonly int _value;

        ResourceId(int value)
        {
            _value = value;
        }

        /// <summary>
        /// Create a ResourceId for a component type.
        /// Bit 31 is masked off to ensure components occupy the non-negative half of the key space.
        /// Two component types that differ only in bit 31 will share a scheduler slot (slightly
        /// conservative, but harmless — the scheduler just syncs them together).
        /// </summary>
        [MethodImpl(MethodImplOptions.AggressiveInlining)]
        public static ResourceId Component(ComponentId id) => new(id.Value & 0x7FFFFFFF);

        /// <summary>
        /// Create a ResourceId for a set.
        /// Bit 31 is forced on to ensure sets occupy the negative half of the key space,
        /// guaranteeing no collision with component ResourceIds.
        /// </summary>
        [MethodImpl(MethodImplOptions.AggressiveInlining)]
        public static ResourceId Set(SetId id) => new(id.Id | unchecked((int)0x80000000));

        public int Value
        {
            [MethodImpl(MethodImplOptions.AggressiveInlining)]
            get => _value;
        }

        [MethodImpl(MethodImplOptions.AggressiveInlining)]
        public bool Equals(ResourceId other) => _value == other._value;

        public override bool Equals(object obj) => obj is ResourceId other && Equals(other);

        public override int GetHashCode() => _value;

        [MethodImpl(MethodImplOptions.AggressiveInlining)]
        public static bool operator ==(ResourceId a, ResourceId b) => a._value == b._value;

        [MethodImpl(MethodImplOptions.AggressiveInlining)]
        public static bool operator !=(ResourceId a, ResourceId b) => a._value != b._value;
    }
}
