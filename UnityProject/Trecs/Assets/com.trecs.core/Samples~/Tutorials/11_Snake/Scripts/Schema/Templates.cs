using Unity.Mathematics;

namespace Trecs.Samples.Snake
{
    public static partial class SnakeTemplates
    {
        public partial class SnakeGlobals : ITemplate, IExtends<TrecsTemplates.Globals>
        {
            // Retain: the snake only moves once every N fixed
            // frames, so an input the player makes between move ticks must
            // persist until the next tick to be processed. Retain
            // keeps the last requested direction in place across frames.
            [Input(MissingInputBehavior.Retain)]
            MoveInput MoveInput = default;

            SnakeLength SnakeLength = new(4);
            Score Score = default;
            MoveTickCounter MoveTickCounter = default;
        }

        public partial class SnakeHeadEntity
            : ITemplate,
                IExtends<CommonTemplates.RenderableGameObject>,
                ITagged<SnakeTags.SnakeHead>
        {
            GridPos GridPos = default;
            Direction Direction = new(new int2(1, 0));
            PrefabId PrefabId = new(SnakePrefabs.Head);
        }

        public partial class SnakeSegmentEntity
            : ITemplate,
                IExtends<CommonTemplates.RenderableGameObject>,
                ITagged<SnakeTags.SnakeSegment>
        {
            GridPos GridPos = default;
            SegmentAge SegmentAge = default;
            PrefabId PrefabId = new(SnakePrefabs.Segment);
        }

        public partial class SnakeFoodEntity
            : ITemplate,
                IExtends<CommonTemplates.RenderableGameObject>,
                ITagged<SnakeTags.SnakeFood>
        {
            GridPos GridPos = default;
            PrefabId PrefabId = new(SnakePrefabs.Food);
        }
    }
}
