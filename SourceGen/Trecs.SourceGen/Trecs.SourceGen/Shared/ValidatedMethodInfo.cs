using System.Collections.Generic;
using System.Linq;
using Microsoft.CodeAnalysis;
using Trecs.SourceGen.Performance;

namespace Trecs.SourceGen.Shared
{
    /// <summary>
    /// Classification of a single parameter slot in a system [ForEach*] method.
    /// Tracked in declaration order so the generator can emit the call to the user's
    /// method preserving the original parameter order regardless of how the user
    /// arranged their iteration vs custom args.
    /// </summary>
    internal enum ParamSlotKind
    {
        /// <summary>An iteration component (in/ref IEntityComponent). Used by [ForEachEntity] (components path).</summary>
        LoopComponent,

        /// <summary>The iteration aspect view (in IAspect). Used by [ForEachEntity] (aspect path) — at most one per method.</summary>
        LoopAspect,

        /// <summary>The loop's entity index (Trecs.EntityIndex, no modifiers, no [PassThroughArgument]).</summary>
        LoopEntityIndex,

        /// <summary>The loop's stable entity handle (Trecs.EntityHandle, no modifiers, no [PassThroughArgument]).</summary>
        LoopEntityHandle,

        /// <summary>The loop's world accessor (Trecs.WorldAccessor, no modifiers, no [PassThroughArgument]).</summary>
        LoopWorldAccessor,

        /// <summary>A user-supplied pass-through arg, forwarded by name to the public generated overloads.</summary>
        Custom,

        /// <summary>A SetAccessor&lt;T&gt; parameter (main-thread only). Created via World.Set&lt;T&gt;().</summary>
        LoopSetAccessor,

        /// <summary>A SetRead&lt;T&gt; parameter (main-thread only). Created via World.Set&lt;T&gt;().Read.</summary>
        LoopSetRead,

        /// <summary>A SetWrite&lt;T&gt; parameter (main-thread only). Created via World.Set&lt;T&gt;().Write.</summary>
        LoopSetWrite,

        /// <summary>
        /// A parameter marked <c>[SingleEntity]</c> that is hoisted out of the iteration
        /// loop. The framework resolves the entity once per call via
        /// <c>Query().WithTags&lt;...&gt;().SingleIndex()</c> and binds the resulting
        /// aspect view (or component value) to the parameter. Index points into
        /// <see cref="ClassifiedParameters.HoistedSingletons"/> /
        /// <see cref="ValidatedMethodInfo.HoistedSingletons"/>.
        /// </summary>
        HoistedSingleton,
    }

    /// <summary>
    /// Info for a parameter (or field) marked <c>[SingleEntity]</c>. The source
    /// generator emits a hoist preamble before the iteration loop (or run-once
    /// method body) that resolves the singleton via
    /// <c>Query().WithTags&lt;...&gt;().SingleIndex()</c>, materializes the
    /// aspect / fetches the component, and binds it to <c>__&lt;ParamName&gt;</c>.
    /// </summary>
    internal class HoistedSingletonInfo
    {
        /// <summary>The user's parameter / field name.</summary>
        public string ParamName { get; }

        /// <summary>True for an aspect-typed singleton; false for a component-typed one.</summary>
        public bool IsAspect { get; }

        /// <summary>Inline tag types from <c>[SingleEntity(Tag = ...)]</c> / <c>[SingleEntity(Tags = ...)]</c>.</summary>
        public List<ITypeSymbol> TagTypes { get; }

        // Aspect-singleton fields:

        /// <summary>Display string of the aspect type (e.g. <c>"Svkj.Tusk.PlayerView"</c>). Null for component singletons.</summary>
        public string? AspectTypeDisplay { get; }

        /// <summary>
        /// Parsed aspect data — IRead/IWrite component lists and the canonical
        /// <see cref="Trecs.SourceGen.Aspect.AspectAttributeData.AllComponentTypes"/>
        /// list whose order MUST match the aspect's generated EntityIndex constructor.
        /// Null for component singletons.
        /// </summary>
        public Trecs.SourceGen.Aspect.AspectAttributeData? AspectData { get; }

        /// <summary>Resolved aspect type symbol (used for namespace collection). Null for component singletons.</summary>
        public ITypeSymbol? AspectTypeSymbol { get; }

        // Component-singleton fields:

        /// <summary>Display string of the component type (e.g. <c>"Svkj.Tusk.CHealth"</c>). Null for aspect singletons.</summary>
        public string? ComponentTypeDisplay { get; }

