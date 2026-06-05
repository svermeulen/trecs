using System.Collections.Immutable;
using System.Linq;
using Microsoft.CodeAnalysis;
using Microsoft.CodeAnalysis.Diagnostics;
using NUnit.Framework;

namespace Trecs.SourceGen.Tests;

/// <summary>
/// Tests for TRECS133 — a non-<c>readonly</c> instance member invoked on a
/// <c>[NonCopyable]</c> value through a read-only reference (an <c>in</c> parameter
/// or a <c>ref readonly</c> local), which the compiler services with a silent
/// defensive copy of the receiver. Emitted by <see cref="NonCopyableAnalyzer"/>.
///
/// The shared fixture exposes a non-copyable struct with both a mutating instance
/// method and a non-readonly property/indexer (the copy-prone members) plus a
/// <c>readonly</c> method (the copy-safe member), so each case can pick the member
/// that exercises it.
/// </summary>
[TestFixture]
public class Diagnostics_TRECS133_NonCopyableReadonlyRefTests
{
    const string Fixture = """
        namespace Sample
        {
            [Trecs.NonCopyable]
            public struct Writer
            {
                int _count;
                public void Add(int x) { _count += x; }     // non-readonly instance method
                public readonly int Peek() => _count;        // readonly method — copy-safe
                public int Count                             // non-readonly getter + setter
                {
                    get { return _count; }
                    set { _count = value; }
                }
                public int this[int i]                       // non-readonly indexer
                {
                    get { return _count; }
                    set { _count = value; }
                }
                public static Writer Make() => new Writer();
            }
        }
        """;

    [Test]
    public void InParameter_NonReadonlyMethod_Fires()
    {
        const string source = """
            namespace Sample
            {
                public static class Caller
                {
                    public static void Use(in Writer w) { w.Add(1); }
                }
            }
            """;

        AssertFires(Fixture + source, "TRECS133");
    }

    [Test]
    public void RefParameter_NonReadonlyMethod_DoesNotFire()
    {
        // A writable `ref` receiver is mutated in place — no defensive copy.
        const string source = """
            namespace Sample
            {
                public static class Caller
                {
                    public static void Use(ref Writer w) { w.Add(1); }
                }
            }
            """;

        AssertDoesNotFire(Fixture + source, "TRECS133");
    }

    [Test]
    public void InParameter_ReadonlyMethod_DoesNotFire()
    {
        // A `readonly` member reads through the `in` reference without copying.
        const string source = """
            namespace Sample
            {
                public static class Caller
                {
                    public static int Use(in Writer w) => w.Peek();
                }
            }
            """;

        AssertDoesNotFire(Fixture + source, "TRECS133");
    }

    [Test]
    public void InParameter_NonReadonlyGetter_Fires()
    {
        const string source = """
            namespace Sample
            {
                public static class Caller
                {
                    public static int Use(in Writer w) => w.Count;
                }
            }
            """;

        AssertFires(Fixture + source, "TRECS133");
    }

    [Test]
    public void InParameter_IndexerSetter_Fires()
    {
        // `w[0] = 5` invokes the non-readonly setter on a readonly receiver — the
        // assignment lands on the throwaway copy and is silently lost.
        const string source = """
            namespace Sample
            {
                public static class Caller
                {
                    public static void Use(in Writer w) { w[0] = 5; }
                }
            }
            """;

        AssertFires(Fixture + source, "TRECS133");
    }

    [Test]
    public void RefReadonlyLocal_NonReadonlyMethod_Fires()
    {
        const string source = """
            namespace Sample
            {
                public static class Caller
                {
                    public static void Use(in Writer w)
                    {
                        ref readonly var local = ref w;
                        local.Add(1);
                    }
                }
            }
            """;

        AssertFires(Fixture + source, "TRECS133");
    }

