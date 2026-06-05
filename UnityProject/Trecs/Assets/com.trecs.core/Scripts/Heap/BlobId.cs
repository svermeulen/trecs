using System;

namespace Trecs
{
    /// <summary>
    /// Identifier for a shared blob allocation in <see cref="SharedPtr{T}"/> and
    /// <see cref="NativeSharedPtr{T}"/> heaps. Most blobs are <b>content-addressed</b> — the id is
    /// derived for you by hashing the content or a descriptor recipe (the blessed default; you never
    /// name the blob), via <see cref="BlobIdGenerator.FromContent{T}(WorldAccessor, in T)"/> or
    /// <see cref="BlobIdGenerator.FromBytes"/>. When you instead have a stable external identity —
    /// chiefly the out-of-core / baked opaque-blob store, which keys blobs by a durable domain key (a
    /// level id, prefab id, asset id) — wrap it directly with the <see cref="BlobId(long)"/>
    /// constructor. A zero value represents a null (unallocated) blob.
    /// </summary>
    [TypeId(283746019)]
    public readonly struct BlobId : IEquatable<BlobId>
    {
        public readonly long Value;

        /// <summary>
        /// Wraps a caller-chosen <paramref name="value"/> as a blob id — for durable external keys (a
        /// level id, prefab id, asset id). Content-addressed ids should instead be derived via
        /// <see cref="BlobIdGenerator.FromContent{T}(WorldAccessor, in T)"/>. <c>0</c> is
        /// <see cref="Null"/>.
        /// </summary>
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

        // Stable hash across sessions.
        public override int GetHashCode()
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
