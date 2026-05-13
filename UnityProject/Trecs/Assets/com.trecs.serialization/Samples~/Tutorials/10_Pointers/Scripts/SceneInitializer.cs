using Unity.Mathematics;
using UnityEngine;

namespace Trecs.Serialization.Samples.Pointers
{
    public class SceneInitializer
    {
        readonly WorldAccessor _world;
        readonly int _followerCount;
        readonly RenderableGameObjectManager _goManager;

        public SceneInitializer(
            World world,
            int followerCount,
            RenderableGameObjectManager goManager
        )
        {
            _world = world.CreateAccessor(AccessorRole.Fixed);
            _followerCount = followerCount;
            _goManager = goManager;
        }

        public void Initialize()
        {
            _goManager.RegisterFactory(PointersPrefabs.Follower, CreateFollower);

            for (int i = 0; i < _followerCount; i++)
            {
                // Stagger each follower's starting phase so they fan out
                // around the figure-8 rather than stacking at the same point.
                float phase = (float)i / _followerCount * 2f * math.PI;

                // ─── Allocate UniquePtr ─────────────────────────────────
                // Each entity gets its own TrailHistory with an empty list
                // that will grow dynamically as the entity moves.
                // _world.Heap.AllocUnique stores the managed object in the
                // world's UniqueHeap and returns a 4-byte handle that we
                // embed in the entity's Trail component.
                var trailPtr = _world.Heap.AllocUnique(new TrailHistory { MaxLength = 60 });

                _world
                    .AddEntity<PatrolTags.Follower>()
                    .Set(new Position(PatrolMovementSystem.FigureEightAt(phase)))
                    .Set(new PathPhase(phase))
                    .Set(new Trail { Value = trailPtr });
            }
        }

        static GameObject CreateFollower()
        {
            var color = Color.cyan;

            var go = SampleUtil.CreatePrimitive(PrimitiveType.Sphere);
            go.transform.localScale = Vector3.one * 0.5f;
            go.GetComponent<Renderer>().material.color = color;

            // Add LineRenderer for trail visualization
            var lr = go.AddComponent<LineRenderer>();
            lr.startWidth = 0.15f;
            lr.endWidth = 0.02f;
            lr.material = new Material(Shader.Find("Sprites/Default"));
            lr.startColor = color;
            lr.endColor = new Color(color.r, color.g, color.b, 0.3f);
            lr.positionCount = 0;

            return go;
        }
    }
}
