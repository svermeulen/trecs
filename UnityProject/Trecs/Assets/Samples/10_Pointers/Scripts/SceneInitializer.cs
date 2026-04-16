using System.Collections.Generic;
using Unity.Mathematics;
using UnityEngine;

namespace Trecs.Samples.Pointers
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
            // Each route's waypoint list is a managed List<Vector3> that
            // cannot exist in a struct component. AllocShared creates a
            // heap object with refcount 1.
            var circleRoute = _world.Heap.AllocShared(
                new PatrolRoute
                {
                    Waypoints = GenerateCircle(new Vector3(-8, 0, 0), 3f, 20),
                    Color = Color.cyan,
                    Speed = 2f,
                }
            );

            var figure8Route = _world.Heap.AllocShared(
                new PatrolRoute
                {
                    Waypoints = GenerateFigure8(Vector3.zero, 4f, 24),
                    Color = Color.yellow,
                    Speed = 3f,
                }
            );

            var starRoute = _world.Heap.AllocShared(
                new PatrolRoute
                {
                    Waypoints = GenerateStar(new Vector3(8, 0, 0), 4f, 1.5f, 5),
                    Color = Color.magenta,
                    Speed = 2.5f,
                }
            );

            // ─── Spawn followers ────────────────────────────────────
            SpawnFollowers(circleRoute, _entitiesPerRoute);
            SpawnFollowers(figure8Route, _entitiesPerRoute);
            SpawnFollowers(starRoute, _entitiesPerRoute);

            // ─── Dispose original SharedPtrs ────────────────────────
            // Each entity holds its own Clone. The originals are no longer
            // needed — dispose to decrement refcount. Objects stay alive
            // because entity clones still reference them.
            circleRoute.Dispose(_world);
            figure8Route.Dispose(_world);
            starRoute.Dispose(_world);
        }

        void SpawnFollowers(SharedPtr<PatrolRoute> routePtr, int count)
        {
            var route = routePtr.Get(_world);

            for (int i = 0; i < count; i++)
            {
                // Stagger entities along the route
                float progress = (float)i / count * route.Waypoints.Count;

                int idx = (int)progress;
                int nextIdx = (idx + 1) % route.Waypoints.Count;
                float t = progress - idx;
                var pos = Vector3.Lerp(route.Waypoints[idx], route.Waypoints[nextIdx], t);

                var go = GameObject.CreatePrimitive(PrimitiveType.Sphere);
                go.name = $"Follower_{route.Color}_{i}";
                go.transform.position = pos;
                go.transform.localScale = Vector3.one * 0.5f;
                go.GetComponent<Renderer>().material.color = route.Color;

                // Add LineRenderer for trail visualization
                var lr = go.AddComponent<LineRenderer>();
                lr.startWidth = 0.15f;
                lr.endWidth = 0.02f;
                lr.material = new Material(Shader.Find("Sprites/Default"));
                lr.startColor = route.Color;
                lr.endColor = new Color(route.Color.r, route.Color.g, route.Color.b, 0.3f);
                lr.positionCount = 0;

                // ─── Clone SharedPtr ────────────────────────────────
                // Clone increments refcount. Each entity gets its own handle,
                // all pointing to the same PatrolRoute object.
                var routeClone = routePtr.Clone(_world);

                // ─── Allocate UniquePtr ─────────────────────────────
                // Each entity gets its own TrailHistory with an empty list
                // that will grow dynamically as the entity moves.
                var trailPtr = _world.Heap.AllocUnique(new TrailHistory { MaxLength = 50 });

                _world.AddEntity<PatrolTags.Follower>()
                    .Set(new Position((float3)pos))
                    .Set(new CRoute { Value = routeClone, Progress = progress })
                    .Set(new CTrail { Value = trailPtr })
                    .Set(_gameObjectRegistry.Register(go))
                    .AssertComplete();
            }
        }

        static List<Vector3> GenerateCircle(Vector3 center, float radius, int count)
        {
            var waypoints = new List<Vector3>(count);
            for (int i = 0; i < count; i++)
            {
                float angle = 2f * math.PI * i / count;
                waypoints.Add(
                    center + new Vector3(math.cos(angle) * radius, 0, math.sin(angle) * radius)
                );
            }
            return waypoints;
        }

        static List<Vector3> GenerateFigure8(Vector3 center, float size, int count)
        {
            var waypoints = new List<Vector3>(count);
            for (int i = 0; i < count; i++)
            {
                float t = 2f * math.PI * i / count;
                waypoints.Add(
                    center + new Vector3(size * math.sin(t), 0, size * math.sin(t) * math.cos(t))
                );
            }
            return waypoints;
        }

        static List<Vector3> GenerateStar(
            Vector3 center,
            float outerRadius,
            float innerRadius,
            int points
        )
        {
            int vertexCount = points * 2;
            var vertices = new List<Vector3>(vertexCount);

            for (int i = 0; i < vertexCount; i++)
            {
                float angle = math.PI / 2f + 2f * math.PI * i / vertexCount;
                float r = (i % 2 == 0) ? outerRadius : innerRadius;
                vertices.Add(center + new Vector3(math.cos(angle) * r, 0, math.sin(angle) * r));
            }

            // Subdivide edges for smoother movement
            int segmentsPerEdge = 3;
            var waypoints = new List<Vector3>(vertexCount * segmentsPerEdge);

            for (int i = 0; i < vertexCount; i++)
            {
                var a = vertices[i];
                var b = vertices[(i + 1) % vertexCount];
                for (int s = 0; s < segmentsPerEdge; s++)
                {
                    float frac = (float)s / segmentsPerEdge;
                    waypoints.Add(Vector3.Lerp(a, b, frac));
                }
            }

            return waypoints;
        }
    }
}
