using System.Runtime.CompilerServices;

namespace Trecs.Internal
{
    /// <summary>
    /// Extension methods that expose the <see cref="EntityIndex"/>-based
    /// overloads of <see cref="NativeWorldAccessor"/> to source-generated code
    /// in user assemblies. EntityIndex is internal-only API, so user code in
    /// jobs should use the <see cref="EntityHandle"/> overloads on
    /// <see cref="NativeWorldAccessor"/> directly.
    /// </summary>
    public static class NativeWorldAccessorInternalExtensions
    {
        [MethodImpl(MethodImplOptions.AggressiveInlining)]
        public static void RemoveEntity(
            this in NativeWorldAccessor world,
            EntityIndex entityIndex
        ) => world.RemoveEntity(entityIndex);

        [MethodImpl(MethodImplOptions.AggressiveInlining)]
        public static void SetTag<T>(this in NativeWorldAccessor world, EntityIndex entityIndex)
            where T : struct, ITag => world.SetTag<T>(entityIndex);

        [MethodImpl(MethodImplOptions.AggressiveInlining)]
        public static void UnsetTag<T>(this in NativeWorldAccessor world, EntityIndex entityIndex)
            where T : struct, ITag => world.UnsetTag<T>(entityIndex);

        [MethodImpl(MethodImplOptions.AggressiveInlining)]
        public static bool TryGetEntityIndex(
            this in NativeWorldAccessor world,
            EntityHandle entityHandle,
            out EntityIndex entityIndex
        ) => world.TryGetEntityIndex(entityHandle, out entityIndex);

        [MethodImpl(MethodImplOptions.AggressiveInlining)]
        public static EntityIndex GetEntityIndex(
            this in NativeWorldAccessor world,
            EntityHandle entityHandle
        ) => world.GetEntityIndex(entityHandle);

        [MethodImpl(MethodImplOptions.AggressiveInlining)]
        public static EntityHandle GetEntityHandle(
            this in NativeWorldAccessor world,
            EntityIndex entityIndex
        ) => world.GetEntityHandle(entityIndex);

        [MethodImpl(MethodImplOptions.AggressiveInlining)]
        public static void SetAdd<TSet>(this in NativeWorldAccessor world, EntityIndex entityIndex)
            where TSet : struct, IEntitySet => world.SetAdd<TSet>(entityIndex);

        [MethodImpl(MethodImplOptions.AggressiveInlining)]
        public static void SetRemove<TSet>(
            this in NativeWorldAccessor world,
            EntityIndex entityIndex
        )
            where TSet : struct, IEntitySet => world.SetRemove<TSet>(entityIndex);
    }
}