    [Test]
    public void ByValueLocal_NonReadonlyMethod_DoesNotFire()
    {
        // A by-value local is writable storage — the mutation happens in place.
        // (Initialized from a method return, so TRECS118 does not fire either.)
        const string source = """
            namespace Sample
            {
                public static class Caller
                {
                    public static void Use()
                    {
                        var w = Writer.Make();
                        w.Add(1);
                    }
                }
            }
            """;

        AssertDoesNotFire(Fixture + source, "TRECS133");
    }

    [Test]
    public void InParameter_CopyableType_DoesNotFire()
    {
        // Plain (non-[NonCopyable]) struct — a defensive copy through `in` is harmless
        // and expected, so the rule must not fire.
        const string source = """
            namespace Sample
            {
                public struct Plain { public void Add(int x) { } }

                public static class Caller
                {
                    public static void Use(in Plain p) { p.Add(1); }
                }
            }
            """;

        AssertDoesNotFire(source, "TRECS133");
    }

    [Test]
    public void InParameter_EntityComponent_Fires()
    {
        // IEntityComponent is non-copyable by default — calling a non-readonly member on an
        // `in` component parameter (the shape [ForEachEntity] hands out) copies it.
        const string source = """
            namespace Sample
            {
                public struct CData : Trecs.IEntityComponent { int _x; public void Bump() { _x++; } }

                public static class Caller
                {
                    public static void Use(in CData c) { c.Bump(); }
                }
            }
            """;

        AssertFires(source, "TRECS133");
    }

    [Test]
    public void InParameter_CopyableComponent_DoesNotFire()
    {
        const string source = """
            namespace Sample
            {
                [Trecs.Copyable]
                public struct CHandle : Trecs.IEntityComponent { int _x; public void Bump() { _x++; } }

                public static class Caller
                {
                    public static void Use(in CHandle c) { c.Bump(); }
                }
            }
            """;

        AssertDoesNotFire(source, "TRECS133");
    }

    [Test]
    public void InParameter_TransitiveNonCopyable_Fires()
    {
        // A struct that transitively contains a [NonCopyable] field is itself non-copyable.
        const string source = """
            namespace Sample
            {
                [Trecs.NonCopyable]
                public struct Inner { int _x; }

                public struct Wrapper { public Inner Value; public void Touch() { } }

                public static class Caller
                {
                    public static void Use(in Wrapper w) { w.Touch(); }
                }
            }
            """;

        AssertFires(source, "TRECS133");
    }

    [Test]
    public void InParameter_OnlyCopyableFields_DoesNotFire()
    {
        // Regression lock for the aspect-safety reasoning: a generated aspect has non-readonly
        // ref-returning getters but is passed `in`. It stays safe only because its fields are
        // `readonly struct` accessors (copyable), so the aspect is not non-copyable. A struct
        // whose fields are all copyable must not fire even with a non-readonly getter.
        const string source = """
            namespace Sample
            {
                public readonly struct Accessor { public readonly int X; }

                public struct AspectLike
                {
                    Accessor _a;
                    public int Read() => _a.X;   // non-readonly getter, like a generated aspect property
                }

                public static class Caller
                {
                    public static int Use(in AspectLike a) => a.Read();
                }
            }
            """;

        AssertDoesNotFire(source, "TRECS133");
    }

    [Test]
    public void RefThisExtensionMutator_DoesNotFire()
    {
        // Mutators exposed as `ref this` extension methods are static; calling one
        // through a writable `ref` is the normal, copy-free path. (The compiler itself
        // blocks the `in` misuse via CS8329, so there's nothing for TRECS133 to add.)
        const string source = """
            namespace Sample
            {
                [Trecs.NonCopyable]
                public struct Buf { public int Count; }

                public static class BufExtensions
                {
                    public static void Bump(this ref Buf b) { b.Count++; }
                }

                public static class Caller
                {
                    public static void Use(ref Buf b) { b.Bump(); }
                }
            }
            """;

        AssertDoesNotFire(source, "TRECS133");
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
