using System.Diagnostics;

namespace Trecs
{
    /// <summary>
    /// Severity levels for <see cref="Trecs.Internal.TrecsLog"/>. Set the minimum
    /// emit level via <see cref="WorldSettings.MinLogLevel"/>.
    /// </summary>
    /// <remarks>
    /// <see cref="Error"/> messages are always emitted regardless of this setting and
    /// regardless of build configuration. The setting controls the minimum level for
    /// Warning, Info, Debug, and Trace messages. Use <see cref="None"/> to suppress
    /// everything below Error.
    /// </remarks>
    public enum LogLevel
    {
        Trace,
        Debug,
        Info,
        Warning,
        Error,
        None,
    }
}

namespace Trecs.Internal
{
    /// <summary>
    /// Trecs's shared logger. Exactly one instance is constructed per <c>World</c>
    /// by <c>WorldBuilder</c> and injected into every framework class that needs to
    /// log; user systems read it from <c>WorldAccessor.Log</c>.
    /// </summary>
    /// <remarks>
    /// <para>
    /// Compile-time gating: <b>Error</b> and <b>Warning</b> always compile in (any
    /// build configuration). <b>Info</b> and <b>Debug</b> are gated by
    /// <c>UNITY_EDITOR</c> — they are stripped from non-editor builds. <b>Trace</b>
    /// is gated by <c>TRECS_TRACE_LOGGING_ENABLED</c> — stripped from every build
    /// unless that define is set.
    /// </para>
    /// <para>
    /// Runtime gating: in builds where a call compiles in, it is then filtered by
    /// <see cref="WorldSettings.MinLogLevel"/>. Error ignores this and always emits.
    /// </para>
    /// </remarks>
    public sealed class TrecsLog
    {
        const string EditorDefine = "UNITY_EDITOR";
        const string TraceLoggingDefine = "TRECS_TRACE_LOGGING_ENABLED";

        const string Prefix = "[Trecs] ";

        readonly WorldSettings _settings;

        public TrecsLog(WorldSettings settings)
        {
            _settings = settings;
        }

        /// <summary>
        /// Convenience logger for code paths that genuinely run with no
        /// <see cref="World"/> in scope — the file-peek serialization helpers
        /// (<c>BinarySerializationReader</c>, <c>BinarySerializationWriter</c>,
        /// <c>SerializerRegistry</c>, <c>RecordingBundleSerializer</c>) and test
        /// fixtures that exercise individual heaps. Anything that has a
        /// <see cref="World"/> should use <c>world.Log</c> instead so it honors
        /// that world's <see cref="WorldSettings.MinLogLevel"/>.
        /// </summary>
        public static TrecsLog Default { get; } = new(new WorldSettings());

        public bool IsLevelEnabled(LogLevel level) => level >= _settings.MinLogLevel;

        public bool IsTraceEnabled() => IsLevelEnabled(LogLevel.Trace);

        public bool IsDebugEnabled() => IsLevelEnabled(LogLevel.Debug);

        public bool IsInfoEnabled() => IsLevelEnabled(LogLevel.Info);

        public bool IsWarningEnabled() => IsLevelEnabled(LogLevel.Warning);

        ////////////////////////////////
        // Trace
        ////////////////////////////////

        [Conditional(TraceLoggingDefine)]
        public void Trace(string messageTemplate)
        {
            if (IsLevelEnabled(LogLevel.Trace))
            {
                UnityEngine.Debug.LogFormat(Prefix + messageTemplate);
            }
        }

        [Conditional(TraceLoggingDefine)]
        public void Trace<T0>(string messageTemplate, T0 p0)
        {
            if (IsLevelEnabled(LogLevel.Trace))
            {
                UnityEngine.Debug.LogFormat(Prefix + messageTemplate, p0);
            }
        }

        [Conditional(TraceLoggingDefine)]
        public void Trace<T0, T1>(string messageTemplate, T0 p0, T1 p1)
        {
            if (IsLevelEnabled(LogLevel.Trace))
            {
                UnityEngine.Debug.LogFormat(Prefix + messageTemplate, p0, p1);
            }
        }

