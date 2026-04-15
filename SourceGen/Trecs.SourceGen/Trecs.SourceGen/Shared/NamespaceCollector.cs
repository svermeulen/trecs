#nullable enable

using System;
using System.Collections.Generic;
using Microsoft.CodeAnalysis;
using Trecs.SourceGen.Performance;

namespace Trecs.SourceGen.Shared
{
    /// <summary>
    /// Collects required namespaces from <see cref="ValidatedMethodInfo"/> for emitting
    /// <c>using</c> directives in generated partial classes.
    /// </summary>
    internal static class NamespaceCollector
    {
        /// <summary>
        /// Collects required namespaces from validated method info, returning a set
        /// suitable for generating using directives.
        /// </summary>
        /// <param name="compilation">The compilation (used to detect the global namespace).</param>
        /// <param name="info">Validated method info containing all type references.</param>
        /// <param name="includeSystemNamespace">
        /// When <c>true</c>, adds <c>"System"</c> to the base set. The ForSingle* generators
        /// need this; the ForEach* generators intentionally exclude it to avoid <c>Object</c>
        /// ambiguity when the user file also imports <c>UnityEngine</c>.
        /// </param>
        internal static HashSet<string> Collect(
            Compilation compilation,
            ValidatedMethodInfo info,
            bool includeSystemNamespace = false
        )
        {
            var namespaces = new HashSet<string>
            {
                "Unity.Jobs",
                "Trecs",
                "Trecs.Internal",
                "Trecs.Collections",
            };

            if (includeSystemNamespace)
            {
                namespaces.Add("System");
            }

            // Closure over compilation + namespaces for the helper lambda.
            var globalNs = PerformanceCache.GetDisplayString(compilation.GlobalNamespace) ?? "";

            void AddNamespaceIfNeeded(ITypeSymbol typeSymbol)
            {
                var ns = PerformanceCache.GetDisplayString(typeSymbol.ContainingNamespace);
                if (
                    !string.IsNullOrEmpty(ns)
                    && ns != "System"
                    && ns != null
                    && !ns.StartsWith("System.")
                    && ns != globalNs
                )
                {
                    namespaces.Add(ns);
                }
            }

            // Aspect type (aspect-based generators)
            if (info.AspectTypeSymbol != null)
            {
                AddNamespaceIfNeeded(info.AspectTypeSymbol);
            }

            // Component types from aspect interfaces (aspect-based generators)
            foreach (var componentType in info.ComponentTypes)
            {
                AddNamespaceIfNeeded(componentType);
            }

            // Component parameters (component-based generators)
            foreach (var param in info.ComponentParameters)
            {
                if (param.TypeSymbol != null)
                {
                    AddNamespaceIfNeeded(param.TypeSymbol);
                }
            }

            // Custom parameters
            foreach (var param in info.CustomParameters)
            {
                if (param.TypeSymbol != null)
                {
                    AddNamespaceIfNeeded(param.TypeSymbol);
                }
            }

            // Attribute tag types
            foreach (var tagType in info.AttributeTagTypes)
            {
                AddNamespaceIfNeeded(tagType);
                if (tagType.ContainingType != null)
                {
                    AddNamespaceIfNeeded(tagType.ContainingType);
                }
            }

            // Set types (from attribute Set/Sets)
            foreach (var setType in info.SetTypes)
            {
                AddNamespaceIfNeeded(setType);
                if (setType.ContainingType != null)
                {
                    AddNamespaceIfNeeded(setType.ContainingType);
                }
            }

            // SetAccessor<T> parameter type arguments
            foreach (var sa in info.SetAccessorParameters)
            {
                AddNamespaceIfNeeded(sa.SetTypeArgSymbol);
                if (sa.SetTypeArgSymbol.ContainingType != null)
                {
                    AddNamespaceIfNeeded(sa.SetTypeArgSymbol.ContainingType);
                }
            }

            // SetRead<T> parameter type arguments
            foreach (var sr in info.SetReadParameters)
            {
                AddNamespaceIfNeeded(sr.SetTypeArgSymbol);
                if (sr.SetTypeArgSymbol.ContainingType != null)
                {
                    AddNamespaceIfNeeded(sr.SetTypeArgSymbol.ContainingType);
                }
            }

            // SetWrite<T> parameter type arguments
            foreach (var sw in info.SetWriteParameters)
            {
                AddNamespaceIfNeeded(sw.SetTypeArgSymbol);
                if (sw.SetTypeArgSymbol.ContainingType != null)
                {
                    AddNamespaceIfNeeded(sw.SetTypeArgSymbol.ContainingType);
                }
            }

            return namespaces;
        }

        /// <summary>
        /// Emits <c>var {paramName} = {worldVar}.Set&lt;{setTypeArg}&gt;();</c> for each
        /// <see cref="SetAccessorParameterInfo"/> in the method info.
        /// These are group-agnostic, so they go before the outer loop.
        /// </summary>
        internal static void EmitSetAccessorDeclarations(
            OptimizedStringBuilder sb,
            ValidatedMethodInfo info,
            int indentLevel,
            string worldVar
        )
        {
            foreach (var sa in info.SetAccessorParameters)
            {
                sb.AppendLine(
                    indentLevel,
                    $"var {sa.ParamName} = {worldVar}.Set<{sa.SetTypeArg}>();"
                );
            }
            foreach (var sr in info.SetReadParameters)
            {
                sb.AppendLine(
                    indentLevel,
                    $"var {sr.ParamName} = {worldVar}.Set<{sr.SetTypeArg}>().Read;"
                );
            }
            foreach (var sw in info.SetWriteParameters)
            {
                sb.AppendLine(
                    indentLevel,
                    $"var {sw.ParamName} = {worldVar}.Set<{sw.SetTypeArg}>().Write;"
                );
            }
        }
    }
}
