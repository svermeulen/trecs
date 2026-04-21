using System.Text;
using TMPro;

namespace Trecs.Samples.JobSystem
{
    [VariableUpdate]
    public partial class TextDisplaySystem : ISystem
    {
        readonly TMP_Text _displayText;
        readonly StringBuilder _sb = new();

        float _refreshCountdown;
        int _frameCount;
        float _elapsedSinceRefresh;
        float _fps;

        public TextDisplaySystem(TMP_Text displayText)
        {
            _displayText = displayText;
        }

        public void Execute()
        {
            _frameCount++;
            _elapsedSinceRefresh += World.DeltaTime;
            _refreshCountdown -= World.DeltaTime;
            if (_refreshCountdown > 0)
            {
                return;
            }

            _refreshCountdown = RefreshInterval;

            if (_elapsedSinceRefresh > 0)
            {
                _fps = _frameCount / _elapsedSinceRefresh;
            }
            _frameCount = 0;
            _elapsedSinceRefresh = 0;

            // -1 to exclude global entity
            int particleCount = World.CountAllEntities() - 1;
            int desiredCount = World.GlobalComponent<DesiredNumParticles>().Read.Value;
            bool jobsEnabled = World.GlobalComponent<IsJobsEnabled>().Read.Value;

            _sb.Clear();
            AppendStat("Entity Count", $"{particleCount:N0}");
            float frameMs = _fps > 0 ? 1000f / _fps : 0f;
            AppendStat("FPS", $"{_fps:N0} ({frameMs:N1}ms)");
            AppendStat("Desired Count", $"{desiredCount:N0}");
            AppendStat("Jobs", jobsEnabled ? "Enabled" : "Disabled");

            _sb.AppendLine();
            AppendNote("Up/Down arrows: adjust particle count");
            AppendNote("J: toggle jobs on/off");
            _sb.AppendLine();
            AppendNote("Check FPS at 100k+ with and without jobs");

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