        [Conditional(TraceLoggingDefine)]
        public void Trace<T0, T1, T2>(string messageTemplate, T0 p0, T1 p1, T2 p2)
        {
            if (IsLevelEnabled(LogLevel.Trace))
            {
                UnityEngine.Debug.LogFormat(Prefix + messageTemplate, p0, p1, p2);
            }
        }

        [Conditional(TraceLoggingDefine)]
        public void Trace<T0, T1, T2, T3>(string messageTemplate, T0 p0, T1 p1, T2 p2, T3 p3)
        {
            if (IsLevelEnabled(LogLevel.Trace))
            {
                UnityEngine.Debug.LogFormat(Prefix + messageTemplate, p0, p1, p2, p3);
            }
        }

        [Conditional(TraceLoggingDefine)]
        public void Trace<T0, T1, T2, T3, T4>(
            string messageTemplate,
            T0 p0,
            T1 p1,
            T2 p2,
            T3 p3,
            T4 p4
        )
        {
            if (IsLevelEnabled(LogLevel.Trace))
            {
                UnityEngine.Debug.LogFormat(Prefix + messageTemplate, p0, p1, p2, p3, p4);
            }
        }

        ////////////////////////////////
        // Debug
        ////////////////////////////////

        [Conditional(EditorDefine)]
        public void Debug(string messageTemplate)
        {
            if (IsLevelEnabled(LogLevel.Debug))
            {
                UnityEngine.Debug.LogFormat(Prefix + messageTemplate);
            }
        }

        [Conditional(EditorDefine)]
        public void Debug<T0>(string messageTemplate, T0 p0)
        {
            if (IsLevelEnabled(LogLevel.Debug))
            {
                UnityEngine.Debug.LogFormat(Prefix + messageTemplate, p0);
            }
        }

        [Conditional(EditorDefine)]
        public void Debug<T0, T1>(string messageTemplate, T0 p0, T1 p1)
        {
            if (IsLevelEnabled(LogLevel.Debug))
            {
                UnityEngine.Debug.LogFormat(Prefix + messageTemplate, p0, p1);
            }
        }

        [Conditional(EditorDefine)]
        public void Debug<T0, T1, T2>(string messageTemplate, T0 p0, T1 p1, T2 p2)
        {
            if (IsLevelEnabled(LogLevel.Debug))
            {
                UnityEngine.Debug.LogFormat(Prefix + messageTemplate, p0, p1, p2);
            }
        }

        [Conditional(EditorDefine)]
        public void Debug<T0, T1, T2, T3>(string messageTemplate, T0 p0, T1 p1, T2 p2, T3 p3)
        {
            if (IsLevelEnabled(LogLevel.Debug))
            {
                UnityEngine.Debug.LogFormat(Prefix + messageTemplate, p0, p1, p2, p3);
            }
        }

        [Conditional(EditorDefine)]
        public void Debug<T0, T1, T2, T3, T4>(
            string messageTemplate,
            T0 p0,
            T1 p1,
            T2 p2,
            T3 p3,
            T4 p4
        )
        {
            if (IsLevelEnabled(LogLevel.Debug))
            {
                UnityEngine.Debug.LogFormat(Prefix + messageTemplate, p0, p1, p2, p3, p4);
            }
        }

        ////////////////////////////////
        // Info
        ////////////////////////////////

        [Conditional(EditorDefine)]
        public void Info(string messageTemplate)
        {
            if (IsLevelEnabled(LogLevel.Info))
            {
                UnityEngine.Debug.LogFormat(Prefix + messageTemplate);
            }
        }

        [Conditional(EditorDefine)]
        public void Info<T0>(string messageTemplate, T0 p0)
        {
            if (IsLevelEnabled(LogLevel.Info))
            {
                UnityEngine.Debug.LogFormat(Prefix + messageTemplate, p0);
            }
        }

        [Conditional(EditorDefine)]
        public void Info<T0, T1>(string messageTemplate, T0 p0, T1 p1)
        {
            if (IsLevelEnabled(LogLevel.Info))
            {
                UnityEngine.Debug.LogFormat(Prefix + messageTemplate, p0, p1);
            }
        }

        [Conditional(EditorDefine)]
        public void Info<T0, T1, T2>(string messageTemplate, T0 p0, T1 p1, T2 p2)
        {
            if (IsLevelEnabled(LogLevel.Info))
            {
                UnityEngine.Debug.LogFormat(Prefix + messageTemplate, p0, p1, p2);
            }
        }

