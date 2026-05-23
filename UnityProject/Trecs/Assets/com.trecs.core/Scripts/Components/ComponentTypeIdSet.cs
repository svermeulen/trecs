using System;
using System.Collections.Generic;
using System.Runtime.CompilerServices;

namespace Trecs
{
    /// <summary>
    /// Strongly-typed view over a <see cref="TypeIdSet"/> whose members are component
    /// <see cref="TypeId"/>s. Used by queries that constrain on a component-set; layered
    /// onto the same intern table that backs <see cref="TagSet"/>.
    /// </summary>
    public readonly struct ComponentTypeIdSet : IEquatable<ComponentTypeIdSet>
    {
        readonly TypeIdSet _inner;

        public int Id => _inner.Id;

        public static readonly ComponentTypeIdSet Null;

        // Internal: external callers should never need to construct from a raw int —
        // use Add / ComponentTypeIdSet<T...>. The ctor exists for serialization
        // round-trips of an already-interned id.
        internal ComponentTypeIdSet(int id)
        {
            _inner = new TypeIdSet(id);
        }

        internal ComponentTypeIdSet(TypeIdSet inner)
        {
            _inner = inner;
        }

        public bool IsNull => _inner.IsNull;

        public IReadOnlyList<TypeId> Components => _inner.Members;

        public ComponentTypeIdSet Add(TypeId componentTypeId) => new(_inner.Add(componentTypeId));

        [MethodImpl(MethodImplOptions.AggressiveInlining)]
        public bool Equals(ComponentTypeIdSet other) => _inner == other._inner;

        public override bool Equals(object obj) => obj is ComponentTypeIdSet other && Equals(other);

        [MethodImpl(MethodImplOptions.AggressiveInlining)]
        public override int GetHashCode() => _inner.Id;

        public static bool operator ==(ComponentTypeIdSet a, ComponentTypeIdSet b) =>
            a._inner == b._inner;

        public static bool operator !=(ComponentTypeIdSet a, ComponentTypeIdSet b) =>
            a._inner != b._inner;

        public override string ToString()
        {
            if (IsNull)
            {
                return "ComponentTypeIdSet(Null)";
            }
            return $"ComponentTypeIdSet({_inner})";
        }
    }

    /// <inheritdoc cref="ComponentTypeIdSet"/>
    public static class ComponentTypeIdSet<T1>
        where T1 : unmanaged, IEntityComponent
    {
        public static readonly ComponentTypeIdSet Value = new(
            TypeIdSet.FromMember(TypeId<T1>.Value)
        );
    }

    /// <inheritdoc cref="ComponentTypeIdSet"/>
    public static class ComponentTypeIdSet<T1, T2>
        where T1 : unmanaged, IEntityComponent
        where T2 : unmanaged, IEntityComponent
    {
        public static readonly ComponentTypeIdSet Value = ComponentTypeIdSet<T1>.Value.Add(
            TypeId<T2>.Value
        );
    }

    /// <inheritdoc cref="ComponentTypeIdSet"/>
    public static class ComponentTypeIdSet<T1, T2, T3>
        where T1 : unmanaged, IEntityComponent
        where T2 : unmanaged, IEntityComponent
        where T3 : unmanaged, IEntityComponent
    {
        public static readonly ComponentTypeIdSet Value = ComponentTypeIdSet<T1, T2>.Value.Add(
            TypeId<T3>.Value
        );
    }

    /// <inheritdoc cref="ComponentTypeIdSet"/>
    public static class ComponentTypeIdSet<T1, T2, T3, T4>
        where T1 : unmanaged, IEntityComponent
        where T2 : unmanaged, IEntityComponent
        where T3 : unmanaged, IEntityComponent
        where T4 : unmanaged, IEntityComponent
    {
        public static readonly ComponentTypeIdSet Value = ComponentTypeIdSet<T1, T2, T3>.Value.Add(
            TypeId<T4>.Value
        );
    }
}
