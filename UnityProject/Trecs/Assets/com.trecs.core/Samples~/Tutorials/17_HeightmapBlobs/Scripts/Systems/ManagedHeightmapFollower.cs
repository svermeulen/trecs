namespace Trecs.Samples.HeightmapBlobs
{
    /// <summary>
    /// Managed flavor: dereferences the <see cref="SharedPtr{T}"/> on the
    /// main thread, samples the heightmap class instance, and writes the
    /// resulting Y into <see cref="Position"/>.
    ///
    /// All entities sampling the same shared blob see identical heights —
    /// the data lives once on the world's shared heap, and every entity
    /// holds just a 12-byte handle into it. Compare with
    /// <see cref="NativeHeightmapFollower"/> for the Burst-job variant.
    /// </summary>
    [ExecuteAfter(typeof(CharacterMover))]
    public partial class ManagedHeightmapFollower : ISystem
    {
        [ForEachEntity(typeof(SampleTags.Character), typeof(SampleTags.ManagedFollower))]
        void Execute(in ManagedHeightmapRef heightmap, ref Position position)
        {
            var data = heightmap.Value.Get(World);
            float y = HeightmapBuilder.SampleManaged(data, position.Value.x, position.Value.z);
            position.Value.y = y;
        }
    }
}
