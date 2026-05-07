using System;
using System.Collections.Generic;
using System.Linq;
using Trecs.Internal;
using Unity.Burst;
using Unity.Collections;
using Unity.Collections.LowLevel.Unsafe;
using Unity.Jobs;
using UnityEngine;
using UnityEngine.Rendering;

namespace Trecs.Samples
{
    [ExecuteIn(SystemPhase.Presentation)]
    public partial class RendererSystem : ISystem, IDisposable
    {
        // Batch size for the fallback path. Must match the shader's
        // `instancing_options maxcount:250`. 250 keeps the instanced cbuffer
        // under the 16 KiB minimum guaranteed by WebGL 2 / GLES 3.0
        // (3 × 250 × 16 B ≈ 12 KB).
        const int FallbackBatchSize = 250;

        readonly bool _supportsIndirect;

        readonly GraphicsBuffer.IndirectDrawIndexedArgs[] _argsCache;

        readonly List<RenderableInfo> _renderables = new();

        readonly Matrix4x4[] _fallbackMatrices;
        readonly Vector4[] _fallbackPosScales;
        readonly Vector4[] _fallbackRotations;
        readonly Vector4[] _fallbackColors;

        public RendererSystem()
        {
            _supportsIndirect = !SampleRenderingPath.UseFallback;

            if (_supportsIndirect)
            {
                _argsCache = new GraphicsBuffer.IndirectDrawIndexedArgs[1];
            }
            else
            {
                _fallbackMatrices = new Matrix4x4[FallbackBatchSize];
                _fallbackPosScales = new Vector4[FallbackBatchSize];
                _fallbackRotations = new Vector4[FallbackBatchSize];
                _fallbackColors = new Vector4[FallbackBatchSize];
                for (int i = 0; i < FallbackBatchSize; i++)
                    _fallbackMatrices[i] = Matrix4x4.identity;
            }
        }

        public void RegisterRenderable(TagSet tags, Mesh mesh, Material material, int maxAmount)
        {
            Assert.That(!_renderables.Where(info => info.Tags == tags).Any());

            var matProps = new MaterialPropertyBlock();
            var info = new RenderableInfo { Tags = tags, Mesh = mesh };

            if (_supportsIndirect)
            {
                info.CommandBuffer = new GraphicsBuffer(
                    GraphicsBuffer.Target.IndirectArguments,
                    1,
                    GraphicsBuffer.IndirectDrawIndexedArgs.size
                );

                info.InstanceBuffer = new GraphicsBuffer(
                    GraphicsBuffer.Target.Structured,
                    GraphicsBuffer.UsageFlags.LockBufferForWrite,
                    maxAmount,
                    UnsafeUtility.SizeOf<InstanceData>()
                );

                matProps.SetBuffer("_InstanceData", info.InstanceBuffer);
            }
            else
            {
                info.InstanceNative = new NativeArray<InstanceData>(
                    maxAmount,
                    Allocator.Persistent,
                    NativeArrayOptions.UninitializedMemory
                );
                info.MatProps = matProps;
            }

            info.RenderParams = new RenderParams(material)
            {
                receiveShadows = false,
                shadowCastingMode = ShadowCastingMode.Off,
                matProps = matProps,
                worldBounds = new Bounds(Vector3.zero, Vector3.one * 10000),
            };

            _renderables.Add(info);
        }

        partial void OnReady()
        {
            var groupsProcessed = new HashSet<GroupIndex>();

            foreach (var info in _renderables)
            {
                foreach (var group in World.WorldInfo.GetGroupsWithTags(info.Tags))
                {
                    var wasAdded = groupsProcessed.Add(group);
                    Assert.That(
                        wasAdded,
                        "Attempted to register multiple renderables with overlapping groups"
                    );
                }
            }

            foreach (var group in World.WorldInfo.GetGroupsWithTags<CommonTags.Renderable>())
            {
                var wasRemoved = groupsProcessed.Remove(group);
                Assert.That(wasRemoved, "No renderable registered for group {}", group);
            }
        }

        public void Dispose()
        {
            foreach (var info in _renderables)
            {
                info.InstanceBuffer?.Release();
                info.CommandBuffer?.Release();
                if (info.InstanceNative.IsCreated)
                    info.InstanceNative.Dispose();
            }
        }

        public void Execute()
        {
            if (_supportsIndirect)
                ExecuteIndirect();
            else
                ExecuteFallback();
        }

