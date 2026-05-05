using Unity.Mathematics;

namespace Trecs.Serialization.Samples.Snake
{
    public static partial class SnakeTemplates
    {
        public partial class Renderable : ITemplate
        {
            PrefabId PrefabId;
            GameObjectId GameObjectId = default;
        }

        public partial class SnakeGlobals : ITemplate, IExtends<TrecsTemplates.Globals>
        {
            // RetainCurrent: the snake only moves once every N fixed
            // frames, so an input the player makes between move ticks must
            // persist until the next tick to be processed. RetainCurrent
            // keeps the last requested direction in place across frames.
            [Input(MissingInputFrameBehaviour.RetainCurrent)]
            MoveInput MoveInput = default;

            SnakeLength SnakeLength = new(4);
            Score Score = default;
            MoveTickCounter MoveTickCounter = default;
        }

        public partial class SnakeHeadEntity
            : ITemplate,
                IExtends<Renderable>,
                IHasTags<SnakeTags.SnakeHead>
        {
            GridPos GridPos = default;
            Direction Direction = new(new int2(1, 0));
            PrefabId PrefabId = new(SnakePrefabs.Head);
        }

        public partial class SnakeSegmentEntity
            : ITemplate,
                IExtends<Renderable>,
                IHasTags<SnakeTags.SnakeSegment>
        {
            GridPos GridPos = default;
            SegmentAge SegmentAge = default;
            PrefabId PrefabId = new(SnakePrefabs.Segment);
        }

        public partial class SnakeFoodEntity
            : ITemplate,
                IExtends<Renderable>,
                IHasTags<SnakeTags.SnakeFood>
        {
            GridPos GridPos = default;
            PrefabId PrefabId = new(SnakePrefabs.Food);
        }
    }
}
