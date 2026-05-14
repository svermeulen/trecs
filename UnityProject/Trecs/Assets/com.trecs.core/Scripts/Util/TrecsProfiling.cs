using System;
using UnityEngine.Profiling;

namespace Trecs.Internal
{
    public class NullDisposable : IDisposable
    {
        private NullDisposable() { }

        public void Dispose() { }

        public static readonly NullDisposable Instance = new();
    }

    public static class TrecsProfiling
    {
#if ENABLE_PROFILER
        class ProfilerScope : IDisposable
        {
            public void Dispose()
            {
                Profiler.EndSample();
            }
        }

        static readonly ProfilerScope _sharedScope = new();

        public static bool IsEnabled
        {
            get { return Profiler.enabled; }
        }

        public static IDisposable Start(string messageTemplate)
        {
            if (!IsEnabled)
            {
                return NullDisposable.Instance;
            }

            Profiler.BeginSample(messageTemplate);
            return _sharedScope;
        }

        public static IDisposable Start<T>(string messageTemplate, T propertyValue)
        {
            if (!IsEnabled)
            {
                return NullDisposable.Instance;
            }

            Profiler.BeginSample(string.Format(messageTemplate, propertyValue));
            return _sharedScope;
        }
#else
        public static bool IsEnabled
        {
            get { return false; }
        }

        public static IDisposable Start(string _)
        {
            return NullDisposable.Instance;
        }

        public static IDisposable Start<T>(string messageTemplate, T propertyValue)
        {
            return NullDisposable.Instance;
        }
#endif
    }
}
