using Unity.Burst;
using Unity.Collections;
using Unity.Jobs;
using Unity.Mathematics;
using UnityEngine;
using Random = Unity.Mathematics.Random;

namespace Trecs.Samples.JobSystem
{
    public partial class ParticleSpawnerSystem : ISystem
    {
        readonly float _areaSize;
        readonly float _maxSpeed;
        readonly float _particleSize;
        readonly Color _particleColor;
        readonly int _maxPerFrame;

        public ParticleSpawnerSystem(
            float areaSize,
            float maxSpeed,
            float particleSize,
            Color particleColor,
            int maxPerFrame
        )
        {
            _areaSize = areaSize;
            _maxSpeed = maxSpeed;
            _particleSize = particleSize;
            _particleColor = particleColor;
            _maxPerFrame = maxPerFrame;
        }

        public void Execute()
        {
            int desired = World.GlobalComponent<DesiredNumParticles>().Read.Value;
            int current = World.CountEntitiesWithTags<SampleTags.Particle>();
            int delta = desired - current;

            if (delta > 0)
            {
                int count = math.min(delta, _maxPerFrame);

                if (World.GlobalComponent<IsJobsEnabled>().Read.Value)
                {
                    SpawnParticlesAsJob(count);
                }
                else
                {
                    SpawnParticles(count);
                }
            }
            else if (delta < 0)
            {
                RemoveParticles(math.min(-delta, _maxPerFrame));
            }
        }

        void SpawnParticlesAsJob(int count)
        {
            uint baseSeed = World.Rng.NextUint();
            var reservedRefs = World.ReserveEntityHandles(count, Allocator.TempJob);

            var jobHandle = new SpawnParticleJob
            {
                ReservedRefs = reservedRefs,
                Tags = TagSet<SampleTags.Particle>.Value,
                BaseSeed = baseSeed,
                HalfSize = _areaSize / 2f,
                MaxSpeed = _maxSpeed,
                ParticleSize = _particleSize,
            }.ScheduleParallel(World, count);

            reservedRefs.Dispose(jobHandle);
        }

        void SpawnParticles(int count)
        {
            float halfSize = _areaSize / 2f;
            var rng = World.Rng;

            for (int i = 0; i < count; i++)
            {
                var position = new float3(
                    rng.NextFloat(-halfSize, halfSize),
                    rng.NextFloat(-halfSize, halfSize),
                    rng.NextFloat(-halfSize, halfSize)
                );

                var velocity =
                    math.normalize(
                        new float3(
                            rng.NextFloat(-1f, 1f),
                            rng.NextFloat(-1f, 1f),
                            rng.NextFloat(-1f, 1f)
                        )
                    ) * rng.NextFloat(1f, _maxSpeed);

                World
                    .AddEntity<SampleTags.Particle>()
                    .Set(new Position(position))
                    .Set(new Rotation(quaternion.identity))
                    .Set(new Velocity(velocity))
                    .Set(new UniformScale(_particleSize))
                    .Set(new ColorComponent(HsvToRgb(rng.Next(), 0.8f, 1f)));
            }
        }

        void RemoveParticles(int count)
        {
            int removed = 0;

            foreach (var entity in World.Query().WithTags<SampleTags.Particle>().Entities())
            {
                entity.Remove();
                removed++;
                if (removed >= count)
                    return;
            }
        }

        // Unity has Color.HSVToRGB but this isn't burst compatible
        static Color HsvToRgb(float h, float s, float v)
        {
            float c = v * s;
            float x = c * (1f - math.abs(math.fmod(h * 6f, 2f) - 1f));
            float m = v - c;

            float3 rgb;
            float sector = h * 6f;

            if (sector < 1f)
                rgb = new float3(c, x, 0f);
            else if (sector < 2f)
                rgb = new float3(x, c, 0f);
            else if (sector < 3f)
                rgb = new float3(0f, c, x);
            else if (sector < 4f)
                rgb = new float3(0f, x, c);
            else if (sector < 5f)
                rgb = new float3(x, 0f, c);
            else
                rgb = new float3(c, 0f, x);

            return new Color(rgb.x + m, rgb.y + m, rgb.z + m, 1f);
        }

        [BurstCompile]
        partial struct SpawnParticleJob : IJobFor
        {
            [FromWorld]
            public NativeWorldAccessor World;

            [ReadOnly]
            public NativeArray<EntityHandle> ReservedRefs;
            public TagSet Tags;
            public uint BaseSeed;
            public float HalfSize;
            public float MaxSpeed;
            public float ParticleSize;

            public void Execute(int i)
            {
                var rng = new Random(BaseSeed + (uint)i * 0x9E3779B9u + 1);

                var position = new float3(
                    rng.NextFloat(-HalfSize, HalfSize),
                    rng.NextFloat(-HalfSize, HalfSize),
                    rng.NextFloat(-HalfSize, HalfSize)
                );

                var velocity =
                    math.normalize(
                        new float3(
                            rng.NextFloat(-1f, 1f),
                            rng.NextFloat(-1f, 1f),
                            rng.NextFloat(-1f, 1f)
                        )
                    ) * rng.NextFloat(1f, MaxSpeed);

                var color = HsvToRgb(rng.NextFloat(), 0.8f, 1f);

                World
                    .AddEntity(Tags, (uint)i, ReservedRefs[i])
                    .Set(new Position(position))
                    .Set(new Rotation(quaternion.identity))
                    .Set(new Velocity(velocity))
                    .Set(new UniformScale(ParticleSize))
                    .Set(new ColorComponent(color));
            }
        }
    }
}
