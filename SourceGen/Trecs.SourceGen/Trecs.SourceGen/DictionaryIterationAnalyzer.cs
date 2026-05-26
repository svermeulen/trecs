#nullable enable

using System.Collections.Concurrent;
using System.Collections.Immutable;
using System.Linq;
using Microsoft.CodeAnalysis;
using Microsoft.CodeAnalysis.Diagnostics;
using Microsoft.CodeAnalysis.Operations;
using Trecs.SourceGen.Shared;

namespace Trecs.SourceGen
{
    [DiagnosticAnalyzer(LanguageNames.CSharp)]
    public class DictionaryIterationAnalyzer : DiagnosticAnalyzer
    {
        public override ImmutableArray<DiagnosticDescriptor> SupportedDiagnostics { get; } =
            ImmutableArray.Create(
                DiagnosticDescriptors.DictionaryIteration,
                DiagnosticDescriptors.NativeHashMapIteration
            );

        public override void Initialize(AnalysisContext context)
        {
            context.ConfigureGeneratedCodeAnalysis(GeneratedCodeAnalysisFlags.None);
            context.EnableConcurrentExecution();

            context.RegisterCompilationStartAction(start =>
            {
                if (!ReferencesTrecs(start.Compilation))
                    return;

                bool globalCheck = ReadGlobalCheckSetting(start.Compilation);

                var systemCache = globalCheck
                    ? null
                    : new ConcurrentDictionary<INamedTypeSymbol, bool>(
                        SymbolEqualityComparer.Default
                    );

                start.RegisterOperationAction(
                    ctx => AnalyzeForEach(ctx, globalCheck, systemCache),
                    OperationKind.Loop
                );
                start.RegisterOperationAction(
                    ctx => AnalyzeInvocation(ctx, globalCheck, systemCache),
                    OperationKind.Invocation
                );
            });
        }

        static void AnalyzeForEach(
            OperationAnalysisContext context,
            bool globalCheck,
            ConcurrentDictionary<INamedTypeSymbol, bool>? systemCache
        )
        {
            if (context.Operation is not IForEachLoopOperation forEach)
                return;

            var collectionType = forEach.Collection.Type;
            if (collectionType == null)
                return;

            ReportIfNonDeterministic(
                context,
                collectionType,
                forEach.Collection.Syntax.GetLocation(),
                globalCheck,
                systemCache
            );
        }

        static void AnalyzeInvocation(
            OperationAnalysisContext context,
            bool globalCheck,
            ConcurrentDictionary<INamedTypeSymbol, bool>? systemCache
        )
        {
            var invocation = (IInvocationOperation)context.Operation;

            if (invocation.TargetMethod.Name != "GetEnumerator")
                return;

            var receiverType = invocation.Instance?.Type;
            if (receiverType == null)
                return;

            ReportIfNonDeterministic(
                context,
                receiverType,
                invocation.Syntax.GetLocation(),
                globalCheck,
                systemCache
            );
        }

        static void ReportIfNonDeterministic(
            OperationAnalysisContext context,
            ITypeSymbol type,
            Location location,
            bool globalCheck,
            ConcurrentDictionary<INamedTypeSymbol, bool>? systemCache
        )
        {
            var named = type as INamedTypeSymbol;
            if (named == null)
                return;

            var original = named.OriginalDefinition;

            DiagnosticDescriptor? descriptor = null;
            string? replacement = null;

            switch (original.Name)
            {
                case "Dictionary" when IsInNamespace(original, "System.Collections.Generic"):
                    descriptor = DiagnosticDescriptors.DictionaryIteration;
                    replacement = "IterableDictionary<TKey, TValue>";
                    break;

                case "KeyCollection"
                or "ValueCollection"
                    when original.ContainingType is { Name: "Dictionary" } container
                        && IsInNamespace(
                            container.OriginalDefinition,
                            "System.Collections.Generic"
                        ):
                    descriptor = DiagnosticDescriptors.DictionaryIteration;
                    replacement = "IterableDictionary<TKey, TValue>";
                    break;

                case "IDictionary"
                or "IReadOnlyDictionary" when IsInNamespace(original, "System.Collections.Generic"):
                    descriptor = DiagnosticDescriptors.DictionaryIteration;
                    replacement = "IReadOnlyIterableDictionary<TKey, TValue>";
                    break;

                case "HashSet" when IsInNamespace(original, "System.Collections.Generic"):
                    descriptor = DiagnosticDescriptors.DictionaryIteration;
                    replacement = "IterableHashSet<T>";
                    break;

                case "NativeHashMap"
                or "NativeParallelHashMap"
                or "NativeParallelMultiHashMap" when IsInNamespace(original, "Unity.Collections"):
                    descriptor = DiagnosticDescriptors.NativeHashMapIteration;
                    replacement = "NativeIterableDictionary<TKey, TValue>";
                    break;

                case "NativeHashSet" when IsInNamespace(original, "Unity.Collections"):
                    descriptor = DiagnosticDescriptors.NativeHashMapIteration;
                    replacement = "NativeIterableDictionary<TKey, TValue> (keys only)";
                    break;
            }

            if (descriptor == null)
                return;

            if (!globalCheck)
            {
                var containingType = FixedUpdateSystemHelper.GetContainingNamedType(
                    context.ContainingSymbol
                );
                if (containingType == null)
                    return;

                if (!FixedUpdateSystemHelper.IsFixedUpdateSystem(containingType, systemCache!))
                    return;
            }

            context.ReportDiagnostic(
                Diagnostic.Create(
                    descriptor,
                    location,
                    type.ToDisplayString(SymbolDisplayFormat.MinimallyQualifiedFormat),
                    replacement
                )
            );
        }

        static bool IsInNamespace(ITypeSymbol type, string expectedNamespace)
        {
            return type.ContainingNamespace?.ToDisplayString() == expectedNamespace;
        }

        static bool ReadGlobalCheckSetting(Compilation compilation)
        {
            foreach (var attr in compilation.Assembly.GetAttributes())
            {
                if (attr.AttributeClass?.Name != TrecsAttributeNames.SourceGenSettings)
                    continue;
                if (attr.AttributeClass?.ContainingNamespace?.ToDisplayString() != "Trecs")
                    continue;

                foreach (var namedArg in attr.NamedArguments)
                {
                    if (
                        namedArg.Key == "GlobalCollectionIterationCheck"
                        && namedArg.Value.Value is bool value
                    )
                    {
                        return value;
                    }
                }
            }
            return false;
        }

        static bool ReferencesTrecs(Compilation compilation)
        {
            if (
                compilation.AssemblyName?.Equals("Trecs", System.StringComparison.OrdinalIgnoreCase)
                == true
            )
                return true;

            return compilation.ReferencedAssemblyNames.Any(a =>
                a.Name.Equals("Trecs", System.StringComparison.OrdinalIgnoreCase)
            );
        }
    }
}
