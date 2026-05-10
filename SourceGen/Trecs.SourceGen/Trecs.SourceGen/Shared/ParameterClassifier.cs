#nullable enable

using System.Collections.Generic;
using System.Collections.Immutable;
using System.Linq;
using Microsoft.CodeAnalysis;
using Microsoft.CodeAnalysis.CSharp;
using Microsoft.CodeAnalysis.CSharp.Syntax;
using Trecs.SourceGen.Aspect;
using Trecs.SourceGen.Performance;

namespace Trecs.SourceGen.Shared
{
    /// <summary>
    /// Whether the iteration method uses an Aspect parameter or direct component parameters.
    /// </summary>
    internal enum IterationMode
    {
        Aspect,
        Components,
    }

    /// <summary>
    /// Result of classifying a method's parameters via <see cref="ParameterClassifier.Classify"/>.
    /// </summary>
    internal class ClassifiedParameters
    {
        /// <summary>Parameter slots in user declaration order.</summary>
        public List<ParamSlot> ParameterSlots { get; } = new();

        /// <summary>SetAccessor&lt;T&gt; parameters detected in the method signature.</summary>
        public List<SetAccessorParameterInfo> SetAccessorParameters { get; } = new();

        /// <summary>SetRead&lt;T&gt; parameters detected in the method signature.</summary>
        public List<SetAccessorParameterInfo> SetReadParameters { get; } = new();

        /// <summary>SetWrite&lt;T&gt; parameters detected in the method signature.</summary>
        public List<SetAccessorParameterInfo> SetWriteParameters { get; } = new();

        /// <summary>Whether an EntityIndex parameter was found.</summary>
        public bool HasEntityIndex { get; set; }

        /// <summary>Whether an EntityHandle parameter was found.</summary>
        public bool HasEntityHandle { get; set; }

        /// <summary>Whether an EntityAccessor parameter was found.</summary>
        public bool HasEntityAccessor { get; set; }

        /// <summary>Whether a WorldAccessor parameter was found.</summary>
        public bool HasWorldAccessor { get; set; }

        // --- Component mode only ---

        /// <summary>Component parameters (IEntityComponent). Only populated in Components mode.</summary>
        public List<ParameterInfo> ComponentParameters { get; } = new();

        // --- Aspect mode only ---

        /// <summary>The syntax node of the pre-detected aspect parameter. Only set in Aspect mode.</summary>
        public ParameterSyntax? AspectParam { get; set; }

        /// <summary>The type symbol of the pre-detected aspect parameter. Only set in Aspect mode.</summary>
        public INamedTypeSymbol? AspectParamType { get; set; }

        // --- Both modes ---

        /// <summary>Custom/PassThrough parameters.</summary>
        public List<ParameterInfo> CustomParameters { get; } = new();

        /// <summary>
        /// <c>[SingleEntity]</c> parameters that are hoisted out of the iteration loop.
        /// Each entry is referenced by a <see cref="ParamSlotKind.HoistedSingleton"/>
        /// slot in <see cref="ParameterSlots"/>.
        /// </summary>
        public List<HoistedSingletonInfo> HoistedSingletons { get; } = new();
    }

