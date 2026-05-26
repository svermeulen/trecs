using Trecs.Internal;
using Unity.Mathematics;

namespace Trecs.Samples.HeightmapBlobs
{
    /// <summary>
    /// Native flavor: a [WrapAsJob] Burst-compiled job resolves the
    /// <see cref="NativeSharedPtr{T}"/> via the
    /// <see cref="NativeSharedPtrResolver"/> on
    /// <see cref="NativeWorldAccessor.SharedPtrResolver"/>, samples the
    /// unmanaged heightmap inline, and writes the resulting Y into
    /// <see cref="Position"/>.
    ///
    /// Per-blob <c>AtomicSafetyHandle</c>s are read-only, so multiple jobs
    /// can read the same shared blob in parallel without coordination. The
    /// source generator turns this static method into a Burst-compiled job
    /// struct that fans out across the worker pool.
    /// </summary>
    [ExecuteAfter(typeof(CharacterMover))]
    public partial class NativeHeightmapFollower : ISystem
    {
        [ForEachEntity(typeof(SampleTags.Character), typeof(SampleTags.NativeFollower))]
        [WrapAsJob]
        static void Execute(
            in NativeHeightmapRef heightmap,
            ref Position position,
            in NativeWorldAccessor world
        )
        {
            // Resolve the handle via the resolver. In Burst, this returns a
            // NativeSharedRead<T> wrapper with the per-blob safety handle
            // attached, so Unity's job-safety walker treats this as a
            // legitimate read.
            ref readonly var data = ref heightmap.Value.Read(world).Value;

            // Inline bilinear sample — same arithmetic as
            // HeightmapBuilder.SampleManaged, but reading the inline
            // FixedArray256 directly so Burst can inline everything. We
            // don't call the shared helper because passing a managed-array
            // delegate would bounce out of Burst.
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
            float h00 = data.Get(z0 * res + x0);
            float h10 = data.Get(z0 * res + x1);
            float h01 = data.Get(z1 * res + x0);
            float h11 = data.Get(z1 * res + x1);

            float h0 = math.lerp(h00, h10, fx);
            float h1 = math.lerp(h01, h11, fx);
            position.Value.y = math.lerp(h0, h1, fz);
        }
    }
}
