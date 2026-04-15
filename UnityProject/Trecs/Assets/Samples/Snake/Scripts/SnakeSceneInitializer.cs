using Unity.Mathematics;
using UnityEngine;

namespace Trecs.Samples.Snake
{
    /// <summary>
    /// Spawns the initial snake state at startup: one head entity at the
    /// grid center, plus <c>InitialSnakeLength - 1</c> segments trailing
    /// behind it. Also creates a flat ground plane for visual context;
    /// the ground is not tied to any entity and is unaffected by bookmark
    /// loads.
    /// </summary>
    public class SnakeSceneInitializer
    {
        readonly SnakeSettings _settings;
        readonly World _world;
        readonly SnakeGameObjectManager _goManager;

        public SnakeSceneInitializer(
            SnakeSettings settings,
            World world,
            SnakeGameObjectManager goManager
        )
        {
            _settings = settings;
            _world = world;
            _goManager = goManager;
        }

        public void Initialize()
        {
            CreateGroundPlane();

            var ecs = _world.CreateAccessor(nameof(SnakeSceneInitializer));

            int center = _settings.GridSize / 2;
            var headPos = new int2(center, center);

            // Globals: seed the move tick so the snake takes its first
            // step quickly after startup, and set the initial length.
            ref var counter = ref ecs.GlobalComponent<MoveTickCounter>().Write;
            counter.FramesUntilNextMove = _settings.FramesPerMove;

            ref var length = ref ecs.GlobalComponent<SnakeLength>().Write;
            length.Value = _settings.InitialSnakeLength;

            // Head moves right at start, sitting at the center cell.
            ecs.AddEntity<SnakeTags.SnakeHead>()
                .Set(new GridPos(headPos))
                .Set(new Direction(new int2(1, 0)))
                .Set(_goManager.CreateHead())
                .AssertComplete();

            // Initial body segments stretch leftward from the head, with
            // descending FrameSpawned so the trim system would (in
            // principle) trim the leftmost ones first as the snake grows.
            for (int i = 1; i < _settings.InitialSnakeLength; i++)
            {
                int x =
                    ((center - i) % _settings.GridSize + _settings.GridSize) % _settings.GridSize;
                ecs.AddEntity<SnakeTags.SnakeSegment>()
                    .Set(new GridPos(new int2(x, center)))
                    // Negative frames so any segment spawned at runtime
                    // has a higher (newer) frame number than these
                    // initial segments and gets trimmed first.
                    .Set(new SegmentAge(-i))
                    .Set(_goManager.CreateSegment())
                    .AssertComplete();
            }
        }

        void CreateGroundPlane()
        {
            // Unity's plane primitive is 10 units across; scale to match
            // the grid (one cell = 1 unit, plane sits centered on grid).
            var plane = GameObject.CreatePrimitive(PrimitiveType.Plane);
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
            UnityEngine.Object.Destroy(plane.GetComponent<Collider>());
        }
    }
}
