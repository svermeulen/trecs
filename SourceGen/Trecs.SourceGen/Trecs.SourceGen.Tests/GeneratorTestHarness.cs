using System.Collections.Generic;
using System.Collections.Immutable;
using System.Linq;
using System.Text;
using Microsoft.CodeAnalysis;
using Microsoft.CodeAnalysis.CSharp;
using Trecs.SourceGen;

namespace Trecs.SourceGen.Tests;

/// <summary>
/// Drives Trecs source generators against an in-memory compilation. Two modes:
///
/// <para><see cref="RunGenerator"/> — runs IncrementalAspectGenerator only and returns its
/// emitted diagnostics. Used by the existing diagnostic-focused tests.</para>
///
/// <para><see cref="Run"/> / <see cref="Run(IIncrementalGenerator[], string)"/> — runs any
/// generator(s) AND re-compiles (input + generated output + Trecs stubs), surfacing both the
/// generators' diagnostics and any C# errors in the generated code. Used by compile-cleanliness
/// tests to catch regressions where a generator emits invalid C#.</para>
/// </summary>
internal static class GeneratorTestHarness
{
    /// <summary>
    /// Runs IncrementalAspectGenerator and returns only its emitted diagnostics. Kept for
    /// the diagnostic-focused tests that don't care about post-generation compilation.
    /// </summary>
    public static ImmutableArray<Diagnostic> RunGenerator(string userSource)
    {
        return Run(new IncrementalAspectGenerator(), userSource).GenDiagnostics;
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

        var driver = CSharpGeneratorDriver.Create(generators);
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
    /// Builds a compilation containing the Trecs stubs + the user's source, ready for the
    /// generator driver to run against. Assembly name is "Trecs" so the source-gen project's
    /// AssemblyFilterHelper.CreateTrecsReferenceCheck doesn't filter out the user types.
    /// </summary>
    private static CSharpCompilation BuildInputCompilation(string userSource)
    {
        var syntaxTrees = new[]
        {
            CSharpSyntaxTree.ParseText(TrecsStubs.Source),
            CSharpSyntaxTree.ParseText(userSource),
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
