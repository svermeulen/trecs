using System.Diagnostics;
using System.Text;

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
    /// <b>Error</b> calls are never gated by build configuration or by
    /// <see cref="WorldSettings.MinLogLevel"/> — they always emit. <b>Warning</b>,
    /// <b>Info</b>, and <b>Debug</b> calls are compiled out unless one of
    /// <c>UNITY_EDITOR</c>, <c>DEVELOPMENT_BUILD</c>, or <c>TRECS_LOGGING_ENABLED</c>
    /// is defined. <b>Trace</b> calls are compiled out unless
    /// <c>TRECS_TRACE_LOGGING_ENABLED</c> is defined.
    /// </para>
    /// <para>
    /// In builds where the calls compile in, the runtime level check (against
    /// <see cref="WorldSettings.MinLogLevel"/>) filters further.
    /// </para>
    /// </remarks>
    public sealed class TrecsLog
    {
        const string EditorDefine = "UNITY_EDITOR";
        const string DevelopmentBuildDefine = "DEVELOPMENT_BUILD";
        const string LoggingDefine = "TRECS_LOGGING_ENABLED";
        const string TraceLoggingDefine = "TRECS_TRACE_LOGGING_ENABLED";

        const string Prefix = "[Trecs] ";

        readonly WorldSettings _settings;

        public TrecsLog(WorldSettings settings)
        {
            _settings = settings;
        }

        /// <summary>
        /// Convenience logger for code paths that run outside a <c>World</c>'s
        /// lifecycle (test fixtures, file-peek serialization, etc.). Uses a default
        /// <see cref="WorldSettings"/> so its level is <see cref="LogLevel.Warning"/>.
        /// Production framework code should not use this — it should accept the
        /// world's <c>TrecsLog</c> via constructor injection.
        /// </summary>
        public static TrecsLog Default { get; } = new(new WorldSettings());

        public bool IsLevelEnabled(LogLevel level) => level >= _settings.MinLogLevel;

        public bool IsTraceEnabled() => IsLevelEnabled(LogLevel.Trace);

        public bool IsDebugEnabled() => IsLevelEnabled(LogLevel.Debug);

        public bool IsInfoEnabled() => IsLevelEnabled(LogLevel.Info);

        public bool IsWarningEnabled() => IsLevelEnabled(LogLevel.Warning);

        // Trecs log call sites use Rust/Serilog-style placeholders — `{}` for the next
        // positional arg, optionally with a format hint like `{0.00}`, `{l}`, or `{@}`.
        // Translate them to .NET String.Format syntax (`{0}`, `{0:0.00}`, ...) so the
        // underlying Debug.LogFormat call can render them. The `{l}` and `{@}` hints
        // come from Serilog (stringify list / destructure object); here we just route
        // them through the default ToString since UnityEngine.Debug.LogFormat doesn't
        // do structured logging.
        static string ConvertPlaceholders(string template)
        {
            if (string.IsNullOrEmpty(template) || template.IndexOf('{') < 0)
            {
                return template;
            }
            var sb = new StringBuilder(template.Length + 8);
            int argIndex = 0;
            int i = 0;
            while (i < template.Length)
            {
                char c = template[i];
                if (c == '{')
                {
                    int end = template.IndexOf('}', i + 1);
                    if (end < 0)
                    {
                        sb.Append(c);
                        i++;
                        continue;
                    }
                    var inner = template.Substring(i + 1, end - i - 1);
                    sb.Append('{').Append(argIndex);
                    if (inner.Length > 0 && inner != "@" && inner != "l")
                    {
                        sb.Append(':').Append(inner);
                    }
                    sb.Append('}');
                    argIndex++;
                    i = end + 1;
                }
                else
                {
                    sb.Append(c);
                    i++;
                }
            }
            return sb.ToString();
        }

        string FormatTemplate(string messageTemplate) =>
            Prefix + ConvertPlaceholders(messageTemplate);

        ////////////////////////////////
        // Trace
        ////////////////////////////////

        [Conditional(TraceLoggingDefine)]
        public void Trace(string messageTemplate)
        {
            if (IsLevelEnabled(LogLevel.Trace))
            {
                UnityEngine.Debug.LogFormat(FormatTemplate(messageTemplate));
            }
        }

        [Conditional(TraceLoggingDefine)]
        public void Trace<T0>(string messageTemplate, T0 p0)
        {
            if (IsLevelEnabled(LogLevel.Trace))
            {
                UnityEngine.Debug.LogFormat(FormatTemplate(messageTemplate), p0);
            }
        }

        [Conditional(TraceLoggingDefine)]
        public void Trace<T0, T1>(string messageTemplate, T0 p0, T1 p1)
        {
            if (IsLevelEnabled(LogLevel.Trace))
            {
                UnityEngine.Debug.LogFormat(FormatTemplate(messageTemplate), p0, p1);
            }
        }

        [Conditional(TraceLoggingDefine)]
        public void Trace<T0, T1, T2>(string messageTemplate, T0 p0, T1 p1, T2 p2)
        {
            if (IsLevelEnabled(LogLevel.Trace))
            {
                UnityEngine.Debug.LogFormat(FormatTemplate(messageTemplate), p0, p1, p2);
            }
        }

        [Conditional(TraceLoggingDefine)]
        public void Trace<T0, T1, T2, T3>(string messageTemplate, T0 p0, T1 p1, T2 p2, T3 p3)
        {
            if (IsLevelEnabled(LogLevel.Trace))
            {
                UnityEngine.Debug.LogFormat(FormatTemplate(messageTemplate), p0, p1, p2, p3);
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
                UnityEngine.Debug.LogFormat(FormatTemplate(messageTemplate), p0, p1, p2, p3, p4);
            }
        }

        ////////////////////////////////
        // Debug
        ////////////////////////////////

        [Conditional(EditorDefine)]
        [Conditional(DevelopmentBuildDefine)]
        [Conditional(LoggingDefine)]
        public void Debug(string messageTemplate)
        {
            if (IsLevelEnabled(LogLevel.Debug))
            {
                UnityEngine.Debug.LogFormat(FormatTemplate(messageTemplate));
            }
        }

        [Conditional(EditorDefine)]
        [Conditional(DevelopmentBuildDefine)]
        [Conditional(LoggingDefine)]
        public void Debug<T0>(string messageTemplate, T0 p0)
        {
            if (IsLevelEnabled(LogLevel.Debug))
            {
                UnityEngine.Debug.LogFormat(FormatTemplate(messageTemplate), p0);
            }
        }

        [Conditional(EditorDefine)]
        [Conditional(DevelopmentBuildDefine)]
        [Conditional(LoggingDefine)]
        public void Debug<T0, T1>(string messageTemplate, T0 p0, T1 p1)
        {
            if (IsLevelEnabled(LogLevel.Debug))
            {
                UnityEngine.Debug.LogFormat(FormatTemplate(messageTemplate), p0, p1);
            }
        }

        [Conditional(EditorDefine)]
        [Conditional(DevelopmentBuildDefine)]
        [Conditional(LoggingDefine)]
        public void Debug<T0, T1, T2>(string messageTemplate, T0 p0, T1 p1, T2 p2)
        {
            if (IsLevelEnabled(LogLevel.Debug))
            {
                UnityEngine.Debug.LogFormat(FormatTemplate(messageTemplate), p0, p1, p2);
            }
        }

        [Conditional(EditorDefine)]
        [Conditional(DevelopmentBuildDefine)]
        [Conditional(LoggingDefine)]
        public void Debug<T0, T1, T2, T3>(string messageTemplate, T0 p0, T1 p1, T2 p2, T3 p3)
        {
            if (IsLevelEnabled(LogLevel.Debug))
            {
                UnityEngine.Debug.LogFormat(FormatTemplate(messageTemplate), p0, p1, p2, p3);
            }
        }

        [Conditional(EditorDefine)]
        [Conditional(DevelopmentBuildDefine)]
        [Conditional(LoggingDefine)]
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
                UnityEngine.Debug.LogFormat(FormatTemplate(messageTemplate), p0, p1, p2, p3, p4);
            }
        }

        ////////////////////////////////
        // Info
        ////////////////////////////////

        [Conditional(EditorDefine)]
        [Conditional(DevelopmentBuildDefine)]
        [Conditional(LoggingDefine)]
        public void Info(string messageTemplate)
        {
            if (IsLevelEnabled(LogLevel.Info))
            {
                UnityEngine.Debug.LogFormat(FormatTemplate(messageTemplate));
            }
        }

        [Conditional(EditorDefine)]
        [Conditional(DevelopmentBuildDefine)]
        [Conditional(LoggingDefine)]
        public void Info<T0>(string messageTemplate, T0 p0)
        {
            if (IsLevelEnabled(LogLevel.Info))
            {
                UnityEngine.Debug.LogFormat(FormatTemplate(messageTemplate), p0);
            }
        }

        [Conditional(EditorDefine)]
        [Conditional(DevelopmentBuildDefine)]
        [Conditional(LoggingDefine)]
        public void Info<T0, T1>(string messageTemplate, T0 p0, T1 p1)
        {
            if (IsLevelEnabled(LogLevel.Info))
            {
                UnityEngine.Debug.LogFormat(FormatTemplate(messageTemplate), p0, p1);
            }
        }

        [Conditional(EditorDefine)]
        [Conditional(DevelopmentBuildDefine)]
        [Conditional(LoggingDefine)]
        public void Info<T0, T1, T2>(string messageTemplate, T0 p0, T1 p1, T2 p2)
        {
            if (IsLevelEnabled(LogLevel.Info))
            {
                UnityEngine.Debug.LogFormat(FormatTemplate(messageTemplate), p0, p1, p2);
            }
        }

        [Conditional(EditorDefine)]
        [Conditional(DevelopmentBuildDefine)]
        [Conditional(LoggingDefine)]
        public void Info<T0, T1, T2, T3>(string messageTemplate, T0 p0, T1 p1, T2 p2, T3 p3)
        {
            if (IsLevelEnabled(LogLevel.Info))
            {
                UnityEngine.Debug.LogFormat(FormatTemplate(messageTemplate), p0, p1, p2, p3);
            }
        }

        [Conditional(EditorDefine)]
        [Conditional(DevelopmentBuildDefine)]
        [Conditional(LoggingDefine)]
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
                UnityEngine.Debug.LogFormat(FormatTemplate(messageTemplate), p0, p1, p2, p3, p4);
            }
        }

        ////////////////////////////////
        // Warning
        ////////////////////////////////

        [Conditional(EditorDefine)]
        [Conditional(DevelopmentBuildDefine)]
        [Conditional(LoggingDefine)]
        public void Warning(string messageTemplate)
        {
            if (IsLevelEnabled(LogLevel.Warning))
            {
                UnityEngine.Debug.LogWarningFormat(FormatTemplate(messageTemplate));
            }
        }

        [Conditional(EditorDefine)]
        [Conditional(DevelopmentBuildDefine)]
        [Conditional(LoggingDefine)]
        public void Warning<T0>(string messageTemplate, T0 p0)
        {
            if (IsLevelEnabled(LogLevel.Warning))
            {
                UnityEngine.Debug.LogWarningFormat(FormatTemplate(messageTemplate), p0);
            }
        }

        [Conditional(EditorDefine)]
        [Conditional(DevelopmentBuildDefine)]
        [Conditional(LoggingDefine)]
        public void Warning<T0, T1>(string messageTemplate, T0 p0, T1 p1)
        {
            if (IsLevelEnabled(LogLevel.Warning))
            {
                UnityEngine.Debug.LogWarningFormat(FormatTemplate(messageTemplate), p0, p1);
            }
        }

        [Conditional(EditorDefine)]
        [Conditional(DevelopmentBuildDefine)]
        [Conditional(LoggingDefine)]
        public void Warning<T0, T1, T2>(string messageTemplate, T0 p0, T1 p1, T2 p2)
        {
            if (IsLevelEnabled(LogLevel.Warning))
            {
                UnityEngine.Debug.LogWarningFormat(FormatTemplate(messageTemplate), p0, p1, p2);
            }
        }

        [Conditional(EditorDefine)]
        [Conditional(DevelopmentBuildDefine)]
        [Conditional(LoggingDefine)]
        public void Warning<T0, T1, T2, T3>(string messageTemplate, T0 p0, T1 p1, T2 p2, T3 p3)
        {
            if (IsLevelEnabled(LogLevel.Warning))
            {
                UnityEngine.Debug.LogWarningFormat(FormatTemplate(messageTemplate), p0, p1, p2, p3);
            }
        }

        [Conditional(EditorDefine)]
        [Conditional(DevelopmentBuildDefine)]
        [Conditional(LoggingDefine)]
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
                UnityEngine.Debug.LogWarningFormat(
                    FormatTemplate(messageTemplate),
                    p0,
                    p1,
                    p2,
                    p3,
                    p4
                );
            }
        }

        ////////////////////////////////
        // Error — always emits in every build configuration; ignores MinLogLevel.
        ////////////////////////////////

        public void Error(string messageTemplate)
        {
            UnityEngine.Debug.LogErrorFormat(FormatTemplate(messageTemplate));
        }

        public void Error<T0>(string messageTemplate, T0 p0)
        {
            UnityEngine.Debug.LogErrorFormat(FormatTemplate(messageTemplate), p0);
        }

        public void Error<T0, T1>(string messageTemplate, T0 p0, T1 p1)
        {
            UnityEngine.Debug.LogErrorFormat(FormatTemplate(messageTemplate), p0, p1);
        }

        public void Error<T0, T1, T2>(string messageTemplate, T0 p0, T1 p1, T2 p2)
        {
            UnityEngine.Debug.LogErrorFormat(FormatTemplate(messageTemplate), p0, p1, p2);
        }

        public void Error<T0, T1, T2, T3>(string messageTemplate, T0 p0, T1 p1, T2 p2, T3 p3)
        {
            UnityEngine.Debug.LogErrorFormat(FormatTemplate(messageTemplate), p0, p1, p2, p3);
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
            UnityEngine.Debug.LogErrorFormat(FormatTemplate(messageTemplate), p0, p1, p2, p3, p4);
        }
    }
}
