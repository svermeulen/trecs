using System.Text;
using TMPro;

namespace Trecs.Samples.Snake
{
    [VariableUpdate]
    public partial class TextDisplaySystem : ISystem
    {
        readonly TMP_Text _displayText;
        readonly RecordAndPlaybackController _recordController;
        readonly StringBuilder _sb = new();

        float _refreshCountdown;

        public TextDisplaySystem(TMP_Text displayText, RecordAndPlaybackController recordController)
        {
            _displayText = displayText;
            _recordController = recordController;
        }

        public void Execute()
        {
            _refreshCountdown -= World.DeltaTime;
            if (_refreshCountdown > 0)
            {
                return;
            }

            _refreshCountdown = RefreshInterval;

            int score = World.GlobalComponent<Score>().Read.Value;
            int length = World.GlobalComponent<SnakeLength>().Read.Value;
            int frame = World.FixedFrame;

            var direction = World
                .Query()
                .WithTags<SnakeTags.SnakeHead>()
                .Single()
                .Get<Direction>()
                .Read.Value;

            _sb.Clear();
            AppendStat("Score", $"{score}");
            AppendStat("Length", $"{length}");
            AppendStat("Frame", $"{frame}");
            AppendStat("Heading", DirectionName(direction.x, direction.y));
            AppendStat("Mode", $"{_recordController.State}");
            _sb.AppendLine();
            AppendHeader("Controls:");
            AppendNote("   WASD - Move Snake");
            AppendNote("   F5 - Start recording");
            AppendNote("   F6 - Stop recording / playback");
            AppendNote("   F7 - Play recording");
            AppendNote("   F8 - Save bookmark");
            AppendNote("   F9 - Load bookmark");

            _displayText.text = _sb.ToString();
        }

        static string DirectionName(int x, int y)
        {
            if (x > 0)
            {
                return "Right";
            }
            if (x < 0)
            {
                return "Left";
            }
            if (y > 0)
            {
                return "Up";
            }
            if (y < 0)
            {
                return "Down";
            }
            return "?";
        }

        void AppendHeader(string text)
        {
            _sb.AppendLine($"<b><color={LabelColor}>{text}</color></b>");
        }

        void AppendStat(string label, string value)
        {
            _sb.Append($"<color={LabelColor}>{label}:</color> ");
            _sb.Append($"<color={ValueColor}>{value}</color>");
            _sb.AppendLine();
        }

        void AppendNote(string text)
        {
            _sb.AppendLine($"<color={SecondaryColor}>{text}</color>");
        }

        const float RefreshInterval = 0.25f;

        const string LabelColor = "#E0E0E0";
        const string ValueColor = "#00E5FF";
        const string SecondaryColor = "#9E9E9E";
    }
}
