using System.Collections.Immutable;
using System.Linq;
using Microsoft.CodeAnalysis;
using Microsoft.CodeAnalysis.Diagnostics;
using NUnit.Framework;

namespace Trecs.SourceGen.Tests;

/// <summary>
/// Tests for <see cref="NativeSharedPtrImmutabilityAnalyzer"/> (TRECS124). The
/// analyzer flags <c>NativeSharedPtr&lt;T&gt;</c> / <c>NativeSharedRead&lt;T&gt;</c>
/// and the factory methods on the static <c>Trecs.NativeSharedPtr</c> class
/// when T isn't defensive-copy-safe. Two shapes pass: <c>readonly struct</c>
/// (every instance method implicitly readonly), or a non-readonly struct
/// whose every instance method/accessor explicitly carries the
/// <c>readonly</c> modifier (the relaxed form, added to let BlobBuilder
/// users mutate fields directly during construction).
/// </summary>
[TestFixture]
public class Diagnostics_TRECS124_NativeSharedPtrImmutabilityTests
{
    [Test]
    public void TRECS124_PrimitiveTypeArg_DoesNotFire()
    {
        const string source = """
            namespace Sample
            {
                public class Holder
                {
                    public Trecs.NativeSharedPtr<int> Field;
                }
            }
            """;

        AssertDoesNotFire(source, "TRECS124");
    }

    [Test]
    public void TRECS124_FloatTypeArg_DoesNotFire()
    {
        const string source = """
            namespace Sample
            {
                public class Holder
                {
                    public Trecs.NativeSharedPtr<float> Field;
                }
            }
            """;

        AssertDoesNotFire(source, "TRECS124");
    }

    [Test]
    public void TRECS124_EnumTypeArg_DoesNotFire()
    {
        const string source = """
            namespace Sample
            {
                public enum Flavor { A, B }

                public class Holder
                {
                    public Trecs.NativeSharedPtr<Flavor> Field;
                }
            }
            """;

        AssertDoesNotFire(source, "TRECS124");
    }

    [Test]
    public void TRECS124_ReadonlyStructTypeArg_DoesNotFire()
    {
        const string source = """
            namespace Sample
            {
                public readonly struct Blob { public readonly int X; }

                public class Holder
                {
                    public Trecs.NativeSharedPtr<Blob> Field;
                }
            }
            """;

        AssertDoesNotFire(source, "TRECS124");
    }

    [Test]
    public void TRECS124_NonReadonlyStructWithNonReadonlyMethod_FieldDeclaration_Fires()
    {
        const string source = """
            namespace Sample
            {
                public struct Mutable { public int X; public void Inc() { X++; } }

                public class Holder
                {
                    public Trecs.NativeSharedPtr<Mutable> Field;
                }
            }
            """;

        AssertFires(source, "TRECS124");
    }

    [Test]
    public void TRECS124_NonReadonlyStructWithNonReadonlyMethod_LocalVariable_Fires()
    {
        const string source = """
            namespace Sample
            {
                public struct Mutable { public int X; public void Inc() { X++; } }

                public class Holder
                {
                    public void Foo()
                    {
                        Trecs.NativeSharedPtr<Mutable> ptr = default;
                    }
                }
            }
            """;

        AssertFires(source, "TRECS124");
    }

    [Test]
    public void TRECS124_NonReadonlyStructWithNonReadonlyMethod_Parameter_Fires()
    {
        const string source = """
            namespace Sample
            {
                public struct Mutable { public int X; public void Inc() { X++; } }

                public class Holder
                {
                    public void Foo(Trecs.NativeSharedPtr<Mutable> ptr) { }
                }
            }
            """;

        AssertFires(source, "TRECS124");
    }

    [Test]
    public void TRECS124_NativeSharedRead_NonReadonlyStructWithNonReadonlyMethod_Fires()
    {
        const string source = """
            namespace Sample
            {
                public struct Mutable { public int X; public void Inc() { X++; } }

                public class Holder
                {
                    public Trecs.NativeSharedRead<Mutable> Field;
                }
            }
            """;

        AssertFires(source, "TRECS124");
    }

    [Test]
    public void TRECS124_NativeSharedRead_ReadonlyStruct_DoesNotFire()
    {
        const string source = """
            namespace Sample
            {
                public readonly struct Blob { public readonly int X; }

                public class Holder
                {
                    public Trecs.NativeSharedRead<Blob> Field;
                }
            }
            """;

        AssertDoesNotFire(source, "TRECS124");
    }

    [Test]
    public void TRECS124_StaticFactoryAlloc_NonReadonlyStructWithNonReadonlyMethod_Fires()
    {
        const string source = """
            namespace Sample
            {
                public struct Mutable { public int X; public void Inc() { X++; } }

                public static class Helpers
                {
                    public static void Build()
                    {
                        var ptr = Trecs.NativeSharedPtr.Alloc<Mutable>(default);
                    }
                }
            }
            """;

        AssertFires(source, "TRECS124");
    }

    [Test]
    public void TRECS124_StaticFactoryAlloc_ReadonlyStruct_DoesNotFire()
    {
        const string source = """
            namespace Sample
            {
                public readonly struct Blob { public readonly int X; }

                public static class Helpers
                {
                    public static void Build()
                    {
                        var ptr = Trecs.NativeSharedPtr.Alloc<Blob>(default);
                    }
                }
            }
            """;

        AssertDoesNotFire(source, "TRECS124");
    }

