using System;
using System.Collections.Generic;
using System.Collections.Immutable;
using System.Linq;
using Microsoft.CodeAnalysis;
using Microsoft.CodeAnalysis.CSharp.Syntax;
using Trecs.SourceGen.Performance;
using Trecs.SourceGen.Shared;

namespace Trecs.SourceGen.Aspect
{
    /// <summary>
    /// Builds an equatable <see cref="AspectModel"/> from a Roslyn syntax+symbol pair. This is
    /// the *only* place in the aspect pipeline that touches <see cref="ITypeSymbol"/>; the
    /// resulting model carries only strings, bools, and value-equality collections so it can
    /// flow through the incremental cache without pinning compilations or producing spurious
    /// invalidations.
    /// </summary>
    internal static class AspectModelBuilder
    {
        /// <summary>
        /// Runs in the pipeline transform stage. The returned model has <see cref="AspectModel.IsValid"/>
        /// set according to validator output; downstream stages emit code only when valid, but
        /// always replay diagnostics.
        /// </summary>
        public static AspectModel Build(TypeDeclarationSyntax typeDecl, INamedTypeSymbol symbol)
        {
            var diagnostics = new List<DiagnosticInfo>();
            var location = LocationInfo.From(typeDecl.GetLocation());
            var isInterface = symbol.TypeKind == TypeKind.Interface;
            var typeName = symbol.Name;
            var ns = SymbolAnalyzer.GetNamespaceChain(symbol);
            var accessibility = SymbolAnalyzer.GetAccessibilityModifier(symbol);
            var containing = SymbolAnalyzer.GetContainingTypeChainInfo(symbol).ToEquatableArray();
            var hintFileName = SymbolAnalyzer.GetSafeFileName(
                symbol,
                isInterface ? "AspectInterface" : "Aspect"
            );

            AspectComponents components = AspectComponents.Empty;
            bool isValid;

            try
            {
                if (isInterface)
                {
                    var ifaceDecl = (InterfaceDeclarationSyntax)typeDecl;
                    var isPartial = ValidateInterfaceIsPartial(ifaceDecl, symbol, diagnostics);
                    if (isPartial)
                    {
                        var built = TryBuildInterfaceComponents(symbol);
                        if (built is null)
                        {
                            // Symbol wasn't recognized as an aspect interface — the syntactic
                            // predicate was conservative, so this is the final gate. Drop it.
                            return Invalid(
                                typeName,
                                ns,
                                accessibility,
                                hintFileName,
                                containing,
                                isInterface,
                                diagnostics
                            );
                        }
                        components = built;
                        isValid = true;
                    }
                    else
                    {
                        isValid = false;
                    }
                }
                else
                {
                    components = BuildStructComponents(symbol);
                    isValid = ValidateStruct(typeDecl, symbol, components, location, diagnostics);
                }
            }
            catch (Exception ex)
            {
                diagnostics.Add(
                    DiagnosticInfo.Create(
                        DiagnosticDescriptors.SourceGenerationError,
                        location,
                        "Aspect parse/validate",
                        ex.Message
                    )
                );
                isValid = false;
                components = AspectComponents.Empty;
            }

            return new AspectModel(
                TypeName: typeName,
                Namespace: ns,
                Accessibility: accessibility,
                HintFileName: hintFileName,
                ContainingTypes: containing,
                IsInterface: isInterface,
                IsValid: isValid,
                Components: components,
                Diagnostics: diagnostics.ToEquatableArray()
            );
        }

        // -------------------------------------------------------------------------------------
        // Component extraction
        // -------------------------------------------------------------------------------------

