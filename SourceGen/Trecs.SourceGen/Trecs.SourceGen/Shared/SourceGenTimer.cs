// To enable timing, add SOURCEGEN_TIMING to the DefineConstants in the .csproj:
//   <DefineConstants>$(DefineConstants);SOURCEGEN_TIMING</DefineConstants>

using System;
using System.Collections.Generic;
using System.Diagnostics;
using System.Linq;
using System.Text;

namespace Trecs.SourceGen.Shared
{
    internal static class SourceGenTimer
    {
#if SOURCEGEN_TIMING
        private static readonly object _lock = new();
        private static readonly Dictionary<string, List<double>> _timings = new();

        public static IDisposable Time(string label)
        {
            return new TimingScope(label);
        }

        private static void Record(string label, double elapsedMs)
        {
            lock (_lock)
            {
                if (!_timings.TryGetValue(label, out var list))
                {
                    list = new List<double>();
                    _timings[label] = list;
                }
                list.Add(elapsedMs);
            }

            SourceGenLogger.Log($"[Timer] {label}: {elapsedMs:F2}ms");
        }

        public static void WriteSummary()
        {
            lock (_lock)
            {
                if (_timings.Count == 0)
                    return;

                var sb = new StringBuilder();
                sb.AppendLine("=== Source Generator Timing Summary ===");

                foreach (var entry in _timings.OrderByDescending(kv => kv.Value.Sum()))
                {
                    var vals = entry.Value;
                    sb.AppendLine(
                        $"  {entry.Key}: {vals.Sum():F1}ms total, {vals.Count} calls, {vals.Average():F2}ms avg, {vals.Max():F2}ms max"
                    );
                }

                SourceGenLogger.Log(sb.ToString());
                _timings.Clear();
            }
        }

        private class TimingScope : IDisposable
        {
            private readonly string _label;
            private readonly Stopwatch _sw;

            public TimingScope(string label)
            {
                _label = label;
                _sw = Stopwatch.StartNew();
            }

            public void Dispose()
            {
                _sw.Stop();
                Record(_label, _sw.Elapsed.TotalMilliseconds);
            }
        }
#else
        public static IDisposable Time(string label) => NullDisposable.Instance;

        public static void WriteSummary() { }

        private class NullDisposable : IDisposable
        {
            public static readonly NullDisposable Instance = new();

            public void Dispose() { }
        }
#endif
    }
}
