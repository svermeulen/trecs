using System.Collections.Generic;
using System.Collections.Immutable;
using System.Linq;
using System.Text;
using Microsoft.CodeAnalysis;
using Microsoft.CodeAnalysis.CSharp;

namespace Trecs.SourceGen.Tests;

/// <summary>
/// Verifies that the incremental generators' pipeline outputs are properly cached. The
/// rule we're enforcing: when a generator's transform stage produces an equatable model,
/// running the driver twice against compilations that differ only in irrelevant ways
/// (e.g. an unrelated source file gains a whitespace edit) should mark the
/// <c>SourceOutput</c> step as <see cref="IncrementalStepRunReason.Cached"/> rather than
/// <see cref="IncrementalStepRunReason.Modified"/>. If any pipeline node leaks a Roslyn
/// symbol or syntax node into its output, the driver compares those by reference and
/// re-runs the downstream stages on every compilation pump.
///
/// <para>The harness runs the driver once to warm the cache, edits an unrelated source
/// tree (adds a trailing comment), runs again, and returns the per-step reason map. A
/// passing test asserts the named stages were <c>Cached</c> or <c>Unchanged</c>.</para>
/// </summary>
internal static class IncrementalCacheTestHarness
{
    /// <summary>
    /// Runs the supplied generators against a baseline compilation, then against an
    /// updated compilation differing only in an unrelated source tree. Returns the
    /// tracked-step reasons for the *second* run — the run we expect to hit the cache.
    /// </summary>
    /// <param name="relevantSource">The user source that the generators care about
    /// (e.g. an aspect declaration). Must not be edited between the two runs.</param>
    /// <param name="unrelatedSource">An additional source tree that the generators
    /// should NOT care about. The harness mutates this between the two runs.</param>
    public static CacheRunResult Run(
        IIncrementalGenerator[] generators,
        string relevantSource,
        string unrelatedSource = "namespace __Unrelated { public class Filler {} }"
    )
    {
        var parseOptions = new CSharpParseOptions(LanguageVersion.Preview);

        // Stable across both runs — the generators should observe these as Unchanged.
        var stubsTree = CSharpSyntaxTree.ParseText(TrecsStubs.Source, parseOptions);
        var relevantTree = CSharpSyntaxTree.ParseText(relevantSource, parseOptions);

        // Edited between runs — content shouldn't matter to any tested generator.
        var unrelatedTreeV1 = CSharpSyntaxTree.ParseText(unrelatedSource, parseOptions);
        var unrelatedTreeV2 = CSharpSyntaxTree.ParseText(
            unrelatedSource + "\n// edit between runs to force compilation reuse to differ",
            parseOptions
        );

        var references = System
            .AppDomain.CurrentDomain.GetAssemblies()
            .Where(a => !a.IsDynamic && !string.IsNullOrEmpty(a.Location))
            .Select(a => MetadataReference.CreateFromFile(a.Location))
            .Cast<MetadataReference>()
            .ToList();

        var compilationV1 = CSharpCompilation.Create(
            assemblyName: "Trecs",
            syntaxTrees: new[] { stubsTree, relevantTree, unrelatedTreeV1 },
            references: references,
            options: new CSharpCompilationOptions(
                OutputKind.DynamicallyLinkedLibrary,
                allowUnsafe: true
            )
        );

        // trackIncrementalGeneratorSteps populates GeneratorRunResult.TrackedSteps so we
        // can inspect each pipeline node's run reason.
        var driver = (GeneratorDriver)
            CSharpGeneratorDriver.Create(
                generators: generators.Select(GeneratorExtensions.AsSourceGenerator),
                additionalTexts: ImmutableArray<AdditionalText>.Empty,
                parseOptions: parseOptions,
                optionsProvider: null,
                driverOptions: new GeneratorDriverOptions(
                    disabledOutputs: IncrementalGeneratorOutputKind.None,
                    trackIncrementalGeneratorSteps: true
                )
            );

        // Warmup run — populates the cache.
        driver = driver.RunGenerators(compilationV1);

        // Edit only the unrelated tree. The relevant source is byte-for-byte identical.
        var compilationV2 = compilationV1.ReplaceSyntaxTree(unrelatedTreeV1, unrelatedTreeV2);

        driver = driver.RunGenerators(compilationV2);
        return CollectStepReasons(driver);
    }

