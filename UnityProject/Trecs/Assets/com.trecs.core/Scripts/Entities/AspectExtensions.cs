using System.Runtime.CompilerServices;

namespace Trecs
{
    public static class AspectExtensions
    {
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
        public static EntityHandle Handle<TAspect>(
            this TAspect aspect,
            in NativeWorldAccessor world
        )
            where TAspect : struct, IAspect => aspect.EntityIndex.ToHandle(world);

        [MethodImpl(MethodImplOptions.AggressiveInlining)]
        public static void Remove<TAspect>(this TAspect aspect, WorldAccessor world)
            where TAspect : struct, IAspect => aspect.EntityIndex.Remove(world);

        [MethodImpl(MethodImplOptions.AggressiveInlining)]
        public static void Remove<TAspect>(this TAspect aspect, in NativeWorldAccessor world)
            where TAspect : struct, IAspect => aspect.EntityIndex.Remove(world);
    }
}
