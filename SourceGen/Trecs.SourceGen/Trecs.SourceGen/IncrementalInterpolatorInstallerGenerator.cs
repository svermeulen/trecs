#nullable enable

using System;
using System.Collections.Immutable;
using System.Linq;
using System.Text;
using Microsoft.CodeAnalysis;
using Microsoft.CodeAnalysis.CSharp;
using Microsoft.CodeAnalysis.CSharp.Syntax;
using Trecs.SourceGen.Performance;
using Trecs.SourceGen.Shared;

namespace Trecs.SourceGen
{
    /// <summary>
    /// Incremental source generator that generates WorldBuilder extension methods
    /// for registering interpolation systems and their previous-value savers.
    /// </summary>
    [Generator]
    public class IncrementalInterpolatorInstallerGenerator : IIncrementalGenerator
    {
        public void Initialize(IncrementalGeneratorInitializationContext context)
        {
            // Check if compilation references Trecs assembly for better performance
            var hasTrecsReference = AssemblyFilterHelper.CreateTrecsReferenceCheck(context);

            // Create provider for methods with GenerateInterpolatorSystemAttribute
            var interpolatorMethodProviderRaw = context
                .SyntaxProvider.ForAttributeWithMetadataName(
                    "Trecs.GenerateInterpolatorSystemAttribute",
                    predicate: static (node, _) => node is MethodDeclarationSyntax,
                    transform: static (ctx, _) => GetInterpolatorMethodData(ctx)
                )
                .Where(static m => m is not null);
            var interpolatorMethodProvider = AssemblyFilterHelper.FilterByTrecsReference(
                interpolatorMethodProviderRaw,
                hasTrecsReference
            );

            // Collect all interpolator methods to group by group name
            var allInterpolatorMethods = interpolatorMethodProvider.Collect();

            // Combine with compilation provider
            var interpolatorMethodsWithCompilation = allInterpolatorMethods.Combine(
                context.CompilationProvider
            );

            // Register source output
            context.RegisterSourceOutput(
                interpolatorMethodsWithCompilation,
                static (spc, source) =>
                    GenerateInterpolatorExtensions(spc, source.Left, source.Right)
            );
        }

        private static InterpolatorInstallerMethodData? GetInterpolatorMethodData(
            GeneratorAttributeSyntaxContext context
        )
        {
            var methodDecl = (MethodDeclarationSyntax)context.TargetNode;
            var methodSymbol = context.TargetSymbol as IMethodSymbol;

            if (methodSymbol == null || !methodSymbol.IsStatic)
                return null;

            var attribute = context.Attributes.FirstOrDefault();
            if (attribute == null || methodSymbol.Parameters.Length == 0)
                return null;

            return new InterpolatorInstallerMethodData(methodDecl, methodSymbol, attribute);
        }

        private static void GenerateInterpolatorExtensions(
            SourceProductionContext context,
            ImmutableArray<InterpolatorInstallerMethodData?> methodData,
            Compilation compilation
        )
        {
            if (methodData.IsEmpty)
                return;

            try
            {
                using var _timer_ = SourceGenTimer.Time("InterpolatorInstallerGenerator.Total");
                SourceGenLogger.Log(
                    $"[IncrementalInterpolatorInstallerGenerator] Processing {methodData.Length} interpolator methods"
                );

                // GroupIndex methods by group name (filter out nulls first)
                var groups = methodData
                    .Where(m => m != null)
                    .GroupBy(m => GetGroupName(m!.AttributeData))
                    .Where(g => !string.IsNullOrEmpty(g.Key))
                    .ToList();

                foreach (var group in groups)
                {
                    var groupName = group.Key!;
                    var methods = group.ToList();

                    // Get namespace from first method (they should all be in same namespace for a given group)
                    var nameSpace = PerformanceCache.GetDisplayString(
                        methods.First()!.MethodSymbol.ContainingNamespace
                    );

                    var source = GenerateExtension(nameSpace, groupName, methods);
                    var fileName = SymbolAnalyzer.GetSafeFileName(
                        methods.First()!.MethodSymbol.ContainingType,
                        $"{groupName}WorldBuilderExtensions"
                    );

                    context.AddSource(fileName, source);
                    SourceGenLogger.WriteGeneratedFile(fileName, source);
                }
            }
            catch (Exception ex)
            {
                SourceGenLogger.Log(
                    $"[IncrementalInterpolatorInstallerGenerator] Error generating extensions: {ex.Message}"
                );

                // Report error for any unhandled exceptions
                var diagnostic = Diagnostic.Create(
                    DiagnosticDescriptors.CouldNotResolveSymbol,
                    Location.None,
                    $"InterpolatorExtension: {ex.Message}"
                );
                context.ReportDiagnostic(diagnostic);
            }
        }

        private static string? GetGroupName(AttributeData attributeData)
        {
            return attributeData.ConstructorArguments.Length >= 2
                ? attributeData.ConstructorArguments[1].Value as string
                : null;
        }

        private static string? GetSystemName(AttributeData attributeData)
        {
            return attributeData.ConstructorArguments.Length >= 1
                ? attributeData.ConstructorArguments[0].Value as string
                : null;
        }

        private static string GenerateExtension(
            string nameSpace,
            string groupName,
            System.Collections.Generic.List<InterpolatorInstallerMethodData?> methods
        )
        {
            var registrationsBuilder = new StringBuilder();

            for (int i = 0; i < methods.Count; i++)
            {
                var method = methods[i];
                if (method == null)
                    continue;
                var systemName = GetSystemName(method.AttributeData);
                var componentType = PerformanceCache.GetDisplayString(
                    method.MethodSymbol.Parameters[0].Type
                );

                registrationsBuilder.AppendLine(
                    $"            builder.AddInterpolatedPreviousSaver(new InterpolatedPreviousSaver<{componentType}>());"
                );
                registrationsBuilder.AppendLine(
                    $"            builder.AddSystem(new {systemName}());"
                );

                if (i < methods.Count - 1)
                {
                    registrationsBuilder.AppendLine();
                }
            }

            return $@"{CommonUsings.AsDirectives}

namespace {nameSpace}
{{
    public static class {groupName}WorldBuilderExtensions
    {{
        public static WorldBuilder Add{groupName}(this WorldBuilder builder)
        {{
{registrationsBuilder.ToString().TrimEnd()}
            return builder;
        }}
    }}
}}";
        }
    }

    /// <summary>
    /// Data structure for interpolator method information used in incremental generation
    /// </summary>
    internal class InterpolatorInstallerMethodData
    {
        public MethodDeclarationSyntax MethodDecl { get; }
        public IMethodSymbol MethodSymbol { get; }
        public AttributeData AttributeData { get; }

        public InterpolatorInstallerMethodData(
            MethodDeclarationSyntax methodDecl,
            IMethodSymbol methodSymbol,
            AttributeData attributeData
        )
        {
            MethodDecl = methodDecl;
            MethodSymbol = methodSymbol;
            AttributeData = attributeData;
        }
    }
}
