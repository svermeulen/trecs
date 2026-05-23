using System.Collections.Immutable;
using System.Linq;
using Microsoft.CodeAnalysis;
using Microsoft.CodeAnalysis.Diagnostics;
using NUnit.Framework;

namespace Trecs.SourceGen.Tests;

/// <summary>
/// Tests for the <c>[NonCopyable]</c> / <c>[Copyable]</c> diagnostics group
/// (TRECS118-120). All three are emitted by <see cref="NonCopyableAnalyzer"/>,
/// a <see cref="DiagnosticAnalyzer"/> rather than an
/// <see cref="IIncrementalGenerator"/>, so the tests drive it through
/// <see cref="GeneratorTestHarness.RunAnalyzers"/>.
///
/// Codes covered: 118, 119, 120.
/// </summary>
[TestFixture]
public class Diagnostics_TRECS118_to_120_NonCopyableTests
{
    [Test]
    public void TRECS118_CopyToLocalFromField_Fires()
    {
        const string source = """
            namespace Sample
            {
                [Trecs.NonCopyable]
                public struct Inline { public int X; }

                public class Holder
                {
                    public Inline Field;

                    public void Foo()
                    {
                        var copy = Field;
                    }
                }
            }
            """;

        AssertFires(source, "TRECS118");
    }

    [Test]
    public void TRECS118_CopyToLocalFromLocal_Fires()
    {
        const string source = """
            namespace Sample
            {
                [Trecs.NonCopyable]
                public struct Inline { public int X; }

                public static class Helpers
                {
                    public static void Run()
                    {
                        var a = new Inline();
                        var b = a;
                    }
                }
            }
            """;

        AssertFires(source, "TRECS118");
    }

    [Test]
    public void TRECS118_CopyToLocalFromParameter_Fires()
    {
        const string source = """
            namespace Sample
            {
                [Trecs.NonCopyable]
                public struct Inline { public int X; }

                public static class Helpers
                {
                    public static void Run(ref Inline arg)
                    {
                        var copy = arg;
                    }
                }
            }
            """;

        AssertFires(source, "TRECS118");
    }

    [Test]
    public void TRECS118_NewInstance_DoesNotFire()
    {
        const string source = """
            namespace Sample
            {
                [Trecs.NonCopyable]
                public struct Inline { public int X; }

                public static class Helpers
                {
                    public static void Run()
                    {
                        var a = new Inline();
                    }
                }
            }
            """;

        AssertDoesNotFire(source, "TRECS118");
    }

    [Test]
    public void TRECS118_DefaultInitializer_DoesNotFire()
    {
        const string source = """
            namespace Sample
            {
                [Trecs.NonCopyable]
                public struct Inline { public int X; }

                public static class Helpers
                {
                    public static void Run()
                    {
                        Inline a = default;
                    }
                }
            }
            """;

        AssertDoesNotFire(source, "TRECS118");
    }

    [Test]
    public void TRECS118_MethodReturn_DoesNotFire()
    {
        const string source = """
            namespace Sample
            {
                [Trecs.NonCopyable]
                public struct Inline { public int X; }

                public static class Helpers
                {
                    public static Inline Make() => new Inline();

                    public static void Run()
                    {
                        var a = Make();
                    }
                }
            }
            """;

        AssertDoesNotFire(source, "TRECS118");
    }

    [Test]
    public void TRECS118_RefLocalFromField_DoesNotFire()
    {
        const string source = """
            namespace Sample
            {
                [Trecs.NonCopyable]
                public struct Inline { public int X; }

                public class Holder
                {
                    public Inline Field;

                    public void Foo()
                    {
                        ref var alias = ref Field;
                    }
                }
            }
            """;

        AssertDoesNotFire(source, "TRECS118");
    }

