namespace Trecs.Samples.Snake
{
    public static partial class SnakeTemplates
    {
        public partial class SnakeGlobals : ITemplate, IExtends<TrecsTemplates.Globals>
        {
            // RetainCurrent: the snake only moves once every N fixed
            // frames, so an input the player makes between move ticks must
            // persist until the next tick to be processed. RetainCurrent
            // keeps the last requested direction in place across frames.
            [Input(MissingInputFrameBehaviour.RetainCurrent)]
            public MoveInput MoveInput = default;

            public SnakeLength SnakeLength = SnakeLength.Default;
            public Score Score = default;
            public MoveTickCounter MoveTickCounter = default;
        }

        public partial class SnakeHeadEntity : ITemplate, IHasTags<SnakeTags.SnakeHead>
        {
            public GridPos GridPos = default;
            public Direction Direction = Direction.Default;
            public GameObjectId GameObjectId;
        }

        public partial class SnakeSegmentEntity : ITemplate, IHasTags<SnakeTags.SnakeSegment>
        {
            public GridPos GridPos = default;
            public SegmentAge SegmentAge = default;
            public GameObjectId GameObjectId;
        }

        public partial class SnakeFoodEntity : ITemplate, IHasTags<SnakeTags.SnakeFood>
        {
            public GridPos GridPos = default;
            public GameObjectId GameObjectId;
        }
    }
}
