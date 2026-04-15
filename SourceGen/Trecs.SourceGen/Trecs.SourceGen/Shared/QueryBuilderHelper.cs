#nullable enable

using System;
using System.Collections.Generic;
using System.Linq;
using System.Text;
using Microsoft.CodeAnalysis;
using Trecs.SourceGen.Performance;

namespace Trecs.SourceGen.Shared
{
    internal static class QueryBuilderHelper
    {
        /// <summary>
        /// Appends chained <c>.WithTags&lt;…&gt;()</c> calls, chunking into groups
        /// of at most <see cref="TrecsCodeGenConstants.MaxTagsPerCall"/> type args
        /// to stay within the generic arity of the QueryBuilder overloads.
        /// </summary>
        public static void AppendWithTagsChain(StringBuilder sb, IEnumerable<ITypeSymbol> tagTypes)
        {
            const int max = TrecsCodeGenConstants.MaxTagsPerCall;
            var list = tagTypes.ToList();

            for (int i = 0; i < list.Count; i += max)
            {
                var count = Math.Min(max, list.Count - i);
                var names = list.Skip(i)
                    .Take(count)
                    .Select(t => PerformanceCache.GetDisplayString(t));
                sb.Append($".WithTags<{string.Join(", ", names)}>()");
            }
        }

        /// <summary>
        /// Builds a chained string of <c>.WithTags&lt;…&gt;().WithComponents&lt;…&gt;()</c>
        /// calls from the supplied tag and component type lists. Returns an empty string
        /// when there are no criteria to emit.
        /// </summary>
        public static string BuildAttributeCriteriaChain(
            IEnumerable<ITypeSymbol> tagTypes,
            bool matchByComponents,
            IEnumerable<ITypeSymbol> componentTypes)
        {
            var chain = new StringBuilder();

            var tagList = tagTypes.ToList();
            if (tagList.Count > 0)
            {
                AppendWithTagsChain(chain, tagList);
            }

            if (matchByComponents)
            {
                foreach (var compType in componentTypes)
                    chain.Append(
                        $".WithComponents<{PerformanceCache.GetDisplayString(compType)}>()"
                    );
            }

            return chain.ToString();
        }
    }
}