    [Test]
    public void TRECS119_ByValueParameter_Fires()
    {
        const string source = """
            namespace Sample
            {
                [Trecs.NonCopyable]
                public struct Inline { public int X; }

                public static class Helpers
                {
                    public static void Take(Inline arg) { }
                }
            }
            """;

        AssertFires(source, "TRECS119");
    }

    [Test]
    public void TRECS119_RefParameter_DoesNotFire()
    {
        const string source = """
            namespace Sample
            {
                [Trecs.NonCopyable]
                public struct Inline { public int X; }

                public static class Helpers
                {
                    public static void Take(ref Inline arg) { }
                    public static void TakeIn(in Inline arg) { }
                    public static void TakeOut(out Inline arg) { arg = default; }
                }
            }
            """;

        AssertDoesNotFire(source, "TRECS119");
    }

    [Test]
    public void TRECS119_MemberOfNonCopyableType_DoesNotFire()
    {
        // The non-copyable type itself controls its own internals — Equals(in T)
        // and operators take by-value self in some patterns; don't fight them.
        const string source = """
            namespace Sample
            {
                [Trecs.NonCopyable]
                public struct Inline
                {
                    public int X;
                    public bool EqualsInline(Inline other) => other.X == X;
                }
            }
            """;

        AssertDoesNotFire(source, "TRECS119");
    }

    [Test]
    public void TRECS118_EntityComponent_NonCopyableByDefault_Fires()
    {
        // IEntityComponent is non-copyable by default — copying to a by-value local
        // from a field bypasses the component-buffer ref-access pattern.
        const string source = """
            namespace Sample
            {
                public struct CData : Trecs.IEntityComponent { public int X; }

                public class Holder
                {
                    public CData Field;

                    public void Foo()
                    {
                        var copy = Field;
                    }
                }
            }
            """;

        AssertFires(source, "TRECS118");
    }

    [Test]
    public void TRECS119_EntityComponent_ByValueParameter_Fires()
    {
        const string source = """
            namespace Sample
            {
                public struct CData : Trecs.IEntityComponent { public int X; }

                public static class Helpers
                {
                    public static void Take(CData arg) { }
                }
            }
            """;

        AssertFires(source, "TRECS119");
    }

    [Test]
    public void TRECS118_EntityComponent_WithCopyable_DoesNotFire()
    {
        // [Copyable] opts the component back into the C# default of free value-copy.
        const string source = """
            namespace Sample
            {
                [Trecs.Copyable]
                public struct CHandle : Trecs.IEntityComponent { public int Value; }

                public class Holder
                {
                    public CHandle Field;

                    public void Foo()
                    {
                        var copy = Field;
                    }
                }
            }
            """;

        AssertDoesNotFire(source, "TRECS118");
    }

    [Test]
    public void TRECS119_EntityComponent_WithCopyable_ByValueParameter_DoesNotFire()
    {
        const string source = """
            namespace Sample
            {
                [Trecs.Copyable]
                public struct CHandle : Trecs.IEntityComponent { public int Value; }

                public static class Helpers
                {
                    public static void Take(CHandle arg) { }
                }
            }
            """;

        AssertDoesNotFire(source, "TRECS119");
    }

    [Test]
    public void TRECS120_BothAttributes_Fires()
    {
        const string source = """
            namespace Sample
            {
                [Trecs.NonCopyable]
                [Trecs.Copyable]
                public struct Conflict : Trecs.IEntityComponent { public int X; }
            }
            """;

        AssertFires(source, "TRECS120");
    }

    [Test]
    public void TRECS120_OnlyNonCopyable_DoesNotFire()
    {
        const string source = """
            namespace Sample
            {
                [Trecs.NonCopyable]
                public struct Inline { public int X; }
            }
            """;

        AssertDoesNotFire(source, "TRECS120");
    }

    [Test]
    public void TRECS120_OnlyCopyable_DoesNotFire()
    {
        const string source = """
            namespace Sample
            {
                [Trecs.Copyable]
                public struct CData : Trecs.IEntityComponent { public int X; }
            }
            """;

        AssertDoesNotFire(source, "TRECS120");
    }

