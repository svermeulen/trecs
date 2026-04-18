using System;
using System.Text;
using TMPro;
using Unity.Mathematics;

namespace Trecs.Samples.FeedingFrenzyBenchmark
{
    [VariableUpdate]
    public partial class TextDisplaySystem : ISystem
    {
        readonly TMP_Text _displayText;
        readonly int[] _fishCountPresets;
        readonly CommonSettings _commonSettings;
        readonly StringBuilder _sb = new();

        float _refreshCountdown;

        public TextDisplaySystem(
            TMP_Text displayText,
            int[] fishCountPresets,
            CommonSettings commonSettings
        )
        {
            _displayText = displayText;
            _fishCountPresets = fishCountPresets;
            _commonSettings = commonSettings;
        }

        public void Execute()
        {
            _refreshCountdown -= World.DeltaTime;
            if (_refreshCountdown > 0)
            {
                return;
            }

            _refreshCountdown = RefreshInterval;

            ref readonly var stats = ref World.GlobalComponent<PerformanceStats>().Read;
            ref readonly var config = ref World.GlobalComponent<FrenzyConfig>().Read;

            _sb.Clear();
            AppendStat("Entity Count", $"{stats.EntityCount:N0}");
            int presetIndex = World.GlobalComponent<DesiredPreset>().Read.Value;
            int desiredFishCount = _fishCountPresets[
                math.clamp(presetIndex, 0, _fishCountPresets.Length - 1)
            ];
            int desiredMealCount = (int)(desiredFishCount * _commonSettings.MealCountRatio);
            int desiredEntityCount = desiredFishCount + desiredMealCount;
            AppendStat(
                "Desired Entity Count",
                $"{desiredEntityCount:N0}",
                secondary: $"(preset {presetIndex + 1}/{_fishCountPresets.Length})",
                hotkey: "Up/Down"
            );

            _sb.AppendLine();
            AppendStat(
                "Sim Tick",
                $"{stats.SimTickMs:F2}ms",
                secondary: $"({stats.SimTickHzAvg} ticks/s)"
            );
            AppendStat("Sim/Frame", $"{stats.SimPerFrameMs:F2}ms");
            AppendStat("Frame", $"{stats.FrameMs:F2}ms", secondary: $"({stats.FpsAvg} fps)");
            AppendStat("Mem", $"{stats.TotalMemMb}MB", secondary: $"(Mono: {stats.MonoMemMb}MB)");

            _sb.AppendLine();
            AppendStat(
                "Subset",
                config.SubsetApproach.ToString(),
                secondary: $"({(int)config.SubsetApproach + 1}/{SubsetApproachNames.Length})",
                hotkey: "F1/F2/F3"
            );
            AppendStat(
                "Iteration Mode",
                config.IterationStyle.ToString(),
                secondary: $"({(int)config.IterationStyle + 1}/{IterationStyleNames.Length})",
                hotkey: "Tab"
            );
            AppendStat(
                "Deterministic",
                config.Deterministic ? "On" : "Off",
                valueColor: config.Deterministic ? OnColor : OffColor
            );

            _sb.AppendLine();
            AppendNote("Note: F1/F2/F3 reload the scene to switch");
            AppendNote("subset approach.  Deterministic is fixed at");
            AppendNote("runtime — adjust on FrenzyCompositionRoot.");

            _displayText.text = _sb.ToString();
        }

        void AppendStat(
            string label,
            string value,
            string secondary = null,
            string hotkey = null,
            string valueColor = ValueColor
        )
        {
            if (hotkey != null)
            {
                _sb.Append(
                    $"<b><color={LabelColor}>{label}</color></b> <color={HotkeyColor}>[{hotkey}]</color><color={LabelColor}>:</color> "
                );
            }
            else
            {
                _sb.Append($"<b><color={LabelColor}>{label}:</color></b> ");
            }
            _sb.Append($"<color={valueColor}>{value}</color>");
            if (secondary != null)
            {
                _sb.Append($" <color={SecondaryColor}>{secondary}</color>");
            }
            _sb.AppendLine();
        }

        void AppendNote(string text)
        {
            _sb.AppendLine($"<i><color={SecondaryColor}>{text}</color></i>");
        }

        const float RefreshInterval = 0.5f;

        const string LabelColor = "#E0E0E0";
        const string ValueColor = "#00E5FF";
        const string SecondaryColor = "#9E9E9E";
        const string HotkeyColor = "#FFB347";
        const string OnColor = "#7CFC7C";
        const string OffColor = "#FF6B6B";

        static readonly string[] SubsetApproachNames = Enum.GetNames(typeof(FrenzySubsetApproach));
        static readonly string[] IterationStyleNames = Enum.GetNames(typeof(IterationStyle));
    }
}
