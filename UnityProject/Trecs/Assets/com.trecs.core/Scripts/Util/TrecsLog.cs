// #define TRECS_LOGGING_ENABLED
// #define TRECS_TRACE_LOGGING_ENABLED

using System.Diagnostics;

namespace Trecs.Internal
{
    public enum LogLevels
    {
        Trace,
        Debug,
        Info,
        Warning,
        Error,
    }

    public class TrecsLog
    {
#if TRECS_LOGGING_ENABLED
        static readonly Dictionary<int, float> _throttledLogTimes = new();

        [RuntimeInitializeOnLoadMethod(RuntimeInitializeLoadType.BeforeSceneLoad)]
        static void OnRuntimeInitialize()
        {
            _throttledLogTimes.Clear();
        }
#endif

        public TrecsLog(string _) { }

        public bool IsLevelEnabled(LogLevels level)
        {
#if TRECS_LOGGING_ENABLED
            return level >= LogLevels.Info;
            // return level >= LogLevels.Debug;
#else
            return false;
#endif
        }

        public bool IsTraceEnabled()
        {
            return IsLevelEnabled(LogLevels.Trace);
        }

        public bool IsDebugEnabled()
        {
            return IsLevelEnabled(LogLevels.Debug);
        }

        public bool IsInfoEnabled()
        {
            return IsLevelEnabled(LogLevels.Info);
        }

        public bool IsWarningEnabled()
        {
            return IsLevelEnabled(LogLevels.Warning);
        }

        public bool IsErrorEnabled()
        {
            return IsLevelEnabled(LogLevels.Error);
        }

        ////////////////////////////////
        // Trace
        ////////////////////////////////

        [Conditional("TRECS_TRACE_LOGGING_ENABLED")]
        public void Trace(string messageTemplate)
        {
            if (IsLevelEnabled(LogLevels.Trace))
            {
                UnityEngine.Debug.LogFormat(messageTemplate);
            }
        }

        [Conditional("TRECS_TRACE_LOGGING_ENABLED")]
        public void Trace<T0>(string messageTemplate, T0 p0)
        {
            if (IsLevelEnabled(LogLevels.Trace))
            {
                UnityEngine.Debug.LogFormat(messageTemplate, p0);
            }
        }

        [Conditional("TRECS_TRACE_LOGGING_ENABLED")]
        public void Trace<T0, T1>(string messageTemplate, T0 p0, T1 p1)
        {
            if (IsLevelEnabled(LogLevels.Trace))
            {
                UnityEngine.Debug.LogFormat(messageTemplate, p0, p1);
            }
        }

        [Conditional("TRECS_TRACE_LOGGING_ENABLED")]
        public void Trace<T0, T1, T2>(string messageTemplate, T0 p0, T1 p1, T2 p2)
        {
            if (IsLevelEnabled(LogLevels.Trace))
            {
                UnityEngine.Debug.LogFormat(messageTemplate, p0, p1, p2);
            }
        }

        [Conditional("TRECS_TRACE_LOGGING_ENABLED")]
        public void Trace<T0, T1, T2, T3>(string messageTemplate, T0 p0, T1 p1, T2 p2, T3 p3)
        {
            if (IsLevelEnabled(LogLevels.Trace))
            {
                UnityEngine.Debug.LogFormat(messageTemplate, p0, p1, p2, p3);
            }
        }

        [Conditional("TRECS_TRACE_LOGGING_ENABLED")]
        public void Trace<T0, T1, T2, T3, T4>(
            string messageTemplate,
            T0 p0,
            T1 p1,
            T2 p2,
            T3 p3,
            T4 p4
        )
        {
            if (IsLevelEnabled(LogLevels.Trace))
            {
                UnityEngine.Debug.LogFormat(messageTemplate, p0, p1, p2, p3, p4);
            }
        }

        ////////////////////////////////
        // Debug
        ////////////////////////////////

        [Conditional("TRECS_LOGGING_ENABLED")]
        public void Debug(string messageTemplate)
        {
            if (IsLevelEnabled(LogLevels.Debug))
            {
                UnityEngine.Debug.LogFormat(messageTemplate);
            }
        }

