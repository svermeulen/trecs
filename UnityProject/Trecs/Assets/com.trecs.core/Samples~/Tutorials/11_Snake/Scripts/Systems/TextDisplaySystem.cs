using System.Text;
using TMPro;

namespace Trecs.Samples.Snake
{
    [ExecuteIn(SystemPhase.Presentation)]
    public partial class TextDisplaySystem : ISystem
    {
        readonly TMP_Text _displayText;
        readonly StringBuilder _sb = new();

        float _refreshCountdown;

        public TextDisplaySystem(TMP_Text displayText)
        {
            _displayText = displayText;
        }

        void Execute(
            [SingleEntity(typeof(TrecsTags.Globals))] in Globals globals,
            [SingleEntity(typeof(SnakeTags.SnakeHead))] in Head head
        )
        {
            _refreshCountdown -= World.DeltaTime;
            if (_refreshCountdown > 0)
            {
                return;
            }

            _refreshCountdown = RefreshInterval;

            var direction = head.Direction;

            _sb.Clear();
            AppendStat("Score", $"{globals.Score}");
            AppendStat("Length", $"{globals.SnakeLength}");
            AppendStat("Frame", $"{World.FixedFrame}");
            AppendStat("Heading", DirectionName(direction.x, direction.y));
            _sb.AppendLine();
            AppendHeader("Controls:");
            AppendControl("WASD", "Move Snake");

            _displayText.text = _sb.ToString();
        }

        partial struct Globals : IAspect, IRead<Score, SnakeLength> { }

        partial struct Head : IAspect, IRead<Direction> { }

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
    }
}
