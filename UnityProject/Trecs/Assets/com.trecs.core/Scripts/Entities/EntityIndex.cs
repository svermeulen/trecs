using System;
using System.Runtime.CompilerServices;
using Trecs.Internal;
using Unity.Mathematics;

namespace Trecs
{
    /// <summary>
    /// A transient entity identifier composed of a buffer index within a specific group.
    /// Unlike <see cref="EntityHandle"/>, this value may change when entities are added or removed.
    /// </summary>
    public readonly struct EntityIndex
        : IEquatable<EntityIndex>,
            IComparable<EntityIndex>,
            IComparable,
            IStableHashProvider
    {
        /// <summary>
        /// The index of the entity within its group's component buffers.
        /// </summary>
        public readonly int Index;

        /// <summary>
        /// The group this entity belongs to.
        /// </summary>
        public readonly Group Group;

        /// <summary>
        /// A sentinel value representing no entity.
        /// </summary>
        public static EntityIndex Null => default;

        /// <summary>
        /// Returns true if this is the null sentinel value.
        /// </summary>
        public bool IsNull
        {
            get { return Group.IsNull; }
        }

        /// <inheritdoc/>
        public static bool operator ==(EntityIndex obj1, EntityIndex obj2)
        {
            return obj1.Index == obj2.Index && obj1.Group == obj2.Group;
        }

        /// <inheritdoc/>
        public static bool operator !=(EntityIndex obj1, EntityIndex obj2)
        {
            return obj1.Index != obj2.Index || obj1.Group != obj2.Group;
        }

        public EntityIndex(int index, Group groupId)
            : this()
        {
            this.Index = index;
            this.Group = groupId;
        }

        /// <inheritdoc/>
        public override bool Equals(object obj)
        {
            return obj is EntityIndex other && Equals(other);
        }

        /// <inheritdoc/>
        public bool Equals(EntityIndex other)
        {
            return other.Index == Index && other.Group == Group;
        }

        public readonly int GetStableHashCode()
        {
            // we don't want to use HashCode.Combine or GetHashCode because
            // it's not deterministic across restarts
            return unchecked((int)math.hash(new int2(Index, Group.GetStableHashCode())));
        }

        /// <inheritdoc/>
        public override int GetHashCode()
        {
            return GetStableHashCode();
        }

        /// <inheritdoc/>
        public int CompareTo(EntityIndex other)
        {
            return Group.Id != other.Group.Id
                ? Group.Id.CompareTo(other.Group.Id)
                : Index.CompareTo(other.Index);
        }

        /// <inheritdoc/>
        public override string ToString()
        {
            var value = Group.ToString();

            return $"index {Index} group {value}";
        }

        /// <summary>
        /// Returns a new EntityIndex in the same group but with a different buffer index.
        /// </summary>
        [MethodImpl(MethodImplOptions.AggressiveInlining)]
        public EntityIndex WithIndex(int index)
        {
            return new EntityIndex(index, Group);
        }

        /// <summary>
        /// Resolves this transient index to a stable <see cref="EntityHandle"/>.
        /// </summary>
        [MethodImpl(MethodImplOptions.AggressiveInlining)]
        internal EntityHandle ToHandle(EntityQuerier entitiesQuerier)
        {
            return entitiesQuerier.GetEntityHandle(this);
        }

        /// <summary>
        /// Resolves this transient index to a stable <see cref="EntityHandle"/>.
        /// </summary>
        [MethodImpl(MethodImplOptions.AggressiveInlining)]
        public EntityHandle ToHandle(WorldAccessor accessor)
        {
            return ToHandle(accessor.World.EntityQuerier);
        }

        /// <summary>
        /// Resolves this transient index to a stable <see cref="EntityHandle"/> using a native accessor.
        /// </summary>
        [MethodImpl(MethodImplOptions.AggressiveInlining)]
        public EntityHandle ToHandle(NativeWorldAccessor accessor)
        {
            return accessor.GetEntityHandle(this);
        }

        /// <summary>
        /// Creates a live <see cref="EntityAccessor"/> bound to the given <see cref="WorldAccessor"/>.
        /// </summary>
        [MethodImpl(MethodImplOptions.AggressiveInlining)]
        public EntityAccessor ToEntity(WorldAccessor accessor)
        {
            return new EntityAccessor(accessor, this);
        }

        /// <inheritdoc/>
        public int CompareTo(object obj)
        {
            if (obj is EntityIndex other)
            {
                return CompareTo(other);
            }

            throw new ArgumentException(
                $"Object must be of type {nameof(EntityIndex)}",
                nameof(obj)
            );
        }
    }
}
