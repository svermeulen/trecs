using System;
using System.Runtime.CompilerServices;

namespace Trecs
{
    /// <summary>
    /// A compact, runtime-only handle for a group. Unlike <see cref="TagSet"/>
    /// (stable across runs, hash-identified), <c>GroupIndex</c> is a sequential
    /// handle assigned at world-build time and is safe to use as a direct array
    /// index on the ECS hot path via <see cref="Index"/>.
    /// <para>
    /// Storage is 1-based: raw value 0 is the <see cref="Null"/> sentinel, real
    /// groups are 1..N. This makes <c>default(GroupIndex) == Null</c>, matching
    /// the convention used by <c>EntityHandle</c>, <c>TagSet</c>, etc., so
    /// uninitialized struct fields and cache-miss defaults don't silently
    /// collide with a real group.
    /// </para>
    /// <para>
    /// Obtain a <c>GroupIndex</c> from <see cref="WorldInfo"/> query methods or
    /// from a group slice. The constructor is internal; user code cannot mint
    /// these directly.
    /// </para>
    /// </summary>
    public readonly struct GroupIndex : IEquatable<GroupIndex>, IComparable<GroupIndex>
    {
        // 1-based raw storage. 0 = Null; real groups are 1..N.
        // Kept private so callers can't accidentally treat it as a 0-based index.
        readonly ushort _raw;

        // Private so all internal construction flows through FromIndex (for real
        // groups) or default/Null (for the null sentinel). Prevents accidental
        // `new GroupIndex((ushort)i)` with a 0-based `i` that would produce Null.
        GroupIndex(ushort raw)
        {
            _raw = raw;
        }

        /// <summary>
        /// Constructs a GroupIndex from a 0-based registry index.
        /// </summary>
        internal static GroupIndex FromIndex(int index) => new(checked((ushort)(index + 1)));

        /// <summary>
        /// The null sentinel. Equals <c>default(GroupIndex)</c>.
        /// </summary>
        public static GroupIndex Null => default;

        /// <summary>
        /// Returns <c>true</c> if this is the null sentinel.
        /// </summary>
        public bool IsNull
        {
            [MethodImpl(MethodImplOptions.AggressiveInlining)]
            get => _raw == 0;
        }

        /// <summary>
        /// 0-based index for array access. Throws if this is the null sentinel —
        /// guard with <see cref="IsNull"/> first when that's possible.
        /// </summary>
        public int Index
        {
            [MethodImpl(MethodImplOptions.AggressiveInlining)]
            get
            {
                if (_raw == 0)
                {
                    throw new TrecsException(
                        "Cannot get Index of a null GroupIndex. Check IsNull first."
                    );
                }
                return _raw - 1;
            }
        }

        [MethodImpl(MethodImplOptions.AggressiveInlining)]
        public readonly bool Equals(GroupIndex other) => _raw == other._raw;

        [MethodImpl(MethodImplOptions.AggressiveInlining)]
        public override readonly bool Equals(object obj) =>
            obj is GroupIndex other && Equals(other);

        [MethodImpl(MethodImplOptions.AggressiveInlining)]
        public override readonly int GetHashCode() => _raw;

        [MethodImpl(MethodImplOptions.AggressiveInlining)]
        public readonly int CompareTo(GroupIndex other) => _raw.CompareTo(other._raw);

        [MethodImpl(MethodImplOptions.AggressiveInlining)]
        public static bool operator ==(GroupIndex a, GroupIndex b) => a._raw == b._raw;

        [MethodImpl(MethodImplOptions.AggressiveInlining)]
        public static bool operator !=(GroupIndex a, GroupIndex b) => a._raw != b._raw;

        public override readonly string ToString() =>
            _raw == 0 ? "GroupIndex(Null)" : $"GroupIndex({_raw - 1})";
    }
}
