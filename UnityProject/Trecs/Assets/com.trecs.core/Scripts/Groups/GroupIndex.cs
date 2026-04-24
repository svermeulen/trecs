using System;
using System.Runtime.CompilerServices;

namespace Trecs
{
    /// <summary>
    /// A compact, runtime-only handle for a group. Unlike <see cref="TagSet"/>
    /// (stable across runs, hash-identified), <c>GroupIndex</c> is a sequential
    /// <see cref="ushort"/> assigned at world-build time and is safe to use as a
    /// direct array index on the ECS hot path.
    /// <para>
    /// Real groups are numbered <c>0..N-1</c>. There is no null sentinel — use
    /// <c>GroupIndex?</c> when nullability is required.
    /// </para>
    /// <para>
    /// Obtain a <c>GroupIndex</c> from <see cref="WorldInfo"/> query methods or
    /// from a group slice. The constructor is internal; user code cannot mint
    /// these directly.
    /// </para>
    /// </summary>
    public readonly struct GroupIndex : IEquatable<GroupIndex>, IComparable<GroupIndex>
    {
        public readonly ushort Value;

        internal GroupIndex(ushort value)
        {
            Value = value;
        }

        [MethodImpl(MethodImplOptions.AggressiveInlining)]
        public readonly bool Equals(GroupIndex other) => Value == other.Value;

        [MethodImpl(MethodImplOptions.AggressiveInlining)]
        public override readonly bool Equals(object obj) =>
            obj is GroupIndex other && Equals(other);

        [MethodImpl(MethodImplOptions.AggressiveInlining)]
        public override readonly int GetHashCode() => Value;

        [MethodImpl(MethodImplOptions.AggressiveInlining)]
        public readonly int CompareTo(GroupIndex other) => Value.CompareTo(other.Value);

        [MethodImpl(MethodImplOptions.AggressiveInlining)]
        public static bool operator ==(GroupIndex a, GroupIndex b) => a.Value == b.Value;

        [MethodImpl(MethodImplOptions.AggressiveInlining)]
        public static bool operator !=(GroupIndex a, GroupIndex b) => a.Value != b.Value;

        public override readonly string ToString() => $"GroupIndex({Value})";
    }
}