    /// <summary>
    /// Variant of <see cref="Run"/> that edits the *relevant* source between the two
    /// runs. The expectation is the inverse: the SourceOutput step should NOT cache,
    /// because the input changed in a way the generator must observe. Used by negative
    /// tests that pin the assertion's tightness — without this, a stray pass on every
    /// generator could indicate the assertion is vacuous.
    /// </summary>
    public static CacheRunResult RunWithRelevantEdit(
        IIncrementalGenerator[] generators,
        string relevantSourceV1,
        string relevantSourceV2
    )
    {
        var parseOptions = new CSharpParseOptions(LanguageVersion.Preview);

        var stubsTree = CSharpSyntaxTree.ParseText(TrecsStubs.Source, parseOptions);
        var v1Tree = CSharpSyntaxTree.ParseText(relevantSourceV1, parseOptions);
        var v2Tree = CSharpSyntaxTree.ParseText(relevantSourceV2, parseOptions);

        var references = System
            .AppDomain.CurrentDomain.GetAssemblies()
            .Where(a => !a.IsDynamic && !string.IsNullOrEmpty(a.Location))
            .Select(a => MetadataReference.CreateFromFile(a.Location))
            .Cast<MetadataReference>()
            .ToList();

        var compilationV1 = CSharpCompilation.Create(
            assemblyName: "Trecs",
            syntaxTrees: new[] { stubsTree, v1Tree },
            references: references,
            options: new CSharpCompilationOptions(
                OutputKind.DynamicallyLinkedLibrary,
                allowUnsafe: true
            )
        );

        var driver = (GeneratorDriver)
            CSharpGeneratorDriver.Create(
                generators: generators.Select(GeneratorExtensions.AsSourceGenerator),
                additionalTexts: ImmutableArray<AdditionalText>.Empty,
                parseOptions: parseOptions,
                optionsProvider: null,
                driverOptions: new GeneratorDriverOptions(
                    disabledOutputs: IncrementalGeneratorOutputKind.None,
                    trackIncrementalGeneratorSteps: true
                )
            );

        driver = driver.RunGenerators(compilationV1);
        var compilationV2 = compilationV1.ReplaceSyntaxTree(v1Tree, v2Tree);
        driver = driver.RunGenerators(compilationV2);

        return CollectStepReasons(driver);
    }

    private static CacheRunResult CollectStepReasons(GeneratorDriver driver)
    {
        var run = driver.GetRunResult();
        var accum = new Dictionary<string, List<IncrementalStepRunReason>>();

        // Roslyn keys TrackedSteps by step name. Multiple distinct pipeline endpoints
        // (e.g. two RegisterSourceOutput calls in one generator, or three generators
        // sharing the default "SourceOutput" name) all show up under the same key —
        // accumulate the reasons across them so the test sees every output, not just
        // the last endpoint's. Otherwise an earlier non-Cached output gets clobbered
        // by a later Cached one and the assertion silently lies.
        foreach (var result in run.Results)
        {
            foreach (var step in result.TrackedSteps)
            {
                if (!accum.TryGetValue(step.Key, out var list))
                {
                    list = new List<IncrementalStepRunReason>();
                    accum[step.Key] = list;
                }
                foreach (var s in step.Value)
                {
                    foreach (var o in s.Outputs)
                        list.Add(o.Reason);
                }
            }
        }

        var stepMap = accum.ToDictionary(
            kv => kv.Key,
            kv => (IReadOnlyList<IncrementalStepRunReason>)kv.Value
        );
        return new CacheRunResult(stepMap);
    }
}

/// <summary>
/// Cache-run summary for the second run of <see cref="IncrementalCacheTestHarness.Run"/>.
/// Exposes per-step reason lists plus a helper for the central assertion: the named
/// stage's outputs are all <see cref="IncrementalStepRunReason.Cached"/> or
/// <see cref="IncrementalStepRunReason.Unchanged"/>.
/// </summary>
internal sealed class CacheRunResult
{
    public IReadOnlyDictionary<string, IReadOnlyList<IncrementalStepRunReason>> Steps { get; }

    public CacheRunResult(
        IReadOnlyDictionary<string, IReadOnlyList<IncrementalStepRunReason>> steps
    )
    {
        Steps = steps;
    }

    /// <summary>
    /// True when every output of <paramref name="stepName"/> in the second run was either
    /// <see cref="IncrementalStepRunReason.Cached"/> (downstream of an Unchanged input
    /// node, so we re-used the prior output) or <see cref="IncrementalStepRunReason.Unchanged"/>
    /// (the step's own output equals its prior output). Either is a cache hit; the
    /// distinction is operational (which node noticed first) not user-facing.
    /// </summary>
    public bool IsCached(string stepName) =>
        Steps.TryGetValue(stepName, out var reasons)
        && reasons.Count > 0
        && reasons.All(r =>
            r == IncrementalStepRunReason.Cached || r == IncrementalStepRunReason.Unchanged
        );

    /// <summary>
    /// True when at least one output of <paramref name="stepName"/> was
    /// <see cref="IncrementalStepRunReason.Modified"/> or
    /// <see cref="IncrementalStepRunReason.New"/>. The complement of <see cref="IsCached"/>,
    /// used by negative tests to confirm the cache assertion is not vacuous — if the
    /// step name were wrong, both <see cref="IsCached"/> and this would return false.
    /// </summary>
    public bool HasMisses(string stepName) =>
        Steps.TryGetValue(stepName, out var reasons)
        && reasons.Any(r =>
            r == IncrementalStepRunReason.Modified || r == IncrementalStepRunReason.New
        );

    /// <summary>
    /// Dumps the full step-reason map for assertion failure messages so the test author
    /// can see which stage broke the cache.
    /// </summary>
    public string Format()
    {
        var sb = new StringBuilder();
        sb.AppendLine("--- Tracked steps (second run) ---");
        foreach (var kv in Steps.OrderBy(k => k.Key))
        {
            sb.Append("  ").Append(kv.Key).Append(": ");
            sb.AppendLine(string.Join(", ", kv.Value));
        }
        return sb.ToString();
    }
}