        [Conditional("TRECS_LOGGING_ENABLED")]
        public void Debug<T0>(string messageTemplate, T0 p0)
        {
            if (IsLevelEnabled(LogLevels.Debug))
            {
                UnityEngine.Debug.LogFormat(messageTemplate, p0);
            }
        }

        [Conditional("TRECS_LOGGING_ENABLED")]
        public void Debug<T0, T1>(string messageTemplate, T0 p0, T1 p1)
        {
            if (IsLevelEnabled(LogLevels.Debug))
            {
                UnityEngine.Debug.LogFormat(messageTemplate, p0, p1);
            }
        }

        [Conditional("TRECS_LOGGING_ENABLED")]
        public void Debug<T0, T1, T2>(string messageTemplate, T0 p0, T1 p1, T2 p2)
        {
            if (IsLevelEnabled(LogLevels.Debug))
            {
                UnityEngine.Debug.LogFormat(messageTemplate, p0, p1, p2);
            }
        }

        [Conditional("TRECS_LOGGING_ENABLED")]
        public void Debug<T0, T1, T2, T3>(string messageTemplate, T0 p0, T1 p1, T2 p2, T3 p3)
        {
            if (IsLevelEnabled(LogLevels.Debug))
            {
                UnityEngine.Debug.LogFormat(messageTemplate, p0, p1, p2, p3);
            }
        }

        [Conditional("TRECS_LOGGING_ENABLED")]
        public void Debug<T0, T1, T2, T3, T4>(
            string messageTemplate,
            T0 p0,
            T1 p1,
            T2 p2,
            T3 p3,
            T4 p4
        )
        {
            if (IsLevelEnabled(LogLevels.Debug))
            {
                UnityEngine.Debug.LogFormat(messageTemplate, p0, p1, p2, p3, p4);
            }
        }

        ////////////////////////////////
        // Info
        ////////////////////////////////

        [Conditional("TRECS_LOGGING_ENABLED")]
        public void Info(string messageTemplate)
        {
            if (IsLevelEnabled(LogLevels.Info))
            {
                UnityEngine.Debug.LogFormat(messageTemplate);
            }
        }

        [Conditional("TRECS_LOGGING_ENABLED")]
        public void Info<T0>(string messageTemplate, T0 p0)
        {
            if (IsLevelEnabled(LogLevels.Info))
            {
                UnityEngine.Debug.LogFormat(messageTemplate, p0);
            }
        }

        [Conditional("TRECS_LOGGING_ENABLED")]
        public void Info<T0, T1>(string messageTemplate, T0 p0, T1 p1)
        {
            if (IsLevelEnabled(LogLevels.Info))
            {
                UnityEngine.Debug.LogFormat(messageTemplate, p0, p1);
            }
        }

        [Conditional("TRECS_LOGGING_ENABLED")]
        public void Info<T0, T1, T2>(string messageTemplate, T0 p0, T1 p1, T2 p2)
        {
            if (IsLevelEnabled(LogLevels.Info))
            {
                UnityEngine.Debug.LogFormat(messageTemplate, p0, p1, p2);
            }
        }

        [Conditional("TRECS_LOGGING_ENABLED")]
        public void Info<T0, T1, T2, T3>(string messageTemplate, T0 p0, T1 p1, T2 p2, T3 p3)
        {
            if (IsLevelEnabled(LogLevels.Info))
            {
                UnityEngine.Debug.LogFormat(messageTemplate, p0, p1, p2, p3);
            }
        }

        [Conditional("TRECS_LOGGING_ENABLED")]
        public void Info<T0, T1, T2, T3, T4>(
            string messageTemplate,
            T0 p0,
            T1 p1,
            T2 p2,
            T3 p3,
            T4 p4
        )
        {
            if (IsLevelEnabled(LogLevels.Info))
            {
                UnityEngine.Debug.LogFormat(messageTemplate, p0, p1, p2, p3, p4);
            }
        }

        ////////////////////////////////
        // Warning
        ////////////////////////////////

        [Conditional("TRECS_LOGGING_ENABLED")]
        public void Warning(string messageTemplate)
        {
            if (IsLevelEnabled(LogLevels.Warning))
            {
                UnityEngine.Debug.LogWarningFormat(messageTemplate);
            }
        }

