using System;
using System.Text;

namespace Trecs.SourceGen.Shared
{
    /// <summary>
    /// Utility class for code formatting and indentation
    /// </summary>
    internal static class CodeFormatter
    {
        public const int SpacesPerIndent = 4;
        public const string IndentString = "    "; // 4 spaces

        private const int IndentCacheSize = 16;
        private static readonly string[] IndentCache = InitIndentCache();

        private static string[] InitIndentCache()
        {
            var cache = new string[IndentCacheSize];
            for (int i = 0; i < IndentCacheSize; i++)
            {
                cache[i] = new string(' ', i * SpacesPerIndent);
            }
            return cache;
        }

        /// <summary>
        /// Creates an indentation string for the specified level
        /// </summary>
        public static string GetIndent(int level)
        {
            if (level <= 0)
                return string.Empty;
            if (level < IndentCacheSize)
                return IndentCache[level];
            return new string(' ', level * SpacesPerIndent);
        }

        /// <summary>
        /// Appends a line with proper indentation
        /// </summary>
        public static void AppendLine(StringBuilder sb, int indentLevel, string line = "")
        {
            if (string.IsNullOrEmpty(line))
            {
                sb.AppendLine();
            }
            else
            {
                sb.Append(GetIndent(indentLevel));
                sb.AppendLine(line);
            }
        }

        /// <summary>
        /// Appends multiple lines with proper indentation
        /// </summary>
        public static void AppendLines(StringBuilder sb, int indentLevel, params string[] lines)
        {
            foreach (var line in lines)
            {
                AppendLine(sb, indentLevel, line);
            }
        }

        /// <summary>
        /// Appends a block of code with opening and closing braces
        /// </summary>
        public static void AppendBlock(
            StringBuilder sb,
            int indentLevel,
            string blockStart,
            Action<StringBuilder, int> blockContent,
            bool addNewLineAfter = true
        )
        {
            AppendLine(sb, indentLevel, blockStart);
            AppendLine(sb, indentLevel, "{");

            blockContent(sb, indentLevel + 1);

            AppendLine(sb, indentLevel, "}");

            if (addNewLineAfter)
            {
                sb.AppendLine();
            }
        }

        /// <summary>
        /// Appends a method signature with proper formatting
        /// </summary>
        public static void AppendMethodSignature(
            StringBuilder sb,
            int indentLevel,
            string accessibility,
            string returnType,
            string methodName,
            string parameters
        )
        {
            AppendLine(sb, indentLevel, $"{accessibility} {returnType} {methodName}({parameters})");
        }

        /// <summary>
        /// Appends a property with proper formatting
        /// </summary>
        public static void AppendProperty(
            StringBuilder sb,
            int indentLevel,
            string accessibility,
            string type,
            string name,
            string getter,
            string? setter = null
        )
        {
            if (setter != null)
            {
                AppendLine(sb, indentLevel, $"{accessibility} {type} {name}");
                AppendLine(sb, indentLevel, "{");
                AppendLine(sb, indentLevel + 1, getter);
                AppendLine(sb, indentLevel + 1, setter);
                AppendLine(sb, indentLevel, "}");
            }
            else
            {
                AppendLine(sb, indentLevel, $"{accessibility} {type} {name} => {getter};");
            }
        }

        /// <summary>
        /// Appends an auto-property with proper formatting
        /// </summary>
        public static void AppendAutoProperty(
            StringBuilder sb,
            int indentLevel,
            string accessibility,
            string type,
            string name,
            bool hasPublicSetter = false
        )
        {
            var setter = hasPublicSetter ? "set;" : "private set;";
            AppendLine(sb, indentLevel, $"{accessibility} {type} {name} {{ get; {setter} }}");
        }

        /// <summary>
        /// Appends using statements at the top of a file
        /// </summary>
        public static void AppendUsings(StringBuilder sb, params string[] namespaces)
        {
            foreach (var ns in namespaces)
            {
                sb.AppendLine($"using {ns};");
            }

            if (namespaces.Length > 0)
            {
                sb.AppendLine();
            }
        }

        /// <summary>
        /// Wraps content in a namespace declaration
        /// </summary>
        public static void WrapInNamespace(
            StringBuilder sb,
            string namespaceName,
            Action<StringBuilder> content
        )
        {
            AppendLine(sb, 0, $"namespace {namespaceName}");
            AppendLine(sb, 0, "{");

            content(sb);

            AppendLine(sb, 0, "}");
        }

        /// <summary>
        /// Sanitizes a string for use in generated code (removes invalid characters)
        /// </summary>
        public static string SanitizeIdentifier(string identifier)
        {
            if (string.IsNullOrEmpty(identifier))
                return "_";

            var sb = new StringBuilder(identifier.Length);

            // First character must be letter or underscore
            char first = identifier[0];
            if (char.IsLetter(first) || first == '_')
            {
                sb.Append(first);
            }
            else
            {
                sb.Append('_');
            }

            // Subsequent characters can be letters, digits, or underscores
            for (int i = 1; i < identifier.Length; i++)
            {
                char c = identifier[i];
                if (char.IsLetterOrDigit(c) || c == '_')
                {
                    sb.Append(c);
                }
                else
                {
                    sb.Append('_');
                }
            }

            return sb.ToString();
        }
    }
}
