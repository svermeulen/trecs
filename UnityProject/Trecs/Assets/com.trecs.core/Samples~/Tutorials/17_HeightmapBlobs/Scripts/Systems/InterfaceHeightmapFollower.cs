namespace Trecs.Samples.HeightmapBlobs
{
    /// <summary>
    /// Interface-route managed flavor: dereferences the
    /// <see cref="SharedPtr{T}"/> on the main thread, but the resolved type
    /// is <see cref="IReadOnlyHeightmapData"/> rather than a concrete class.
    /// The underlying <see cref="MutableHeightmapData"/> is reachable only
    /// through the read-only interface from here — no path to its mutable
    /// fields without an explicit downcast.
    ///
    /// <para>Functionally identical to <see cref="ManagedHeightmapFollower"/>;
    /// the difference is purely on the type axis — what the
    /// <see cref="SharedPtr{T}"/> is parameterised on.</para>
    /// </summary>
    [ExecuteAfter(typeof(CharacterMover))]
    public partial class InterfaceHeightmapFollower : ISystem
    {
        [ForEachEntity(typeof(SampleTags.Character), typeof(SampleTags.InterfaceFollower))]
        void Execute(in InterfaceHeightmapRef heightmap, ref Position position)
        {
            IReadOnlyHeightmapData data = heightmap.Value.Get(World);
            float y = HeightmapBuilder.SampleInterface(data, position.Value.x, position.Value.z);
            position.Value.y = y;
        }
    }
}
