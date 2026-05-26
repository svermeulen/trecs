#nullable enable

using System.Collections.Immutable;
using System.Linq;
using Microsoft.CodeAnalysis;
using Microsoft.CodeAnalysis.Diagnostics;
using Microsoft.CodeAnalysis.Operations;

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

                start.RegisterOperationAction(AnalyzeForEach, OperationKind.Loop);
                start.RegisterOperationAction(AnalyzeInvocation, OperationKind.Invocation);
            });
        }

        static void AnalyzeForEach(OperationAnalysisContext context)
        {
            if (context.Operation is not IForEachLoopOperation forEach)
                return;

            var collectionType = forEach.Collection.Type;
            if (collectionType == null)
                return;

            ReportIfNonDeterministic(
                context,
                collectionType,
                forEach.Collection.Syntax.GetLocation()
            );
        }

        static void AnalyzeInvocation(OperationAnalysisContext context)
        {
            var invocation = (IInvocationOperation)context.Operation;

            if (invocation.TargetMethod.Name != "GetEnumerator")
                return;

            var receiverType = invocation.Instance?.Type;
            if (receiverType == null)
                return;

            ReportIfNonDeterministic(context, receiverType, invocation.Syntax.GetLocation());
        }

        static void ReportIfNonDeterministic(
            OperationAnalysisContext context,
            ITypeSymbol type,
            Location location
        )
        {
            var named = type as INamedTypeSymbol;
            if (named == null)
                return;

            var original = named.OriginalDefinition;

            // Fast short-circuit on type name before any namespace resolution
            switch (original.Name)
            {
                case "Dictionary" when IsInNamespace(original, "System.Collections.Generic"):
                    Report(
                        context,
                        location,
                        type,
                        DiagnosticDescriptors.DictionaryIteration,
                        "IterableDictionary<TKey, TValue>"
                    );
                    return;

                case "KeyCollection"
                or "ValueCollection"
                    when original.ContainingType is { Name: "Dictionary" } container
                        && IsInNamespace(
                            container.OriginalDefinition,
                            "System.Collections.Generic"
                        ):
                    Report(
                        context,
                        location,
                        type,
                        DiagnosticDescriptors.DictionaryIteration,
                        "IterableDictionary<TKey, TValue>"
                    );
                    return;

                case "IDictionary"
                or "IReadOnlyDictionary" when IsInNamespace(original, "System.Collections.Generic"):
                    Report(
                        context,
                        location,
                        type,
                        DiagnosticDescriptors.DictionaryIteration,
                        "IReadOnlyIterableDictionary<TKey, TValue>"
                    );
                    return;

                case "HashSet" when IsInNamespace(original, "System.Collections.Generic"):
                    Report(
                        context,
                        location,
                        type,
                        DiagnosticDescriptors.DictionaryIteration,
                        "IterableHashSet<T>"
                    );
                    return;

                case "NativeHashMap"
                or "NativeParallelHashMap"
                or "NativeParallelMultiHashMap" when IsInNamespace(original, "Unity.Collections"):
                    Report(
                        context,
                        location,
                        type,
                        DiagnosticDescriptors.NativeHashMapIteration,
                        "NativeIterableDictionary<TKey, TValue>"
                    );
                    return;

                case "NativeHashSet" when IsInNamespace(original, "Unity.Collections"):
                    Report(
                        context,
                        location,
                        type,
                        DiagnosticDescriptors.NativeHashMapIteration,
                        "NativeIterableDictionary<TKey, TValue> (keys only)"
                    );
                    return;
            }
        }

        static void Report(
            OperationAnalysisContext context,
            Location location,
            ITypeSymbol type,
            DiagnosticDescriptor descriptor,
            string replacement
        )
        {
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
