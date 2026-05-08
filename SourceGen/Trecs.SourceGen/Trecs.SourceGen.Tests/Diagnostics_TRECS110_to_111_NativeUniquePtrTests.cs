using System.Collections.Immutable;
using System.Linq;
using Microsoft.CodeAnalysis;
using Microsoft.CodeAnalysis.Diagnostics;
using NUnit.Framework;

namespace Trecs.SourceGen.Tests;

/// <summary>
/// Negative tests for the NativeUniquePtr copy-prevention diagnostics group
/// (TRECS110-111). Both are emitted by <see cref="NativeUniquePtrCopyAnalyzer"/>,
/// a <see cref="DiagnosticAnalyzer"/> rather than an
/// <see cref="IIncrementalGenerator"/>, so the tests drive it through
/// <see cref="GeneratorTestHarness.RunAnalyzers"/>.
///
/// Codes covered: 110, 111.
/// </summary>
[TestFixture]
public class Diagnostics_TRECS110_to_111_NativeUniquePtrTests
{
    [Test]
    public void TRECS110_NativeUniquePtrCopiedToLocalFromField()
    {
        // Copying a NativeUniquePtr<T> field into a by-value local bypasses the
        // ref-this enforcement on Set/GetMut, so the analyzer flags it.
        const string source = """
            namespace Sample
            {
                public struct CPayload : Trecs.IEntityComponent { public int X; }

                public class Holder
                {
                    public Trecs.NativeUniquePtr<CPayload> Ptr;

                    public void Foo()
                    {
                        var copy = Ptr;
                    }
                }
            }
            """;

        AssertAnalyzerDiagnostic(
            source,
            "TRECS110",
            new DiagnosticAnalyzer[] { new NativeUniquePtrCopyAnalyzer() }
        );
    }

    [Test]
    public void TRECS111_NativeUniquePtrPassedAsByValueParameter()
    {
        // A by-value parameter of NativeUniquePtr<T> would let the callee mutate
        // a copy of the pointer instead of the owning storage. Must be ref / in / out.
        const string source = """
            namespace Sample
            {
                public struct CPayload : Trecs.IEntityComponent { public int X; }

                public static class Helpers
                {
                    public static void Take(Trecs.NativeUniquePtr<CPayload> ptr) { }
                }
            }
            """;

        AssertAnalyzerDiagnostic(
            source,
            "TRECS111",
            new DiagnosticAnalyzer[] { new NativeUniquePtrCopyAnalyzer() }
        );
    }

    static void AssertAnalyzerDiagnostic(
        string source,
        string expectedId,
        DiagnosticAnalyzer[] analyzers
    )
    {
        var diagnostics = GeneratorTestHarness.RunAnalyzers(analyzers, source);
        var diag = diagnostics.FirstOrDefault(d => d.Id == expectedId);
        Assert.That(
            diag,
            Is.Not.Null,
            $"Expected {expectedId}, got:\n{FormatDiagnostics(diagnostics)}"
        );
    }

    static string FormatDiagnostics(ImmutableArray<Diagnostic> diagnostics)
    {
        if (diagnostics.IsEmpty)
            return "  (none)";
        return string.Join(
            "\n",
            diagnostics.Select(d =>
                $"  {d.Severity} {d.Id} at {d.Location.GetLineSpan()}: {d.GetMessage()}"
            )
        );
    }
}
