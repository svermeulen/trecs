#nullable enable

using System;

namespace Trecs
{
    /// <summary>
    /// Assembly-level attribute to configure Trecs source generator behavior.
    /// </summary>
    /// <example>
    /// <code>
    /// [assembly: TrecsSourceGenSettings(ComponentPrefix = "C")]
    /// </code>
    /// </example>
    [AttributeUsage(AttributeTargets.Assembly, AllowMultiple = false)]
    public sealed class TrecsSourceGenSettingsAttribute : Attribute
    {
        /// <summary>
        /// When set, the source generator strips this prefix from component type names
        /// when generating property names and variable names.
        /// For example, with ComponentPrefix = "C", a component named "CPosition"
        /// will generate a property named "Position" instead of "CPosition".
        /// Default is null (no prefix stripping).
        /// </summary>
        public string? ComponentPrefix { get; set; }
    }
}