        [Conditional("TRECS_LOGGING_ENABLED")]
        public void Warning<T0>(string messageTemplate, T0 p0)
        {
            if (IsLevelEnabled(LogLevels.Warning))
            {
                UnityEngine.Debug.LogWarningFormat(messageTemplate, p0);
            }
        }

        [Conditional("TRECS_LOGGING_ENABLED")]
        public void Warning<T0, T1>(string messageTemplate, T0 p0, T1 p1)
        {
            if (IsLevelEnabled(LogLevels.Warning))
            {
                UnityEngine.Debug.LogWarningFormat(messageTemplate, p0, p1);
            }
        }

        [Conditional("TRECS_LOGGING_ENABLED")]
        public void Warning<T0, T1, T2>(string messageTemplate, T0 p0, T1 p1, T2 p2)
        {
            if (IsLevelEnabled(LogLevels.Warning))
            {
                UnityEngine.Debug.LogWarningFormat(messageTemplate, p0, p1, p2);
            }
        }

        [Conditional("TRECS_LOGGING_ENABLED")]
        public void Warning<T0, T1, T2, T3>(string messageTemplate, T0 p0, T1 p1, T2 p2, T3 p3)
        {
            if (IsLevelEnabled(LogLevels.Warning))
            {
                UnityEngine.Debug.LogWarningFormat(messageTemplate, p0, p1, p2, p3);
            }
        }

        [Conditional("TRECS_LOGGING_ENABLED")]
        public void Warning<T0, T1, T2, T3, T4>(
            string messageTemplate,
            T0 p0,
            T1 p1,
            T2 p2,
            T3 p3,
            T4 p4
        )
        {
            if (IsLevelEnabled(LogLevels.Warning))
            {
                UnityEngine.Debug.LogWarningFormat(messageTemplate, p0, p1, p2, p3, p4);
            }
        }

        ////////////////////////////////
        // Error
        ////////////////////////////////

        [Conditional("TRECS_LOGGING_ENABLED")]
        public void Error(string messageTemplate)
        {
            if (IsLevelEnabled(LogLevels.Error))
            {
                UnityEngine.Debug.LogErrorFormat(messageTemplate);
            }
        }

        [Conditional("TRECS_LOGGING_ENABLED")]
        public void Error<T0>(string messageTemplate, T0 p0)
        {
            if (IsLevelEnabled(LogLevels.Error))
            {
                UnityEngine.Debug.LogErrorFormat(messageTemplate, p0);
            }
        }

        [Conditional("TRECS_LOGGING_ENABLED")]
        public void Error<T0, T1>(string messageTemplate, T0 p0, T1 p1)
        {
            if (IsLevelEnabled(LogLevels.Error))
            {
                UnityEngine.Debug.LogErrorFormat(messageTemplate, p0, p1);
            }
        }

        [Conditional("TRECS_LOGGING_ENABLED")]
        public void Error<T0, T1, T2>(string messageTemplate, T0 p0, T1 p1, T2 p2)
        {
            if (IsLevelEnabled(LogLevels.Error))
            {
                UnityEngine.Debug.LogErrorFormat(messageTemplate, p0, p1, p2);
            }
        }

        [Conditional("TRECS_LOGGING_ENABLED")]
        public void Error<T0, T1, T2, T3>(string messageTemplate, T0 p0, T1 p1, T2 p2, T3 p3)
        {
            if (IsLevelEnabled(LogLevels.Error))
            {
                UnityEngine.Debug.LogErrorFormat(messageTemplate, p0, p1, p2, p3);
            }
        }

        [Conditional("TRECS_LOGGING_ENABLED")]
        public void Error<T0, T1, T2, T3, T4>(
            string messageTemplate,
            T0 p0,
            T1 p1,
            T2 p2,
            T3 p3,
            T4 p4
        )
        {
            if (IsLevelEnabled(LogLevels.Error))
            {
                UnityEngine.Debug.LogErrorFormat(messageTemplate, p0, p1, p2, p3, p4);
            }
        }
    }
}
