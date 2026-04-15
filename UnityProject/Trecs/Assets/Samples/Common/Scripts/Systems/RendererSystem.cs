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
    [VariableUpdate]
    public partial class RendererSystem : ISystem, IDisposable
    {
        readonly GraphicsBuffer.IndirectDrawIndexedArgs[] _argsCache =
            new GraphicsBuffer.IndirectDrawIndexedArgs[1];

        readonly List<RenderableInfo> _renderables = new();

        public void RegisterRenderable(TagSet tags, Mesh mesh, Material material, int maxAmount)
        {
            Assert.That(!_renderables.Where(info => info.Tags == tags).Any());

            _renderables.Add(
                new RenderableInfo
                {
                    Tags = tags,
                    Mesh = mesh,
                    Material = material,
                    MaxAmount = maxAmount,
                }
            );
        }

        partial void OnReady()
        {
            var groupsProcessed = new HashSet<Group>();

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

                info.CommandBuffer = new GraphicsBuffer(
                    GraphicsBuffer.Target.IndirectArguments,
                    1,
                    GraphicsBuffer.IndirectDrawIndexedArgs.size
                );

                info.InstanceBuffer = new GraphicsBuffer(
                    GraphicsBuffer.Target.Structured,
                    GraphicsBuffer.UsageFlags.LockBufferForWrite,
                    info.MaxAmount,
                    UnsafeUtility.SizeOf<InstanceData>()
                );

                var matProps = new MaterialPropertyBlock();
                matProps.SetBuffer("_InstanceData", info.InstanceBuffer);

                info.RenderParams = new RenderParams(info.Material)
                {
                    receiveShadows = false,
                    shadowCastingMode = ShadowCastingMode.Off,
                    matProps = matProps,
                    worldBounds = new Bounds(Vector3.zero, Vector3.one * 10000),
                };
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
            }
        }

        public void Execute()
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
            public Material Material;
            public int MaxAmount;
            public int CachedCount;

            public RenderParams RenderParams;
            public GraphicsBuffer InstanceBuffer;
            public GraphicsBuffer CommandBuffer;
        }
    }
}
