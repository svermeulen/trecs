using System;
using System.Collections.Generic;
using System.Linq;
using System.Text;

namespace Trecs.SourceGen.Shared
{
    /// <summary>
    /// High-performance string builder with pre-allocation and optimized patterns for source generation
    /// </summary>
    internal sealed class OptimizedStringBuilder
    {
        private readonly StringBuilder _sb;
        private const int DefaultCapacity = 8192; // 8KB initial capacity
        private const int LargeCapacity = 32768; // 32KB for large files
        private const int SpacesPerIndent = CodeFormatter.SpacesPerIndent;
        private const int InlineParameterThreshold = 3;

        public OptimizedStringBuilder(int initialCapacity = DefaultCapacity)
        {
            _sb = new StringBuilder(initialCapacity);
        }

        /// <summary>
        /// Creates a builder with capacity estimated for typical Aspect generation
        /// </summary>
        public static OptimizedStringBuilder ForAspect(int componentCount = 5)
        {
            // Estimate: ~200 lines per component + 500 lines base structure
            var estimatedLines = (componentCount * 200) + 500;
            var estimatedCapacity = estimatedLines * 50; // ~50 chars per line average
            return new OptimizedStringBuilder(Math.Max(estimatedCapacity, DefaultCapacity));
        }

        /// <summary>
        /// Creates a builder with capacity estimated for large code generation
        /// </summary>
        public static OptimizedStringBuilder ForLargeGeneration()
        {
            return new OptimizedStringBuilder(LargeCapacity);
        }

        /// <summary>
        /// Appends a line with specified indentation level
        /// </summary>
        public OptimizedStringBuilder AppendLine(int indentLevel, string content)
        {
            if (indentLevel > 0)
            {
                _sb.Append(' ', indentLevel * SpacesPerIndent);
            }
            _sb.AppendLine(content);
            return this;
        }

        /// <summary>
        /// Appends an empty line
        /// </summary>
        public OptimizedStringBuilder AppendLine()
        {
            _sb.AppendLine();
            return this;
        }

        /// <summary>
        /// Appends content without a newline
        /// </summary>
        public OptimizedStringBuilder Append(string content)
        {
            _sb.Append(content);
            return this;
        }

        /// <summary>
        /// Appends multiple using statements efficiently
        /// </summary>
        public OptimizedStringBuilder AppendUsings(params string[] namespaces)
        {
            foreach (var ns in namespaces.OrderBy(n => n))
            {
                _sb.Append("using ").Append(ns).AppendLine(";");
            }
            _sb.AppendLine();
            return this;
        }

        /// <summary>
        /// Appends a method parameter list with optimized formatting
        /// </summary>
        public OptimizedStringBuilder AppendParameterList(
            IEnumerable<string> parameters,
            int indentLevel = 0
        )
        {
            var paramList = parameters.ToList();
            if (paramList.Count == 0)
                return this;

            var baseIndent = new string(' ', indentLevel * SpacesPerIndent);
            var paramIndent = new string(' ', (indentLevel + 1) * SpacesPerIndent);

            for (int i = 0; i < paramList.Count; i++)
            {
                if (i == 0)
                {
                    _sb.Append(paramList[i]);
                }
                else
                {
                    _sb.AppendLine(",");
                    _sb.Append(paramIndent).Append(paramList[i]);
                }
            }

            return this;
        }

        /// <summary>
        /// Appends a constructor call with parameters
        /// </summary>
        public OptimizedStringBuilder AppendConstructorCall(
            string typeName,
            IEnumerable<string> parameters,
            int indentLevel = 0
        )
        {
            var paramList = parameters.ToList();
            var indent = new string(' ', indentLevel * SpacesPerIndent);

            _sb.Append(indent).Append("new ").Append(typeName).Append("(");

            if (paramList.Count <= InlineParameterThreshold)
            {
                // Inline for short parameter lists
                _sb.Append(string.Join(", ", paramList));
            }
            else
            {
                // Multi-line for long parameter lists
                _sb.AppendLine();
                var paramIndent = new string(' ', (indentLevel + 1) * SpacesPerIndent);
                for (int i = 0; i < paramList.Count; i++)
                {
                    _sb.Append(paramIndent).Append(paramList[i]);
                    if (i < paramList.Count - 1)
                    {
                        _sb.AppendLine(",");
                    }
                    else
                    {
                        _sb.AppendLine();
                        _sb.Append(indent);
                    }
                }
            }

            _sb.Append(")");
            return this;
        }

