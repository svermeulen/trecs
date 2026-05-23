using Trecs.Collections;
using Unity.Mathematics;
using UnityEngine;

namespace Trecs.Samples.HeightmapBlobs
{
    /// <summary>
    /// Pure helpers for building and sampling a heightmap. Same noise
    /// function used by both flavors — so given the same
    /// <see cref="HeightmapDescriptor"/> they produce identical heights,
    /// which is the whole point of deriving the <see cref="BlobId"/> from
    /// the descriptor: same recipe ⇒ same content ⇒ same cache slot.
    /// </summary>
    public static class HeightmapBuilder
    {
        /// <summary>
        /// Build a managed heights array. The same descriptor always
        /// produces the same bytes — so once the blob is cached under the
        /// descriptor's content hash, subsequent worlds skip this work
        /// entirely.
        /// </summary>
        public static float[] BuildManagedHeights(in HeightmapDescriptor d)
        {
            var cells = d.Resolution * d.Resolution;
            var heights = new float[cells];

            for (int z = 0; z < d.Resolution; z++)
            {
                for (int x = 0; x < d.Resolution; x++)
                {
                    heights[z * d.Resolution + x] = SampleNoise(x, z, d);
                }
            }

            return heights;
        }

        /// <summary>
        /// Build a fully-populated, immutable <see cref="NativeHeightmapData"/>
        /// from <paramref name="d"/>. Resolution × Resolution must be ≤ 256.
        /// Returns the populated blob by value; the caller hands it off to
        /// <c>NativeSharedPtr.Alloc(world, blobId, in data)</c>, which copies
        /// the bytes onto the native heap. Since <see cref="NativeHeightmapData"/>
        /// is a <c>readonly struct</c>, mutation happens here on a fresh local
        /// <see cref="FixedArray256{T}"/> before the blob is sealed by the
        /// constructor.
        ///
        /// <para>This pattern is the simplest seed path but pays for one
        /// intermediate stack-to-field copy of the ~1 KB array and is capped
        /// at 256 cells. For larger heightmaps — or to eliminate that copy
        /// — see <see cref="SceneInitializer.InitializeNativeLarge"/>, which
        /// uses <c>BlobBuilder</c> to build the heights directly into a
        /// single fresh allocation via a <c>BlobArray&lt;float&gt;</c>.</para>
        /// </summary>
        public static NativeHeightmapData BuildNativeHeightsInline(in HeightmapDescriptor d)
        {
            var heights = default(FixedArray256<float>);

            for (int z = 0; z < d.Resolution; z++)
            {
                for (int x = 0; x < d.Resolution; x++)
                {
                    heights.Mut(z * d.Resolution + x) = SampleNoise(x, z, d);
                }
            }

            return new NativeHeightmapData(d, heights);
        }

