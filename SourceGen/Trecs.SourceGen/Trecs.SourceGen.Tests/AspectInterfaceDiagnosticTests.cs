using System.Linq;
using NUnit.Framework;

namespace Trecs.SourceGen.Tests;

[TestFixture]
public class AspectInterfaceDiagnosticTests
{
    // Aspect interfaces must be declared partial — the generator attaches an emitted partial
    // that declares the ref-returning property contract. Without partial, the user's interface
    // would have no members the generic helpers can call.
    [Test]
    public void NonPartial_AspectInterface_EmitsMustBePartial()
    {
        const string source = """
            namespace Sample
            {
                public partial struct CFoo : Trecs.IEntityComponent { public int V; }

                // Missing 'partial' — should trigger TRECS020
                public interface IFooAspect : Trecs.IAspect, Trecs.IRead<CFoo> { }
            }
            """;

        var diagnostics = GeneratorTestHarness.RunGenerator(source);
        var partialDiag = diagnostics.FirstOrDefault(d => d.Id == "TRECS020");

        Assert.That(partialDiag, Is.Not.Null, FormatDiagnostics(diagnostics));
        Assert.That(partialDiag!.GetMessage(), Does.Contain("IFooAspect"));
    }

    // A user-visible cycle in the aspect-interface hierarchy (A inherits B, B inherits A) is
    // caught by C# itself (CS0529 "Inherited interface causes a cycle"). The generator's
    // defensive cycle guard in AspectInterfaceParser.ExtractInterfaceComponentsRecursive still
    // exists to protect the recursive walker against malformed semantic graphs, but there is
    // no source the user can write that exercises it end-to-end — C# rejects the compilation
    // before the generator runs. Not tested here by design; see TRECS021's descriptor.

    // A non-cyclic multi-level aspect-interface chain should succeed (no error diagnostics).
    // This is a safety net against a regression that would make the chain walker emit a
    // spurious cycle diagnostic on a valid hierarchy.
    [Test]
    public void NonCyclicChain_CompilesWithoutDiagnostics()
    {
        const string source = """
            namespace Sample
            {
                public partial struct CFoo : Trecs.IEntityComponent { public int V; }
                public partial struct CBar : Trecs.IEntityComponent { public int V; }

                public partial interface IA : Trecs.IAspect, Trecs.IRead<CFoo> { }
                public partial interface IB : IA, Trecs.IRead<CBar> { }

                public partial struct ChainAspect : IB { }
            }
            """;

        var diagnostics = GeneratorTestHarness.RunGenerator(source);

        Assert.That(
            diagnostics.Any(d => d.Severity == Microsoft.CodeAnalysis.DiagnosticSeverity.Error),
            Is.False,
            FormatDiagnostics(diagnostics)
        );
    }

    private static string FormatDiagnostics(
        System.Collections.Generic.IEnumerable<Microsoft.CodeAnalysis.Diagnostic> diagnostics
    )
    {
        var list = diagnostics.ToList();
        if (list.Count == 0)
            return "No diagnostics were emitted.";
        return "Actual diagnostics:\n"
            + string.Join("\n", list.Select(d => $"  {d.Id}: {d.GetMessage()}"));
    }
}
