using System;
using System.Collections.Generic;
using System.Runtime.CompilerServices;
using Trecs.Internal;

namespace Trecs
{
    /// <summary>
    /// An immutable, order-independent set of <see cref="TypeId"/>s, identified by a single int
    /// (XOR of member values). Set membership and string formatting are looked up in a shared
    /// intern table. Backs <see cref="ComponentTypeIdSet"/> and <see cref="TagSet"/>.
    /// </summary>
    public readonly struct TypeIdSet : IEquatable<TypeIdSet>
    {
        public readonly int Id;

        public static readonly TypeIdSet Null;

        // Internal: external callers should never need to construct from a raw int —
        // use Add / FromMember / CombineWith. The ctor exists for serialization
        // round-trips and for the wrapper structs (ComponentTypeIdSet, TagSet) that
        // forward an already-interned id from disk.
        internal TypeIdSet(int id)
        {
            Id = id;
        }

        public bool IsNull => Id == 0;

        public IReadOnlyList<TypeId> Members => TypeIdSetRegistry.GetMembers(this);

        public static TypeIdSet FromMember(TypeId member) => TypeIdSetRegistry.FromSingle(member);

        public TypeIdSet Add(TypeId member)
        {
            if (IsNull)
            {
                return TypeIdSetRegistry.FromSingle(member);
            }
            return TypeIdSetRegistry.AddMember(this, member);
        }

        public TypeIdSet CombineWith(TypeIdSet other)
        {
            if (other.IsNull)
                return this;
            if (IsNull)
                return other;
            if (this == other)
                return this;
            return TypeIdSetRegistry.Combine(this, other);
        }

        [MethodImpl(MethodImplOptions.AggressiveInlining)]
        public bool Equals(TypeIdSet other) => Id == other.Id;

        public override bool Equals(object obj) => obj is TypeIdSet other && Equals(other);

        [MethodImpl(MethodImplOptions.AggressiveInlining)]
        public override int GetHashCode() => Id;

        [MethodImpl(MethodImplOptions.AggressiveInlining)]
        public static bool operator ==(TypeIdSet a, TypeIdSet b) => a.Id == b.Id;

        [MethodImpl(MethodImplOptions.AggressiveInlining)]
        public static bool operator !=(TypeIdSet a, TypeIdSet b) => a.Id != b.Id;

        public override string ToString() => TypeIdSetRegistry.SetToString(this);
    }
}
