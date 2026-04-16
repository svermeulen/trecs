using System.Text;
using TMPro;

namespace Trecs.Samples.FeedingFrenzy101
{
    [VariableUpdate]
    public partial class TextDisplaySystem : ISystem
    {
        readonly TMP_Text _displayText;
        readonly StringBuilder _sb = new();

        float _lastRefreshTime;

        public TextDisplaySystem(TMP_Text displayText)
        {
            _displayText = displayText;
        }

        public void Execute()
        {
            if (World.ElapsedTime - _lastRefreshTime < RefreshInterval)
            {
                return;
            }

            _lastRefreshTime = World.ElapsedTime;

            int fishCount = World.CountEntitiesWithTags<FrenzyTags.Fish>();
            int mealCount = World.CountEntitiesWithTags<FrenzyTags.Meal>();

            int desiredNumFish = World.GlobalComponent<DesiredFishCount>().Read.Value;

            _sb.Clear();
            AppendStat("Total Entity Count", $"{fishCount + mealCount:N0}");
            _sb.AppendLine();
            AppendStat("Meal Count", $"{mealCount:N0}");
            AppendStat("Fish Count", $"{fishCount:N0}");
            AppendStat("Desired Fish Count", $"{desiredNumFish:N0}");

            _sb.AppendLine();
            AppendNote("Press Up/Down arrows to adjust desired fish count.");

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