        private static AspectComponents BuildStructComponents(INamedTypeSymbol symbol)
        {
            var readSymbols = new List<ITypeSymbol>();
            var writeSymbols = new List<ITypeSymbol>();
            var interfaceSymbols = new List<ITypeSymbol>();

            InterfaceComponentExtractor.ExtractComponentsFromInterfaces(
                symbol,
                readSymbols,
                writeSymbols,
                interfaceSymbols
            );

            // Cascade components from any aspect-interface bases (transitively, deduped).
            AspectInterfaceParser.ExtractInterfaceComponents(
                interfaceSymbols,
                readSymbols,
                writeSymbols
            );

            var distinctRead = PerformanceCache.GetDistinctTypes(readSymbols);
            var distinctWrite = PerformanceCache.GetDistinctTypes(writeSymbols);

            return BuildComponents(distinctRead, distinctWrite);
        }

        private static AspectComponents? TryBuildInterfaceComponents(INamedTypeSymbol symbol)
        {
            if (!SymbolAnalyzer.IsAspectInterface(symbol))
                return null;

            var readSymbols = new List<ITypeSymbol>();
            var writeSymbols = new List<ITypeSymbol>();
            var ignoredInterfaces = new List<ITypeSymbol>();

            InterfaceComponentExtractor.ExtractComponentsFromInterfaces(
                symbol,
                readSymbols,
                writeSymbols,
                ignoredInterfaces
            );

            return BuildComponents(readSymbols.ToImmutableArray(), writeSymbols.ToImmutableArray());
        }

        private static AspectComponents BuildComponents(
            ImmutableArray<ITypeSymbol> readSymbols,
            ImmutableArray<ITypeSymbol> writeSymbols
        )
        {
            // Read and Write are disjoint after TRECS025 (DuplicateComponentType) check, but
            // be defensive: classify each component once against the write set so behavior
            // matches the legacy IsReadOnlyComponent.
            var writeSet = new HashSet<ITypeSymbol>(writeSymbols, SymbolEqualityComparer.Default);

            var readModels = new ComponentModel[readSymbols.Length];
            for (int i = 0; i < readSymbols.Length; i++)
            {
                bool isReadOnly = !writeSet.Contains(readSymbols[i]);
                readModels[i] = BuildComponentModel(readSymbols[i], isReadOnly);
            }

            var writeModels = new ComponentModel[writeSymbols.Length];
            for (int i = 0; i < writeSymbols.Length; i++)
            {
                writeModels[i] = BuildComponentModel(writeSymbols[i], isReadOnly: false);
            }

            // All = read order followed by writes not already in reads. Matches the legacy
            // PerformanceCache.MergeDistinctTypes(reads, writes) result, which the codegen
            // iterates for fields, constructor params, factory, and enumerator state.
            var readKeys = new HashSet<ITypeSymbol>(readSymbols, SymbolEqualityComparer.Default);
            var all = new List<ComponentModel>(readSymbols.Length + writeSymbols.Length);
            all.AddRange(readModels);
            for (int i = 0; i < writeSymbols.Length; i++)
            {
                if (!readKeys.Contains(writeSymbols[i]))
                    all.Add(writeModels[i]);
            }

            return new AspectComponents(
                Read: new EquatableArray<ComponentModel>(readModels),
                Write: new EquatableArray<ComponentModel>(writeModels),
                All: new EquatableArray<ComponentModel>(all.ToArray())
            );
        }

        private static ComponentModel BuildComponentModel(
            ITypeSymbol componentType,
            bool isReadOnly
        )
        {
            var displayString = PerformanceCache.GetDisplayString(componentType);
            var propertyName = ComponentTypeHelper.GetPropertyName(componentType);
            var camelPropertyName = ComponentTypeHelper.ToCamelCase(propertyName);

            // Precompute return-type strings (with unwrap-target substitution applied) and
            // the unwrap-access suffix so the codegen never has to walk symbols.
            var (unwrappedFinalType, wasUnwrapped) = ComponentTypeHelper.UnwrapComponent(
                componentType
            );

            string returnTypeBase;
            string unwrapSuffix;
            if (wasUnwrapped)
            {
                returnTypeBase = PerformanceCache.GetDisplayString(unwrappedFinalType);
                unwrapSuffix = BuildUnwrapAccessSuffix(componentType);
            }
            else
            {
                returnTypeBase = displayString;
                unwrapSuffix = string.Empty;
            }

            return new ComponentModel(
                DisplayString: displayString,
                PropertyName: propertyName,
                CamelPropertyName: camelPropertyName,
                IsReadOnly: isReadOnly,
                ReadReturnType: $"ref readonly {returnTypeBase}",
                WriteReturnType: $"ref {returnTypeBase}",
                UnwrapAccessSuffix: unwrapSuffix
            );
        }

