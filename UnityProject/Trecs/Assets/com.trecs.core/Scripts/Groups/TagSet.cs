using System;
using System.Collections.Generic;
using System.Runtime.CompilerServices;
using Trecs.Internal;

namespace Trecs
{
    /// <summary>
    /// An immutable, order-independent combination of <see cref="Tag"/>s that uniquely identifies
    /// a <see cref="GroupIndex"/>. Two tag sets containing the same tags always resolve to the same
    /// <see cref="Id"/>, regardless of the order the tags were specified. Use the generic helpers
    /// (e.g. <c>TagSet&lt;Red, Fast&gt;.Value</c>) for zero-allocation access in hot paths.
    /// Wraps a <see cref="TypeIdSet"/> — tag identities and component-type identities share the
    /// same intern table.
    /// </summary>
    [TypeId(912438516)]
    public readonly struct TagSet : IEquatable<TagSet>, IComparable<TagSet>
    {
        readonly TypeIdSet _inner;

        public int Id => _inner.Id;

        /// <summary>
        /// Sentinel value representing an empty tag set.
        /// </summary>
        // Must be a static field (not const/property) for Burst compatibility
        public static readonly TagSet Null;

        // Internal: external callers should never need to construct from a raw int —
        // use FromTags / CombineWith / TagSet<T...>. The ctor exists for serialization
        // round-trips of an already-interned id.
        internal TagSet(int id)
        {
            _inner = new TypeIdSet(id);
        }

        internal TagSet(TypeIdSet inner)
        {
            _inner = inner;
        }

        /// <summary>
        /// The individual <see cref="Tag"/>s that make up this set. Performs a registry lookup
        /// on each access — cache the result if called in a tight loop.
        /// </summary>
        public readonly IReadOnlyList<Tag> Tags
        {
            get { return TagSetCache.GetTags(_inner); }
        }

        public readonly bool IsNull => _inner.IsNull;

        // Stable hash across sessions.
        [MethodImpl(MethodImplOptions.AggressiveInlining)]
        public override readonly int GetHashCode()
        {
            return _inner.Id;
        }

        [MethodImpl(MethodImplOptions.AggressiveInlining)]
        public static bool operator ==(TagSet c1, TagSet c2)
        {
            return c1._inner == c2._inner;
        }

        [MethodImpl(MethodImplOptions.AggressiveInlining)]
        public static bool operator !=(TagSet c1, TagSet c2)
        {
            return c1._inner != c2._inner;
        }

        [MethodImpl(MethodImplOptions.AggressiveInlining)]
        public override readonly bool Equals(object obj)
        {
            return obj is TagSet other && Equals(other);
        }

        [MethodImpl(MethodImplOptions.AggressiveInlining)]
        public readonly bool Equals(TagSet other)
        {
            return _inner == other._inner;
        }

        [MethodImpl(MethodImplOptions.AggressiveInlining)]
        public readonly int CompareTo(TagSet other)
        {
            return _inner.Id.CompareTo(other._inner.Id);
        }

        // Routes through TagSetCache, which renders each member's name via
        // Tag.ToString → TypeIdReverseLookup so error messages and logs read as
        // e.g. "TagSet(McAlive-McDead (1234))" rather than "TagSet(-9876-1234 (1234))".
        // Cached in TagSetCache after the first call per set.
        public override readonly string ToString()
        {
            return $"TagSet({TagSetCache.GetDisplayString(_inner)})";
        }

        public static TagSet FromTags(IReadOnlyList<Tag> tags)
        {
            if (tags.Count == 0)
            {
                return Null;
            }

            var set = TypeIdSet.FromMember(new TypeId(tags[0].Value));
            for (int i = 1; i < tags.Count; i++)
            {
                set = set.Add(new TypeId(tags[i].Value));
            }
            return new TagSet(set);
        }

        public static TagSet FromTags(Tag t1) => new(TypeIdSet.FromMember(new TypeId(t1.Value)));

        public static TagSet FromTags(Tag t1, Tag t2) =>
            new(TypeIdSet.FromMember(new TypeId(t1.Value)).Add(new TypeId(t2.Value)));

        public static TagSet FromTags(Tag t1, Tag t2, Tag t3) =>
            new(
                TypeIdSet
                    .FromMember(new TypeId(t1.Value))
                    .Add(new TypeId(t2.Value))
                    .Add(new TypeId(t3.Value))
            );

        public static TagSet FromTags(Tag t1, Tag t2, Tag t3, Tag t4) =>
            new(
                TypeIdSet
                    .FromMember(new TypeId(t1.Value))
                    .Add(new TypeId(t2.Value))
                    .Add(new TypeId(t3.Value))
                    .Add(new TypeId(t4.Value))
            );

        /// <summary>
        /// Returns a new <see cref="TagSet"/> containing the union of this set's tags and
        /// <paramref name="other"/>'s tags. Returns <c>this</c> unchanged if <paramref name="other"/>
        /// is null or identical.
        /// </summary>
        public readonly TagSet CombineWith(TagSet other) => new(_inner.CombineWith(other._inner));

        public static implicit operator TagSet(Tag tag)
        {
            return FromTags(tag);
        }

        // ── Burst-safe type-parameterised factories ──────────────────────────
        //
        // The <see cref="TagSet{T1}"/> family caches its value in a
        // `static readonly TagSet Value` field whose initializer calls
        // <see cref="Tag{T}.Value"/>. Burst's IL interpreter tries to
        // AOT-evaluate that initializer when a Burst job references it, and
        // <c>Tag&lt;T&gt;._value</c> is non-readonly (its managed
        // <c>[BurstDiscard]</c> Init writes to it), so Burst rejects the read
        // with BC1040.
        //
        // These factories sidestep that by computing the resulting TagSet.Id
        // at runtime via XOR of each member's <see cref="TypeId{T}"/>.Value
        // — TypeId&lt;T&gt;._value is `static readonly` with a
        // Burst-AOT-evaluable cctor (BurstRuntime.GetHashCode32 is a Burst
        // intrinsic; the reverse-lookup registration is BurstDiscard'd), so
        // Burst constant-folds the read.
        //
        // The XOR matches what
        // <see cref="Trecs.Internal.TypeIdSetRegistry"/> produces for the
        // same inputs (XOR with the same `id == 0 → 1` normalization), so
        // the result is a valid intern-table key as long as the world's
        // managed init has registered each tag — true by construction
        // during world construction.
        //
        // Caller contract: the type arguments must be distinct ITag types.
        // Duplicates would XOR-cancel and yield a different id than the
        // managed-side dedup path produces. Asserted in DEBUG.

        public static TagSet BurstableFromTags<T1>()
            where T1 : struct, ITag
        {
            int id = TypeId<T1>.Value.Value;
            // TypeId values come from BurstRuntime.GetHashCode32 normalized to
            // non-zero at TypeId construction, so id is guaranteed non-zero
            // here. Re-asserting matches TypeIdSetRegistry.FromSingle exactly.
            if (id == 0)
                id = 1;
            return new TagSet(id);
        }

        public static TagSet BurstableFromTags<T1, T2>()
            where T1 : struct, ITag
            where T2 : struct, ITag
        {
            int id1 = TypeId<T1>.Value.Value;
            int id2 = TypeId<T2>.Value.Value;
            TrecsDebugAssert.That(
                id1 != id2,
                "BurstableFromTags called with duplicate tag types — XOR would cancel and produce an incorrect TagSet id"
            );
            int id = id1 ^ id2;
            if (id == 0)
                id = 1;
            return new TagSet(id);
        }

        public static TagSet BurstableFromTags<T1, T2, T3>()
            where T1 : struct, ITag
            where T2 : struct, ITag
            where T3 : struct, ITag
        {
            int id1 = TypeId<T1>.Value.Value;
            int id2 = TypeId<T2>.Value.Value;
            int id3 = TypeId<T3>.Value.Value;
            TrecsDebugAssert.That(
                id1 != id2 && id1 != id3 && id2 != id3,
                "BurstableFromTags called with duplicate tag types — XOR would cancel and produce an incorrect TagSet id"
            );
            int id = id1 ^ id2 ^ id3;
            if (id == 0)
                id = 1;
            return new TagSet(id);
        }

        public static TagSet BurstableFromTags<T1, T2, T3, T4>()
            where T1 : struct, ITag
            where T2 : struct, ITag
            where T3 : struct, ITag
            where T4 : struct, ITag
        {
            int id1 = TypeId<T1>.Value.Value;
            int id2 = TypeId<T2>.Value.Value;
            int id3 = TypeId<T3>.Value.Value;
            int id4 = TypeId<T4>.Value.Value;
            TrecsDebugAssert.That(
                id1 != id2 && id1 != id3 && id1 != id4 && id2 != id3 && id2 != id4 && id3 != id4,
                "BurstableFromTags called with duplicate tag types — XOR would cancel and produce an incorrect TagSet id"
            );
            int id = id1 ^ id2 ^ id3 ^ id4;
            if (id == 0)
                id = 1;
            return new TagSet(id);
        }
    }

