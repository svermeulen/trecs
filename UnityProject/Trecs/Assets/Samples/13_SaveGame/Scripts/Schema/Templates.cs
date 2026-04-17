namespace Trecs.Samples.SaveGame
{
    public static partial class SaveGameTemplates
    {
        public partial class SaveGameGlobals : ITemplate, IExtends<TrecsTemplates.Globals>
        {
            // ResetToDefault: a tap produces exactly one move on the next
            // fixed tick. If the queued input has already been consumed and
            // no new key is pressed, MoveInput reverts to zero instead of
            // causing auto-repeat.
            [Input(MissingInputFrameBehaviour.ResetToDefault)]
            public MoveInput MoveInput = default;
        }

        public partial class PlayerEntity : ITemplate, IHasTags<SaveGameTags.Player>
        {
            public GridPos GridPos = default;
        }

        public partial class BoxEntity : ITemplate, IHasTags<SaveGameTags.Box>
        {
            public GridPos GridPos = default;
        }

        public partial class TargetEntity : ITemplate, IHasTags<SaveGameTags.Target>
        {
            public GridPos GridPos = default;
        }

        public partial class WallEntity : ITemplate, IHasTags<SaveGameTags.Wall>
        {
            public GridPos GridPos = default;
        }
    }
}