        /// <summary>
        /// Appends a property with getter using optimized formatting
        /// </summary>
        public OptimizedStringBuilder AppendProperty(
            string returnType,
            string propertyName,
            string getterExpression,
            int indentLevel = 0,
            bool isInlined = true
        )
        {
            var indent = new string(' ', indentLevel * SpacesPerIndent);
            var innerIndent = new string(' ', (indentLevel + 1) * SpacesPerIndent);

            _sb.Append(indent)
                .Append("public ")
                .Append(returnType)
                .Append(" ")
                .AppendLine(propertyName);
            _sb.Append(indent).AppendLine("{");

            if (isInlined)
            {
                _sb.Append(innerIndent)
                    .AppendLine("[MethodImpl(MethodImplOptions.AggressiveInlining)]");
            }

            _sb.Append(innerIndent).Append("get => ").Append(getterExpression).AppendLine(";");
            _sb.Append(indent).AppendLine("}");

            return this;
        }

        /// <summary>
        /// Wraps content in a namespace block
        /// </summary>
        public OptimizedStringBuilder WrapInNamespace(
            string namespaceName,
            Action<OptimizedStringBuilder> contentGenerator
        )
        {
            if (string.IsNullOrEmpty(namespaceName))
            {
                contentGenerator(this);
                return this;
            }

            _sb.Append("namespace ").AppendLine(namespaceName);
            _sb.AppendLine("{");

            contentGenerator(this);

            _sb.AppendLine("}");
            return this;
        }

        /// <summary>
        /// Opens a stack of <c>partial &lt;kind&gt; &lt;Name&gt;&lt;TypeParams&gt;</c>
        /// wrappers for every entry in <paramref name="containingTypes"/>
        /// (outer-first), invokes <paramref name="contentGenerator"/> with the
        /// indent level inside the innermost wrapper, then closes the
        /// wrappers in reverse. When the chain is empty, just calls the
        /// content generator at <paramref name="startIndentLevel"/>.
        /// </summary>
        public OptimizedStringBuilder WrapInContainingTypes(
            IReadOnlyList<ContainingTypeInfo> containingTypes,
            int startIndentLevel,
            Action<OptimizedStringBuilder, int> contentGenerator
        )
        {
            int indentLevel = startIndentLevel;
            foreach (var ct in containingTypes)
            {
                AppendLine(
                    indentLevel,
                    $"{ct.Accessibility} partial {ct.Kind} {ct.Name}{ct.TypeParameterList}"
                );
                AppendLine(indentLevel, "{");
                indentLevel++;
            }

            contentGenerator(this, indentLevel);

            for (int i = containingTypes.Count - 1; i >= 0; i--)
            {
                indentLevel--;
                AppendLine(indentLevel, "}");
            }
            return this;
        }

        /// <summary>
        /// Wraps content in a class or struct block
        /// </summary>
        public OptimizedStringBuilder WrapInType(
            string accessibility,
            string typeKind,
            string typeName,
            Action<OptimizedStringBuilder> contentGenerator,
            int indentLevel = 0,
            string? extraModifiers = null
        )
        {
            var indent = new string(' ', indentLevel * SpacesPerIndent);

            _sb.Append(indent).AppendLine(GeneratedCodeAttributes.Line);
            _sb.Append(indent).Append(accessibility).Append(" ");
            if (extraModifiers != null)
            {
                _sb.Append(extraModifiers).Append(" ");
            }
            _sb.Append("partial ").Append(typeKind).Append(" ").AppendLine(typeName);
            _sb.Append(indent).AppendLine("{");

            contentGenerator(this);

            _sb.Append(indent).AppendLine("}");
            return this;
        }

        /// <summary>
        /// Efficiently appends a block of similar statements (e.g., field declarations)
        /// </summary>
        public OptimizedStringBuilder AppendBlock<T>(
            IEnumerable<T> items,
            Func<T, string> formatter,
            int indentLevel = 0
        )
        {
            var indent = new string(' ', indentLevel * SpacesPerIndent);

            foreach (var item in items)
            {
                _sb.Append(indent).AppendLine(formatter(item));
            }

            return this;
        }

        /// <summary>
        /// Gets the current capacity of the underlying StringBuilder
        /// </summary>
        public int Capacity => _sb.Capacity;

        /// <summary>
        /// Gets the current length of the built string
        /// </summary>
        public int Length => _sb.Length;

        /// <summary>
        /// Returns the built string
        /// </summary>
        public override string ToString()
        {
            return _sb.ToString();
        }

        /// <summary>
        /// Clears the builder for reuse (preserves capacity)
        /// </summary>
        public OptimizedStringBuilder Clear()
        {
            _sb.Clear();
            return this;
        }
    }
}
