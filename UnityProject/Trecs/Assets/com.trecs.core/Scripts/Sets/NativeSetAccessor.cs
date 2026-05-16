using System.Runtime.CompilerServices;

namespace Trecs
{
    /// <summary>
    /// Burst-compatible set gateway returned by <see cref="NativeWorldAccessor.Set{T}"/>.
    /// All operations are deferred — Burst jobs cannot sync, so there is no
    /// <c>.Read</c> / <c>.Write</c> counterpart to <see cref="SetAccessor{T}"/>
    /// on the native side. Operations are buffered with sort keys to ensure
    /// deterministic ordering when multiple jobs write concurrently, and are
    /// applied during the next <c>SubmitEntities()</c> call on the main thread.
    /// </summary>
    public readonly struct NativeSetAccessor<T>
        where T : struct, IEntitySet
    {
        readonly NativeWorldAccessor _native;

        internal NativeSetAccessor(in NativeWorldAccessor native)
        {
            _native = native;
        }

        /// <summary>
        /// Queue an Add for the next submission. Job-safe — writes to the
        /// worker's own slot in the per-set deferred queue.
        /// </summary>
        [MethodImpl(MethodImplOptions.AggressiveInlining)]
        public void DeferredAdd(EntityIndex entityIndex) => _native.DeferredSetAdd<T>(entityIndex);

        /// <summary>
        /// Queue an Add for the next submission. See
        /// <see cref="DeferredAdd(EntityIndex)"/>.
        /// </summary>
        [MethodImpl(MethodImplOptions.AggressiveInlining)]
        public void DeferredAdd(EntityHandle entityHandle) =>
            _native.DeferredSetAdd<T>(entityHandle);

        /// <summary>
        /// Queue a Remove for the next submission. Job-safe.
        /// </summary>
        [MethodImpl(MethodImplOptions.AggressiveInlining)]
        public void DeferredRemove(EntityIndex entityIndex) =>
            _native.DeferredSetRemove<T>(entityIndex);

        /// <summary>
        /// Queue a Remove for the next submission. See
        /// <see cref="DeferredRemove(EntityIndex)"/>.
        /// </summary>
        [MethodImpl(MethodImplOptions.AggressiveInlining)]
        public void DeferredRemove(EntityHandle entityHandle) =>
            _native.DeferredSetRemove<T>(entityHandle);

        /// <summary>
        /// Queue a Clear for the next submission. The clear supersedes any
        /// pending <see cref="DeferredAdd(EntityIndex)"/> /
        /// <see cref="DeferredRemove(EntityIndex)"/> for the same set,
        /// regardless of call order or originating thread.
        /// </summary>
        [MethodImpl(MethodImplOptions.AggressiveInlining)]
        public void DeferredClear() => _native.DeferredSetClear<T>();
    }
}
