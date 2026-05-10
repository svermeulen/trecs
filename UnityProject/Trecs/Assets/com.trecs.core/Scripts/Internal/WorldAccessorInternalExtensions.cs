using System.Runtime.CompilerServices;

namespace Trecs.Internal
{
    /// <summary>
    /// Extension methods that expose the <see cref="EntityIndex"/>-based
    /// overloads of <see cref="WorldAccessor"/> to source-generated code in
    /// user assemblies. EntityIndex is internal-only API, so user code should
    /// use the <see cref="EntityHandle"/> overloads on <see cref="WorldAccessor"/>
    /// directly. These extensions exist so that aspect-emitted methods and
    /// other generated code can keep using indices without re-flowing through
    /// a handle round-trip.
    /// </summary>
    public static class WorldAccessorInternalExtensions
    {
        [MethodImpl(MethodImplOptions.AggressiveInlining)]
        public static void MoveTo(this WorldAccessor world, EntityIndex entityIndex, TagSet tags) =>
            world.MoveTo(entityIndex, tags);

        [MethodImpl(MethodImplOptions.AggressiveInlining)]
        public static void MoveTo<T1>(this WorldAccessor world, EntityIndex entityIndex)
            where T1 : struct, ITag => world.MoveTo<T1>(entityIndex);

        [MethodImpl(MethodImplOptions.AggressiveInlining)]
        public static void MoveTo<T1, T2>(this WorldAccessor world, EntityIndex entityIndex)
            where T1 : struct, ITag
            where T2 : struct, ITag => world.MoveTo<T1, T2>(entityIndex);

        [MethodImpl(MethodImplOptions.AggressiveInlining)]
        public static void MoveTo<T1, T2, T3>(this WorldAccessor world, EntityIndex entityIndex)
            where T1 : struct, ITag
            where T2 : struct, ITag
            where T3 : struct, ITag => world.MoveTo<T1, T2, T3>(entityIndex);

        [MethodImpl(MethodImplOptions.AggressiveInlining)]
        public static void MoveTo<T1, T2, T3, T4>(this WorldAccessor world, EntityIndex entityIndex)
            where T1 : struct, ITag
            where T2 : struct, ITag
            where T3 : struct, ITag
            where T4 : struct, ITag => world.MoveTo<T1, T2, T3, T4>(entityIndex);

        [MethodImpl(MethodImplOptions.AggressiveInlining)]
        public static void AddTag<T>(this WorldAccessor world, EntityIndex entityIndex)
            where T : struct, ITag => world.AddTag<T>(entityIndex);

        [MethodImpl(MethodImplOptions.AggressiveInlining)]
        public static void SetTag<T>(this WorldAccessor world, EntityIndex entityIndex)
            where T : struct, ITag => world.SetTag<T>(entityIndex);

        [MethodImpl(MethodImplOptions.AggressiveInlining)]
        public static void RemoveTag<T>(this WorldAccessor world, EntityIndex entityIndex)
            where T : struct, ITag => world.RemoveTag<T>(entityIndex);

        [MethodImpl(MethodImplOptions.AggressiveInlining)]
        public static void RemoveEntity(this WorldAccessor world, EntityIndex entityIndex) =>
            world.RemoveEntity(entityIndex);

        [MethodImpl(MethodImplOptions.AggressiveInlining)]
        public static ComponentAccessor<T> Component<T>(
            this WorldAccessor world,
            EntityIndex entityIndex
        )
            where T : unmanaged, IEntityComponent => world.Component<T>(entityIndex);

        [MethodImpl(MethodImplOptions.AggressiveInlining)]
        public static bool TryComponent<T>(
            this WorldAccessor world,
            EntityIndex entityIndex,
            out ComponentAccessor<T> componentRef
        )
            where T : unmanaged, IEntityComponent =>
            world.TryComponent(entityIndex, out componentRef);

        [MethodImpl(MethodImplOptions.AggressiveInlining)]
        public static EntityAccessor Entity(this WorldAccessor world, EntityIndex entityIndex) =>
            world.Entity(entityIndex);

        [MethodImpl(MethodImplOptions.AggressiveInlining)]
        public static EntityHandle GetEntityHandle(
            this WorldAccessor world,
            EntityIndex entityIndex
        ) => world.GetEntityHandle(entityIndex);

        [MethodImpl(MethodImplOptions.AggressiveInlining)]
        public static EntityIndex GetEntityIndex(
            this WorldAccessor world,
            EntityHandle entityHandle
        ) => world.GetEntityIndex(entityHandle);

        [MethodImpl(MethodImplOptions.AggressiveInlining)]
        public static bool TryGetEntityIndex(
            this WorldAccessor world,
            EntityHandle entityHandle,
            out EntityIndex entityIndex
        ) => world.TryGetEntityIndex(entityHandle, out entityIndex);

        [MethodImpl(MethodImplOptions.AggressiveInlining)]
        public static void AddInput<T>(
            this WorldAccessor world,
            EntityIndex entityIndex,
            in T value
        )
            where T : unmanaged, IEntityComponent => world.AddInput(entityIndex, value);

        [MethodImpl(MethodImplOptions.AggressiveInlining)]
        public static EntityIndex GlobalEntityIndex(this WorldAccessor world) =>
            world.GlobalEntityIndex;
    }
}