        [Conditional(EditorDefine)]
        public void Info<T0, T1, T2, T3>(string messageTemplate, T0 p0, T1 p1, T2 p2, T3 p3)
        {
            if (IsLevelEnabled(LogLevel.Info))
            {
                UnityEngine.Debug.LogFormat(Prefix + messageTemplate, p0, p1, p2, p3);
            }
        }

        [Conditional(EditorDefine)]
        public void Info<T0, T1, T2, T3, T4>(
            string messageTemplate,
            T0 p0,
            T1 p1,
            T2 p2,
            T3 p3,
            T4 p4
        )
        {
            if (IsLevelEnabled(LogLevel.Info))
            {
                UnityEngine.Debug.LogFormat(Prefix + messageTemplate, p0, p1, p2, p3, p4);
            }
        }

        ////////////////////////////////
        // Warning
        ////////////////////////////////

        public void Warning(string messageTemplate)
        {
            if (IsLevelEnabled(LogLevel.Warning))
            {
                UnityEngine.Debug.LogWarningFormat(Prefix + messageTemplate);
            }
        }

        public void Warning<T0>(string messageTemplate, T0 p0)
        {
            if (IsLevelEnabled(LogLevel.Warning))
            {
                UnityEngine.Debug.LogWarningFormat(Prefix + messageTemplate, p0);
            }
        }

        public void Warning<T0, T1>(string messageTemplate, T0 p0, T1 p1)
        {
            if (IsLevelEnabled(LogLevel.Warning))
            {
                UnityEngine.Debug.LogWarningFormat(Prefix + messageTemplate, p0, p1);
            }
        }

        public void Warning<T0, T1, T2>(string messageTemplate, T0 p0, T1 p1, T2 p2)
        {
            if (IsLevelEnabled(LogLevel.Warning))
            {
                UnityEngine.Debug.LogWarningFormat(Prefix + messageTemplate, p0, p1, p2);
            }
        }

        public void Warning<T0, T1, T2, T3>(string messageTemplate, T0 p0, T1 p1, T2 p2, T3 p3)
        {
            if (IsLevelEnabled(LogLevel.Warning))
            {
                UnityEngine.Debug.LogWarningFormat(Prefix + messageTemplate, p0, p1, p2, p3);
            }
        }

        public void Warning<T0, T1, T2, T3, T4>(
            string messageTemplate,
            T0 p0,
            T1 p1,
            T2 p2,
            T3 p3,
            T4 p4
        )
        {
            if (IsLevelEnabled(LogLevel.Warning))
            {
                UnityEngine.Debug.LogWarningFormat(Prefix + messageTemplate, p0, p1, p2, p3, p4);
            }
        }

        ////////////////////////////////
        // Error — always emits in every build configuration; ignores MinLogLevel.
        ////////////////////////////////

        public void Error(string messageTemplate)
        {
            UnityEngine.Debug.LogErrorFormat(Prefix + messageTemplate);
        }

        public void Error<T0>(string messageTemplate, T0 p0)
        {
            UnityEngine.Debug.LogErrorFormat(Prefix + messageTemplate, p0);
        }

        public void Error<T0, T1>(string messageTemplate, T0 p0, T1 p1)
        {
            UnityEngine.Debug.LogErrorFormat(Prefix + messageTemplate, p0, p1);
        }

        public void Error<T0, T1, T2>(string messageTemplate, T0 p0, T1 p1, T2 p2)
        {
            UnityEngine.Debug.LogErrorFormat(Prefix + messageTemplate, p0, p1, p2);
        }

        public void Error<T0, T1, T2, T3>(string messageTemplate, T0 p0, T1 p1, T2 p2, T3 p3)
        {
            UnityEngine.Debug.LogErrorFormat(Prefix + messageTemplate, p0, p1, p2, p3);
        }

        public void Error<T0, T1, T2, T3, T4>(
            string messageTemplate,
            T0 p0,
            T1 p1,
            T2 p2,
            T3 p3,
            T4 p4
        )
        {
            UnityEngine.Debug.LogErrorFormat(Prefix + messageTemplate, p0, p1, p2, p3, p4);
        }
    }
}