    /// <summary>
    /// Shared parameter classification logic for the four iteration source generators.
    /// Walks the method's parameters and classifies each as SetAccessor, NativeSet (rejected),
    /// EntityIndex, WorldAccessor, Component, Aspect, or Custom/PassThrough.
    /// </summary>
    internal static class ParameterClassifier
    {
        /// <summary>
        /// Classifies all parameters of the given method symbol.
        /// </summary>
        /// <param name="parameters">The syntax parameter list.</param>
        /// <param name="semanticModel">Semantic model for the method's syntax tree.</param>
        /// <param name="mode">Aspect or Components iteration.</param>
        /// <param name="context">Source production context for reporting diagnostics.</param>
        /// <param name="methodName">Method name for diagnostic messages (aspect mode only, used in MixedAspectAndComponentParams).</param>
        /// <param name="aspectParam">
        /// For Aspect mode: the pre-detected aspect ParameterSyntax (already validated).
        /// The classifier will record it as LoopAspect and skip classification for that parameter.
        /// </param>
        /// <param name="isValid">
        /// Tracks cumulative validity. The caller passes its existing isValid flag by ref;
        /// the classifier sets it to false on errors but never sets it back to true.
        /// </param>
        /// <returns>
        /// A <see cref="ClassifiedParameters"/> with all classified slots, or null only if
        /// a fatal error occurs that prevents further classification (currently always returns non-null).
        /// </returns>
        internal static ClassifiedParameters Classify(
            SeparatedSyntaxList<ParameterSyntax> parameters,
            SemanticModel semanticModel,
            IterationMode mode,
            System.Action<Diagnostic> reportDiagnostic,
            string? methodName,
            ParameterSyntax? aspectParam,
            ref bool isValid
        )
        {
            var result = new ClassifiedParameters();

            foreach (var param in parameters)
            {
                var paramType =
                    param.Type != null ? semanticModel.GetTypeInfo(param.Type).Type : null;
                if (paramType == null)
                {
                    isValid = false;
                    continue;
                }

                bool isRef = param.Modifiers.Any(m => m.IsKind(SyntaxKind.RefKeyword));
                bool isIn = param.Modifiers.Any(m => m.IsKind(SyntaxKind.InKeyword));

                if (isRef && isIn)
                {
                    reportDiagnostic(
                        Diagnostic.Create(
                            DiagnosticDescriptors.InvalidParameterModifiers,
                            param.GetLocation(),
                            param.Identifier.Text
                        )
                    );
                    isValid = false;
                    continue;
                }

                // In Aspect mode, the aspect parameter was pre-detected and validated
                // by the caller. Just record it as LoopAspect.
                if (mode == IterationMode.Aspect && object.ReferenceEquals(param, aspectParam))
                {
                    result.ParameterSlots.Add(new ParamSlot(ParamSlotKind.LoopAspect, 0));
                    continue;
                }

                var paramSymbol = semanticModel.GetDeclaredSymbol(param);
                bool isPassThrough =
                    paramSymbol != null
                    && PerformanceCache.HasAttributeByName(
                        paramSymbol,
                        TrecsAttributeNames.PassThroughArgument,
                        TrecsNamespaces.Trecs
                    );
                bool hasSingleEntity =
                    paramSymbol != null
                    && PerformanceCache.HasAttributeByName(
                        paramSymbol,
                        TrecsAttributeNames.SingleEntity,
                        TrecsNamespaces.Trecs
                    );

                // [SingleEntity] params are hoisted out of the loop. Validate and
                // record before any iteration-target classification, so a [SingleEntity]
                // aspect/component param is never mis-classified as a loop aspect/component.
                if (hasSingleEntity)
                {
                    bool hasFromWorld =
                        paramSymbol != null
                        && PerformanceCache.HasAttributeByName(
                            paramSymbol,
                            TrecsAttributeNames.FromWorld,
                            TrecsNamespaces.Trecs
                        );
                    if (hasFromWorld || isPassThrough)
                    {
                        reportDiagnostic(
                            Diagnostic.Create(
                                DiagnosticDescriptors.SingleEntityConflictingAttributes,
                                param.GetLocation(),
                                param.Identifier.Text,
                                hasFromWorld ? "FromWorld" : "PassThroughArgument"
                            )
                        );
                        isValid = false;
                        continue;
                    }

                    var hoisted = ClassifyHoistedSingleton(
                        param,
                        paramType,
                        paramSymbol!,
                        isRef,
                        isIn,
                        reportDiagnostic
                    );
                    if (hoisted == null)
                    {
                        isValid = false;
                        continue;
                    }
                    var hoistedIndex = result.HoistedSingletons.Count;
                    result.HoistedSingletons.Add(hoisted);
                    result.ParameterSlots.Add(
                        new ParamSlot(ParamSlotKind.HoistedSingleton, hoistedIndex)
                    );
                    continue;
                }

                // NativeSetRead<T> / NativeSetCommandBuffer<T> detection — these are job-only,
                // forbidden in main-thread iteration methods.
                if (
                    !isPassThrough
                    && paramType is INamedTypeSymbol namedNativeSet
                    && (
                        namedNativeSet.Name == "NativeSetRead"
                        || namedNativeSet.Name == "NativeSetCommandBuffer"
                    )
                    && namedNativeSet.TypeArguments.Length == 1
                    && PerformanceCache.GetDisplayString(namedNativeSet.ContainingNamespace)
                        == "Trecs"
                )
                {
                    var nativeSetTypeArg = PerformanceCache.GetDisplayString(
                        namedNativeSet.TypeArguments[0]
                    );
                    reportDiagnostic(
                        Diagnostic.Create(
                            DiagnosticDescriptors.NativeSetNotAllowedOnMainThread,
                            param.GetLocation(),
                            param.Identifier.Text,
                            $"{namedNativeSet.Name}<{nativeSetTypeArg}>",
                            nativeSetTypeArg
                        )
                    );
                    isValid = false;
                    continue;
                }

                // SetAccessor<T> detection (main-thread only, allow 'in' or no modifier, reject 'ref')
                if (
                    !isPassThrough
                    && paramType is INamedTypeSymbol namedSetAccessor
                    && namedSetAccessor.Name == "SetAccessor"
                    && namedSetAccessor.TypeArguments.Length == 1
                    && PerformanceCache.GetDisplayString(namedSetAccessor.ContainingNamespace)
                        == "Trecs"
                )
                {
                    if (isRef)
                    {
                        reportDiagnostic(
                            Diagnostic.Create(
                                DiagnosticDescriptors.ParameterMustBeByValue,
                                param.GetLocation(),
                                param.Identifier.Text
                            )
                        );
                        isValid = false;
                        continue;
                    }
                    var setTypeArgSymbol = namedSetAccessor.TypeArguments[0];
                    var setTypeArg = PerformanceCache.GetDisplayString(setTypeArgSymbol);
                    var setIndex = result.SetAccessorParameters.Count;
                    result.SetAccessorParameters.Add(
                        new SetAccessorParameterInfo(
                            setTypeArg,
                            setTypeArgSymbol,
                            param.Identifier.ToString(),
                            isIn
                        )
                    );
                    result.ParameterSlots.Add(
                        new ParamSlot(ParamSlotKind.LoopSetAccessor, setIndex)
                    );
                    continue;
                }

                // SetRead<T> detection (main-thread only, requires 'in' modifier)
                if (
                    !isPassThrough
                    && paramType is INamedTypeSymbol namedSetRead
                    && namedSetRead.Name == "SetRead"
                    && namedSetRead.TypeArguments.Length == 1
                    && PerformanceCache.GetDisplayString(namedSetRead.ContainingNamespace)
                        == "Trecs"
                )
                {
                    if (!isIn || isRef)
                    {
                        reportDiagnostic(
                            Diagnostic.Create(
                                DiagnosticDescriptors.ParameterMustBeIn,
                                param.GetLocation(),
                                param.Identifier.Text,
                                PerformanceCache.GetDisplayString(paramType)
                            )
                        );
                        isValid = false;
                        continue;
                    }
                    var srTypeArgSymbol = namedSetRead.TypeArguments[0];
                    var srTypeArg = PerformanceCache.GetDisplayString(srTypeArgSymbol);
                    var srIndex = result.SetReadParameters.Count;
                    result.SetReadParameters.Add(
                        new SetAccessorParameterInfo(
                            srTypeArg,
                            srTypeArgSymbol,
                            param.Identifier.ToString(),
                            isIn: true
                        )
                    );
                    result.ParameterSlots.Add(new ParamSlot(ParamSlotKind.LoopSetRead, srIndex));
                    continue;
                }

                // SetWrite<T> detection (main-thread only, requires 'in' modifier)
                if (
                    !isPassThrough
                    && paramType is INamedTypeSymbol namedSetWrite
                    && namedSetWrite.Name == "SetWrite"
                    && namedSetWrite.TypeArguments.Length == 1
                    && PerformanceCache.GetDisplayString(namedSetWrite.ContainingNamespace)
                        == "Trecs"
                )
                {
                    if (!isIn || isRef)
                    {
                        reportDiagnostic(
                            Diagnostic.Create(
                                DiagnosticDescriptors.ParameterMustBeIn,
                                param.GetLocation(),
                                param.Identifier.Text,
                                PerformanceCache.GetDisplayString(paramType)
                            )
                        );
                        isValid = false;
                        continue;
                    }
                    var swTypeArgSymbol = namedSetWrite.TypeArguments[0];
                    var swTypeArg = PerformanceCache.GetDisplayString(swTypeArgSymbol);
                    var swIndex = result.SetWriteParameters.Count;
                    result.SetWriteParameters.Add(
                        new SetAccessorParameterInfo(
                            swTypeArg,
                            swTypeArgSymbol,
                            param.Identifier.ToString(),
                            isIn: true
                        )
                    );
                    result.ParameterSlots.Add(new ParamSlot(ParamSlotKind.LoopSetWrite, swIndex));
                    continue;
                }

                bool isEntityIndex = SymbolAnalyzer.IsExactType(
                    paramType,
                    "EntityIndex",
                    TrecsNamespaces.TrecsInternal
                );
                bool isEntityHandle = SymbolAnalyzer.IsExactType(
                    paramType,
                    "EntityHandle",
                    TrecsNamespaces.Trecs
                );
                bool isEntityAccessor = SymbolAnalyzer.IsExactType(
                    paramType,
                    "EntityAccessor",
                    TrecsNamespaces.Trecs
                );
                bool isWorldAccessor = SymbolAnalyzer.IsExactType(
                    paramType,
                    "WorldAccessor",
                    TrecsNamespaces.Trecs
                );

                if (!isPassThrough && isEntityIndex)
                {
                    if (
                        TryClassifyByValueLoopParam(
                            param,
                            isRef,
                            isIn,
                            alreadyPresent: result.HasEntityIndex,
                            typeName: "EntityIndex",
                            slotKind: ParamSlotKind.LoopEntityIndex,
                            result,
                            reportDiagnostic,
                            ref isValid
                        )
                    )
                        result.HasEntityIndex = true;
                    continue;
                }

                if (!isPassThrough && isEntityHandle)
                {
                    if (
                        TryClassifyByValueLoopParam(
                            param,
                            isRef,
                            isIn,
                            alreadyPresent: result.HasEntityHandle,
                            typeName: "EntityHandle",
                            slotKind: ParamSlotKind.LoopEntityHandle,
                            result,
                            reportDiagnostic,
                            ref isValid
                        )
                    )
                        result.HasEntityHandle = true;
                    continue;
                }

                if (!isPassThrough && isEntityAccessor)
                {
                    if (
                        TryClassifyByValueLoopParam(
                            param,
                            isRef,
                            isIn,
                            alreadyPresent: result.HasEntityAccessor,
                            typeName: "EntityAccessor",
                            slotKind: ParamSlotKind.LoopEntityAccessor,
                            result,
                            reportDiagnostic,
                            ref isValid
                        )
                    )
                        result.HasEntityAccessor = true;
                    continue;
                }

                if (!isPassThrough && isWorldAccessor)
                {
                    if (
                        TryClassifyByValueLoopParam(
                            param,
                            isRef,
                            isIn,
                            alreadyPresent: result.HasWorldAccessor,
                            typeName: "WorldAccessor",
                            slotKind: ParamSlotKind.LoopWorldAccessor,
                            result,
                            reportDiagnostic,
                            ref isValid
                        )
                    )
                        result.HasWorldAccessor = true;
                    continue;
                }

                // IEntityComponent detection
                bool isComponent = paramType.AllInterfaces.Any(i => i.Name == "IEntityComponent");

                if (mode == IterationMode.Components && !isPassThrough && isComponent)
                {
                    // Component mode: accept IEntityComponent params with in/ref modifier.
                    if (!isRef && !isIn)
                    {
                        reportDiagnostic(
                            Diagnostic.Create(
                                DiagnosticDescriptors.ComponentParameterMustBeInOrRef,
                                param.GetLocation(),
                                param.Identifier.Text
                            )
                        );
                        isValid = false;
                        continue;
                    }

                    var componentIndex = result.ComponentParameters.Count;
                    result.ComponentParameters.Add(
                        new ParameterInfo(paramType, param.Identifier.ToString(), isRef, isIn)
                    );
                    result.ParameterSlots.Add(
                        new ParamSlot(ParamSlotKind.LoopComponent, componentIndex)
                    );
                    continue;
                }

                if (mode == IterationMode.Aspect && !isPassThrough && isComponent)
                {
                    // Aspect mode: IEntityComponent params are not valid — the aspect
                    // already declares the components. Report the specific error.
                    reportDiagnostic(
                        Diagnostic.Create(
                            DiagnosticDescriptors.MixedAspectAndComponentParams,
                            param.GetLocation(),
                            methodName ?? "",
                            param.Identifier.Text,
                            PerformanceCache.GetDisplayString(paramType)
                        )
                    );
                    isValid = false;
                    continue;
                }

                // Unrecognized type without [PassThroughArgument] — report error.
                if (!isPassThrough)
                {
                    reportDiagnostic(
                        Diagnostic.Create(
                            DiagnosticDescriptors.UnrecognizedParameterType,
                            param.GetLocation(),
                            param.Identifier.Text,
                            PerformanceCache.GetDisplayString(paramType)
                        )
                    );
                    isValid = false;
                    continue;
                }

                // Explicit [PassThroughArgument] — pass-through custom arg.
                var customIndex = result.CustomParameters.Count;
                result.CustomParameters.Add(
                    new ParameterInfo(paramType, param.Identifier.ToString(), isRef, isIn)
                );
                result.ParameterSlots.Add(new ParamSlot(ParamSlotKind.Custom, customIndex));
            }

            return result;
        }