    [Test]
    public void TRECS124_StaticFactoryAlloc_Primitive_DoesNotFire()
    {
        const string source = """
            namespace Sample
            {
                public static class Helpers
                {
                    public static void Build()
                    {
                        var ptr = Trecs.NativeSharedPtr.Alloc<int>(default);
                    }
                }
            }
            """;

        AssertDoesNotFire(source, "TRECS124");
    }

    [Test]
    public void TRECS124_UnresolvedTypeParameter_DoesNotFire()
    {
        // Generic helper that doesn't know what T is yet — defer to whoever instantiates it.
        const string source = """
            namespace Sample
            {
                public static class Helpers<T> where T : unmanaged
                {
                    public static Trecs.NativeSharedPtr<T> Field;
                }
            }
            """;

        AssertDoesNotFire(source, "TRECS124");
    }

    [Test]
    public void TRECS124_ReadonlyStructWrappingNonReadonlyStruct_DoesNotFire()
    {
        // The canonical "hide non-readonly internals behind a readonly outer" pattern —
        // the analyzer only checks the outer type's modifier, intentionally. Catching
        // internal-field defensive copies is out of scope (see analyzer docs).
        const string source = """
            namespace Sample
            {
                public struct InnerMutable { public int X; }

                public readonly struct OuterReadonly
                {
                    readonly InnerMutable _inner;
                    public OuterReadonly(InnerMutable inner) { _inner = inner; }
                }

                public class Holder
                {
                    public Trecs.NativeSharedPtr<OuterReadonly> Field;
                }
            }
            """;

        AssertDoesNotFire(source, "TRECS124");
    }

    // ── Relaxed shape: non-readonly struct, but defensive-copy-safe ──

    [Test]
    public void TRECS124_NonReadonlyStructWithNoMethods_DoesNotFire()
    {
        // Mutable fields are fine — the access API (NativeSharedRead<T>.Value
        // returns ref readonly) prevents external mutation, and there are no
        // methods that could trigger defensive copies.
        const string source = """
            namespace Sample
            {
                public struct Blob { public int Header; public float Y; }

                public class Holder
                {
                    public Trecs.NativeSharedPtr<Blob> Field;
                }
            }
            """;

        AssertDoesNotFire(source, "TRECS124");
    }

    [Test]
    public void TRECS124_NonReadonlyStructWithAllReadonlyMethods_DoesNotFire()
    {
        // Methods carry the explicit `readonly` modifier — defensive copies
        // through ref readonly are prevented just like with a readonly struct.
        const string source = """
            namespace Sample
            {
                public struct Blob
                {
                    public int Header;
                    public readonly int Doubled() => Header * 2;
                    public readonly int GetHeader() => Header;
                }

                public class Holder
                {
                    public Trecs.NativeSharedPtr<Blob> Field;
                }
            }
            """;

        AssertDoesNotFire(source, "TRECS124");
    }

    [Test]
    public void TRECS124_NonReadonlyStructWithReadonlyPropertyGetter_DoesNotFire()
    {
        // Auto-property without setter is implicitly readonly. Computed
        // property with `readonly get` also passes.
        const string source = """
            namespace Sample
            {
                public struct Blob
                {
                    public int Header;
                    public int AutoProp { get; }
                    public readonly int Computed => Header + 1;
                }

                public class Holder
                {
                    public Trecs.NativeSharedPtr<Blob> Field;
                }
            }
            """;

        AssertDoesNotFire(source, "TRECS124");
    }

    [Test]
    public void TRECS124_NonReadonlyStructWithAutoPropertySetter_Fires()
    {
        // Auto-prop with set has a non-readonly setter accessor — that's a
        // mutation method, which would trigger defensive copies through
        // ref readonly.
        const string source = """
            namespace Sample
            {
                public struct Blob
                {
                    public int Header { get; set; }
                }

                public class Holder
                {
                    public Trecs.NativeSharedPtr<Blob> Field;
                }
            }
            """;

        AssertFires(source, "TRECS124");
    }

    [Test]
    public void TRECS124_NonReadonlyStructWithMixedMethods_Fires()
    {
        // One readonly, one non-readonly — the whole struct still fails.
        const string source = """
            namespace Sample
            {
                public struct Blob
                {
                    public int Header;
                    public readonly int GetHeader() => Header;
                    public void Mutate() { Header++; }
                }

                public class Holder
                {
                    public Trecs.NativeSharedPtr<Blob> Field;
                }
            }
            """;

        AssertFires(source, "TRECS124");
    }

    static void AssertFires(string source, string expectedId)
    {
        var diagnostics = GeneratorTestHarness.RunAnalyzers(
            new DiagnosticAnalyzer[] { new NativeSharedPtrImmutabilityAnalyzer() },
            source
        );
        var diag = diagnostics.FirstOrDefault(d => d.Id == expectedId);
        Assert.That(diag, Is.Not.Null, $"Expected {expectedId}, got:\n{Format(diagnostics)}");
    }

    static void AssertDoesNotFire(string source, string id)
    {
        var diagnostics = GeneratorTestHarness.RunAnalyzers(
            new DiagnosticAnalyzer[] { new NativeSharedPtrImmutabilityAnalyzer() },
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
