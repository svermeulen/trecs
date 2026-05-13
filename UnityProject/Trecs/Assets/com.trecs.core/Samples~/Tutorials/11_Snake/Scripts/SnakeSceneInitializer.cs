using Unity.Mathematics;
using UnityEngine;

namespace Trecs.Samples.Snake
{
    /// <summary>
    /// Spawns the initial snake state at startup: one head entity at the
    /// grid center, plus <c>InitialSnakeLength - 1</c> segments trailing
    /// behind it. Also creates a flat ground plane for visual context;
    /// the ground is not tied to any entity and is unaffected by snapshot
    /// loads.
    /// </summary>
    public class SnakeSceneInitializer
    {
        readonly SnakeSettings _settings;
        readonly WorldAccessor _world;
        readonly RenderableGameObjectManager _goManager;

        public SnakeSceneInitializer(
            SnakeSettings settings,
            World world,
            RenderableGameObjectManager goManager
        )
        {
            _settings = settings;
            _world = world.CreateAccessor(AccessorRole.Fixed);
            _goManager = goManager;
        }

        public void Initialize()
        {
            RegisterPrefabs();
            CreateGroundPlane();

            int center = _settings.GridSize / 2;
            var headPos = new int2(center, center);

            // Globals: seed the move tick so the snake takes its first
            // step quickly after startup, and set the initial length.
            ref var counter = ref _world.GlobalComponent<MoveTickCounter>().Write;
            counter.FramesUntilNextMove = _settings.FramesPerMove;

            ref var length = ref _world.GlobalComponent<SnakeLength>().Write;
            length.Value = _settings.InitialSnakeLength;

            // Head moves right at start, sitting at the center cell.
            _world
                .AddEntity<SnakeTags.SnakeHead>()
                .Set(new GridPos(headPos))
                .Set(new Direction(new int2(1, 0)));

            // Initial body segments stretch leftward from the head, with
            // descending FrameSpawned so the trim system would (in
            // principle) trim the leftmost ones first as the snake grows.
            for (int i = 1; i < _settings.InitialSnakeLength; i++)
            {
                int x =
                    ((center - i) % _settings.GridSize + _settings.GridSize) % _settings.GridSize;
                _world
                    .AddEntity<SnakeTags.SnakeSegment>()
                    .Set(new GridPos(new int2(x, center)))
                    // Negative frames so any segment spawned at runtime
                    // has a higher (newer) frame number than these
                    // initial segments and gets trimmed first.
                    .Set(new SegmentAge(-i));
            }
        }

        void RegisterPrefabs()
        {
            var headMat = SampleUtil.CreateMaterial(new Color(0.2f, 0.9f, 0.4f));
            var segmentMat = SampleUtil.CreateMaterial(new Color(0.4f, 0.7f, 0.3f));
            var foodMat = SampleUtil.CreateMaterial(new Color(0.9f, 0.7f, 0.2f));

            _goManager.RegisterFactory(SnakePrefabs.Head, () => CreateCube(headMat, 1.0f, "Head"));
            _goManager.RegisterFactory(
                SnakePrefabs.Segment,
                () => CreateCube(segmentMat, 0.85f, "Segment")
            );
            _goManager.RegisterFactory(SnakePrefabs.Food, () => CreateCube(foodMat, 0.6f, "Food"));
        }

        static GameObject CreateCube(Material material, float scale, string name)
        {
            var go = SampleUtil.CreatePrimitive(PrimitiveType.Cube);
            go.name = name;
            go.transform.localScale = Vector3.one * scale;
            go.GetComponent<Renderer>().sharedMaterial = material;
            Object.Destroy(go.GetComponent<Collider>());
            return go;
        }

        void CreateGroundPlane()
        {
            // Unity's plane primitive is 10 units across; scale to match
            // the grid (one cell = 1 unit, plane sits centered on grid).
            var plane = SampleUtil.CreatePrimitive(PrimitiveType.Plane);
            plane.name = "GridPlane";
            plane.transform.localScale = new Vector3(
                _settings.GridSize / 10f,
                1f,
                _settings.GridSize / 10f
            );
            plane.transform.position = new Vector3(
                (_settings.GridSize - 1) * 0.5f,
                0f,
                (_settings.GridSize - 1) * 0.5f
            );
            plane.GetComponent<Renderer>().sharedMaterial = SampleUtil.CreateMaterial(
                new Color(0.15f, 0.15f, 0.18f)
            );
            Object.Destroy(plane.GetComponent<Collider>());
        }
    }
}
