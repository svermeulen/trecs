using System;
using System.Collections.Generic;
using System.ComponentModel;
using System.Globalization;
using System.Text;

namespace Trecs.Internal
{
    [EditorBrowsable(EditorBrowsableState.Never)]
    public static class CustomFormatter
    {
        [ThreadStatic]
        static StringBuilder _customFormatBuffer;

        public static string CustomFormatArgs(string messageTemplate, IReadOnlyList<object> args)
        {
            _customFormatBuffer ??= new StringBuilder();

            var buffer = _customFormatBuffer;
            buffer.Clear();

            int argIndex = 0;
            int pos = 0;
            int length = messageTemplate.Length;

            while (pos < length)
            {
                int openBraceIndex = messageTemplate.IndexOf('{', pos);
                if (openBraceIndex == -1)
                {
                    buffer.Append(messageTemplate, pos, length - pos);
                    break;
                }

                buffer.Append(messageTemplate, pos, openBraceIndex - pos);

                int closeBraceIndex = messageTemplate.IndexOf('}', openBraceIndex + 1);
                if (closeBraceIndex == -1)
                {
                    // No matching closing brace, append the rest and exit
                    buffer.Append(messageTemplate, openBraceIndex, length - openBraceIndex);
                    break;
                }

                // Extract the format inside the braces
                int formatLength = closeBraceIndex - openBraceIndex - 1;
                string format =
                    formatLength > 0
                        ? messageTemplate.Substring(openBraceIndex + 1, formatLength).Trim()
                        : string.Empty;

                // Handle .NET-style indexed placeholders (e.g., "{0:F1}" or "{0}")
                // We don't support indices - args are consumed sequentially - but we try to extract the format
                if (format.Length > 0 && char.IsDigit(format[0]))
                {
                    int colonIndex = format.IndexOf(':');
                    if (colonIndex >= 0)
                    {
                        // "{0:F1}" style - extract format after colon
                        buffer.Append("[unsupported indexed format '").Append(format).Append("']");
                        format = format[(colonIndex + 1)..].Trim();
                    }
                }

                // Format the argument
                if (argIndex >= args.Count)
                {
                    buffer.Append("null");
                }
                else
                {
                    object arg = args[argIndex++];
                    FormatArg(arg, format, buffer);
                }

                pos = closeBraceIndex + 1;
            }

            if (argIndex < args.Count)
            {
                buffer.Append(" [too many args]");
            }

            string result = buffer.ToString(); // alloc here
            buffer.Clear();
            return result;
        }

        static bool IsIndexNumberFormat(string s)
        {
            if (string.IsNullOrEmpty(s))
            {
                return false;
            }

            bool allZeros = true;

            foreach (char c in s)
            {
                if (!char.IsDigit(c))
                {
                    return false;
                }

                if (c != '0')
                {
                    allZeros = false;
                }
            }

            if (allZeros && s.Length > 1)
            {
                return false;
            }

            return true;
        }

        static void FormatArg(object arg, string format, StringBuilder buffer)
        {
            if (format == "@")
            {
                // alloc here
                buffer.Append(arg.ToString());
                return;
            }

            switch (arg)
            {
                case string s:
                {
                    if (format == "l")
                    {
                        buffer.Append(s);
                    }
                    else
                    {
                        if (!string.IsNullOrEmpty(format))
                        {
                            buffer.Append("[invalid format '").Append(format).Append("']");
                        }

                        buffer.Append('\'').Append(s).Append('\'');
                    }
                    break;
                }
                case IFormattable formattable:
                {
                    if (IsIndexNumberFormat(format))
                    {
                        buffer.Append("[unsupported index format '").Append(format).Append("']");
                        format = string.Empty;
                    }

                    try
                    {
                        // alloc here
                        buffer.Append(formattable.ToString(format, CultureInfo.InvariantCulture));
                    }
                    catch (FormatException)
                    {
                        buffer.Append("[format '").Append(format).Append("' failed]");
                    }

                    break;
                }
                default:
                {
                    if (!string.IsNullOrEmpty(format))
                    {
                        buffer.Append("[invalid format '").Append(format).Append("']");
                    }

                    if (arg is Type argType)
                    {
                        buffer.Append(argType.GetPrettyName());
                    }
                    else
                    {
                        // alloc here
                        buffer.Append(arg?.ToString() ?? "null");
                    }

                    break;
                }
            }
        }

        public static string CustomFormat(string messageTemplate, params object[] args)
        {
            return CustomFormatArgs(messageTemplate, args);
        }
    }
}
