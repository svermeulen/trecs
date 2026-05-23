using System;
using Trecs.SourceGen.Shared;

namespace Trecs.SourceGen.Aspect
{
    /// <summary>
    /// Equatable, fully-precomputed model of a single aspect-target — a struct or interface
    /// implementing <c>Trecs.IAspect</c>. Produced by <see cref="AspectModelBuilder"/> in the
    /// transform stage of the incremental pipeline and consumed by <see cref="AspectCodeGenerator"/>
    /// in the terminal stage. Carries zero references to Roslyn symbols, syntax nodes, or
    /// <see cref="Microsoft.CodeAnalysis.Diagnostic"/> — those are forbidden at the pipeline
    /// boundary because they break value equality and prevent the incremental cache from working.
    /// </summary>
    internal sealed record AspectModel(
        string TypeName,
        string Namespace,
        string Accessibility,
        string HintFileName,
        EquatableArray<ContainingTypeInfo> ContainingTypes,
        bool IsInterface,
        bool IsValid,
        AspectComponents Components,
        EquatableArray<DiagnosticInfo> Diagnostics
    );

    /// <summary>
    /// The set of components an aspect reads and writes. Both flavors of aspect (struct and
    /// interface) carry the same shape: per-component pre-resolved strings (display name,
    /// property name, return type, unwrap chain) so the codegen stage never has to touch
    /// the original symbols.
    /// </summary>
    internal sealed record AspectComponents(
        EquatableArray<ComponentModel> Read,
        EquatableArray<ComponentModel> Write,
        EquatableArray<ComponentModel> All
    )
    {
        public static readonly AspectComponents Empty = new(
            EquatableArray<ComponentModel>.Empty,
            EquatableArray<ComponentModel>.Empty,
            EquatableArray<ComponentModel>.Empty
        );
    }

    /// <summary>
    /// Equatable per-component bundle of every string the aspect codegen needs to emit code
    /// for that component. All of these are derived from the component's <see cref="Microsoft.CodeAnalysis.ITypeSymbol"/>
    /// at transform time; once built, the symbol is dropped.
    /// </summary>
    /// <param name="DisplayString">Fully-qualified type name, e.g. <c>"MyNs.CMyComp"</c>. Used inside generic instantiations like <c>NativeComponentBufferRead&lt;T&gt;</c>, <c>WithComponents&lt;T&gt;</c>, <c>ComponentBuffer&lt;T&gt;</c>.</param>
    /// <param name="PropertyName">Prefix-stripped pascal-case property name, e.g. <c>"MyComp"</c> from <c>"CMyComp"</c>. Used to name properties and as a stem for field/variable names.</param>
    /// <param name="CamelPropertyName">Camel-case form of <see cref="PropertyName"/>, e.g. <c>"myComp"</c>. Used for internal field names (<c>__myCompBuffer</c>) and constructor parameter names (<c>myCompBuffer</c>).</param>
    /// <param name="IsReadOnly">True iff the component is in the aspect's read set but not its write set. Determines Read vs Write buffer/lookup types and access expressions.</param>
    /// <param name="ReadReturnType">Return type for the read property, including <c>ref readonly</c> and any unwrap-target substitution, e.g. <c>"ref readonly MyNs.CMyComp"</c>.</param>
    /// <param name="WriteReturnType">Return type for the write property, e.g. <c>"ref MyNs.CMyComp"</c> (with unwrap-target substitution applied).</param>
    /// <param name="UnwrapAccessSuffix">Field-access chain to append after a buffer index expression to reach the unwrapped underlying value, e.g. <c>".Inner.Value"</c>. Empty when the component is not <c>[Unwrap]</c>.</param>
    internal sealed record ComponentModel(
        string DisplayString,
        string PropertyName,
        string CamelPropertyName,
        bool IsReadOnly,
        string ReadReturnType,
        string WriteReturnType,
        string UnwrapAccessSuffix
    )
    {
        public string BufferTypeName =>
            IsReadOnly
                ? $"NativeComponentBufferRead<{DisplayString}>"
                : $"NativeComponentBufferWrite<{DisplayString}>";

        public string LookupTypeName =>
            IsReadOnly
                ? $"NativeComponentLookupRead<{DisplayString}>"
                : $"NativeComponentLookupWrite<{DisplayString}>";

        public string ComponentBufferSuffix => IsReadOnly ? "Read" : "Write";

        public string BufferFieldName => $"__{CamelPropertyName}Buffer";
        public string BufferParamName => $"{CamelPropertyName}Buffer";
    }
}
