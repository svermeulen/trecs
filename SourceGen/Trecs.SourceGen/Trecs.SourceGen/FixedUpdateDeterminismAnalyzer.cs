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
    public class FixedUpdateDeterminismAnalyzer : DiagnosticAnalyzer
    {
        public override ImmutableArray<DiagnosticDescriptor> SupportedDiagnostics { get; } =
            ImmutableArray.Create(DiagnosticDescriptors.NonDeterministicApiInFixedUpdate);

        public override void Initialize(AnalysisContext context)
        {
            context.ConfigureGeneratedCodeAnalysis(GeneratedCodeAnalysisFlags.None);
            context.EnableConcurrentExecution();

            context.RegisterCompilationStartAction(start =>
            {
                if (!ReferencesTrecs(start.Compilation))
                    return;

                var cache = new ConcurrentDictionary<INamedTypeSymbol, bool>(
                    SymbolEqualityComparer.Default
                );

                start.RegisterOperationAction(
                    ctx => AnalyzePropertyReference(ctx, cache),
                    OperationKind.PropertyReference
                );
                start.RegisterOperationAction(
                    ctx => AnalyzeObjectCreation(ctx, cache),
                    OperationKind.ObjectCreation
                );
                start.RegisterOperationAction(
                    ctx => AnalyzeInvocation(ctx, cache),
                    OperationKind.Invocation
                );
            });
        }

        static void AnalyzePropertyReference(
            OperationAnalysisContext context,
            ConcurrentDictionary<INamedTypeSymbol, bool> cache
        )
        {
            var propRef = (IPropertyReferenceOperation)context.Operation;
            var prop = propRef.Property;
            var containingType = prop.ContainingType;

            if (containingType == null)
                return;

            // Fast short-circuit on type name before any allocation
            string? apiName = null;
            string? suggestion = null;

            switch (containingType.Name)
            {
                case "DateTime"
                    when prop.Name is "Now" or "UtcNow" && IsInNamespace(containingType, "System"):
                    apiName = $"DateTime.{prop.Name}";
                    suggestion = "Use World.ElapsedTime or a deterministic clock instead.";
                    break;

                case "Time" when IsInNamespace(containingType, "UnityEngine"):
                    switch (prop.Name)
                    {
                        case "time":
                            apiName = "Time.time";
                            suggestion = "Use World.ElapsedTime for simulation time.";
                            break;
                        case "deltaTime":
                            apiName = "Time.deltaTime";
                            suggestion = "Use EcsAccessor.DeltaTime for the fixed timestep.";
                            break;
                        case "unscaledTime":
                            apiName = "Time.unscaledTime";
                            suggestion = "Use World.ElapsedTime for simulation time.";
                            break;
                        case "realtimeSinceStartup":
                            apiName = "Time.realtimeSinceStartup";
                            suggestion = "Use World.ElapsedTime for simulation time.";
                            break;
                    }
                    break;

                case "Random"
                    when prop.Name
                        is "value"
                            or "state"
                            or "insideUnitCircle"
                            or "insideUnitSphere"
                            or "onUnitSphere"
                            or "rotation"
                            or "rotationUniform"
                        && IsInNamespace(containingType, "UnityEngine"):
                    apiName = $"UnityEngine.Random.{prop.Name}";
                    suggestion = "Use Trecs.Rng with a deterministic seed instead.";
                    break;
            }

            if (apiName != null)
                ReportIfFixedUpdate(
                    context,
                    cache,
                    propRef.Syntax.GetLocation(),
                    apiName,
                    suggestion!
                );
        }

        static void AnalyzeObjectCreation(
            OperationAnalysisContext context,
            ConcurrentDictionary<INamedTypeSymbol, bool> cache
        )
        {
            var creation = (IObjectCreationOperation)context.Operation;
            var createdType = creation.Type;

            if (
                createdType is INamedTypeSymbol named
                && named.Name == "Random"
                && IsInNamespace(named, "System")
            )
            {
                ReportIfFixedUpdate(
                    context,
                    cache,
                    creation.Syntax.GetLocation(),
                    "new System.Random()",
                    "Use Trecs.Rng with a deterministic seed instead."
                );
            }
        }

        static void AnalyzeInvocation(
            OperationAnalysisContext context,
            ConcurrentDictionary<INamedTypeSymbol, bool> cache
        )
        {
            var invocation = (IInvocationOperation)context.Operation;
            var method = invocation.TargetMethod;
            var containingType = method.ContainingType;

            if (containingType == null)
                return;

            // Fast short-circuit on type name
            if (containingType.Name != "Random")
                return;

            string? apiName = null;
            string? suggestion = null;

            if (IsInNamespace(containingType, "UnityEngine"))
            {
                apiName = $"UnityEngine.Random.{method.Name}()";
                suggestion = "Use Trecs.Rng with a deterministic seed instead.";
            }
            else if (IsInNamespace(containingType, "System"))
            {
                apiName = $"System.Random.{method.Name}()";
                suggestion = "Use Trecs.Rng with a deterministic seed instead.";
            }

            if (apiName != null)
                ReportIfFixedUpdate(
                    context,
                    cache,
                    invocation.Syntax.GetLocation(),
                    apiName,
                    suggestion!
                );
        }

        static bool IsInNamespace(INamedTypeSymbol type, string expectedNamespace)
        {
            return type.ContainingNamespace?.ToDisplayString() == expectedNamespace;
        }

        static void ReportIfFixedUpdate(
            OperationAnalysisContext context,
            ConcurrentDictionary<INamedTypeSymbol, bool> cache,
            Location location,
            string apiName,
            string suggestion
        )
        {
            var containingType = FixedUpdateSystemHelper.GetContainingNamedType(
                context.ContainingSymbol
            );
            if (containingType == null)
                return;

            if (!FixedUpdateSystemHelper.IsFixedUpdateSystem(containingType, cache))
                return;

            context.ReportDiagnostic(
                Diagnostic.Create(
                    DiagnosticDescriptors.NonDeterministicApiInFixedUpdate,
                    location,
                    apiName,
                    suggestion
                )
            );
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
