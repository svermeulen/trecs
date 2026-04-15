using System;

namespace Trecs
{
    /// <summary>
    /// Represents a contiguous range of entity indices within a group's component buffers.
    /// Used in observer callbacks to identify which entities were added, removed, or moved.
    /// </summary>
    public readonly struct EntityRange : IEquatable<EntityRange>
    {
        /// <summary>
        /// The starting index (inclusive) in the component buffer.
        /// </summary>
        public readonly int Start;

        /// <summary>
        /// The ending index (exclusive) in the component buffer.
        /// </summary>
        public readonly int End;

        /// <summary>
        /// The number of entities in this range.
        /// </summary>
        public int Count => End - Start;

        /// <summary>
        /// Creates an EntityRange spanning from <paramref name="start"/> (inclusive) to <paramref name="end"/> (exclusive).
        /// </summary>
        public EntityRange(int start, int end)
        {
            Start = start;
            End = end;
        }

        /// <summary>
        /// Returns true if both ranges have the same start and end.
        /// </summary>
        public static bool operator ==(EntityRange left, EntityRange right) =>
            left.Start == right.Start && left.End == right.End;

        /// <summary>
        /// Returns true if the ranges differ in start or end.
        /// </summary>
        public static bool operator !=(EntityRange left, EntityRange right) => !(left == right);

        /// <inheritdoc/>
        public bool Equals(EntityRange other) => this == other;

        /// <inheritdoc/>
        public override bool Equals(object obj) => obj is EntityRange other && Equals(other);

        /// <inheritdoc/>
        public override int GetHashCode() => HashCode.Combine(Start, End);

        /// <inheritdoc/>
        public override string ToString() => $"EntityRange({Start}, {End})";
    }
}
