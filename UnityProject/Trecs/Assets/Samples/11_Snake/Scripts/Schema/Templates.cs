using Unity.Mathematics;

namespace Trecs.Samples.Snake
{
    public static partial class SnakeTemplates
    {
        public partial class Renderable : ITemplate
        {
            public PrefabId PrefabId;
            public GameObjectId GameObjectId = default;
        }

        public partial class SnakeGlobals : ITemplate, IExtends<TrecsTemplates.Globals>
        {
            // RetainCurrent: the snake only moves once every N fixed
            // frames, so an input the player makes between move ticks must
            // persist until the next tick to be processed. RetainCurrent
            // keeps the last requested direction in place across frames.
            [Input(MissingInputFrameBehaviour.RetainCurrent)]
            public MoveInput MoveInput = default;

            public SnakeLength SnakeLength = new(4);
            public Score Score = default;
            public MoveTickCounter MoveTickCounter = default;
        }

        public partial class SnakeHeadEntity
            : ITemplate,
                IExtends<Renderable>,
                IHasTags<SnakeTags.SnakeHead>
        {
            public GridPos GridPos = default;
            public Direction Direction = new(new int2(1, 0));
            public PrefabId PrefabId = new(SnakePrefabs.Head);
        }

        public partial class SnakeSegmentEntity
            : ITemplate,
                IExtends<Renderable>,
                IHasTags<SnakeTags.SnakeSegment>
        {
            public GridPos GridPos = default;
            public SegmentAge SegmentAge = default;
            public PrefabId PrefabId = new(SnakePrefabs.Segment);
        }

        public partial class SnakeFoodEntity
            : ITemplate,
                IExtends<Renderable>,
                IHasTags<SnakeTags.SnakeFood>
        {
            public GridPos GridPos = default;
            public PrefabId PrefabId = new(SnakePrefabs.Food);
        }
    }
}
