#nullable enable

namespace Trecs.SourceGen
{
    /// <summary>
    /// Centralized constants for attribute and interface names used across source generators.
    /// </summary>
    internal static class TrecsAttributeNames
    {
        // Iteration markers. Aspect-vs-components routing is decided by inspecting the
        // method's parameter types (does it take an IAspect or a ref/in IEntityComponent),
        // not by the attribute name itself. ForEachEntity marks an iteration method;
        // SingleEntity is per-parameter / per-field and resolves a singleton entity
        // for that one parameter (with an assertion that exactly one entity matches).
        public const string ForEachEntity = "ForEachEntityAttribute";
        public const string SingleEntity = "SingleEntityAttribute";

        public const string Unwrap = "UnwrapAttribute";
        public const string GenerateInterpolatorSystem = "GenerateInterpolatorSystemAttribute";
        public const string FromWorld = "FromWorldAttribute";
        public const string GlobalIndex = "GlobalIndexAttribute";
        public const string PassThroughArgument = "PassThroughArgumentAttribute";
        public const string WrapAsJob = "WrapAsJobAttribute";
        public const string SourceGenSettings = "TrecsSourceGenSettingsAttribute";
    }

    internal static class TrecsNamespaces
    {
        public const string Trecs = "Trecs";
        public const string TrecsInternal = "Trecs.Internal";
    }

    /// <summary>
    /// Centralized <c>[GeneratedCode]</c> attribute text emitted into every
    /// generator-produced type declaration. Updating <c>Version</c> here
    /// updates every generated file.
    /// </summary>
    internal static class GeneratedCodeAttributes
    {
        public const string Tool = "Trecs.SourceGen";
        public const string Version = "0.2.0";
        public const string Line =
            "[global::System.CodeDom.Compiler.GeneratedCode(\""
            + Tool
            + "\", \""
            + Version
            + "\")]";
    }

    internal static class TrecsCodeGenConstants
    {
        /// <summary>
        /// Maximum number of component types per GetBuffers call.
        /// Matches the maximum generic arity of the GetBuffers overloads.
        /// </summary>
        public const int MaxComponentsPerBatch = 6;

        /// <summary>
        /// Maximum number of tag types per <c>WithTags&lt;…&gt;()</c> call.
        /// Matches the maximum generic arity of the QueryBuilder.WithTags overloads.
        /// </summary>
        public const int MaxTagsPerCall = 4;
    }
}
