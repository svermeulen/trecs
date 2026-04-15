using System;
using System.Collections.Generic;
using System.Runtime.CompilerServices;
using Trecs.Internal;

namespace Trecs
{
    /// <summary>
    /// An immutable set of component type IDs, identified by a single int hash (XOR of ComponentId values).
    /// Analogous to TagSet for tags. The registry stores the actual component list per ID.
    /// </summary>
    public readonly struct ComponentTypeIdSet : IEquatable<ComponentTypeIdSet>
    {
        public readonly int Id;

        public static readonly ComponentTypeIdSet Null;

        public ComponentTypeIdSet(int id)
        {
            Id = id;
        }

        public bool IsNull => Id == 0;

        public IReadOnlyList<ComponentId> Components =>
            ComponentTypeIdSetRegistry.GetComponents(this);

        /// <summary>
        /// Returns a new set with the given component added.
        /// If the component is already in the set, returns the same set.
        /// </summary>
        public ComponentTypeIdSet Add(ComponentId componentId)
        {
            if (IsNull)
            {
                return ComponentTypeIdSetRegistry.FromSingle(componentId);
            }

            return ComponentTypeIdSetRegistry.AddComponent(this, componentId);
        }

        [MethodImpl(MethodImplOptions.AggressiveInlining)]
        public bool Equals(ComponentTypeIdSet other) => Id == other.Id;

        public override bool Equals(object obj) => obj is ComponentTypeIdSet other && Equals(other);

        [MethodImpl(MethodImplOptions.AggressiveInlining)]
        public override int GetHashCode() => Id;

        public static bool operator ==(ComponentTypeIdSet a, ComponentTypeIdSet b) => a.Id == b.Id;

        public static bool operator !=(ComponentTypeIdSet a, ComponentTypeIdSet b) => a.Id != b.Id;

        public override string ToString()
        {
            if (IsNull)
            {
                return "ComponentTypeIdSet(Null)";
            }

            return ComponentTypeIdSetRegistry.SetToString(this);
        }
    }

    public static class ComponentTypeIdSet<T1>
        where T1 : unmanaged, IEntityComponent
    {
        public static readonly ComponentTypeIdSet Value = ComponentTypeIdSetRegistry.FromSingle(
            ComponentTypeId<T1>.Value
        );
    }

    public static class ComponentTypeIdSet<T1, T2>
        where T1 : unmanaged, IEntityComponent
        where T2 : unmanaged, IEntityComponent
    {
        public static readonly ComponentTypeIdSet Value = ComponentTypeIdSet<T1>.Value.Add(
            ComponentTypeId<T2>.Value
        );
    }

    public static class ComponentTypeIdSet<T1, T2, T3>
        where T1 : unmanaged, IEntityComponent
        where T2 : unmanaged, IEntityComponent
        where T3 : unmanaged, IEntityComponent
    {
        public static readonly ComponentTypeIdSet Value = ComponentTypeIdSet<T1, T2>.Value.Add(
            ComponentTypeId<T3>.Value
        );
    }

    public static class ComponentTypeIdSet<T1, T2, T3, T4>
        where T1 : unmanaged, IEntityComponent
        where T2 : unmanaged, IEntityComponent
        where T3 : unmanaged, IEntityComponent
        where T4 : unmanaged, IEntityComponent
    {
        public static readonly ComponentTypeIdSet Value = ComponentTypeIdSet<T1, T2, T3>.Value.Add(
            ComponentTypeId<T4>.Value
        );
    }
}