        /// <summary>
        /// Sample height at world-space (x, z) on the managed blob via
        /// bilinear interpolation. Returns the edge value when (x, z) lies
        /// outside the surface.
        /// </summary>
        public static float SampleManaged(in HeightmapData data, float worldX, float worldZ)
        {
            ComputeBilinearWeights(
                worldX,
                worldZ,
                data.Descriptor,
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

            return BilinearLerp(h00, h10, h01, h11, fx, fz);
        }

        /// <summary>
        /// Sample height at world-space (x, z) on the interface-route blob.
        /// Same bilinear interpolation as <see cref="SampleManaged"/>; the
        /// only difference is the heights backing store is reached via the
        /// <see cref="IReadOnlyHeightmapData.Heights"/> indexer
        /// (<see cref="System.Collections.Generic.IReadOnlyList{T}"/>)
        /// rather than a <see cref="ReadOnlySpan{T}"/>.
        /// </summary>
        public static float SampleInterface(IReadOnlyHeightmapData data, float worldX, float worldZ)
        {
            ComputeBilinearWeights(
                worldX,
                worldZ,
                data.Descriptor,
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

            return BilinearLerp(h00, h10, h01, h11, fx, fz);
        }

        /// <summary>
        /// Bilinear-weight precompute shared by the managed sample and the
        /// Burst-job-side native sample. Returning by out lets the job
        /// inline the indexer into the unmanaged storage (which would
        /// otherwise need a delegate Burst can't compile).
        /// </summary>
        public static void ComputeBilinearWeights(
            float worldX,
            float worldZ,
            in HeightmapDescriptor d,
            out int x0,
            out int x1,
            out int z0,
            out int z1,
            out float fx,
            out float fz
        )
        {
            // Map world-space [-WorldSize/2, +WorldSize/2] into grid-space
            // [0, Resolution-1]. Outside the surface, clamp to the edge.
            float half = d.WorldSize * 0.5f;
            float u = math.unlerp(-half, +half, worldX) * (d.Resolution - 1);
            float v = math.unlerp(-half, +half, worldZ) * (d.Resolution - 1);

            u = math.clamp(u, 0f, d.Resolution - 1f);
            v = math.clamp(v, 0f, d.Resolution - 1f);

            x0 = (int)math.floor(u);
            z0 = (int)math.floor(v);
            x1 = math.min(x0 + 1, d.Resolution - 1);
            z1 = math.min(z0 + 1, d.Resolution - 1);
            fx = u - x0;
            fz = v - z0;
        }

        public static float BilinearLerp(
            float h00,
            float h10,
            float h01,
            float h11,
            float fx,
            float fz
        )
        {
            float h0 = math.lerp(h00, h10, fx);
            float h1 = math.lerp(h01, h11, fx);
            return math.lerp(h0, h1, fz);
        }

        /// <summary>
        /// Build the surface as a one-off Unity GameObject mesh so the
        /// ECS side stays focused on "characters reading shared blob
        /// data". The mesh just visualises what the characters are
        /// walking on; it has no ECS role.
        /// </summary>
        public static GameObject CreateSurfaceVisual(in HeightmapDescriptor descriptor)
        {
            var surface = new GameObject("Heightmap Surface");
            var meshFilter = surface.AddComponent<MeshFilter>();
            var renderer = surface.AddComponent<MeshRenderer>();

            meshFilter.sharedMesh = BuildSurfaceMesh(descriptor);

            var material = SampleUtil.CreateMaterial(new Color(0.45f, 0.55f, 0.4f));
            // Kill the default plastic-y specular highlight so the surface
            // reads as matte terrain. Property name differs between URP/Lit
            // and Built-in Standard, hence the HasProperty guards.
            if (material.HasProperty("_Smoothness"))
                material.SetFloat("_Smoothness", 0f);
            if (material.HasProperty("_Glossiness"))
                material.SetFloat("_Glossiness", 0f);
            renderer.sharedMaterial = material;

            return surface;
        }

        static Mesh BuildSurfaceMesh(in HeightmapDescriptor d)
        {
            int res = d.Resolution;
            int quadCount = (res - 1) * (res - 1);
            // Flat shading: each triangle gets its own three verts so
            // RecalculateNormals produces a single normal per triangle —
            // gives the low-poly heightmap clean faceting instead of the
            // rubbery look smooth-shared normals produce at this resolution.
            int vertCount = quadCount * 6;

            var verts = new Vector3[vertCount];
            var tris = new int[vertCount];

            float half = d.WorldSize * 0.5f;
            float step = d.WorldSize / (res - 1);

            // Use the managed heights for the visual — same noise function
            // both flavors share, so the mesh matches whatever the
            // characters sample.
            var heights = BuildManagedHeights(d);

            Vector3 VertAt(int x, int z) =>
                new Vector3(-half + x * step, heights[z * res + x], -half + z * step);

            int v = 0;
            for (int z = 0; z < res - 1; z++)
            {
                for (int x = 0; x < res - 1; x++)
                {
                    var p00 = VertAt(x, z);
                    var p10 = VertAt(x + 1, z);
                    var p01 = VertAt(x, z + 1);
                    var p11 = VertAt(x + 1, z + 1);

                    verts[v] = p00;
                    tris[v] = v;
                    v++;
                    verts[v] = p01;
                    tris[v] = v;
                    v++;
                    verts[v] = p10;
                    tris[v] = v;
                    v++;

                    verts[v] = p10;
                    tris[v] = v;
                    v++;
                    verts[v] = p01;
                    tris[v] = v;
                    v++;
                    verts[v] = p11;
                    tris[v] = v;
                    v++;
                }
            }

            var mesh = new Mesh { name = "Heightmap" };
            mesh.vertices = verts;
            mesh.triangles = tris;
            mesh.RecalculateNormals();
            mesh.RecalculateBounds();
            return mesh;
        }

        public static float SampleNoise(int x, int z, in HeightmapDescriptor d)
        {
            // Spread the integer seed across the float2 input so adjacent
            // seeds give visibly different surfaces, not just shifted ones.
            float seedOffset = d.Seed * 0.001f;

            // d.Frequency is roughly the number of hills across the surface.
            float freq = d.Frequency / d.Resolution;
            float n = noise.cnoise(
                new float2((x + seedOffset) * freq, (z + seedOffset * 1.7f) * freq)
            );
            // cnoise returns roughly [-1, 1]; map into [0, MaxHeight].
            float h = (n * 0.5f + 0.5f) * d.MaxHeight;

            // Radial falloff so the surface reads as an island rather than
            // a square slab cut out of infinite noise — heights taper to 0
            // toward the edges.
            float nx = (x / (float)(d.Resolution - 1)) * 2f - 1f;
            float nz = (z / (float)(d.Resolution - 1)) * 2f - 1f;
            float r = math.length(new float2(nx, nz));
            float falloff = 1f - math.smoothstep(0.55f, 1.0f, r);

            return h * falloff;
        }
    }
}
