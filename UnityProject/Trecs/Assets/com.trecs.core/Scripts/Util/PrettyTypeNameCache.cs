using System;
using System.Collections.Generic;
using System.Linq;

namespace Trecs.Internal
{
    /// <summary>
    /// Provides functionality to generate human-readable type names with proper generic
    /// and nested type formatting.
    /// </summary>
    public static class PrettyTypeNameCache
    {
        // Cache to avoid redundant formatting operations
        private static readonly Dictionary<Type, string> _formattedTypeNames = new();

        /// <summary>
        /// Formats a type name with proper handling of generics and nested types.
        /// </summary>
        /// <param name="type">The type to format</param>
        /// <returns>A human-readable formatted type name</returns>
        private static string FormatTypeName(Type type)
        {
            // Return type name for non-generic types or generic parameters
            if (type.IsGenericParameter)
            {
                return type.Name;
            }

            // Handle nested types by including the parent type hierarchy
            if (type.IsNested)
            {
                string parentTypeName = FormatTypeName(type.DeclaringType);
                string nestedTypeName = GetFormattedName(type);
                return $"{parentTypeName}.{nestedTypeName}";
            }

            // For regular types
            return GetFormattedName(type);
        }

        /// <summary>
        /// Gets the formatted name for a type, handling generic type arguments if present.
        /// </summary>
        private static string GetFormattedName(Type type)
        {
            if (!type.IsGenericType)
            {
                return type.Name;
            }

            // Extract the base name without generic parameter count marker (`1, `2, etc.)
            string baseName = type.Name.Split('`')[0];

            // Format all generic arguments recursively and join them with commas
            string genericArgs = string.Join(
                ", ",
                type.GetGenericArguments().Select(FormatTypeName)
            );

            return $"{baseName}<{genericArgs}>";
        }

        /// <summary>
        /// Gets a human-readable name for the specified type, with proper handling
        /// of generic type parameters and nested types.
        /// </summary>
        /// <param name="type">The type to get a pretty name for</param>
        /// <returns>A formatted, human-readable type name</returns>
        public static string GetPrettyName(this Type type)
        {
            TrecsAssert.IsNotNull(type);
            // The backing Dictionary<Type, string> has no synchronization. Callers
            // should only reach this from main-thread code: Burst-compiled jobs
            // never reach here because the [BurstDiscard] throw helpers short-
            // circuit before CustomFormatter's Type-formatting path runs. A
            // non-Burst job reaching here is already a misuse worth surfacing
            // loudly instead of silently racing on the cache.
            TrecsAssert.That(
                UnityThreadHelper.IsMainThread,
                "PrettyTypeNameCache is main-thread only"
            );

            // Return from cache if available
            if (_formattedTypeNames.TryGetValue(type, out string prettyName))
            {
                return prettyName;
            }

            // Generate and cache the formatted name
            prettyName = FormatTypeName(type);
            _formattedTypeNames[type] = prettyName;

            return prettyName;
        }
    }
}
