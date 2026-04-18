using System.Collections.Generic;
using System.Text;
using TMPro;
using Unity.Mathematics;

namespace Trecs.Samples.SaveGame
{
    [VariableUpdate]
    public partial class TextDisplaySystem : ISystem
    {
        readonly TMP_Text _displayText;
        readonly SaveGameController _controller;
        readonly StringBuilder _sb = new();
        readonly HashSet<int2> _targetCells = new();

        float _refreshCountdown;

        public TextDisplaySystem(TMP_Text displayText, SaveGameController controller)
        {
            _displayText = displayText;
            _controller = controller;
        }

        public void Execute()
        {
            _refreshCountdown -= World.DeltaTime;
            if (_refreshCountdown > 0)
            {
                return;
            }

            _refreshCountdown = RefreshInterval;

            _targetCells.Clear();
            int targetCount = 0;
            foreach (var t in TargetView.Query(World).WithTags<SaveGameTags.Target>())
            {
                _targetCells.Add(t.GridPos);
                targetCount++;
            }

            int boxesOnTargets = 0;
            foreach (var b in BoxView.Query(World).WithTags<SaveGameTags.Box>())
            {
                if (_targetCells.Contains(b.GridPos))
                {
                    boxesOnTargets++;
                }
            }

            bool solved = targetCount > 0 && boxesOnTargets == targetCount;

            _sb.Clear();
            AppendHeader("Goal:");
            _sb.AppendLine(
                $"<color={SecondaryColor}>Push every box onto a red target.\nSave before a tricky push — a box shoved into a corner can't be pulled back.</color>"
            );
            _sb.AppendLine();
            AppendStat(
                "Boxes on targets",
                $"{boxesOnTargets} / {targetCount}",
                solved ? AccentColor : ValueColor
            );
            AppendStat("Frame", $"{World.FixedFrame}");
            if (solved)
            {
                _sb.Append($"<b><color={AccentColor}>Solved!</color></b>");
                _sb.AppendLine();
            }
            _sb.AppendLine();
            AppendHeader("Save Slots:");
            foreach (var slot in _controller.Slots)
            {
                if (slot.Exists)
                {
                    var when = slot.LastModifiedUtc.ToLocalTime().ToString("HH:mm:ss");
                    AppendControl($"Slot {slot.Index + 1}", $"saved at {when}");
                }
                else
                {
                    AppendControl($"Slot {slot.Index + 1}", "empty");
                }
            }
            _sb.AppendLine();
            AppendHeader("Controls:");
            AppendControl("WASD / Arrows", "Move & push boxes");
            AppendControl("F1 / F2 / F3", "Save to slot 1 / 2 / 3");
            AppendControl("F5 / F6 / F7", "Load slot 1 / 2 / 3");

            if (
                !string.IsNullOrEmpty(_controller.LastActionMessage)
                && _controller.SecondsSinceLastAction < 2.5f
            )
            {
                _sb.AppendLine();
                _sb.AppendLine($"<color={AccentColor}>{_controller.LastActionMessage}</color>");
            }

            _displayText.text = _sb.ToString();
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
        const string AccentColor = "#69F0AE";

        partial struct TargetView : IAspect, IRead<GridPos> { }

        partial struct BoxView : IAspect, IRead<GridPos> { }
    }
}
