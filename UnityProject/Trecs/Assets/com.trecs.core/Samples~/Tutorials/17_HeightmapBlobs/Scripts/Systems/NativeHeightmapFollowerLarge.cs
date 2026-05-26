using Unity.Mathematics;

namespace Trecs.Samples.HeightmapBlobs
{
    /// <summary>
    /// BlobBuilder-built native flavor: identical job shape to
    /// <see cref="NativeHeightmapFollower"/>, but the resolved blob is
    /// <see cref="NativeHeightmapDataLarge"/>, whose <c>Heights</c> field is
    /// a <see cref="BlobArray{T}"/> with relative-offset storage living in
    /// the same allocation. Reads index directly into <c>Heights</c>;
    /// Burst inlines the offset-from-self arithmetic to a direct memory
    /// read.
    /// </summary>
    [ExecuteAfter(typeof(CharacterMover))]
    public partial class NativeHeightmapFollowerLarge : ISystem
    {
        [ForEachEntity(typeof(SampleTags.Character), typeof(SampleTags.NativeFollowerLarge))]
        [WrapAsJob]
        static void Execute(
            in NativeHeightmapRefLarge heightmap,
            ref Position position,
            in NativeWorldAccessor world
        )
        {
            ref readonly var data = ref heightmap.Value.Read(world).Value;

            HeightmapBuilder.ComputeBilinearWeights(
                position.Value.x,
                position.Value.z,
                in data.Descriptor,
                out int x0,
                out int x1,
                out int z0,
                out int z1,
                out float fx,
                out float fz
            );

            int res = data.Descriptor.Resolution;
            float h00 = data.Heights[z0 * res + x0];
            float h10 = data.Heights[z0 * res + x1];
            float h01 = data.Heights[z1 * res + x0];
            float h11 = data.Heights[z1 * res + x1];

            float h0 = math.lerp(h00, h10, fx);
            float h1 = math.lerp(h01, h11, fx);
            position.Value.y = math.lerp(h0, h1, fz);
        }
    }
}