        /// <summary>
        /// Validates and records a by-value loop parameter (no <c>ref</c> / <c>in</c>,
        /// at most one per method). Used by every entity-shaped slot
        /// (<c>EntityIndex</c> / <c>EntityHandle</c> / <c>EntityAccessor</c> /
        /// <c>WorldAccessor</c>) since they share the same shape: validate
        /// modifiers, reject duplicates, register a <see cref="ParamSlot"/>.
        /// Returns <c>true</c> if the slot was successfully registered (caller
        /// should set its <c>HasXxx</c> flag); <c>false</c> if validation failed
        /// (caller should not set the flag, but may still <c>continue</c> — the
        /// diagnostic has already been reported and <paramref name="isValid"/>
        /// flipped to <c>false</c>).
        /// </summary>
        private static bool TryClassifyByValueLoopParam(
            ParameterSyntax param,
            bool isRef,
            bool isIn,
            bool alreadyPresent,
            string typeName,
            ParamSlotKind slotKind,
            ClassifiedParameters result,
            System.Action<Diagnostic> reportDiagnostic,
            ref bool isValid
        )
        {
            if (isRef || isIn)
            {
                reportDiagnostic(
                    Diagnostic.Create(
                        DiagnosticDescriptors.ParameterMustBeByValue,
                        param.GetLocation(),
                        param.Identifier.Text
                    )
                );
                isValid = false;
                return false;
            }

            if (alreadyPresent)
            {
                reportDiagnostic(
                    Diagnostic.Create(
                        DiagnosticDescriptors.DuplicateLoopParameter,
                        param.GetLocation(),
                        param.Identifier.Text,
                        typeName
                    )
                );
                isValid = false;
                return false;
            }

            result.ParameterSlots.Add(new ParamSlot(slotKind, 0));
            return true;
        }

