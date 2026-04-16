using System;
using System.Diagnostics;
using UnityEngine.Profiling;

namespace Trecs.Samples.FeedingFrenzyBenchmark
{
    public class PerformanceStatsUpdater : IDisposable
    {
        const float UpdateInterval = 0.5f;

        readonly DisposeCollection _disposables = new();
        readonly WorldAccessor _world;
        readonly Stopwatch _fixedStopwatch = new();
        readonly Stopwatch _totalStopwatch = new();

        float _lastTotalFrameTimeSeconds;

        public PerformanceStatsUpdater(World world)
        {
            _world = world.CreateAccessor();

            _world.Events.OnFixedUpdateStarted(OnFixedUpdateStarted).AddTo(_disposables);
            _world.Events.OnFixedUpdateCompleted(OnFixedUpdateCompleted).AddTo(_disposables);
            _world.Events.OnVariableUpdateStarted(OnVariableUpdateStarted).AddTo(_disposables);
        }

        void OnFixedUpdateStarted()
        {
            _fixedStopwatch.Restart();
        }

        void OnFixedUpdateCompleted()
        {
            _fixedStopwatch.Stop();

            var tickTimeSeconds = (float)_fixedStopwatch.Elapsed.TotalSeconds;

            if (tickTimeSeconds <= 0f)
            {
                return;
            }

            var tickTimeMs = tickTimeSeconds * 1000f;
            var tickHz = 1f / tickTimeSeconds;

            ref var intermediate = ref _world.GlobalComponent<PerfStatsIntermediate>().Write;

            if (intermediate.SimTickHzSampleCount == 0)
            {
                intermediate.SimTickHzMin = tickHz;
                intermediate.SimTickHzMax = tickHz;
            }
            else
            {
                if (tickHz < intermediate.SimTickHzMin)
                {
                    intermediate.SimTickHzMin = tickHz;
                }

                if (tickHz > intermediate.SimTickHzMax)
                {
                    intermediate.SimTickHzMax = tickHz;
                }
            }

            intermediate.SimTickHzSum += tickHz;
            intermediate.SimTickHzSampleCount++;

            // Accumulate this tick's cost against the current Unity frame; the
            // running total is snapshotted + reset at each OnVariableUpdateStarted.
            intermediate.SimPerFrameMsAccum += tickTimeMs;

            if (_world.FixedElapsedTime - intermediate.LastResetTime >= UpdateInterval)
            {
                intermediate.LastResetTime = _world.FixedElapsedTime;

                ref var stats = ref _world.GlobalComponent<PerformanceStats>().Write;
                stats.SimTickHzAvg = (int)(
                    intermediate.SimTickHzSampleCount > 0
                        ? intermediate.SimTickHzSum / intermediate.SimTickHzSampleCount
                        : 0
                );
                stats.SimTickHzMin = (int)intermediate.SimTickHzMin;
                stats.SimTickHzMax = (int)intermediate.SimTickHzMax;
                stats.FpsAvg = (int)(
                    intermediate.FpsSampleCount > 0
                        ? intermediate.FpsSum / intermediate.FpsSampleCount
                        : 0
                );
                stats.FpsMin = (int)intermediate.FpsMin;
                stats.FpsMax = (int)intermediate.FpsMax;

                stats.EntityCount = _world.CountAllEntities();
                stats.TotalMemMb = (int)(Profiler.GetTotalAllocatedMemoryLong() / (1024f * 1024f));
                stats.MonoMemMb = (int)(Profiler.GetMonoHeapSizeLong() / (1024f * 1024f));
                stats.SimTickMs = tickTimeMs;
                stats.SimPerFrameMs =
                    intermediate.SimPerFrameMsSampleCount > 0
                        ? intermediate.SimPerFrameMsSum / intermediate.SimPerFrameMsSampleCount
                        : 0f;
                stats.FrameMs = _lastTotalFrameTimeSeconds * 1000f;

                intermediate.SimTickHzMin = 0f;
                intermediate.SimTickHzMax = 0f;
                intermediate.SimTickHzSum = 0f;
                intermediate.SimTickHzSampleCount = 0;
                intermediate.FpsMin = 0f;
                intermediate.FpsMax = 0f;
                intermediate.FpsSum = 0f;
                intermediate.FpsSampleCount = 0;
                intermediate.SimPerFrameMsSum = 0f;
                intermediate.SimPerFrameMsSampleCount = 0;
            }
        }

        void OnVariableUpdateStarted()
        {
            // Wall-clock time between consecutive Unity frames (the previous
            // frame's total cost: fixed + variable + rendering + idle).
            if (!_totalStopwatch.IsRunning)
            {
                _totalStopwatch.Restart();
                return;
            }

            var frameTimeSeconds = (float)_totalStopwatch.Elapsed.TotalSeconds;
            _totalStopwatch.Restart();

            if (frameTimeSeconds <= 0f)
            {
                return;
            }

            _lastTotalFrameTimeSeconds = frameTimeSeconds;
            var fps = 1f / frameTimeSeconds;

            ref var intermediate = ref _world.GlobalComponent<PerfStatsIntermediate>().Write;

            if (intermediate.FpsSampleCount == 0)
            {
                intermediate.FpsMin = fps;
                intermediate.FpsMax = fps;
            }
            else
            {
                if (fps < intermediate.FpsMin)
                {
                    intermediate.FpsMin = fps;
                }

                if (fps > intermediate.FpsMax)
                {
                    intermediate.FpsMax = fps;
                }
            }

            intermediate.FpsSum += fps;
            intermediate.FpsSampleCount++;

            // Snapshot the sim cost charged to the Unity frame that just ended
            // (may be 0 if no fixed tick ran inside it), then reset for the
            // next frame. Zero-cost frames are counted so the avg is directly
            // comparable to FrameMs.
            intermediate.SimPerFrameMsSum += intermediate.SimPerFrameMsAccum;
            intermediate.SimPerFrameMsSampleCount++;
            intermediate.SimPerFrameMsAccum = 0f;
        }

        public void Dispose()
        {
            _disposables.Dispose();
        }
    }
}
