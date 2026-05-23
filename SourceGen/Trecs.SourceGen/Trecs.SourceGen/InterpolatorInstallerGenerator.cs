#nullable enable

using System;
using System.Collections.Generic;
using System.Linq;
using System.Text;
using Microsoft.CodeAnalysis;
using Microsoft.CodeAnalysis.CSharp.Syntax;
using Trecs.SourceGen.Performance;
using Trecs.SourceGen.Shared;

namespace Trecs.SourceGen
{
    /// <summary>
    /// Incremental source generator that emits <c>WorldBuilder</c> extension methods for
    /// registering interpolation systems and their previous-value savers. One generated
    /// extension class per <c>groupName</c> (the attribute's second ctor arg).
    ///
    /// <para>Pipeline shape: the transform produces a value-equatable per-method
    /// <see cref="InterpolatorInstallerMethodModel"/>. <c>Collect()</c> brings them all
    /// together so they can be grouped by group name — that combine is inherent to the
    /// "one extension class per group" emission shape. The result is wrapped in an
    /// <see cref="EquatableArray{T}"/> so unrelated edits (formatting, comments) that
    /// produce the same logical model array don't force the terminal stage to re-run.
    /// The old <c>.Combine(CompilationProvider)</c> is gone — the compilation parameter
    /// was passed to the terminal stage but never read.</para>
    /// </summary>
    [Generator]
    public class InterpolatorInstallerGenerator : IIncrementalGenerator
    {
        public void Initialize(IncrementalGeneratorInitializationContext context)
        {
            var hasTrecsReference = AssemblyFilterHelper.CreateTrecsReferenceCheck(context);

            var perMethodRaw = context
                .SyntaxProvider.ForAttributeWithMetadataName(
                    "Trecs.GenerateInterpolatorSystemAttribute",
                    predicate: static (node, _) => node is MethodDeclarationSyntax,
                    transform: static (ctx, _) => BuildModel(ctx)
                )
                .Where(static m => m is not null)
                .Select(static (m, _) => m!.Value);
            var perMethod = AssemblyFilterHelper.FilterByTrecsReference(
                perMethodRaw,
                hasTrecsReference
            );

            // Collect + wrap-in-EquatableArray gives the downstream node structural-equality
            // semantics so the terminal stage caches when the array of per-method models is
            // unchanged. The per-method models cache individually upstream; this combine
            // only re-runs when something observable about *some* method actually changed.
            var allMethods = perMethod
                .Collect()
                .Select(
                    static (arr, _) =>
                        new EquatableArray<InterpolatorInstallerMethodModel>(arr.ToArray())
                );

            context.RegisterSourceOutput(
                allMethods,
                static (spc, models) => GenerateInterpolatorExtensions(spc, models)
            );
        }

        private static InterpolatorInstallerMethodModel? BuildModel(
            GeneratorAttributeSyntaxContext context
        )
        {
            if (context.TargetSymbol is not IMethodSymbol methodSymbol || !methodSymbol.IsStatic)
                return null;
            if (methodSymbol.Parameters.Length == 0)
                return null;

            var attribute = context.Attributes.FirstOrDefault();
            if (attribute is null)
                return null;

            // The attribute layout is (className, groupName). systemName is the first
            // ctor arg — same string used by InterpolatorJobGenerator to name the emitted
            // class. groupName drives the extension class name and groups multiple
            // interpolators under one WorldBuilder.Add{groupName}() call.
            string? systemName =
                attribute.ConstructorArguments.Length >= 1
                    ? attribute.ConstructorArguments[0].Value as string
                    : null;
            string? groupName =
                attribute.ConstructorArguments.Length >= 2
                    ? attribute.ConstructorArguments[1].Value as string
                    : null;

            if (string.IsNullOrEmpty(systemName) || string.IsNullOrEmpty(groupName))
                return null;

            return new InterpolatorInstallerMethodModel(
                SystemName: systemName!,
                GroupName: groupName!,
                Namespace: PerformanceCache.GetDisplayString(methodSymbol.ContainingNamespace),
                ComponentTypeDisplay: PerformanceCache.GetDisplayString(
                    methodSymbol.Parameters[0].Type
                ),
                // The hint-file name is derived from any *containing type* in the group so
                // every method in the group lands in the same generated file path; the
                // group's first-seen method's containing type wins.
                ContainingTypeFileName: SymbolAnalyzer.GetSafeFileName(
                    methodSymbol.ContainingType,
                    $"{groupName}WorldBuilderExtensions"
                )
            );
        }

        private static void GenerateInterpolatorExtensions(
            SourceProductionContext context,
            EquatableArray<InterpolatorInstallerMethodModel> methods
        )
        {
            if (methods.Length == 0)
                return;

            try
            {
                using var _timer_ = SourceGenTimer.Time("InterpolatorInstallerGenerator.Total");
                SourceGenLogger.Log(
                    $"[InterpolatorInstallerGenerator] Processing {methods.Length} interpolator methods"
                );

                // GroupBy on the value-equatable model groups by group name; FirstOrDefault
                // members tie-broken by enumeration order, same as the legacy implementation.
                var groups = methods
                    .GroupBy(m => m.GroupName)
                    .Where(g => !string.IsNullOrEmpty(g.Key))
                    .ToList();

                foreach (var group in groups)
                {
                    var groupMethods = group.ToList();
                    var first = groupMethods[0];

                    var source = GenerateExtension(first.Namespace, group.Key, groupMethods);
                    context.AddSource(first.ContainingTypeFileName, source);
                    SourceGenLogger.WriteGeneratedFile(first.ContainingTypeFileName, source);
                }
            }
            catch (Exception ex)
            {
                SourceGenLogger.Log(
                    $"[InterpolatorInstallerGenerator] Error generating extensions: {ex.Message}"
                );

                var diagnostic = Diagnostic.Create(
                    DiagnosticDescriptors.CouldNotResolveSymbol,
                    Location.None,
                    $"InterpolatorExtension: {ex.Message}"
                );
                context.ReportDiagnostic(diagnostic);
            }
        }

        private static string GenerateExtension(
            string nameSpace,
            string groupName,
            List<InterpolatorInstallerMethodModel> methods
        )
        {
            var registrationsBuilder = new StringBuilder();

            for (int i = 0; i < methods.Count; i++)
            {
                var method = methods[i];
                registrationsBuilder.AppendLine(
                    $"            builder.AddInterpolatedPreviousSaver(new InterpolatedPreviousSaver<{method.ComponentTypeDisplay}>());"
                );
                registrationsBuilder.AppendLine(
                    $"            builder.AddSystem(new {method.SystemName}());"
                );

                if (i < methods.Count - 1)
                {
                    registrationsBuilder.AppendLine();
                }
            }

            return $@"{CommonUsings.AsDirectives}

namespace {nameSpace}
{{
    {GeneratedCodeAttributes.Line}
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
    /// Value-equality model for a single <c>[GenerateInterpolatorSystem]</c> method,
    /// holding everything the terminal stage needs to emit the registration for that
    /// method. No symbols / syntax — the cache can hit per-method and the Collect-wrapped
    /// downstream can detect when nothing in the array has changed.
    /// </summary>
    internal readonly record struct InterpolatorInstallerMethodModel(
        string SystemName,
        string GroupName,
        string Namespace,
        string ComponentTypeDisplay,
        string ContainingTypeFileName
    );
}
