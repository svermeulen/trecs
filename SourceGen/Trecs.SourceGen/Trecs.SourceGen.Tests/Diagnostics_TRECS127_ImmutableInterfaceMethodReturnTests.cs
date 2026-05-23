using System.Collections.Immutable;
using System.Linq;
using Microsoft.CodeAnalysis;
using Microsoft.CodeAnalysis.Diagnostics;
using NUnit.Framework;

namespace Trecs.SourceGen.Tests;

/// <summary>
/// Tests for <see cref="ImmutableAnalyzer"/> — TRECS127 (warn when a method
/// declared on an <c>[Immutable]</c> interface returns a type that is not
/// provably immutable per the existing TRECS126 safe-type walker, unless the
/// method is opted out with <c>[Trecs.AllowMutableReturn]</c>).
/// </summary>
[TestFixture]
public class Diagnostics_TRECS127_ImmutableInterfaceMethodReturnTests
{
    // ───────────── Safe return types ─────────────

    [Test]
    public void TRECS127_PrimitiveReturn_DoesNotFire()
    {
        const string source = """
            namespace Sample
            {
                [Trecs.Immutable]
                public interface IGood
                {
                    int Compute(int x);
                }
            }
            """;

        AssertDoesNotFire(source, "TRECS127");
    }

    [Test]
    public void TRECS127_StringReturn_DoesNotFire()
    {
        const string source = """
            namespace Sample
            {
                [Trecs.Immutable]
                public interface IGood
                {
                    string Describe();
                }
            }
            """;

        AssertDoesNotFire(source, "TRECS127");
    }

    [Test]
    public void TRECS127_EnumReturn_DoesNotFire()
    {
        const string source = """
            namespace Sample
            {
                public enum Mode { A, B, C }

                [Trecs.Immutable]
                public interface IGood
                {
                    Mode GetMode();
                }
            }
            """;

        AssertDoesNotFire(source, "TRECS127");
    }

    [Test]
    public void TRECS127_ReadonlyStructReturn_DoesNotFire()
    {
        const string source = """
            namespace Sample
            {
                public readonly struct Snap { public readonly int X; }

                [Trecs.Immutable]
                public interface IGood
                {
                    Snap GetSnap();
                }
            }
            """;

        AssertDoesNotFire(source, "TRECS127");
    }

    [Test]
    public void TRECS127_ImmutableClassReturn_DoesNotFire()
    {
        const string source = """
            namespace Sample
            {
                [Trecs.Immutable]
                public sealed class Inner { public readonly int X; }

                [Trecs.Immutable]
                public interface IGood
                {
                    Inner Get();
                }
            }
            """;

        AssertDoesNotFire(source, "TRECS127");
    }

    [Test]
    public void TRECS127_ImmutableInterfaceReturn_DoesNotFire()
    {
        const string source = """
            namespace Sample
            {
                [Trecs.Immutable]
                public interface IReadOnlyInner { int X { get; } }

                [Trecs.Immutable]
                public interface IGood
                {
                    IReadOnlyInner GetInner();
                }
            }
            """;

        AssertDoesNotFire(source, "TRECS127");
    }

    [Test]
    public void TRECS127_NativeArrayReadOnlyReturn_DoesNotFire()
    {
        // Picks up the Unity allowlist via the shared IsSafeType walker.
        const string source = """
            namespace Sample
            {
                [Trecs.Immutable]
                public interface IGood
                {
                    Unity.Collections.NativeArray<int>.ReadOnly GetTriangles();
                }
            }
            """;

        AssertDoesNotFire(source, "TRECS127");
    }

    [Test]
    public void TRECS127_IReadOnlyListReturn_DoesNotFire()
    {
        const string source = """
            namespace Sample
            {
                [Trecs.Immutable]
                public interface IGood
                {
                    System.Collections.Generic.IReadOnlyList<int> GetItems();
                }
            }
            """;

        AssertDoesNotFire(source, "TRECS127");
    }

    // ───────────── Non-safe return types ─────────────

    [Test]
    public void TRECS127_MutableClassReturn_Fires()
    {
        const string source = """
            namespace Sample
            {
                public sealed class Mutable { public int X; }

                [Trecs.Immutable]
                public interface IBad
                {
                    Mutable LookUp(int id);
                }
            }
            """;

        AssertFires(source, "TRECS127");
    }

    [Test]
    public void TRECS127_ListReturn_Fires()
    {
        const string source = """
            namespace Sample
            {
                [Trecs.Immutable]
                public interface IBad
                {
                    System.Collections.Generic.List<int> GetItems();
                }
            }
            """;

        AssertFires(source, "TRECS127");
    }

    [Test]
    public void TRECS127_PlainInterfaceReturn_Fires()
    {
        // The returned interface isn't [Immutable], so callers might mutate
        // through it.
        const string source = """
            namespace Sample
            {
                public interface IPlain { int X { get; set; } }

                [Trecs.Immutable]
                public interface IBad
                {
                    IPlain GetIt();
                }
            }
            """;

        AssertFires(source, "TRECS127");
    }

    [Test]
    public void TRECS127_ArrayReturn_Fires()
    {
        // Arrays are never safe (elements can be reassigned).
        const string source = """
            namespace Sample
            {
                [Trecs.Immutable]
                public interface IBad
                {
                    int[] GetValues();
                }
            }
            """;

        AssertFires(source, "TRECS127");
    }

    // ───────────── Opt-out attribute ─────────────

