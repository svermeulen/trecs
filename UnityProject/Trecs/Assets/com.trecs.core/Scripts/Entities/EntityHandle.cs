using System;
using System.Runtime.CompilerServices;
using Trecs.Internal;
using Unity.Mathematics;

namespace Trecs
{
    /// <summary>
    /// A stable entity identifier that persists across structural changes such as adds, removes, and group swaps.
    /// Convert to <see cref="EntityIndex"/> for direct component buffer access.
    /// </summary>
    [TypeId(847291053)]
    public readonly struct EntityHandle : IEquatable<EntityHandle>
    {
        /// <summary>
        /// The slot identifier for this entity within the world. Combined with <see cref="Version"/>
        /// to form a stable handle that survives slot reuse. Not globally unique: slot ids are
        /// recycled when entities are destroyed, with <see cref="Version"/> distinguishing reuses.
        /// </summary>
        public readonly int Id;

        /// <summary>
        /// The version counter used to detect stale references after entity destruction and reuse.
        /// </summary>
        public readonly int Version;

        internal int index => Id - 1;

        /// <inheritdoc/>
        public static bool operator ==(EntityHandle obj1, EntityHandle obj2)
        {
            return obj1.Id == obj2.Id && obj1.Version == obj2.Version;
        }

        /// <inheritdoc/>
        public static bool operator !=(EntityHandle obj1, EntityHandle obj2)
        {
            return obj1.Id != obj2.Id || obj1.Version != obj2.Version;
        }

        // Stable hash across sessions.
        /// <inheritdoc/>
        public override readonly int GetHashCode()
        {
            // we don't want to use HashCode.Combine because
            // it's not deterministic across restarts
            return unchecked((int)math.hash(new uint2((uint)Id, (uint)Version)));
        }

        public EntityHandle(int id, int version)
            : this()
        {
            this.Id = id;
            this.Version = version;
        }

        /// <inheritdoc/>
        public override readonly bool Equals(object obj)
        {
            return obj is EntityHandle other && Equals(other);
        }

        /// <inheritdoc/>
        public bool Equals(EntityHandle other)
        {
            return other.Id == Id && other.Version == Version;
        }

        /// <inheritdoc/>
        public override string ToString()
        {
            return $"id:{Id} version:{Version}";
        }

        /// <summary>
        /// Resolves this stable reference to a transient <see cref="EntityIndex"/> for component access.
        /// </summary>
        [MethodImpl(MethodImplOptions.AggressiveInlining)]
        internal EntityIndex ToIndex(EntityQuerier entitiesQuerier)
        {
            if (IsNull)
            {
                return EntityIndex.Null;
            }

            return entitiesQuerier.GetEntityIndex(this);
        }

        /// <summary>
        /// Attempts to resolve this reference to an <see cref="EntityIndex"/>, returning false if the entity no longer exists.
        /// </summary>
        [MethodImpl(MethodImplOptions.AggressiveInlining)]
        internal bool TryToIndex(EntityQuerier entitiesQuerier, out EntityIndex entityIndex)
        {
            if (IsNull)
            {
                entityIndex = EntityIndex.Null;
                return false;
            }

            if (!entitiesQuerier.TryGetEntityIndex(this, out entityIndex))
                return false;

            if (!entitiesQuerier.EntityIndexExists(entityIndex))
            {
                entityIndex = EntityIndex.Null;
                return false;
            }

            return true;
        }

        /// <summary>
        /// Returns true if this entity has been submitted and currently exists in the world.
        /// </summary>
        [MethodImpl(MethodImplOptions.AggressiveInlining)]
        internal readonly bool Exists(EntityQuerier entitiesQuerier)
        {
            return TryToIndex(entitiesQuerier, out _);
        }

        /// <summary>
        /// Resolves this stable reference to a transient <see cref="EntityIndex"/> for component access.
        /// </summary>
        [MethodImpl(MethodImplOptions.AggressiveInlining)]
        public EntityIndex ToIndex(World world)
        {
            return ToIndex(world.EntityQuerier);
        }

        /// <summary>
        /// Resolves this stable reference to a transient <see cref="EntityIndex"/> for component access.
        /// </summary>
        [MethodImpl(MethodImplOptions.AggressiveInlining)]
        public EntityIndex ToIndex(WorldAccessor accessor)
        {
            return ToIndex(accessor.World.EntityQuerier);
        }

        /// <summary>
        /// Creates a live <see cref="EntityAccessor"/> bound to the given <see cref="WorldAccessor"/>.
        /// </summary>
        [MethodImpl(MethodImplOptions.AggressiveInlining)]
        public EntityAccessor ToEntity(WorldAccessor accessor)
        {
            return new EntityAccessor(accessor, this);
        }

        /// <summary>
        /// Attempts to create a live <see cref="EntityAccessor"/> bound to the given <see cref="WorldAccessor"/>,
        /// returning false if the entity no longer exists.
        /// </summary>
        [MethodImpl(MethodImplOptions.AggressiveInlining)]
        public bool TryToEntity(WorldAccessor accessor, out EntityAccessor entity)
        {
            if (!TryToIndex(accessor.World.EntityQuerier, out var entityIndex))
            {
                entity = default;
                return false;
            }

            entity = new EntityAccessor(accessor, entityIndex);
            return true;
        }

        /// <summary>
        /// Attempts to resolve this reference to an <see cref="EntityIndex"/>, returning false if the entity no longer exists.
        /// </summary>
        [MethodImpl(MethodImplOptions.AggressiveInlining)]
        public bool TryToIndex(World world, out EntityIndex entityIndex)
        {
            return TryToIndex(world.EntityQuerier, out entityIndex);
        }

        /// <summary>
        /// Attempts to resolve this reference to an <see cref="EntityIndex"/>, returning false if the entity no longer exists.
        /// </summary>
        [MethodImpl(MethodImplOptions.AggressiveInlining)]
        public bool TryToIndex(WorldAccessor accessor, out EntityIndex entityIndex)
        {
            return TryToIndex(accessor.World.EntityQuerier, out entityIndex);
        }

        /// <summary>
        /// Resolves this stable reference to a transient <see cref="EntityIndex"/> using a native accessor.
        /// </summary>
        [MethodImpl(MethodImplOptions.AggressiveInlining)]
        public EntityIndex ToIndex(NativeWorldAccessor accessor)
        {
            if (IsNull)
            {
                return EntityIndex.Null;
            }

            return accessor.GetEntityIndex(this);
        }

        /// <summary>
        /// Attempts to resolve this reference to an <see cref="EntityIndex"/> using a native accessor, returning false if the entity no longer exists.
        /// </summary>
        [MethodImpl(MethodImplOptions.AggressiveInlining)]
        public bool TryToIndex(NativeWorldAccessor accessor, out EntityIndex entityIndex)
        {
            if (IsNull)
            {
                entityIndex = EntityIndex.Null;
                return false;
            }

            return accessor.TryGetEntityIndex(this, out entityIndex);
        }

        /// <summary>
        /// Returns true if this entity has been submitted and currently exists in the world.
        /// </summary>
        [MethodImpl(MethodImplOptions.AggressiveInlining)]
        public bool Exists(World world)
        {
            return Exists(world.EntityQuerier);
        }

        /// <summary>
        /// Returns true if this entity has been submitted and currently exists in the world.
        /// </summary>
        [MethodImpl(MethodImplOptions.AggressiveInlining)]
        public bool Exists(WorldAccessor accessor)
        {
            return Exists(accessor.World.EntityQuerier);
        }

        /// <summary>
        /// Returns true if this entity has been submitted and currently exists in the world.
        /// </summary>
        [MethodImpl(MethodImplOptions.AggressiveInlining)]
        public bool Exists(NativeWorldAccessor accessor)
        {
            return accessor.EntityExists(this);
        }

        /// <summary>
        /// Returns true if this is the null sentinel value.
        /// </summary>
        public bool IsNull
        {
            get { return this == Null; }
        }

        /// <summary>
        /// A sentinel value representing no entity.
        /// </summary>
        public static EntityHandle Null => default;
    }
}
