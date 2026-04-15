using System.Collections.Generic;
using Unity.Mathematics;
using UnityEngine;
using UnityEngine.Rendering;

namespace Trecs.Samples.JobSystem
{
    /// <summary>
    /// Renders all particles via Graphics.RenderMeshInstanced — no GameObjects.
    /// Collects entity positions into a Matrix4x4 array each frame and draws
    /// them as a single instanced batch.
    /// </summary>
    [VariableUpdate]
    public partial class ParticleRendererSystem : ISystem
    {
        readonly Mesh _mesh;
        readonly Material _material;
        readonly RenderParams _renderParams;
        readonly float _particleSize;

        List<Matrix4x4> _matrices = new();

        public ParticleRendererSystem(Mesh mesh, Material material, float particleSize)
        {
            _mesh = mesh;
            _material = material;
            _particleSize = particleSize;
            _renderParams = new RenderParams(material)
            {
                receiveShadows = false,
                shadowCastingMode = ShadowCastingMode.Off,
            };
        }

        public void Execute()
        {
            _matrices.Clear();

            var groups = World.WorldInfo.GetGroupsWithTags<SampleTags.Particle>();
            var scale = new float3(_particleSize);

            foreach (var group in groups)
            {
                var positions = World.ComponentBuffer<Position>(group).Read;
                var count = World.CountEntitiesInGroup(group);

                for (int i = 0; i < count; i++)
                {
                    _matrices.Add(float4x4.TRS(positions[i].Value, quaternion.identity, scale));
                }
            }

            if (_matrices.Count > 0)
            {
                Graphics.RenderMeshInstanced(_renderParams, _mesh, 0, _matrices);
            }
        }
    }
}
