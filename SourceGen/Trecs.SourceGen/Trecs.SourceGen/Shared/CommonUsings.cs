using System;
using System.Linq;
using System.Text;

namespace Trecs.SourceGen.Shared
{
    /// <summary>
    /// Single source of truth for the `using` directives emitted into every
    /// Trecs-generated file. Edit <see cref="Namespaces"/> to change what's
    /// globally in scope for generated code.
    /// </summary>
    internal static class CommonUsings
    {
        public static readonly string[] Namespaces =
        {
            "Trecs",
            "Trecs.Internal",
            "Trecs.Collections",
        };

        /// <summary>
        /// Using directives joined with newlines (no trailing newline), suitable
        /// for embedding inside a verbatim-string template.
        /// </summary>
        public static readonly string AsDirectives = string.Join(
            "\n",
            Namespaces.Select(ns => $"using {ns};")
        );

        /// <summary>
        /// Appends `using ns;` for each common namespace (no trailing blank line).
        /// </summary>
        public static void AppendTo(StringBuilder sb)
        {
            foreach (var ns in Namespaces)
            {
                sb.Append("using ").Append(ns).AppendLine(";");
            }
        }

        /// <summary>
        /// Returns a new array containing <paramref name="extras"/> followed by
        /// <see cref="Namespaces"/>. Use when calling <c>AppendUsings</c> with
        /// additional namespaces alongside the common set.
        /// </summary>
        public static string[] WithExtras(params string[] extras)
        {
            var result = new string[extras.Length + Namespaces.Length];
            Array.Copy(extras, 0, result, 0, extras.Length);
            Array.Copy(Namespaces, 0, result, extras.Length, Namespaces.Length);
            return result;
        }
    }
}
