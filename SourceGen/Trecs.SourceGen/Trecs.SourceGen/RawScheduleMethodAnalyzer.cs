#nullable enable

using System.Collections.Generic;
using System.Collections.Immutable;
using System.Linq;
using Microsoft.CodeAnalysis;
using Microsoft.CodeAnalysis.Diagnostics;
using Microsoft.CodeAnalysis.Operations;

namespace Trecs.SourceGen
{
    /// <summary>
    /// Warns when raw Unity schedule extension methods (Schedule, ScheduleParallel, Run, etc.)
    /// are called on job structs that contain ComponentBuffer or NativeComponent fields. These
    /// jobs should use the JobGenerator-generated <c>ScheduleParallel(WorldAccessor)</c> /
    /// <c>ScheduleParallel(QueryBuilder)</c> member method instead, which performs proper
    /// dependency tracking via RuntimeJobScheduler.
    /// </summary>
    [DiagnosticAnalyzer(LanguageNames.CSharp)]
    public class RawScheduleMethodAnalyzer : DiagnosticAnalyzer
    {
        private static readonly HashSet<string> RawScheduleMethods = new()
        {
            "Schedule",
            "ScheduleParallel",
            "ScheduleByRef",
            "ScheduleParallelByRef",
            "Run",
            "RunByRef",
        };

        // Generic Trecs types whose presence on a job struct field signals that the job
        // needs source-generator-managed scheduling. Stored as the open-generic name (no
        // arity suffix) — Roslyn's `IsGenericType ? ConstructedFrom.Name` strips the suffix
        // for us. We deliberately match prefixes (`StartsWith`) so e.g. `NativeComponentBufferRead`
        // and `NativeComponentBufferWrite` both light up under "NativeComponentBuffer".
        private static readonly string[] TrecsTypedFieldPrefixes =
        {
            "NativeComponentBuffer", // NativeComponentBufferRead<T>, NativeComponentBufferWrite<T>
            "ComponentAccessor", // ComponentAccessor<T>
            "NativeComponentRead", // NativeComponentRead<T>
            "NativeComponentWrite", // NativeComponentWrite<T>
            "NativeComponentLookup", // NativeComponentLookupRead<T>, NativeComponentLookupWrite<T>
            "NativeSetCommandBuffer", // NativeSetCommandBuffer<TSet>
            "NativeSetRead", // NativeSetRead<TSet>
            "NativeEntitySetIndices", // NativeEntitySetIndices<TSet>
        };

        public override ImmutableArray<DiagnosticDescriptor> SupportedDiagnostics { get; } =
            ImmutableArray.Create(DiagnosticDescriptors.RawScheduleWithTrecsFields);

        public override void Initialize(AnalysisContext context)
        {
            context.ConfigureGeneratedCodeAnalysis(GeneratedCodeAnalysisFlags.None);
            context.EnableConcurrentExecution();
            context.RegisterOperationAction(AnalyzeInvocation, OperationKind.Invocation);
        }

        private static void AnalyzeInvocation(OperationAnalysisContext context)
        {
            var invocation = (IInvocationOperation)context.Operation;
            var method = invocation.TargetMethod;

            if (!RawScheduleMethods.Contains(method.Name))
                return;

            // Check that this is an extension method from Unity's job extensions
            if (!method.IsExtensionMethod)
                return;

            var containingType = method.ContainingType?.Name;
            if (
                containingType != "IJobForExtensions"
                && containingType != "IJobExtensions"
                && containingType != "IJobParallelForExtensions"
            )
                return;

            // Get the job type (first type argument of the extension method)
            if (!method.IsGenericMethod || method.TypeArguments.Length == 0)
                return;

            var jobType = method.TypeArguments[0];
            if (jobType is not INamedTypeSymbol jobNamedType)
                return;

            // Check if the job struct has ComponentBuffer<T> or ComponentAccessor<T> fields
            var trecsFields = GetTrecsComponentFields(jobNamedType);
            if (trecsFields.Count == 0)
                return;

            var fieldNames = string.Join(", ", trecsFields);
            var diagnostic = Diagnostic.Create(
                DiagnosticDescriptors.RawScheduleWithTrecsFields,
                invocation.Syntax.GetLocation(),
                jobType.Name,
                fieldNames,
                method.Name
            );

            context.ReportDiagnostic(diagnostic);
        }

        private static List<string> GetTrecsComponentFields(INamedTypeSymbol jobType)
        {
            var result = new List<string>();

            foreach (var member in jobType.GetMembers())
            {
                if (member is not IFieldSymbol field)
                    continue;

                if (field.Type is not INamedTypeSymbol fieldType)
                    continue;

                if (!fieldType.IsGenericType)
                    continue;

                // Restrict to types in the Trecs namespace so a user struct named
                // (e.g.) `MyApp.NativeComponentBufferRead` doesn't accidentally trip the analyzer.
                var ns = fieldType.ContainingNamespace?.ToDisplayString();
                if (ns != "Trecs")
                    continue;

                var genericName = fieldType.ConstructedFrom.Name;
                foreach (var prefix in TrecsTypedFieldPrefixes)
                {
                    if (genericName.StartsWith(prefix))
                    {
                        result.Add(field.Name);
                        break;
                    }
                }
            }

            return result;
        }
    }
}
