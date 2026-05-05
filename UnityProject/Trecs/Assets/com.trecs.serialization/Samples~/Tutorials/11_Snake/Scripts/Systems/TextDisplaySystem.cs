using System.Text;
using TMPro;

namespace Trecs.Serialization.Samples.Snake
{
    [Phase(SystemPhase.Presentation)]
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

            var state = _recordController.State;

            _sb.Clear();
            AppendStat("Score", $"{score}");
            AppendStat("Length", $"{length}");
            AppendStat("Frame", $"{frame}");
            AppendStat("Heading", DirectionName(direction.x, direction.y));
            AppendStat("Mode", $"{state}", ModeColor(state));
            _sb.AppendLine();
            AppendHeader("Controls:");
            AppendControl("WASD", "Move Snake");
            AppendControl("F5", "Start / stop recording");
            AppendControl("F6", "Start / stop playback");
            AppendControl("F8", "Save snapshot");
            AppendControl("F9", "Load snapshot");

            _displayText.text = _sb.ToString();
        }

        static string ModeColor(RecordAndPlaybackController.ControllerState state)
        {
            return state switch
            {
                RecordAndPlaybackController.ControllerState.Recording => RecordingColor,
                RecordAndPlaybackController.ControllerState.Playback => PlaybackColor,
                _ => ValueColor,
            };
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

        void AppendStat(string label, string value, string valueColor = ValueColor)
        {
            _sb.Append($"<color={LabelColor}>{label}:</color> ");
            _sb.Append($"<color={valueColor}>{value}</color>");
            _sb.AppendLine();
        }

        void AppendControl(string key, string description)
        {
            _sb.Append("   ");
            _sb.Append($"<color={KeyColor}>{key}</color>");
            _sb.Append($"<color={SecondaryColor}> - {description}</color>");
            _sb.AppendLine();
        }

        const float RefreshInterval = 0.25f;

        const string LabelColor = "#E0E0E0";
        const string ValueColor = "#00E5FF";
        const string SecondaryColor = "#9E9E9E";
        const string KeyColor = "#FFD54F";
        const string RecordingColor = "#FF5252";
        const string PlaybackColor = "#69F0AE";
    }
}
