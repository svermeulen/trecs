using System;
using System.Runtime.CompilerServices;

namespace Trecs
{
    /// <summary>
    /// Strongly-typed wrapper for the <see cref="TypeId"/> of an <see cref="IEntityComponent"/>
    /// struct type. Use <see cref="ComponentTypeId{T}"/> for zero-allocation access to a
    /// component type's runtime id; layered onto the same intern table that backs
    /// <see cref="Tag"/> and <see cref="SetId"/>.
    /// </summary>
    public readonly struct ComponentTypeId : IEquatable<ComponentTypeId>
    {
        readonly TypeId _inner;

        public int Value => _inner.Value;

        public ComponentTypeId(int value)
        {
            _inner = new TypeId(value);
        }

        public ComponentTypeId(TypeId id)
        {
            _inner = id;
        }

        [MethodImpl(MethodImplOptions.AggressiveInlining)]
        public bool Equals(ComponentTypeId other) => _inner == other._inner;

        public override bool Equals(object obj) => obj is ComponentTypeId other && Equals(other);

        [MethodImpl(MethodImplOptions.AggressiveInlining)]
        public override int GetHashCode() => _inner.GetHashCode();

        public static bool operator ==(ComponentTypeId a, ComponentTypeId b) =>
            a._inner == b._inner;

        public static bool operator !=(ComponentTypeId a, ComponentTypeId b) =>
            a._inner != b._inner;

        // Safe widening: a ComponentTypeId is a TypeId (of a component-marker-interface type).
        // Code that takes a TypeId receives one transparently; the type-safety boundary is
        // the other direction — callers wanting a ComponentTypeId from a TypeId must `new()`
        // explicitly.
        [MethodImpl(MethodImplOptions.AggressiveInlining)]
        public static implicit operator TypeId(ComponentTypeId id) => id._inner;

        public override string ToString() => _inner.Value.ToString();
    }

    /// <summary>
    /// Zero-allocation accessor for the <see cref="ComponentTypeId"/> of a component type.
    /// Property defers to <see cref="TypeId{T}.Value"/>, which carries the warmup contract.
    /// </summary>
    public static class ComponentTypeId<T>
        where T : unmanaged, IEntityComponent
    {
        public static ComponentTypeId Value
        {
            [MethodImpl(MethodImplOptions.AggressiveInlining)]
            get => new(TypeId<T>.Value);
        }

        public static void Warmup() => _ = TypeId<T>.Value;
    }
}
