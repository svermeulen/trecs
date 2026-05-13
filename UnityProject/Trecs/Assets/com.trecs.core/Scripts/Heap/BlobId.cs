using System;
using Trecs.Internal;

namespace Trecs
{
    /// <summary>
    /// Identifier for a shared blob allocation in <see cref="SharedPtr{T}"/> and
    /// <see cref="NativeSharedPtr{T}"/> heaps. Callers always supply an explicit
    /// <see cref="BlobId"/> for persistent allocations — use one of the factories
    /// (<see cref="FromKey"/>, <see cref="FromGuid"/>, <see cref="FromBytes"/>,
    /// or the content-hash extension from Trecs.Serialization) to obtain one with
    /// the semantics you want. A zero value represents a null (unallocated) blob.
    /// </summary>
    [TypeId(283746019)]
    public readonly struct BlobId : IEquatable<BlobId>, IStableHashProvider
    {
        public readonly long Value;

        public BlobId(long value)
        {
            Value = value;
        }

        public static readonly BlobId Null = default;

        public bool IsNull
        {
            get { return Value == 0; }
        }

        public bool Equals(BlobId other)
        {
            return Value == other.Value;
        }

        public override bool Equals(object obj)
        {
            return obj is BlobId other && Equals(other);
        }

        public override int GetHashCode()
        {
            return GetStableHashCode();
        }

        public int GetStableHashCode()
        {
            return Value.GetHashCode();
        }

        public static bool operator ==(BlobId left, BlobId right)
        {
            return left.Equals(right);
        }

        public static bool operator !=(BlobId left, BlobId right)
        {
            return !left.Equals(right);
        }

        public override string ToString()
        {
            return Value.ToString();
        }
    }
}
