using Trecs.Collections;
using Unity.Mathematics;
using UnityEngine;

namespace Trecs.Samples.NativePointers
{
    public class SceneInitializer
    {
        readonly WorldAccessor _world;
        readonly GameObjectRegistry _gameObjectRegistry;
        readonly int _entitiesPerRoute;

        public SceneInitializer(
            World world,
            GameObjectRegistry gameObjectRegistry,
            int entitiesPerRoute
        )
        {
            _world = world.CreateAccessor();
            _gameObjectRegistry = gameObjectRegistry;
            _entitiesPerRoute = entitiesPerRoute;
        }

        public void Initialize()
        {
            // ─── Allocate shared routes ─────────────────────────────
            // AllocNativeShared stores the unmanaged PatrolRoute struct in the
            // native heap and returns a 12-byte NativeSharedPtr handle.
            // Compare with Sample 10's AllocShared: same refcount semantics,
            // but the payload type must be unmanaged for Burst compatibility.
            var circleRoute = _world.Heap.AllocNativeShared(
                BuildRoute(new float3(-8, 0, 0), RouteShape.Circle, Color.cyan, 2f)
            );

            var figure8Route = _world.Heap.AllocNativeShared(
                BuildRoute(float3.zero, RouteShape.Figure8, Color.yellow, 3f)
            );

            var starRoute = _world.Heap.AllocNativeShared(
                BuildRoute(new float3(8, 0, 0), RouteShape.Star, Color.magenta, 2.5f)
            );

            // ─── Spawn followers ────────────────────────────────────
            SpawnFollowers(circleRoute, _entitiesPerRoute);
            SpawnFollowers(figure8Route, _entitiesPerRoute);
            SpawnFollowers(starRoute, _entitiesPerRoute);

            // ─── Dispose original NativeSharedPtrs ──────────────────
            // Each entity holds its own Clone. The originals are no longer
            // needed — dispose decrements refcount. The blobs stay alive
            // because entity clones still reference them.
            circleRoute.Dispose(_world);
            figure8Route.Dispose(_world);
            starRoute.Dispose(_world);
        }

        void SpawnFollowers(NativeSharedPtr<PatrolRoute> routePtr, int count)
        {
            ref readonly var route = ref routePtr.Get(_world);
            int waypointCount = route.Waypoints.Count;

            for (int i = 0; i < count; i++)
            {
                float progress = (float)i / count * waypointCount;

                int idx = (int)progress;
                int nextIdx = (idx + 1) % waypointCount;
                float t = progress - idx;
                var pos = math.lerp(route.Waypoints[idx], route.Waypoints[nextIdx], t);

                var go = SampleUtil.CreatePrimitive(PrimitiveType.Sphere);
                go.name = $"NativeFollower_{i}";
                go.transform.position = (Vector3)pos;
                go.transform.localScale = Vector3.one * 0.5f;
                go.GetComponent<Renderer>().material.color = route.Color;

                var lr = go.AddComponent<LineRenderer>();
                lr.startWidth = 0.15f;
                lr.endWidth = 0.02f;
                lr.material = new Material(Shader.Find("Sprites/Default"));
                lr.startColor = route.Color;
                lr.endColor = new Color(route.Color.r, route.Color.g, route.Color.b, 0.3f);
                lr.positionCount = 0;

                // ─── Clone NativeSharedPtr ──────────────────────────
                // Clone increments the refcount. Each entity gets its own
                // handle pointing at the same PatrolRoute blob.
                var routeClone = routePtr.Clone(_world);

                // ─── Allocate NativeUniquePtr ───────────────────────
                // Each entity gets its own TrailHistory; Positions defaults to
                // an empty FixedList, which Add() will populate over time.
                var trailPtr = _world.Heap.AllocNativeUnique(new TrailHistory { MaxLength = 40 });

                _world
                    .AddEntity<NativePatrolTags.Follower>()
                    .Set(new Position(pos))
                    .Set(new Route { Value = routeClone, Progress = progress })
                    .Set(new Trail { Value = trailPtr })
                    .Set(_gameObjectRegistry.Register(go));
            }
        }

        enum RouteShape
        {
            Circle,
            Figure8,
            Star,
        }

        static PatrolRoute BuildRoute(float3 center, RouteShape shape, Color color, float speed)
        {
            var waypoints = new FixedList64<float3>();
            switch (shape)
            {
                case RouteShape.Circle:
                    AppendCircle(ref waypoints, center, 3f, 20);
                    break;
                case RouteShape.Figure8:
                    AppendFigure8(ref waypoints, center, 4f, 24);
                    break;
                case RouteShape.Star:
                    AppendStar(ref waypoints, center, 4f, 1.5f, 5);
                    break;
            }
            return new PatrolRoute(waypoints, color, speed);
        }

        static void AppendCircle(
            ref FixedList64<float3> waypoints,
            float3 center,
            float radius,
            int count
        )
        {
            for (int i = 0; i < count; i++)
            {
                float angle = 2f * math.PI * i / count;
                waypoints.Add(
                    center + new float3(math.cos(angle) * radius, 0, math.sin(angle) * radius)
                );
            }
        }

        static void AppendFigure8(
            ref FixedList64<float3> waypoints,
            float3 center,
            float size,
            int count
        )
        {
            for (int i = 0; i < count; i++)
            {
                float t = 2f * math.PI * i / count;
                waypoints.Add(
                    center + new float3(size * math.sin(t), 0, size * math.sin(t) * math.cos(t))
                );
            }
        }

        static void AppendStar(
            ref FixedList64<float3> waypoints,
            float3 center,
            float outerRadius,
            float innerRadius,
            int points
        )
        {
            int vertexCount = points * 2;
            var vertices = new FixedList64<float3>();

            for (int i = 0; i < vertexCount; i++)
            {
                float angle = math.PI / 2f + 2f * math.PI * i / vertexCount;
                float r = (i % 2 == 0) ? outerRadius : innerRadius;
                vertices.Add(center + new float3(math.cos(angle) * r, 0, math.sin(angle) * r));
            }

            int segmentsPerEdge = 3;
            for (int i = 0; i < vertexCount; i++)
            {
                var a = vertices[i];
                var b = vertices[(i + 1) % vertexCount];
                for (int s = 0; s < segmentsPerEdge; s++)
                {
                    float frac = (float)s / segmentsPerEdge;
                    waypoints.Add(math.lerp(a, b, frac));
                }
            }
        }
    }
}
