using System;
using System.Runtime.CompilerServices;
using Trecs.Internal;
using Unity.Mathematics;

namespace Trecs
{
    /// <summary>
    /// A transient entity identifier composed of a buffer index within a specific group.
    /// Secondary to <see cref="EntityHandle"/>: prefer handles in user code, since they
    /// remain stable across structural changes. An <see cref="EntityIndex"/> is only
    /// guaranteed valid until the next entity submission — any add, remove, or tag-change
    /// that touches the entity's group may shift buffer positions, and switching the
    /// entity's partition moves it to a different group entirely.
    /// <para>
    /// Use this overload set in hot loops where a handle has already been resolved and
    /// you want to perform multiple operations on the same entity without paying the
    /// handle-to-index lookup each time. Round-trip via
    /// <see cref="EntityHandle.ToIndex(WorldAccessor)"/> /
    /// <see cref="ToHandle(WorldAccessor)"/> when you need to cross a submission
    /// boundary.
    /// </para>
    /// </summary>
    public readonly struct EntityIndex
        : IEquatable<EntityIndex>,
            IComparable<EntityIndex>,
            IComparable
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

        // Stable hash across sessions.
        /// <inheritdoc/>
        public override int GetHashCode()
        {
            // we don't want to use HashCode.Combine because
            // it's not deterministic across restarts.
            // Uses GroupIndex.GetHashCode() (raw value) so null GroupIndex
            // hashes correctly without throwing.
            return unchecked((int)math.hash(new int2(Index, GroupIndex.GetHashCode())));
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

        // ── Entity-targeted operations ──────────────────────────────
        // No handle-to-index lookup cost — call these from hot loops after
        // converting via handle.ToIndex(world) once.

        /// <summary>
        /// Schedules removal of this entity. Deferred until the next entity submission.
        /// </summary>
        [MethodImpl(MethodImplOptions.AggressiveInlining)]
        public void Remove(WorldAccessor world) => world.RemoveEntity(this);

        /// <summary>
        /// Burst-safe variant of <see cref="Remove(WorldAccessor)"/>.
        /// </summary>
        [MethodImpl(MethodImplOptions.AggressiveInlining)]
        public void Remove(in NativeWorldAccessor world) => world.RemoveEntity(this);

        /// <summary>
        /// Sets <typeparamref name="T"/> as the active tag on this entity's
        /// <see cref="IPartitionedBy{T1}"/> / <see cref="IPartitionedBy{T1, T2}"/> dimension.
        /// </summary>
        [MethodImpl(MethodImplOptions.AggressiveInlining)]
        public void SetTag<T>(WorldAccessor world)
            where T : struct, ITag => world.SetTag<T>(this);

        /// <summary>
        /// Burst-safe variant of <see cref="SetTag{T}(WorldAccessor)"/>.
        /// </summary>
        [MethodImpl(MethodImplOptions.AggressiveInlining)]
        public void SetTag<T>(in NativeWorldAccessor world)
            where T : struct, ITag => world.SetTag<T>(this);

        /// <summary>
        /// Clears <typeparamref name="T"/> from this entity, moving it to the absent
        /// partition of <typeparamref name="T"/>'s presence/absence dimension.
        /// </summary>
        [MethodImpl(MethodImplOptions.AggressiveInlining)]
        public void UnsetTag<T>(WorldAccessor world)
            where T : struct, ITag => world.UnsetTag<T>(this);

        /// <summary>
        /// Burst-safe variant of <see cref="UnsetTag{T}(WorldAccessor)"/>.
        /// </summary>
        [MethodImpl(MethodImplOptions.AggressiveInlining)]
        public void UnsetTag<T>(in NativeWorldAccessor world)
            where T : struct, ITag => world.UnsetTag<T>(this);

        /// <summary>
        /// Enqueues an input component value for this entity for the next fixed-update frame.
        /// Only callable from <see cref="SystemPhase.Input"/> systems.
        /// </summary>
        [MethodImpl(MethodImplOptions.AggressiveInlining)]
        public void AddInput<T>(WorldAccessor world, in T value)
            where T : unmanaged, IEntityComponent => world.AddInput(this, value);

        /// <summary>
        /// Returns a <see cref="ComponentAccessor{T}"/> for lazy read/write access to this
        /// entity's component of type <typeparamref name="T"/>.
        /// </summary>
        [MethodImpl(MethodImplOptions.AggressiveInlining)]
        public ComponentAccessor<T> Component<T>(WorldAccessor world)
            where T : unmanaged, IEntityComponent => world.Component<T>(this);

        /// <summary>
        /// Attempts to access this entity's component of type <typeparamref name="T"/>,
        /// returning false if the entity no longer exists or lacks the component.
        /// </summary>
        [MethodImpl(MethodImplOptions.AggressiveInlining)]
        public bool TryComponent<T>(WorldAccessor world, out ComponentAccessor<T> componentRef)
            where T : unmanaged, IEntityComponent => world.TryComponent(this, out componentRef);
    }
}
