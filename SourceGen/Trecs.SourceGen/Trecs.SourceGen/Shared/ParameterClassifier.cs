#nullable enable

using System.Collections.Generic;
using System.Collections.Immutable;
using System.Linq;
using Microsoft.CodeAnalysis;
using Microsoft.CodeAnalysis.CSharp;
using Microsoft.CodeAnalysis.CSharp.Syntax;
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
        /// <param name="supportsEntityIndex">Whether EntityIndex is a valid parameter (false for ForSingleAspect).</param>
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
            SourceProductionContext context,
            string? methodName,
            bool supportsEntityIndex,
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
                    context.ReportDiagnostic(
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

                // NativeSetRead<T> / NativeSetWrite<T> detection — these are job-only,
                // forbidden in main-thread iteration methods.
                if (
                    !isPassThrough
                    && paramType is INamedTypeSymbol namedNativeSet
                    && (
                        namedNativeSet.Name == "NativeSetRead"
                        || namedNativeSet.Name == "NativeSetWrite"
                    )
                    && namedNativeSet.TypeArguments.Length == 1
                    && PerformanceCache.GetDisplayString(namedNativeSet.ContainingNamespace)
                        == "Trecs"
                )
                {
                    var nativeSetTypeArg = PerformanceCache.GetDisplayString(
                        namedNativeSet.TypeArguments[0]
                    );
                    context.ReportDiagnostic(
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
                        context.ReportDiagnostic(
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
                        context.ReportDiagnostic(
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
                        context.ReportDiagnostic(
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
                    TrecsNamespaces.Trecs
                );
                bool isWorldAccessor = SymbolAnalyzer.IsExactType(
                    paramType,
                    "WorldAccessor",
                    TrecsNamespaces.Trecs
                );

                if (!isPassThrough && isEntityIndex)
                {
                    if (!supportsEntityIndex)
                    {
                        // EntityIndex not supported in this mode — treat as unrecognized.
                        context.ReportDiagnostic(
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

                    if (isRef || isIn)
                    {
                        context.ReportDiagnostic(
                            Diagnostic.Create(
                                DiagnosticDescriptors.ParameterMustBeByValue,
                                param.GetLocation(),
                                param.Identifier.Text
                            )
                        );
                        isValid = false;
                        continue;
                    }

                    if (result.HasEntityIndex)
                    {
                        context.ReportDiagnostic(
                            Diagnostic.Create(
                                DiagnosticDescriptors.DuplicateLoopParameter,
                                param.GetLocation(),
                                param.Identifier.Text,
                                "EntityIndex"
                            )
                        );
                        isValid = false;
                        continue;
                    }

                    result.HasEntityIndex = true;
                    result.ParameterSlots.Add(new ParamSlot(ParamSlotKind.LoopEntityIndex, 0));
                    continue;
                }

                if (!isPassThrough && isWorldAccessor)
                {
                    if (isRef || isIn)
                    {
                        context.ReportDiagnostic(
                            Diagnostic.Create(
                                DiagnosticDescriptors.ParameterMustBeByValue,
                                param.GetLocation(),
                                param.Identifier.Text
                            )
                        );
                        isValid = false;
                        continue;
                    }

                    if (result.HasWorldAccessor)
                    {
                        context.ReportDiagnostic(
                            Diagnostic.Create(
                                DiagnosticDescriptors.DuplicateLoopParameter,
                                param.GetLocation(),
                                param.Identifier.Text,
                                "WorldAccessor"
                            )
                        );
                        isValid = false;
                        continue;
                    }

                    result.HasWorldAccessor = true;
                    result.ParameterSlots.Add(new ParamSlot(ParamSlotKind.LoopWorldAccessor, 0));
                    continue;
                }

                // IEntityComponent detection
                bool isComponent = paramType.AllInterfaces.Any(i => i.Name == "IEntityComponent");

                if (mode == IterationMode.Components && !isPassThrough && isComponent)
                {
                    // Component mode: accept IEntityComponent params with in/ref modifier.
                    if (!isRef && !isIn)
                    {
                        context.ReportDiagnostic(
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
                    context.ReportDiagnostic(
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
                    context.ReportDiagnostic(
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
    }
}
