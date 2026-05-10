using System;

namespace Trecs.Samples.Pointers
{
    /// <summary>
    /// Pointers stored in components MUST be disposed manually when entities
    /// are removed. This handler subscribes to the OnRemoved event for patrol
    /// followers and disposes their Route (SharedPtr) and Trail (UniquePtr)
    /// pointers — without this, disposed pointers would leak and Trecs would
    /// emit warnings.
    /// </summary>
    public partial class PatrolFollowerCleanup : IDisposable
    {
        readonly DisposeCollection _disposables = new();

        public PatrolFollowerCleanup(World world)
        {
            World = world.CreateAccessor(AccessorRole.Fixed);

            World
                .Events.EntitiesWithTags<PatrolTags.Follower>()
                .OnRemoved(OnFollowerRemoved)
                .AddTo(_disposables);
        }

        WorldAccessor World { get; }

        [ForEachEntity]
        void OnFollowerRemoved(in Route route, in Trail trail)
        {
            // Dispose SharedPtr: decrements refcount.
            // Object is freed when the last clone is disposed.
            route.Value.Dispose(World);

            // Dispose UniquePtr: returns object to pool.
            trail.Value.Dispose(World);
        }

        public void Dispose()
        {
            _disposables.Dispose();
        }
    }
}
