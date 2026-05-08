using System.Collections.Generic;
using System.Collections.Immutable;
using System.Linq;
using System.Text;
using System.Threading;
using Microsoft.CodeAnalysis;
using Microsoft.CodeAnalysis.CSharp;
using Microsoft.CodeAnalysis.Diagnostics;
using Trecs.SourceGen;

namespace Trecs.SourceGen.Tests;

/// <summary>
/// Drives Trecs source generators (and DiagnosticAnalyzers) against an in-memory
/// compilation. Three modes:
///
/// <para><see cref="RunGenerator"/> — runs AspectGenerator only and returns its
/// emitted diagnostics. Used by the existing diagnostic-focused tests.</para>
///
/// <para><see cref="Run"/> / <see cref="Run(IIncrementalGenerator[], string)"/> — runs any
/// generator(s) AND re-compiles (input + generated output + Trecs stubs), surfacing both the
/// generators' diagnostics and any C# errors in the generated code. Used by compile-cleanliness
/// tests to catch regressions where a generator emits invalid C#.</para>
///
/// <para><see cref="RunAnalyzers"/> — runs one or more <see cref="DiagnosticAnalyzer"/>s
/// against an in-memory compilation via Roslyn's <see cref="CompilationWithAnalyzers"/> and
/// returns the analyzer-emitted diagnostics. Used by the diagnostic tests for descriptors
/// emitted by analyzers (TRECS070, TRECS110, TRECS111) rather than incremental generators.</para>
/// </summary>
internal static class GeneratorTestHarness
{
    /// <summary>
    /// Runs AspectGenerator and returns only its emitted diagnostics. Kept for
    /// the diagnostic-focused tests that don't care about post-generation compilation.
    /// </summary>
    public static ImmutableArray<Diagnostic> RunGenerator(string userSource)
    {
        return Run(new AspectGenerator(), userSource).GenDiagnostics;
    }

    /// <summary>
    /// Runs a single generator and returns both its diagnostics and the post-generation
    /// compile diagnostics. The latter is what compile-cleanliness tests assert on.
    /// </summary>
    public static GeneratorRun Run(IIncrementalGenerator generator, string userSource)
    {
        return Run(new[] { generator }, userSource);
    }

    /// <summary>
    /// Runs multiple generators in a single driver pass. Some scenarios route through
    /// IterationAttributeRouting and need both the aspect generator and a foreach generator
    /// active at the same time.
    /// </summary>
    public static GeneratorRun Run(IIncrementalGenerator[] generators, string userSource)
    {
        var compilation = BuildInputCompilation(userSource);

        // Match the parse options used for the input compilation so generated trees
        // (which the driver re-parses) don't trip "Inconsistent language versions"
        // when the input trees opt into Preview for generic attributes.
        var driverParseOptions = new CSharpParseOptions(LanguageVersion.Preview);
        var driver = CSharpGeneratorDriver.Create(
            generators: generators.Select(GeneratorExtensions.AsSourceGenerator).ToImmutableArray(),
            additionalTexts: ImmutableArray<AdditionalText>.Empty,
            parseOptions: driverParseOptions,
            optionsProvider: null
        );
        driver = (CSharpGeneratorDriver)
            driver.RunGeneratorsAndUpdateCompilation(
                compilation,
                out var outputCompilation,
                out var generationDiagnostics
            );

        var runResult = driver.GetRunResult();
        var generatedTrees = runResult
            .Results.SelectMany(r => r.GeneratedSources)
            .Select(s => s.SyntaxTree)
            .ToImmutableArray();

        var compileDiagnostics = outputCompilation.GetDiagnostics();

        return new GeneratorRun(
            GenDiagnostics: runResult.Diagnostics,
            CompileDiagnostics: compileDiagnostics,
            GenerationDiagnostics: generationDiagnostics,
            GeneratedTrees: generatedTrees
        );
    }

    /// <summary>
    /// Runs one or more <see cref="DiagnosticAnalyzer"/>s against an in-memory compilation
    /// of the Trecs stubs + the user's source. Returns only the analyzer-emitted diagnostics
    /// (filtered to the analyzers' supported descriptor IDs) — base compile diagnostics are
    /// not surfaced, since analyzer-focused tests assert on the analyzer's own output.
    ///
    /// <para>Some of these diagnostics (TRECS110, TRECS111) are emitted with severity
    /// <see cref="DiagnosticSeverity.Error"/>, which would cause Roslyn to fail the
    /// compilation if the analyzer were plugged in via the IDE. Tests intentionally feed
    /// invalid input to assert the analyzer fires, so we do not assert on absence of errors
    /// here.</para>
    /// </summary>
    public static ImmutableArray<Diagnostic> RunAnalyzers(
        DiagnosticAnalyzer[] analyzers,
        string userSource
    )
    {
        var compilation = BuildInputCompilation(userSource);

        var analyzerArray = analyzers.ToImmutableArray();
        var supportedIds = analyzerArray
            .SelectMany(a => a.SupportedDiagnostics)
            .Select(d => d.Id)
            .ToImmutableHashSet();

        var compilationWithAnalyzers = compilation.WithAnalyzers(analyzerArray);
        var diagnostics = compilationWithAnalyzers
            .GetAnalyzerDiagnosticsAsync(CancellationToken.None)
            .GetAwaiter()
            .GetResult();

        // Filter to the analyzers' supported IDs so test assertions don't have to
        // care about diagnostics emitted by unrelated analyzers (none expected today,
        // but defensive against future additions to the same project).
        return diagnostics.Where(d => supportedIds.Contains(d.Id)).ToImmutableArray();
    }