    /// <summary>
    /// Zero-allocation cache for a <see cref="TagSet"/> composed of a single tag type.
    /// Access via <c>TagSet&lt;T1&gt;.Value</c>.
    /// </summary>
    public sealed class TagSet<T1>
        where T1 : struct, ITag
    {
        public static readonly TagSet Value = TagSet.FromTags(Tag<T1>.Value);

        public static void Warmup() { }
    }

    /// <inheritdoc cref="TagSet{T1}"/>
    public sealed class TagSet<T1, T2>
        where T1 : struct, ITag
        where T2 : struct, ITag
    {
        public static readonly TagSet Value = TagSet.FromTags(Tag<T1>.Value, Tag<T2>.Value);

        public static void Warmup() { }

        TagSet() { }
    }

    /// <inheritdoc cref="TagSet{T1}"/>
    public sealed class TagSet<T1, T2, T3>
        where T1 : struct, ITag
        where T2 : struct, ITag
        where T3 : struct, ITag
    {
        public static readonly TagSet Value = TagSet.FromTags(
            Tag<T1>.Value,
            Tag<T2>.Value,
            Tag<T3>.Value
        );

        public static void Warmup() { }

        TagSet() { }
    }

    /// <inheritdoc cref="TagSet{T1}"/>
    public sealed class TagSet<T1, T2, T3, T4>
        where T1 : struct, ITag
        where T2 : struct, ITag
        where T3 : struct, ITag
        where T4 : struct, ITag
    {
        public static readonly TagSet Value = TagSet.FromTags(
            Tag<T1>.Value,
            Tag<T2>.Value,
            Tag<T3>.Value,
            Tag<T4>.Value
        );

        public static void Warmup() { }

        TagSet() { }
    }
}
