using System.Linq;
using System.Text.RegularExpressions;
using NUnit.Framework;
using Trecs.SourceGen;

namespace Trecs.SourceGen.Tests;

/// <summary>
/// Compile-cleanliness tests for EntityComponentGenerator. The generator emits
/// Equals / GetHashCode / operator overloads for partial structs implementing IEntityComponent;
/// regressions usually surface as the emitted partial failing to compile (wrong type-parameter
/// list, missing namespace, double [Serializable], etc.).
/// </summary>
[TestFixture]
public class EntityComponentGeneratorTests
{
    [Test]
    public void SimpleComponent_CompilesCleanly()
    {
        const string source = """
            namespace Sample
            {
                public partial struct CPosition : Trecs.IEntityComponent
                {
                    public float X;
                    public float Y;
                }
            }
            """;

        var run = GeneratorTestHarness.Run(new EntityComponentGenerator(), source);

        Assert.That(run.CompileErrors, Is.Empty, run.Format());
        Assert.That(run.GenErrors, Is.Empty, run.Format());
        Assert.That(
            run.GeneratedTrees,
            Is.Not.Empty,
            "Expected the generator to emit at least one file."
        );
    }

    [Test]
    public void GenericComponent_PreservesTypeParameterList()
    {
        // Regression guard: dropping <T> from the emitted partial would silently emit a
        // phantom non-generic type that nothing merges into. The generator captures
        // TypeParameterList for exactly this reason — assert the result still compiles.
        const string source = """
            namespace Sample
            {
                public partial struct CGeneric<T> : Trecs.IEntityComponent
                    where T : unmanaged
                {
                    public T Value;
                }
            }
            """;

        var run = GeneratorTestHarness.Run(new EntityComponentGenerator(), source);

        Assert.That(run.CompileErrors, Is.Empty, run.Format());
    }

    [Test]
    public void ComponentWithExistingSerializable_DoesNotDoubleApplyAttribute()
    {
        // The generator skips its own [Serializable] when the user has already written one,
        // because SerializableAttribute has AllowMultiple=false and applying twice is CS0579.
        // This test would catch a regression that re-enabled emission unconditionally.
        const string source = """
            namespace Sample
            {
                [System.Serializable]
                public partial struct CSerialized : Trecs.IEntityComponent
                {
                    public int V;
                }
            }
            """;

        var run = GeneratorTestHarness.Run(new EntityComponentGenerator(), source);

        Assert.That(run.CompileErrors, Is.Empty, run.Format());
        // CS0579 = "Duplicate attribute" — name it specifically so a regression points
        // straight at the right cause.
        Assert.That(
            run.CompileDiagnostics.Any(d => d.Id == "CS0579"),
            Is.False,
            "Generator emitted a duplicate [Serializable] attribute.\n" + run.Format()
        );
    }

    [Test]
    public void NestedComponent_EmitsContainingTypeScopes()
    {
        // Components nested inside a partial outer type need the generator to emit matching
        // outer-type scopes. A regression that dropped containing-type tracking would
        // produce code that doesn't compile because the inner partial has no enclosing scope.
        const string source = """
            namespace Sample
            {
                public partial class Outer
                {
                    public partial struct CInner : Trecs.IEntityComponent
                    {
                        public int V;
                    }
                }
            }
            """;

        var run = GeneratorTestHarness.Run(new EntityComponentGenerator(), source);

        Assert.That(run.CompileErrors, Is.Empty, run.Format());
    }

    // --- Layout-hash const (closes the same-size field-edit fingerprint gap) ---

    // The default stubs include other IEntityComponent types that the generator also
    // emits a layout-hash const for, so scope extraction to the tree that declares the
    // component under test rather than grabbing the first const in the joined output.
    static ulong ExtractLayoutHash(GeneratorRun run, string componentName)
    {
        var tree = run.GeneratedTrees.FirstOrDefault(t =>
            t.ToString().Contains("partial struct " + componentName)
        );
        Assert.That(
            tree,
            Is.Not.Null,
            $"No generated file declared 'partial struct {componentName}'.\n" + run.Format()
        );
        var match = Regex.Match(
            tree!.ToString(),
            EntityComponentGenerator.ComponentLayoutHashFieldName + @"\s*=\s*(\d+)UL"
        );
        Assert.That(match.Success, Is.True, "No layout-hash const emitted.\n" + run.Format());
        return ulong.Parse(match.Groups[1].Value);
    }

    static ulong LayoutHashFor(string componentBody)
    {
        var source =
            "namespace Sample { public partial struct CLayoutProbe : Trecs.IEntityComponent { "
            + componentBody
            + " } }";
        var run = GeneratorTestHarness.Run(new EntityComponentGenerator(), source);
        Assert.That(run.CompileErrors, Is.Empty, run.Format());
        return ExtractLayoutHash(run, "CLayoutProbe");
    }

    [Test]
    public void LayoutHash_IsEmittedAsConst()
    {
        // The runtime WorldSchemaFingerprint reads this const by name via reflection,
        // so its presence (and stable name) is load-bearing.
        Assert.That(LayoutHashFor("public float X; public float Y;"), Is.Not.EqualTo(0UL));
    }

    [Test]
    public void LayoutHash_ChangesWhenSameSizeFieldsReorder()
    {
        // The whole point: swapping two same-size fields keeps UnsafeUtility.SizeOf
        // identical but must change the layout hash so a stale snapshot is rejected.
        var ab = LayoutHashFor("public int A; public float B;");
        var ba = LayoutHashFor("public float B; public int A;");
        Assert.That(ab, Is.Not.EqualTo(ba));
    }

    [Test]
    public void LayoutHash_IsStableAcrossPureRename()
    {
        // Renames are deliberately allowed — only field types + order are hashed,
        // and a rename preserves the blit layout, so snapshots stay compatible.
        var original = LayoutHashFor("public int Flags; public float Speed;");
        var renamed = LayoutHashFor("public int Mask; public float Velocity;");
        Assert.That(original, Is.EqualTo(renamed));
    }

    [Test]
    public void LayoutHash_RecursesIntoNestedStructReorders()
    {
        // A same-size reorder *inside* a nested struct field changes the parent's
        // layout, so it must change the parent component's hash too.
        var a =
            "public Inner V; "
            + "public struct Inner { public int A; public float B; }";
        var b =
            "public Inner V; "
            + "public struct Inner { public float B; public int A; }";
        Assert.That(LayoutHashFor(a), Is.Not.EqualTo(LayoutHashFor(b)));
    }
}
