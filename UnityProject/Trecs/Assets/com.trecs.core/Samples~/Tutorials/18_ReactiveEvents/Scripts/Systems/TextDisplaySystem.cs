using System.Text;
using TMPro;

namespace Trecs.Samples.ReactiveEvents
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

        public void Execute()
        {
            _refreshCountdown -= World.DeltaTime;
            if (_refreshCountdown > 0)
            {
                return;
            }

            _refreshCountdown = RefreshInterval;

            ref readonly var stats = ref World.GlobalComponent<GameStats>().Read;

            _sb.Clear();
            AppendStat("Alive Bubbles", $"{stats.AliveCount:N0}");
            AppendStat("Total Spawned", $"{stats.TotalSpawned:N0}");
            AppendStat("Total Popped", $"{stats.TotalRemoved:N0}");

            _sb.AppendLine();
            AppendNote("Counts updated via OnAdded / OnRemoved callbacks");

            _displayText.text = _sb.ToString();
        }

        void AppendStat(string label, string value)
        {
            _sb.Append($"<b><color={LabelColor}>{label}:</color></b> ");
            _sb.Append($"<color={ValueColor}>{value}</color>");
            _sb.AppendLine();
        }

        void AppendNote(string text)
        {
            _sb.AppendLine($"<i><color={SecondaryColor}>{text}</color></i>");
        }

        const float RefreshInterval = 0.25f;

        const string LabelColor = "#E0E0E0";
        const string ValueColor = "#00E5FF";
        const string SecondaryColor = "#9E9E9E";
    }
}
