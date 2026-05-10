namespace Trecs.Serialization.Samples.SaveGame
{
    public static partial class SaveGameTemplates
    {
        public partial class SaveGameGlobals : ITemplate, IExtends<TrecsTemplates.Globals>
        {
            // Reset: a tap produces exactly one move on the next
            // fixed tick. If the queued input has already been consumed and
            // no new key is pressed, MoveInput reverts to zero instead of
            // causing auto-repeat.
            [Input(MissingInputBehavior.Reset)]
            MoveInput MoveInput = default;
        }

        public partial class PlayerEntity : ITemplate, ITagged<SaveGameTags.Player>
        {
            GridPos GridPos = default;
        }

        public partial class BoxEntity : ITemplate, ITagged<SaveGameTags.Box>
        {
            GridPos GridPos = default;
        }

        public partial class TargetEntity : ITemplate, ITagged<SaveGameTags.Target>
        {
            GridPos GridPos = default;
        }

        public partial class WallEntity : ITemplate, ITagged<SaveGameTags.Wall>
        {
            GridPos GridPos = default;
        }
    }
}
