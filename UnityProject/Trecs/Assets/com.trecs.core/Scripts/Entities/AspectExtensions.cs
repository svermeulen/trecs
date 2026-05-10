using System.Runtime.CompilerServices;
using Trecs.Internal;

namespace Trecs
{
    public static class AspectExtensions
    {
        /// <summary>
        /// Returns an <see cref="EntityAccessor"/> bound to the entity this aspect is
        /// currently pointing at. Use this to perform structural / set / input operations
        /// on the aspect's entity without paying a handle-to-index lookup per call.
        /// </summary>
        [MethodImpl(MethodImplOptions.AggressiveInlining)]
        public static EntityAccessor Entity<TAspect>(this TAspect aspect, WorldAccessor world)
            where TAspect : struct, IAspect => new EntityAccessor(world, aspect.EntityIndex);

        /// <summary>
        /// Resolves this aspect's transient entity index to a stable <see cref="EntityHandle"/>.
        /// Use when storing a reference to this entity in another component or across frames.
        /// </summary>
        [MethodImpl(MethodImplOptions.AggressiveInlining)]
        public static EntityHandle Handle<TAspect>(this TAspect aspect, WorldAccessor world)
            where TAspect : struct, IAspect => aspect.EntityIndex.ToHandle(world);

        /// <summary>
        /// Resolves this aspect's transient entity index to a stable <see cref="EntityHandle"/>
        /// from inside a Burst-compiled job using a <see cref="NativeWorldAccessor"/>.
        /// </summary>
        [MethodImpl(MethodImplOptions.AggressiveInlining)]
        public static EntityHandle Handle<TAspect>(this TAspect aspect, in NativeWorldAccessor world)
            where TAspect : struct, IAspect => world.GetEntityHandle(aspect.EntityIndex);

        [MethodImpl(MethodImplOptions.AggressiveInlining)]
        public static void Remove<TAspect>(this TAspect aspect, WorldAccessor world)
            where TAspect : struct, IAspect => world.RemoveEntity(aspect.EntityIndex);

        [MethodImpl(MethodImplOptions.AggressiveInlining)]
        public static void Remove<TAspect>(this TAspect aspect, in NativeWorldAccessor world)
            where TAspect : struct, IAspect => world.RemoveEntity(aspect.EntityIndex);

        [MethodImpl(MethodImplOptions.AggressiveInlining)]
        public static void MoveTo<TAspect>(this TAspect aspect, WorldAccessor world, TagSet tags)
            where TAspect : struct, IAspect => world.MoveTo(aspect.EntityIndex, tags);

        [MethodImpl(MethodImplOptions.AggressiveInlining)]
        public static void MoveTo<TAspect>(
            this TAspect aspect,
            in NativeWorldAccessor world,
            TagSet tags
        )
            where TAspect : struct, IAspect => world.MoveTo(aspect.EntityIndex, tags);
    }
}
