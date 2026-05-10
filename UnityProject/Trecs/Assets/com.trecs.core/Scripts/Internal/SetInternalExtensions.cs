using System.Runtime.CompilerServices;

namespace Trecs.Internal
{
    /// <summary>
    /// Extension methods that expose the <see cref="EntityIndex"/>-based
    /// overloads of the public set views (<see cref="SetWrite{T}"/>,
    /// <see cref="SetRead{T}"/>, <see cref="SetDefer{T}"/>,
    /// <see cref="NativeSetCommandBuffer{T}"/>, <see cref="NativeSetRead{T}"/>,
    /// <see cref="SetByIdAccessor"/>) to source-generated code in user
    /// assemblies. EntityIndex is internal-only API; user code should use the
    /// <see cref="EntityHandle"/> overloads on the set views directly.
    /// </summary>
    public static class SetInternalExtensions
    {
        // ── SetWrite<T> ───────────────────────────────────────────────────

        [MethodImpl(MethodImplOptions.AggressiveInlining)]
        public static void Add<T>(this SetWrite<T> set, EntityIndex entityIndex)
            where T : struct, IEntitySet => set.Add(entityIndex);

        [MethodImpl(MethodImplOptions.AggressiveInlining)]
        public static void Remove<T>(this SetWrite<T> set, EntityIndex entityIndex)
            where T : struct, IEntitySet => set.Remove(entityIndex);

        [MethodImpl(MethodImplOptions.AggressiveInlining)]
        public static bool Exists<T>(this SetWrite<T> set, EntityIndex entityIndex)
            where T : struct, IEntitySet => set.Exists(entityIndex);

        // ── SetRead<T> ────────────────────────────────────────────────────

        [MethodImpl(MethodImplOptions.AggressiveInlining)]
        public static bool Exists<T>(this SetRead<T> set, EntityIndex entityIndex)
            where T : struct, IEntitySet => set.Exists(entityIndex);

        // ── SetDefer<T> ───────────────────────────────────────────────────

        [MethodImpl(MethodImplOptions.AggressiveInlining)]
        public static void Add<T>(this SetDefer<T> set, EntityIndex entityIndex)
            where T : struct, IEntitySet => set.Add(entityIndex);

        [MethodImpl(MethodImplOptions.AggressiveInlining)]
        public static void Remove<T>(this SetDefer<T> set, EntityIndex entityIndex)
            where T : struct, IEntitySet => set.Remove(entityIndex);

        // ── NativeSetCommandBuffer<T> ─────────────────────────────────────

        [MethodImpl(MethodImplOptions.AggressiveInlining)]
        public static void Add<T>(this NativeSetCommandBuffer<T> set, EntityIndex entityIndex)
            where T : struct, IEntitySet => set.Add(entityIndex);

        [MethodImpl(MethodImplOptions.AggressiveInlining)]
        public static void Remove<T>(this NativeSetCommandBuffer<T> set, EntityIndex entityIndex)
            where T : struct, IEntitySet => set.Remove(entityIndex);

        // ── NativeSetRead<T> ──────────────────────────────────────────────

        [MethodImpl(MethodImplOptions.AggressiveInlining)]
        public static bool Exists<T>(this NativeSetRead<T> set, EntityIndex entityIndex)
            where T : struct, IEntitySet => set.Exists(entityIndex);

        // ── SetByIdAccessor ───────────────────────────────────────────────

        [MethodImpl(MethodImplOptions.AggressiveInlining)]
        public static bool Exists(this SetByIdAccessor set, EntityIndex entityIndex) =>
            set.Exists(entityIndex);
    }
}
