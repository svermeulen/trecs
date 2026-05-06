using System.Linq;
using Microsoft.CodeAnalysis;
using NUnit.Framework;

namespace Trecs.SourceGen.Tests;

/// <summary>
/// Negative tests for the Hook method migration diagnostics group (TRECS061-062).
/// AutoSystemGenerator detects users still on the legacy hook names — non-partial
/// Ready() and partial-method-with-old-name — and nudges them onto the new
/// OnReady / OnDeclareDependencies form.
///
/// Codes covered: 061, 062.
/// </summary>
[TestFixture]
public class Diagnostics_TRECS060_to_062_HookMigrationTests
{
    [Test]
    public void TRECS061_NonPartialReadyHook()
    {
        // The legacy `void Ready()` on a system class is no longer wired up; the
        // generator nudges the user onto `partial void OnReady()`.
        const string source = """
            namespace Sample
            {
                public partial class MySystem : Trecs.ISystem
                {
                    public void Execute() { }

                    void Ready() { }
                }
            }
            """;

        AssertDiagnostic(source, "TRECS061");
    }

    [Test]
    public void TRECS062_PartialMethodWithOldHookName()
    {
        // Legacy partial-method names — `partial void Ready()` should now be
        // `partial void OnReady()`. The validator flags the rename.
        const string source = """
            namespace Sample
            {
                public partial class MySystem : Trecs.ISystem
                {
                    public void Execute() { }

                    partial void Ready();
                }
            }
            """;

        AssertDiagnostic(source, "TRECS062");
    }

    static void AssertDiagnostic(string source, string expectedId)
    {
        var run = GeneratorTestHarness.Run(
            new IIncrementalGenerator[]
            {
                new AutoSystemGenerator(),
                new IncrementalEntityComponentGenerator(),
            },
            source
        );
        var diag = run.GenDiagnostics.FirstOrDefault(d => d.Id == expectedId);
        Assert.That(
            diag,
            Is.Not.Null,
            $"Expected {expectedId}, got:\n{run.Format()}"
        );
    }
}
