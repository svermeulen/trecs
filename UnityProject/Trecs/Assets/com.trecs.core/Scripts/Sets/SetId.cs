using System;
using System.Runtime.CompilerServices;

namespace Trecs
{
    /// <summary>
    /// Stable integer identifier for an entity set definition. Corresponds to a
    /// <see cref="EntitySet"/> registered via <see cref="WorldBuilder.AddSet{T}"/>.
    /// Thin strongly-typed wrapper over <see cref="TypeId"/> — the underlying value
    /// is the <see cref="TypeId"/> of the <c>IEntitySet</c> struct that defines the set.
    /// </summary>
    [TypeId(364820517)]
    public readonly struct SetId : IEquatable<SetId>, IComparable<SetId>, IComparable
    {
        readonly TypeId _inner;

        public int Value => _inner.Value;

        public SetId(int value)
        {
            _inner = new TypeId(value);
        }

        public SetId(TypeId id)
        {
            _inner = id;
        }

        [MethodImpl(MethodImplOptions.AggressiveInlining)]
        public bool Equals(SetId other) => _inner == other._inner;

        public override bool Equals(object obj) => obj is SetId other && Equals(other);

        [MethodImpl(MethodImplOptions.AggressiveInlining)]
        public override int GetHashCode() => _inner.GetHashCode();

        public int CompareTo(SetId other) => _inner.Value.CompareTo(other._inner.Value);

        public int CompareTo(object obj)
        {
            if (obj is SetId other)
                return CompareTo(other);
            throw new ArgumentException("Comparing SetId with the wrong type");
        }

        public override string ToString() => _inner.Value.ToString();

        public static bool operator ==(SetId a, SetId b) => a._inner == b._inner;

        public static bool operator !=(SetId a, SetId b) => a._inner != b._inner;

        // Safe widening: a SetId is a TypeId (of an IEntitySet struct). Code that takes a
        // TypeId receives one transparently; constructing a SetId from a TypeId requires
        // `new()`.
        [MethodImpl(MethodImplOptions.AggressiveInlining)]
        public static implicit operator TypeId(SetId id) => id._inner;
    }

    /// <summary>
    /// Generic per-type accessor for the <see cref="SetId"/> of an <see cref="IEntitySet"/>
    /// struct type. Equivalent to <c>new SetId(TypeId&lt;T&gt;.Value)</c> — the SetId's
    /// underlying value IS the <see cref="TypeId"/> of <typeparamref name="T"/>, so this
    /// is a thin wrapper that defers Burst-safety to <see cref="TypeId{T}"/>.
    /// </summary>
    /// <remarks>
    /// Reading this does not trigger <c>SetFactory.CreateSet</c> registration —
    /// that's <see cref="EntitySet{T}.Value"/>'s job, normally fired from
    /// <c>WorldBuilder.AddSet&lt;T&gt;()</c>.
    /// </remarks>
    public static class SetId<T>
        where T : struct, IEntitySet
    {
        public static SetId Value
        {
            [MethodImpl(MethodImplOptions.AggressiveInlining)]
            get => new(TypeId<T>.Value);
        }

        public static void Warmup() => _ = TypeId<T>.Value;
    }
}
