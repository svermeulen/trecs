using System;
using System.Collections.Generic;
using System.Collections.Immutable;
using System.Linq;
using Microsoft.CodeAnalysis;
using Microsoft.CodeAnalysis.CSharp;

namespace Trecs.SourceGen.Tests;

/// <summary>
/// Minimal test harness for running an <see cref="IIncrementalGenerator"/>
/// against a source snippet and inspecting the diagnostics it reports.
/// </summary>
internal static class GeneratorTestHarness
{
    /// <summary>
    /// Compile <paramref name="source"/> with <paramref name="generator"/> attached
    /// and return any diagnostics the generator reported (plus any compilation
    /// errors in the snippet itself).
    /// </summary>
    public static ImmutableArray<Diagnostic> RunGenerator(
        IIncrementalGenerator generator,
        string source,
        // Name matches what AssemblyFilterHelper.HasTrecsReference expects so
        // filtered providers run the generator against our snippet. Override
        // only if a test specifically wants to exercise the "no Trecs
        // reference → generator no-op" path.
        string assemblyName = "Trecs"
    )
    {
        var syntaxTree = CSharpSyntaxTree.ParseText(source);

        // Reference the framework's core types so `[AttributeUsage]`, `Attribute`,
        // `int`, etc. resolve. We use the current executing assembly's reference
        // set so the test binary's runtime is the source of truth.
        var references = AppDomain
            .CurrentDomain.GetAssemblies()
            .Where(a => !a.IsDynamic && !string.IsNullOrEmpty(a.Location))
            .Select(a => MetadataReference.CreateFromFile(a.Location))
            .Cast<MetadataReference>()
            .ToList();

        var compilation = CSharpCompilation.Create(
            assemblyName,
            new[] { syntaxTree },
            references,
            new CSharpCompilationOptions(OutputKind.DynamicallyLinkedLibrary)
        );

        var driver = CSharpGeneratorDriver.Create(generator);
        driver = (CSharpGeneratorDriver)driver.RunGeneratorsAndUpdateCompilation(
            compilation,
            out var outputCompilation,
            out var genDiagnostics
        );

        // Return both generator-reported diagnostics and snippet compilation
        // errors so a test that writes a bad snippet fails loudly rather than
        // silently producing zero generator diagnostics.
        var all = new List<Diagnostic>();
        all.AddRange(genDiagnostics);
        all.AddRange(outputCompilation.GetDiagnostics());
        return all.ToImmutableArray();
    }

    /// <summary>
    /// Equivalent to <see cref="RunGenerator"/>, but filters out the compilation
    /// noise that tests usually don't care about (CS0... warnings, nullable
    /// warnings, etc.) so assertions can focus on TRECS diagnostics.
    /// </summary>
    public static ImmutableArray<Diagnostic> RunGeneratorAndFilterToTrecs(
        IIncrementalGenerator generator,
        string source
    )
    {
        return RunGenerator(generator, source)
            .Where(d => d.Id.StartsWith("TRECS"))
            .ToImmutableArray();
    }
}
