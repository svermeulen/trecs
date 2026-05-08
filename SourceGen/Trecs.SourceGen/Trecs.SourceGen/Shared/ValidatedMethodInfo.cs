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
        /// <c>Query().WithTags&lt;...&gt;().SingleEntityIndex()</c> and binds the resulting
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
    /// <c>Query().WithTags&lt;...&gt;().SingleEntityIndex()</c>, materializes the
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
    /// One slot in the user's [ForEach*] method parameter list.
    /// </summary>
    internal struct ParamSlot
    {
        public ParamSlotKind Kind;

        /// <summary>
        /// For LoopComponent, index into the generator's component-parameters list.
        /// For Custom, index into the generator's custom-parameters list.
        /// For LoopSetAccessor, index into set-accessor list.
        /// For LoopSetRead, index into set-read list.
        /// For LoopSetWrite, index into set-write list.
        /// Unused for LoopAspect, LoopEntityIndex, and LoopWorldAccessor.
        /// </summary>
        public int Index;

        public ParamSlot(ParamSlotKind kind, int index)
        {
            Kind = kind;
            Index = index;
        }
    }

    /// <summary>
    /// Validated method info extracted from a <c>[ForEachEntity]</c> method (aspect path).
    /// Used by <c>ForEachEntityAspectGenerator</c>.
    /// </summary>
    internal class ValidatedMethodInfo
    {
        public string AspectTypeName { get; set; } = string.Empty;
        public INamedTypeSymbol? AspectTypeSymbol { get; set; }

        public List<ITypeSymbol> ComponentTypes { get; set; } = new();
        public List<ITypeSymbol> ReadComponentTypes { get; set; } = new();
        public List<ITypeSymbol> WriteComponentTypes { get; set; } = new();

        /// <summary>Component parameters (IEntityComponent). Only populated in components mode.</summary>
        public List<ParameterInfo> ComponentParameters { get; set; } = new();

        public List<ParameterInfo> CustomParameters { get; set; } = new();
        public List<ITypeSymbol> AttributeTagTypes { get; set; } = new();
        public bool HasAttributeTags => AttributeTagTypes.Count > 0;
        public bool HasEntityIndexParameter { get; set; }

        /// <summary>
        /// Parameter slots in declaration order. Used by the code generator to emit
        /// the call to the user's method respecting their chosen parameter order.
        /// </summary>
        public List<ParamSlot> ParameterSlots { get; set; } = new();

        public List<ITypeSymbol> SetTypes { get; set; } = new();
        public bool HasSet => SetTypes.Count > 0;
        public bool MatchByComponents { get; set; }

        /// <summary>
        /// SetAccessor&lt;T&gt; parameters detected in the method signature. Each entry
        /// produces a <c>var {ParamName} = __world.Set&lt;{SetTypeArg}&gt;();</c> before the loop.
        /// </summary>
        public List<SetAccessorParameterInfo> SetAccessorParameters { get; set; } = new();

        /// <summary>
        /// SetRead&lt;T&gt; parameters detected in the method signature. Each entry
        /// produces a <c>var {ParamName} = __world.Set&lt;{SetTypeArg}&gt;().Read;</c> before the loop.
        /// </summary>
        public List<SetAccessorParameterInfo> SetReadParameters { get; set; } = new();

        /// <summary>
        /// SetWrite&lt;T&gt; parameters detected in the method signature. Each entry
        /// produces a <c>var {ParamName} = __world.Set&lt;{SetTypeArg}&gt;().Write;</c> before the loop.
        /// </summary>
        public List<SetAccessorParameterInfo> SetWriteParameters { get; set; } = new();

        /// <summary>
        /// Hoisted <c>[SingleEntity]</c> parameters — resolved once before the iteration
        /// loop (or once per call for run-once methods). Each entry corresponds to a
        /// <see cref="ParamSlotKind.HoistedSingleton"/> slot in <see cref="ParameterSlots"/>.
        /// </summary>
        public List<HoistedSingletonInfo> HoistedSingletons { get; set; } = new();

        /// <summary>
        /// Returns true if the given component type is read-only (in ReadComponentTypes but not WriteComponentTypes).
        /// </summary>
        public bool IsReadOnly(ITypeSymbol type)
        {
            return ReadComponentTypes.Any(t => SymbolEqualityComparer.Default.Equals(t, type))
                && !WriteComponentTypes.Any(t => SymbolEqualityComparer.Default.Equals(t, type));
        }
    }

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