    /// <summary>
    /// Builds a compilation containing the Trecs stubs + the user's source, ready for the
    /// generator driver to run against. Assembly name is "Trecs" so the source-gen project's
    /// AssemblyFilterHelper.CreateTrecsReferenceCheck doesn't filter out the user types.
    /// </summary>
    private static CSharpCompilation BuildInputCompilation(string userSource)
    {
        // The stubs and user sources use C# 11 generic attributes. The pinned
        // Microsoft.CodeAnalysis.CSharp 4.3.0 still treats them as a preview feature,
        // so use LanguageVersion.Preview rather than Latest.
        var parseOptions = new CSharpParseOptions(LanguageVersion.Preview);
        var syntaxTrees = new[]
        {
            CSharpSyntaxTree.ParseText(TrecsStubs.Source, parseOptions),
            CSharpSyntaxTree.ParseText(userSource, parseOptions),
        };

        // Reference assemblies provided by the test SDK. Without these, even `object` won't
        // resolve. Loading every loaded assembly is overkill but cheap and covers anything
        // the stubs or generated output might reach for (System.Runtime, etc.).
        var references = System
            .AppDomain.CurrentDomain.GetAssemblies()
            .Where(a => !a.IsDynamic && !string.IsNullOrEmpty(a.Location))
            .Select(a => MetadataReference.CreateFromFile(a.Location))
            .Cast<MetadataReference>()
            .ToList();

        return CSharpCompilation.Create(
            assemblyName: "Trecs",
            syntaxTrees: syntaxTrees,
            references: references,
            // AllowUnsafeBlocks because some emitted helpers (e.g. UnmanagedUtil) use unsafe.
            options: new CSharpCompilationOptions(
                OutputKind.DynamicallyLinkedLibrary,
                allowUnsafe: true
            )
        );
    }
}

/// <summary>
/// Result of a generator run: the generators' own diagnostics, the post-generation compile
/// diagnostics, and the generated syntax trees (for inspection / debugging).
/// </summary>
internal sealed record GeneratorRun(
    ImmutableArray<Diagnostic> GenDiagnostics,
    ImmutableArray<Diagnostic> CompileDiagnostics,
    ImmutableArray<Diagnostic> GenerationDiagnostics,
    ImmutableArray<Microsoft.CodeAnalysis.SyntaxTree> GeneratedTrees
)
{
    /// <summary>
    /// Compile errors only (severity Error). The primary assertion target for
    /// compile-cleanliness tests — warnings are noisy and not load-bearing here.
    /// </summary>
    public IEnumerable<Diagnostic> CompileErrors =>
        CompileDiagnostics.Where(d => d.Severity == DiagnosticSeverity.Error);

    /// <summary>
    /// Generator diagnostics with severity Error (e.g. user wrote something that fails a
    /// TRECS### validation). Compile-cleanliness tests typically assert this is empty too,
    /// since a TRECS### error usually means the generator skipped emission.
    /// </summary>
    public IEnumerable<Diagnostic> GenErrors =>
        GenDiagnostics.Where(d => d.Severity == DiagnosticSeverity.Error);

    /// <summary>
    /// Multi-line dump of every diagnostic and every generated file, suitable for use as
    /// an NUnit Assert message. When a test fails, this is what tells you why.
    /// </summary>
    public string Format()
    {
        var sb = new StringBuilder();

        sb.AppendLine("--- Generator diagnostics ---");
        if (GenDiagnostics.IsEmpty)
        {
            sb.AppendLine("  (none)");
        }
        else
        {
            foreach (var d in GenDiagnostics)
                sb.AppendLine($"  {d.Severity} {d.Id}: {d.GetMessage()}");
        }

        sb.AppendLine("--- Compile diagnostics ---");
        var compileErrorsAndWarnings = CompileDiagnostics
            .Where(d => d.Severity >= DiagnosticSeverity.Warning)
            .ToList();
        if (compileErrorsAndWarnings.Count == 0)
        {
            sb.AppendLine("  (none)");
        }
        else
        {
            foreach (var d in compileErrorsAndWarnings)
                sb.AppendLine(
                    $"  {d.Severity} {d.Id} at {d.Location.GetLineSpan()}: {d.GetMessage()}"
                );
        }

        sb.AppendLine($"--- Generated files ({GeneratedTrees.Length}) ---");
        foreach (var tree in GeneratedTrees)
        {
            sb.AppendLine($"// === {tree.FilePath} ===");
            sb.AppendLine(tree.ToString());
        }

        return sb.ToString();
    }
}