        private static string BuildUnwrapAccessSuffix(ITypeSymbol componentType)
        {
            // Build the same chain that ComponentTypeHelper.GetPropertyAccessExpression does
            // for unwrapped types: ".A.B.C". The chain ends when the current type is no longer
            // an [Unwrap] component.
            var sb = new System.Text.StringBuilder();
            var current = componentType;
            while (
                current is INamedTypeSymbol namedType
                && ComponentTypeHelper.IsUnwrapComponent(namedType)
            )
            {
                var field = ComponentTypeHelper.GetUnwrapComponentField(namedType);
                if (field is null)
                    break;
                sb.Append('.').Append(field.Name);
                current = field.Type;
            }
            return sb.ToString();
        }

        // -------------------------------------------------------------------------------------
        // Validation — produces DiagnosticInfo instead of Diagnostic
        // -------------------------------------------------------------------------------------

        private static bool ValidateInterfaceIsPartial(
            InterfaceDeclarationSyntax decl,
            INamedTypeSymbol symbol,
            List<DiagnosticInfo> diagnostics
        )
        {
            if (SymbolAnalyzer.IsPartialType(decl))
                return true;
            diagnostics.Add(
                DiagnosticInfo.Create(
                    DiagnosticDescriptors.AspectInterfaceMustBePartial,
                    decl.GetLocation(),
                    symbol.Name
                )
            );
            return false;
        }

        private static bool ValidateStruct(
            TypeDeclarationSyntax decl,
            INamedTypeSymbol symbol,
            AspectComponents components,
            LocationInfo location,
            List<DiagnosticInfo> diagnostics
        )
        {
            bool isValid = true;

            if (!SymbolAnalyzer.IsPartialType(decl))
            {
                diagnostics.Add(
                    DiagnosticInfo.Create(
                        DiagnosticDescriptors.AspectMustBePartial,
                        decl.GetLocation(),
                        symbol.Name
                    )
                );
                isValid = false;
            }

            // Duplicate-component-type validation: same component must not appear twice in
            // the (read ∪ write) set. The check is by display string so equivalent symbols
            // from re-typed references are caught.
            var seenDisplay = new HashSet<string>();
            foreach (var c in components.Read)
            {
                if (!seenDisplay.Add(c.DisplayString))
                {
                    diagnostics.Add(
                        DiagnosticInfo.Create(
                            DiagnosticDescriptors.DuplicateComponentType,
                            location,
                            c.DisplayString
                        )
                    );
                    isValid = false;
                }
            }
            foreach (var c in components.Write)
            {
                if (!seenDisplay.Add(c.DisplayString))
                {
                    diagnostics.Add(
                        DiagnosticInfo.Create(
                            DiagnosticDescriptors.DuplicateComponentType,
                            location,
                            c.DisplayString
                        )
                    );
                    isValid = false;
                }
            }

            return isValid;
        }

        private static AspectModel Invalid(
            string typeName,
            string ns,
            string accessibility,
            string hintFileName,
            EquatableArray<ContainingTypeInfo> containing,
            bool isInterface,
            List<DiagnosticInfo> diagnostics
        ) =>
            new AspectModel(
                TypeName: typeName,
                Namespace: ns,
                Accessibility: accessibility,
                HintFileName: hintFileName,
                ContainingTypes: containing,
                IsInterface: isInterface,
                IsValid: false,
                Components: AspectComponents.Empty,
                Diagnostics: diagnostics.ToEquatableArray()
            );
    }
}
