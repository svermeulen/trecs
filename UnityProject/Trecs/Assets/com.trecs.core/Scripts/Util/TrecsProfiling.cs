using System;
using System.Collections.Generic;
using UnityEngine.Profiling;

namespace Trecs.Internal
{
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

            Profiler.BeginSample(FormatStringInterner.GetOrCreate(messageTemplate, propertyValue));
            return _sharedScope;
        }

        static class FormatStringInterner
        {
            static readonly Dictionary<int, string> _cache = new();

            // Can't just use params, and have to use generics, because mem allocs
            static int GetHashCode<T>(string p1, T p2)
            {
                unchecked // Overflow is fine, just wrap
                {
                    int hash = 17;
                    hash = hash * 29 + p1.GetHashCode();
                    hash = hash * 29 + p2.GetHashCode();
                    return hash;
                }
            }

            public static string GetOrCreate<T0>(string messageTemplate, T0 propertyValue0)
            {
                var hash = GetHashCode(messageTemplate, propertyValue0);

                if (!_cache.TryGetValue(hash, out var result))
                {
                    result = string.Format(messageTemplate, propertyValue0);
                    _cache.Add(hash, result);
                }

                return result;
            }
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
