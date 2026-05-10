using System;
using System.Runtime.CompilerServices;
using Unity.Mathematics;

namespace Trecs.Internal
{
    /// <summary>
    /// A transient entity identifier composed of a buffer index within a specific group.
    /// Unlike <see cref="EntityHandle"/>, this value may change when entities are added or removed.
    /// <para>
    /// This type lives in <c>Trecs.Internal</c> to communicate that it is not part of the
    /// public API. It remains <c>public</c> so source-generated code can reference it.
    /// User code should use <see cref="EntityHandle"/> and <see cref="EntityAccessor"/>.
    /// </para>
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
        public readonly GroupIndex GroupIndex;

        /// <summary>
        /// A sentinel value representing no entity. Equals <c>default(EntityIndex)</c>
        /// because <c>default(GroupIndex)</c> is <see cref="GroupIndex.Null"/>.
        /// </summary>
        public static EntityIndex Null => default;

        /// <summary>
        /// Returns true if this is the null sentinel value.
        /// </summary>
        public bool IsNull
        {
            [MethodImpl(MethodImplOptions.AggressiveInlining)]
            get { return GroupIndex.IsNull; }
        }

        /// <inheritdoc/>
        public static bool operator ==(EntityIndex obj1, EntityIndex obj2)
        {
            return obj1.Index == obj2.Index && obj1.GroupIndex == obj2.GroupIndex;
        }

        /// <inheritdoc/>
        public static bool operator !=(EntityIndex obj1, EntityIndex obj2)
        {
            return obj1.Index != obj2.Index || obj1.GroupIndex != obj2.GroupIndex;
        }

        public EntityIndex(int index, GroupIndex groupIndex)
            : this()
        {
            this.Index = index;
            this.GroupIndex = groupIndex;
        }

        /// <inheritdoc/>
        public override bool Equals(object obj)
        {
            return obj is EntityIndex other && Equals(other);
        }

        /// <inheritdoc/>
        public bool Equals(EntityIndex other)
        {
            return other.Index == Index && other.GroupIndex == GroupIndex;
        }

        public readonly int GetStableHashCode()
        {
            // we don't want to use HashCode.Combine or GetHashCode because
            // it's not deterministic across restarts.
            // Uses GroupIndex.GetHashCode() (raw value) so null GroupIndex
            // hashes correctly without throwing.
            return unchecked((int)math.hash(new int2(Index, GroupIndex.GetHashCode())));
        }

        /// <inheritdoc/>
        public override int GetHashCode()
        {
            return GetStableHashCode();
        }

        /// <inheritdoc/>
        public int CompareTo(EntityIndex other)
        {
            // Compare on raw (1-based) values — null sorts before all real groups.
            return GroupIndex.CompareTo(other.GroupIndex) != 0
                ? GroupIndex.CompareTo(other.GroupIndex)
                : Index.CompareTo(other.Index);
        }

        /// <inheritdoc/>
        public override string ToString()
        {
            return $"index {Index} group {GroupIndex}";
        }

        /// <summary>
        /// Returns a new EntityIndex in the same group but with a different buffer index.
        /// </summary>
        [MethodImpl(MethodImplOptions.AggressiveInlining)]
        public EntityIndex WithIndex(int index)
        {
            return new EntityIndex(index, GroupIndex);
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
