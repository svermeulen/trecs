using System.Linq;
using Microsoft.CodeAnalysis;
using NUnit.Framework;

namespace Trecs.SourceGen.Tests;

/// <summary>
/// Tests for the catch-all diagnostics — TRECS015 (CouldNotResolveSymbol),
/// TRECS996 (SourceGenerationError), and TRECS999 (UnhandledSourceGenError).
///
/// These descriptors guard the generator's error-recovery paths: a malformed
/// symbol, an unexpected exception during validation, or a crash inside the
/// emit pipeline should surface as one of these rather than a Roslyn-level
/// generator crash. Only TRECS015 has a reachable user-code trigger today;
/// the other two are documented as effectively unreachable below.
/// </summary>
[TestFixture]
public class Diagnostics_TRECS015_996_999_CatchAllTests
{
    [Test]
    public void TRECS015_PointerFromWorldFieldOnJob()
    {
        // JobGenerator.ScanFromWorldFields casts the field's type to
        // INamedTypeSymbol. Pointer types resolve to IPointerTypeSymbol, so
        // the cast yields null and the generator emits TRECS015 against the
        // field's type syntax.
        const string source = """
            namespace Sample
            {
                public partial struct CPos : Trecs.IEntityComponent { public float X; }

                public unsafe partial struct PointerJob : Unity.Jobs.IJob
                {
                    [Trecs.FromWorld]
                    public int* Ptr;

                    [Trecs.FromWorld]
                    public Trecs.NativeComponentBufferRead<CPos> Buffer;

                    public void Execute() { }
                }
            }
            """;

        AssertDiagnostic(
            source,
            "TRECS015",
            new IIncrementalGenerator[] { new JobGenerator(), new EntityComponentGenerator() }
        );
    }

    // TRECS996 (SourceGenerationError) wraps every generator's validate/emit
    // pipeline via ErrorRecovery.TryExecute and the inline catch blocks in the
    // transform stages. Triggering it requires an *internal* exception inside
    // the generator (e.g. a NullReferenceException), not a user-code shape.
    // Every reachable user-code path we audited either produces a structured
    // diagnostic or completes successfully. Adding a synthetic test would
    // require injecting a bug into the generator, which defeats the purpose
    // of the descriptor. Skipped intentionally — the descriptor exists for
    // resilience against future generator regressions.

    // TRECS999 (UnhandledSourceGenError) is reported only by
    // GeneratorBase.CreateSafeProductionAction. That helper is defined but
    // currently has zero call sites in the source-gen project. The descriptor
    // is preserved for the same resilience reason as TRECS996. Skipped
    // intentionally — no user-code shape can reach the emit site.

    static void AssertDiagnostic(
        string source,
        string expectedId,
        IIncrementalGenerator[] generators
    )
    {
        var run = GeneratorTestHarness.Run(generators, source);
        var diag = run.GenDiagnostics.FirstOrDefault(d => d.Id == expectedId);
        Assert.That(diag, Is.Not.Null, $"Expected {expectedId}, got:\n{run.Format()}");
    }
}