    [Test]
    public void TRECS118_Transitive_WrapperOfNonCopyable_Fires()
    {
        // A non-component struct that contains a [NonCopyable] field is itself
        // non-copyable by induction — copying the wrapper duplicates the inner
        // storage the same way.
        const string source = """
            namespace Sample
            {
                [Trecs.NonCopyable]
                public struct Inner { public int X; }

                public struct Wrapper { public Inner Value; }

                public class Holder
                {
                    public Wrapper Field;

                    public void Foo()
                    {
                        var copy = Field;
                    }
                }
            }
            """;

        AssertFires(source, "TRECS118");
    }

    [Test]
    public void TRECS118_Transitive_ComponentContainingNonCopyableField_Fires()
    {
        // A component without [Copyable] is already non-copyable (the IEntityComponent
        // default). But the transitive rule also applies when [Copyable] is present —
        // [Copyable] does not override the transitive rule, because copying a wrapper
        // of inline-storage data is exactly the bug we want to catch.
        const string source = """
            namespace Sample
            {
                [Trecs.NonCopyable]
                public struct Inner { public int X; }

                [Trecs.Copyable]
                public struct CWrapper : Trecs.IEntityComponent { public Inner Value; }

                public class Holder
                {
                    public CWrapper Field;

                    public void Foo()
                    {
                        var copy = Field;
                    }
                }
            }
            """;

        AssertFires(source, "TRECS118");
    }

    [Test]
    public void TRECS118_Transitive_NestedWrapper_Fires()
    {
        // Transitivity walks multiple levels.
        const string source = """
            namespace Sample
            {
                [Trecs.NonCopyable]
                public struct Inner { public int X; }

                public struct Mid { public Inner Value; }
                public struct Outer { public Mid Value; }

                public class Holder
                {
                    public Outer Field;

                    public void Foo()
                    {
                        var copy = Field;
                    }
                }
            }
            """;

        AssertFires(source, "TRECS118");
    }

    [Test]
    public void TRECS118_NonCopyableViaReferenceField_DoesNotFire()
    {
        // Reference-typed fields don't propagate non-copyability; copying the
        // struct copies the reference, not the underlying storage.
        const string source = """
            namespace Sample
            {
                public class Box { public int X; }

                public struct Wrapper { public Box Value; }

                public class Holder
                {
                    public Wrapper Field;

                    public void Foo()
                    {
                        var copy = Field;
                    }
                }
            }
            """;

        AssertDoesNotFire(source, "TRECS118");
    }

    [Test]
    public void TRECS118_StaticNonCopyableField_DoesNotFire()
    {
        // Static fields don't make the containing struct non-copyable — they
        // aren't part of the instance layout.
        const string source = """
            namespace Sample
            {
                [Trecs.NonCopyable]
                public struct Inner { public int X; }

                public struct Wrapper
                {
                    public static Inner Shared;
                    public int X;
                }

                public class Holder
                {
                    public Wrapper Field;

                    public void Foo()
                    {
                        var copy = Field;
                    }
                }
            }
            """;

        AssertDoesNotFire(source, "TRECS118");
    }

    static void AssertFires(string source, string expectedId)
    {
        var diagnostics = GeneratorTestHarness.RunAnalyzers(
            new DiagnosticAnalyzer[] { new NonCopyableAnalyzer() },
            source
        );
        var diag = diagnostics.FirstOrDefault(d => d.Id == expectedId);
        Assert.That(diag, Is.Not.Null, $"Expected {expectedId}, got:\n{Format(diagnostics)}");
    }

    static void AssertDoesNotFire(string source, string id)
    {
        var diagnostics = GeneratorTestHarness.RunAnalyzers(
            new DiagnosticAnalyzer[] { new NonCopyableAnalyzer() },
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