        /// <summary>
        /// Classifies a parameter marked <c>[SingleEntity]</c>. Validates type
        /// (TRECS112), modifier (TRECS113), inline tags (TRECS114), and parses
        /// the aspect's read/write component types when applicable. Returns
        /// <c>null</c> on any validation failure (with diagnostics already reported).
        /// <para>
        /// Exposed as <c>internal</c> so generators that do their own parameter
        /// walk (e.g. RunOnceGenerator) can route singleton classification through
        /// the same code path as the iteration-style generators.
        /// </para>
        /// </summary>
        internal static HoistedSingletonInfo? ClassifyHoistedSingleton(
            ParameterSyntax param,
            ITypeSymbol paramType,
            IParameterSymbol paramSymbol,
            bool isRef,
            bool isIn,
            System.Action<Diagnostic> reportDiagnostic
        )
        {
            var tagTypes = InlineTagsParser.ParseFromSymbol(
                paramSymbol,
                "SingleEntity",
                param.GetLocation(),
                param.Identifier.Text,
                reportDiagnostic
            );
            if (tagTypes == null)
                return null;
            if (tagTypes.Count == 0)
            {
                reportDiagnostic(
                    Diagnostic.Create(
                        DiagnosticDescriptors.SingleEntityRequiresInlineTags,
                        param.GetLocation(),
                        param.Identifier.Text
                    )
                );
                return null;
            }

            bool isAspect = SymbolAnalyzer.ImplementsInterface(
                paramType,
                "IAspect",
                TrecsNamespaces.Trecs
            );
            bool isComponent = paramType.AllInterfaces.Any(i => i.Name == "IEntityComponent");
            if (!isAspect && !isComponent)
            {
                reportDiagnostic(
                    Diagnostic.Create(
                        DiagnosticDescriptors.SingleEntityWrongType,
                        param.GetLocation(),
                        param.Identifier.Text,
                        PerformanceCache.GetDisplayString(paramType)
                    )
                );
                return null;
            }

            if (isAspect)
            {
                if (!isIn || isRef)
                {
                    reportDiagnostic(
                        Diagnostic.Create(
                            DiagnosticDescriptors.SingleEntityWrongModifier,
                            param.GetLocation(),
                            param.Identifier.Text
                        )
                    );
                    return null;
                }
                if (paramType is not INamedTypeSymbol aspectType)
                    return null;
                var aspectData = AspectAttributeParser.ParseAspectData(aspectType);
                return new HoistedSingletonInfo(
                    paramName: param.Identifier.ToString(),
                    isAspect: true,
                    tagTypes: tagTypes,
                    aspectTypeDisplay: PerformanceCache.GetDisplayString(paramType),
                    aspectData: aspectData,
                    aspectTypeSymbol: paramType
                );
            }

            // Component-typed singleton.
            if (!isIn && !isRef)
            {
                reportDiagnostic(
                    Diagnostic.Create(
                        DiagnosticDescriptors.SingleEntityWrongModifier,
                        param.GetLocation(),
                        param.Identifier.Text
                    )
                );
                return null;
            }
            return new HoistedSingletonInfo(
                paramName: param.Identifier.ToString(),
                isAspect: false,
                tagTypes: tagTypes,
                componentTypeDisplay: PerformanceCache.GetDisplayString(paramType),
                componentTypeSymbol: paramType,
                isRef: isRef
            );
        }
    }
}
