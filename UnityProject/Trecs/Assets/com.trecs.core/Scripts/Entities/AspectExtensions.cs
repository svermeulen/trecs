using System.Runtime.CompilerServices;

namespace Trecs
{
    public static class AspectExtensions
    {
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