    [Test]
    public void TRECS127_AllowMutableReturn_SuppressesNonSafe()
    {
        const string source = """
            namespace Sample
            {
                public sealed class Mutable { public int X; }

                [Trecs.Immutable]
                public interface IGood
                {
                    [Trecs.AllowMutableReturn]
                    Mutable LookUp(int id);
                }
            }
            """;

        AssertDoesNotFire(source, "TRECS127");
    }

    [Test]
    public void TRECS127_AllowMutableReturn_OnSafeReturn_DoesNotFire()
    {
        // The attribute is harmless on a safe return type — the analyzer
        // wouldn't fire anyway, and we shouldn't surprise users by
        // synthesizing a new diagnostic about a redundant opt-out.
        const string source = """
            namespace Sample
            {
                [Trecs.Immutable]
                public interface IGood
                {
                    [Trecs.AllowMutableReturn]
                    int Compute();
                }
            }
            """;

        AssertDoesNotFire(source, "TRECS127");
    }

    [Test]
    public void TRECS127_PartiallyAnnotated_OnlyUnannotatedFires()
    {
        // Mixed-bag interface: two non-safe returns, one annotated, one not.
        // Only the un-annotated method should fire.
        const string source = """
            namespace Sample
            {
                public sealed class Mutable { public int X; }

                [Trecs.Immutable]
                public interface IMixed
                {
                    [Trecs.AllowMutableReturn]
                    Mutable GetAnnotated();

                    Mutable GetUnannotated();
                }
            }
            """;

        var diagnostics = GeneratorTestHarness.RunAnalyzers(
            new DiagnosticAnalyzer[] { new ImmutableAnalyzer() },
            source
        );
        var firings = diagnostics.Where(d => d.Id == "TRECS127").ToArray();
        Assert.That(
            firings,
            Has.Length.EqualTo(1),
            $"Expected exactly one TRECS127:\n{Format(diagnostics)}"
        );
        Assert.That(firings[0].GetMessage(), Does.Contain("GetUnannotated"));
    }

    // ───────────── Methods/methods-shaped that must NOT fire ─────────────

    [Test]
    public void TRECS127_VoidReturn_DoesNotFire()
    {
        const string source = """
            namespace Sample
            {
                [Trecs.Immutable]
                public interface IGood
                {
                    void Notify();
                    void NotifyWithArg(int x);
                }
            }
            """;

        AssertDoesNotFire(source, "TRECS127");
    }

    [Test]
    public void TRECS127_PropertyGetterOfMutableType_DoesNotFire()
    {
        // Property accessors have MethodKind == PropertyGet/Set, not Ordinary
        // — they are not in scope for TRECS127. Property *types* are still
        // audited by TRECS126.
        const string source = """
            namespace Sample
            {
                public sealed class Mutable { public int X; }

                [Trecs.Immutable]
                public interface IBad
                {
                    Mutable Thing { get; }
                }
            }
            """;

        AssertDoesNotFire(source, "TRECS127");
    }

    [Test]
    public void TRECS127_IndexerOfMutableType_DoesNotFire()
    {
        // Indexers manifest as a property named "this[]" with get/set
        // accessor methods — MethodKind == PropertyGet, not Ordinary.
        const string source = """
            namespace Sample
            {
                public sealed class Mutable { public int X; }

                [Trecs.Immutable]
                public interface IBad
                {
                    Mutable this[int i] { get; }
                }
            }
            """;

        AssertDoesNotFire(source, "TRECS127");
    }

    // ───────────── Class-route is out of scope ─────────────

    [Test]
    public void TRECS127_OnImmutableClass_DoesNotFire()
    {
        // Class-route [Immutable] is not in scope for TRECS127 — methods on
        // [Immutable] classes have a different audit (structural; private
        // fields exempt). Adding a method-return check would be redundant
        // with TRECS126's field/property check and would force the entire
        // reachable graph through the safe-type walker.
        const string source = """
            namespace Sample
            {
                public sealed class Mutable { public int X; }

                [Trecs.Immutable]
                public sealed class Good
                {
                    public readonly int Value;
                    public Good(int v) { Value = v; }
                    public Mutable ComputeSomething() => null;
                }
            }
            """;

        AssertDoesNotFire(source, "TRECS127");
    }

    // ───────────── Plain (non-[Immutable]) interface ─────────────

    [Test]
    public void TRECS127_OnPlainInterface_DoesNotFire()
    {
        // The rule keys on [Trecs.Immutable] — a plain interface returning
        // a mutable concrete is the C# baseline and not a TRECS127 problem.
        const string source = """
            namespace Sample
            {
                public sealed class Mutable { public int X; }

                public interface IWhatever
                {
                    Mutable Get();
                }
            }
            """;

        AssertDoesNotFire(source, "TRECS127");
    }

    // ───────────── helpers ─────────────

    static void AssertFires(string source, string expectedId)
    {
        var diagnostics = GeneratorTestHarness.RunAnalyzers(
            new DiagnosticAnalyzer[] { new ImmutableAnalyzer() },
            source
        );
        var diag = diagnostics.FirstOrDefault(d => d.Id == expectedId);
        Assert.That(diag, Is.Not.Null, $"Expected {expectedId}, got:\n{Format(diagnostics)}");
    }

    static void AssertDoesNotFire(string source, string id)
    {
        var diagnostics = GeneratorTestHarness.RunAnalyzers(
            new DiagnosticAnalyzer[] { new ImmutableAnalyzer() },
            source
        );
        var hit = diagnostics.FirstOrDefault(d => d.Id == id);
        Assert.That(hit, Is.Null, $"Unexpected {id}: {hit}");
    }

    static string Format(ImmutableArray<Diagnostic> diagnostics)
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
