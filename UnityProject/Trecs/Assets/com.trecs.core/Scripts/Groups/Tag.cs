using System;
using System.Runtime.CompilerServices;
using Trecs.Internal;
using Unity.Burst;

namespace Trecs
{
    /// <summary>
    /// Lightweight semantic label used to classify entities into <see cref="GroupIndex"/>s.
    /// Each tag type (an <see cref="ITag"/> struct) maps to a unique <see cref="TypeId"/>;
    /// <see cref="Value"/> exposes that as an int for hashing and dictionary keys.
    /// Tags are combined into <see cref="TagSet"/>s to define groups; entities with the same
    /// tag combination share a group and its contiguous component buffers.
    /// Use <see cref="Tag{T}"/> for zero-allocation access to a tag's runtime value.
    /// </summary>
    public readonly struct Tag : IEquatable<Tag>
    {
        readonly TypeId _inner;

        public int Value => _inner.Value;

        public Tag(int value)
        {
            _inner = new TypeId(value);
        }

        public Tag(TypeId id)
        {
            _inner = id;
        }

        public override readonly bool Equals(object obj)
        {
            return obj is Tag other && Equals(other);
        }

        [MethodImpl(MethodImplOptions.AggressiveInlining)]
        public readonly bool Equals(Tag other)
        {
            return _inner == other._inner;
        }

        // Stable hash across sessions.
        public override readonly int GetHashCode()
        {
            return _inner.GetHashCode();
        }

        // Debug-only path: the underlying TypeId is registered against typeof(T)
        // by TypeId<T>'s static ctor, so we can recover the name from the reverse
        // lookup. Falls back to the raw int when no managed code has registered
        // the type (e.g. an id deserialized from a snapshot before warmup).
        public override readonly string ToString()
        {
            return TypeIdReverseLookup.TryGetTypeFromId(_inner, out var type)
                ? type.Name
                : _inner.Value.ToString();
        }

        public static bool operator ==(Tag c1, Tag c2)
        {
            return c1._inner == c2._inner;
        }

        public static bool operator !=(Tag c1, Tag c2)
        {
            return c1._inner != c2._inner;
        }

        // Safe widening: a Tag is a TypeId (of an ITag struct). Code that takes a TypeId
        // receives one transparently; constructing a Tag from a TypeId requires `new()`.
        [MethodImpl(MethodImplOptions.AggressiveInlining)]
        public static implicit operator TypeId(Tag tag) => tag._inner;
    }

    /// <summary>
    /// Zero-allocation cache for the <see cref="Tag"/> instance corresponding to an
    /// <see cref="ITag"/> struct type. Access the cached value via <c>Tag&lt;MyTag&gt;.Value</c>.
    /// Routes through <see cref="TypeId{T}"/>, which carries the warmup contract and
    /// also registers <c>typeof(T)</c> with <see cref="TypeIdReverseLookup"/> for
    /// debug name recovery in <see cref="Tag.ToString"/>.
    /// </summary>
    public static class Tag<T>
        where T : struct, ITag
    {
        static Tag _value;

        /// <summary>
        /// The <see cref="Tag"/> for this <see cref="ITag"/> type. Asserts that the
        /// managed-side static ctor has run — Burst code reading this field before any
        /// managed warmup (via <see cref="Warmup"/> or any other access) sees the default
        /// zero, which would silently corrupt tag-set membership. Force warmup at startup
        /// by referencing <c>Tag&lt;T&gt;.Value</c>, <c>TagSet&lt;T...&gt;</c>, or
        /// <c>IEntitySet&lt;T...&gt;</c> from managed code.
        /// </summary>
        public static Tag Value
        {
            [MethodImpl(MethodImplOptions.AggressiveInlining)]
            get
            {
                var v = _value;
                TrecsAssert.That(
                    v.Value != 0,
                    "Tag<T>.Value accessed from Burst before the managed-side static ctor ran. Force warmup by referencing Tag<T>.Value, TagSet<T...>, or IEntitySet<T...> from managed code at startup."
                );
                return v;
            }
        }

        static Tag()
        {
            Init();
        }

        public static void Warmup() { }

        [BurstDiscard]
        static void Init()
        {
            _value = new Tag(TypeId<T>.Value);
        }
    }
}