        /// <summary>Resolved component type symbol. Null for aspect singletons.</summary>
        public ITypeSymbol? ComponentTypeSymbol { get; }

        /// <summary>True if the parameter uses <c>ref</c> (writable). False for <c>in</c> (read-only). Only meaningful for component singletons.</summary>
        public bool IsRef { get; }

        public HoistedSingletonInfo(
            string paramName,
            bool isAspect,
            List<ITypeSymbol> tagTypes,
            string? aspectTypeDisplay = null,
            Trecs.SourceGen.Aspect.AspectAttributeData? aspectData = null,
            ITypeSymbol? aspectTypeSymbol = null,
            string? componentTypeDisplay = null,
            ITypeSymbol? componentTypeSymbol = null,
            bool isRef = false
        )
        {
            ParamName = paramName;
            IsAspect = isAspect;
            TagTypes = tagTypes;
            AspectTypeDisplay = aspectTypeDisplay;
            AspectData = aspectData;
            AspectTypeSymbol = aspectTypeSymbol;
            ComponentTypeDisplay = componentTypeDisplay;
            ComponentTypeSymbol = componentTypeSymbol;
            IsRef = isRef;
        }
    }

    /// <summary>
    /// One slot in the user's [ForEach*] method parameter list. A value-equality
    /// record struct so it's safe to carry through Roslyn's incremental-pipeline
    /// cache as an <see cref="EquatableArray{ParamSlot}"/>.
    /// <para><see cref="Index"/> meaning depends on <see cref="Kind"/>:
    /// <list type="bullet">
    ///   <item><c>LoopComponent</c>: component-parameters list.</item>
    ///   <item><c>Custom</c>: custom-parameters list.</item>
    ///   <item><c>LoopSetAccessor</c> / <c>LoopSetRead</c> / <c>LoopSetWrite</c>: the matching set-* list.</item>
    ///   <item><c>HoistedSingleton</c>: hoisted-singletons list.</item>
    ///   <item><c>LoopAspect</c>, <c>LoopEntityIndex</c>, <c>LoopEntityHandle</c>, <c>LoopWorldAccessor</c>: unused.</item>
    /// </list>
    /// </para>
    /// </summary>
    internal readonly record struct ParamSlot(ParamSlotKind Kind, int Index);

    /// <summary>
    /// Info about a SetAccessor&lt;T&gt; parameter injected into an [ForEachEntity] method.
    /// The generator creates the accessor via <c>__world.Set&lt;T&gt;()</c> before the loop.
    /// </summary>
    internal class SetAccessorParameterInfo
    {
        /// <summary>The generic type arg display string, e.g. "FrenzySets.Eating".</summary>
        public string SetTypeArg { get; }

        /// <summary>The resolved type symbol for the generic type argument.</summary>
        public ITypeSymbol SetTypeArgSymbol { get; }

        /// <summary>The user's parameter name, e.g. "eatingSet".</summary>
        public string ParamName { get; }

        /// <summary>Whether the user used the 'in' modifier on this parameter.</summary>
        public bool IsIn { get; }

        public SetAccessorParameterInfo(
            string setTypeArg,
            ITypeSymbol setTypeArgSymbol,
            string paramName,
            bool isIn
        )
        {
            SetTypeArg = setTypeArg;
            SetTypeArgSymbol = setTypeArgSymbol;
            ParamName = paramName;
            IsIn = isIn;
        }
    }

    /// <summary>
    /// Info about a method parameter that is not a component or aspect type.
    /// </summary>
    internal class ParameterInfo
    {
        public string Type { get; }
        public ITypeSymbol TypeSymbol { get; }
        public string Name { get; }
        public bool IsRef { get; }
        public bool IsIn { get; }

        public ParameterInfo(
            string type,
            ITypeSymbol typeSymbol,
            string name,
            bool isRef,
            bool isIn
        )
        {
            Type = type;
            TypeSymbol = typeSymbol;
            Name = name;
            IsRef = isRef;
            IsIn = isIn;
        }

        /// <summary>
        /// Convenience constructor that auto-computes the display string from the type symbol.
        /// </summary>
        public ParameterInfo(ITypeSymbol typeSymbol, string name, bool isRef, bool isIn)
            : this(PerformanceCache.GetDisplayString(typeSymbol), typeSymbol, name, isRef, isIn) { }
    }
}
