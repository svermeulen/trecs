using System;
using Trecs.Internal;

namespace Trecs
{
    [TypeId(283746019)]
    public struct BlobId : IEquatable<BlobId>, IStableHashProvider
    {
        public long Value;

        public BlobId(long value)
        {
            Value = value;
        }

        public static readonly BlobId Null = default;

        public readonly bool IsNull
        {
            get { return Value == 0; }
        }

        public readonly bool Equals(BlobId other)
        {
            return Value == other.Value;
        }

        public override readonly bool Equals(object obj)
        {
            return obj is BlobId other && Equals(other);
        }

        public override readonly int GetHashCode()
        {
            return GetStableHashCode();
        }

        public readonly int GetStableHashCode()
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

        public override readonly string ToString()
        {
            return Value.ToString();
        }
    }
}
