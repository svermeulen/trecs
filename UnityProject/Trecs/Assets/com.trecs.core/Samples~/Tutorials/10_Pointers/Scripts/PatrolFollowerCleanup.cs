using System;

namespace Trecs.Samples.Pointers
{
    /// <summary>
    /// Pointers stored in components MUST be disposed manually when entities
    /// are removed. This handler subscribes to the OnRemoved event for patrol
    /// followers and disposes their <see cref="Trail"/> pointer — without
    /// this, the <see cref="TrailHistory"/> object would leak and Trecs
    /// would emit warnings.
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
        void OnFollowerRemoved(in Trail trail)
        {
            // Dispose UniquePtr: returns the managed object to the pool.
            trail.Value.Dispose(World);
        }

        public void Dispose()
        {
            _disposables.Dispose();
        }
    }
}