        void ExecuteIndirect()
        {
            var combined = default(JobHandle);

            foreach (var info in _renderables)
            {
                int total = World.CountEntitiesWithTags(info.Tags);
                info.CachedCount = total;

                if (total == 0)
                {
                    continue;
                }

                Assert.That(info.InstanceBuffer.count >= total);

                combined = JobHandle.CombineDependencies(
                    combined,
                    new BuildInstanceData
                    {
                        Instances = info.InstanceBuffer.LockBufferForWrite<InstanceData>(0, total),
                    }.ScheduleParallel(World.Query().WithTags(info.Tags))
                );
            }

            using (TrecsProfiling.Start("Building native positions buffer"))
            {
                combined.Complete();
            }

            using (TrecsProfiling.Start("Sending data to gpu"))
            {
                foreach (var info in _renderables)
                {
                    if (info.CachedCount == 0)
                        continue;

                    info.InstanceBuffer.UnlockBufferAfterWrite<InstanceData>(info.CachedCount);

                    _argsCache[0] = new GraphicsBuffer.IndirectDrawIndexedArgs
                    {
                        indexCountPerInstance = (uint)info.Mesh.GetIndexCount(0),
                        instanceCount = (uint)info.CachedCount,
                        startIndex = (uint)info.Mesh.GetIndexStart(0),
                        baseVertexIndex = (uint)info.Mesh.GetBaseVertex(0),
                    };
                    info.CommandBuffer.SetData(_argsCache);
                }
            }

            using (TrecsProfiling.Start("Graphics.RenderMeshIndirect"))
            {
                foreach (var info in _renderables)
                {
                    if (info.CachedCount == 0)
                        continue;

                    Graphics.RenderMeshIndirect(info.RenderParams, info.Mesh, info.CommandBuffer);
                }
            }
        }

        void ExecuteFallback()
        {
            var combined = default(JobHandle);

            foreach (var info in _renderables)
            {
                int total = World.CountEntitiesWithTags(info.Tags);
                info.CachedCount = total;

                if (total == 0)
                {
                    continue;
                }

                Assert.That(info.InstanceNative.Length >= total);

                combined = JobHandle.CombineDependencies(
                    combined,
                    new BuildInstanceData
                    {
                        Instances = info.InstanceNative.GetSubArray(0, total),
                    }.ScheduleParallel(World.Query().WithTags(info.Tags))
                );
            }

            using (TrecsProfiling.Start("Building native positions buffer"))
            {
                combined.Complete();
            }

            using (TrecsProfiling.Start("Graphics.RenderMeshInstanced"))
            {
                foreach (var info in _renderables)
                {
                    int count = info.CachedCount;
                    if (count == 0)
                        continue;

                    var src = info.InstanceNative;

                    for (int batchStart = 0; batchStart < count; batchStart += FallbackBatchSize)
                    {
                        int batchCount = Mathf.Min(FallbackBatchSize, count - batchStart);

                        for (int i = 0; i < batchCount; i++)
                        {
                            var inst = src[batchStart + i];
                            _fallbackPosScales[i] = inst.PosScale;
                            _fallbackRotations[i] = inst.Rotation;
                            _fallbackColors[i] = inst.Color;
                        }

                        info.MatProps.SetVectorArray("_PosScale", _fallbackPosScales);
                        info.MatProps.SetVectorArray("_Rotation", _fallbackRotations);
                        info.MatProps.SetVectorArray("_Color", _fallbackColors);

                        Graphics.RenderMeshInstanced(
                            info.RenderParams,
                            info.Mesh,
                            0,
                            _fallbackMatrices,
                            batchCount
                        );
                    }
                }
            }
        }

        public struct InstanceData
        {
            public Vector4 PosScale; // xyz = position, w = scale
            public Vector4 Rotation; // quaternion xyzw
            public Vector4 Color; // rgba per-instance color
        }

        [BurstCompile]
        partial struct BuildInstanceData
        {
            [NativeDisableParallelForRestriction]
            [NativeDisableContainerSafetyRestriction]
            [WriteOnly]
            public NativeArray<InstanceData> Instances;

            // No tag on the attribute — caller passes a builder with the runtime tagset.
            [ForEachEntity]
            public void Execute(
                in Position position,
                in Rotation rotation,
                in UniformScale scale,
                in ColorComponent color,
                [GlobalIndex] int globalIndex
            )
            {
                var pos = position.Value;
                var rot = rotation.Value;
                var col = color.Value;

                Instances[globalIndex] = new InstanceData
                {
                    PosScale = new Vector4(pos.x, pos.y, pos.z, scale.Value),
                    Rotation = new Vector4(rot.value.x, rot.value.y, rot.value.z, rot.value.w),
                    Color = new Vector4(col.r, col.g, col.b, col.a),
                };
            }
        }

        class RenderableInfo
        {
            public TagSet Tags;
            public Mesh Mesh;
            public int CachedCount;

            public RenderParams RenderParams;

            // Indirect (compute) path only.
            public GraphicsBuffer InstanceBuffer;
            public GraphicsBuffer CommandBuffer;

            // Fallback (standard instancing) path only.
            public NativeArray<InstanceData> InstanceNative;
            public MaterialPropertyBlock MatProps;
        }
    }
}
