using System;

namespace Trecs.Samples.NativePointers
{
    /// <summary>
    /// Disposes the NativeSharedPtr and NativeUniquePtr stored on a follower
    /// when the entity is removed. Subscribing to OnRemoved is mandatory —
    /// native pointers stored in components do not free themselves when the
    /// entity disappears, and an undisposed handle leaks the underlying blob.
    /// </summary>
    public partial class PointerCleanupHandler : IDisposable
    {
        readonly DisposeCollection _disposables = new();

        public PointerCleanupHandler(World world)
        {
            World = world.CreateAccessor(AccessorRole.Fixed);

            World
                .Events.EntitiesWithTags<NativePatrolTags.Follower>()
                .OnRemoved(OnFollowerRemoved)
                .AddTo(_disposables);
        }

        WorldAccessor World { get; }

        [ForEachEntity]
        void OnFollowerRemoved(in Route route, in Trail trail)
        {
            // Dispose NativeSharedPtr: decrements the refcount. The blob is
            // freed once the last clone is disposed.
            route.Value.Dispose(World);

            // Dispose NativeUniquePtr: releases this entity's exclusive blob.
            trail.Value.Dispose(World);
        }

        public void Dispose()
        {
            _disposables.Dispose();
        }
    }
}
